using System.Diagnostics;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Runtime.InteropServices;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.Win32;

namespace NetPilot;

public static class ServiceClient
{
    public static bool IsInstalled
    {
        get { using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+AppPaths.ServiceName); return key!=null; }
    }
    public static async Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token=default)
    {
        using var deadline=CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(request.Command is "status" or "history" or "share-candidates"?TimeSpan.FromSeconds(3):TimeSpan.FromSeconds(150));
        using var pipe=new NamedPipeClientStream(".",AppPaths.PipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
        await pipe.ConnectAsync(2000,deadline.Token);
        await PipeMessages.WriteAsync(pipe,JsonData.Encode(request),deadline.Token);
        try { return JsonData.Decode<RpcReply>(await PipeMessages.ReadAsync(pipe,4*1024*1024,deadline.Token)); }
        catch(EndOfStreamException ex)
        { throw new IOException("Служба оборвала соединение до полного ответа. Команда повторно не отправлялась; её результат нужно проверить по состоянию службы.",ex); }
    }
    public static async Task InstallAsync()
    {
        var sid=WindowsIdentity.GetCurrent().User?.Value ?? throw new InvalidOperationException("Не определён пользователь Windows.");
        await RunElevatedAsync("--install-service","--repair-service","--owner-sid",sid);
        for(int i=0;i<20;i++)
        {
            try { var result=await SendAsync(new RpcRequest()); if(result.Ok) return; }
            catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException) { }
            await Task.Delay(500);
        }
        throw new InvalidOperationException("Служба не отвечает. Проверьте service.log в ProgramData\\NetPilot.");
    }
    public static Task UninstallAsync()=>RunElevatedAsync("--uninstall-service");
    internal static async Task InstallPackageAsync(string directory,CancellationToken token=default)
    {
        token.ThrowIfCancellationRequested();
        var full=Path.GetFullPath(directory);
        var version=ServiceInstaller.ReadManagedVersion(File.ReadAllBytes(Path.Combine(full,"RtlineDnsPilot.dll")),"RtlineDnsPilot.dll");
        var packageVersion=version.Revision==0?new Version(version.Major,version.Minor,Math.Max(0,version.Build)).ToString():version.ToString();
        _=ServiceInstaller.ReadPackage(full,packageVersion);
        var sid=WindowsIdentity.GetCurrent().User?.Value??throw new InvalidOperationException("Не определён пользователь Windows.");
        token.ThrowIfCancellationRequested();
        await RunElevatedExecutableAsync(Path.Combine(full,"RtlineDnsPilot.exe"),"--install-service","--repair-service","--owner-sid",sid);
        // Once Windows has started the elevated installer, wait for its actual
        // result rather than treating caller cancellation as a completed rollback.
        for(int i=0;i<20;i++)
        {
            try
            {
                var reply=await SendAsync(new RpcRequest {Command="status"});
                if(reply.Ok && reply.Status is { } status && Version.TryParse(status.ServiceVersion,out var actual)
                    && new Version(actual.Major,actual.Minor,Math.Max(0,actual.Build),Math.Max(0,actual.Revision))==version
                    && !status.SystemDnsManaged && !status.DnsRecoveryPending && !status.Config.ManagementRequested) return;
            }
            catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException) { }
            await Task.Delay(500);
        }
        throw new IOException("После обновления не подтверждены версия службы и восстановление DNS. Окно прежней версии будет сохранено.");
    }
    public static Task RecoverAsync()=>RunElevatedAsync("--restore-dns");
    public static Task ResetAutomaticAsync(string adapterId)
    {
        if(!Guid.TryParse(adapterId,out var id)) throw new ArgumentException("Выберите физический сетевой адаптер для автоматического DNS.",nameof(adapterId));
        var sid=WindowsIdentity.GetCurrent().User?.Value??throw new InvalidOperationException("Не определён пользователь Windows.");
        return RunElevatedAsync("--automatic-dns","--adapter-id",id.ToString("D"),"--owner-sid",sid);
    }
    private static Task RunElevatedAsync(params string[] args)=>RunElevatedExecutableAsync(Path.Combine(AppContext.BaseDirectory,"RtlineDnsPilot.exe"),args);
    private static async Task RunElevatedExecutableAsync(string executable,params string[] args)
    {
        var info=new ProcessStartInfo(executable)
        { UseShellExecute=true,Verb="runas",WindowStyle=ProcessWindowStyle.Hidden };
        foreach(var arg in args) info.ArgumentList.Add(arg);
        using var process=Process.Start(info)??throw new InvalidOperationException("Не удалось открыть окно UAC.");
        await process.WaitForExitAsync();
        if(process.ExitCode!=0) throw new InvalidOperationException("Операция администратора завершилась с ошибкой. См. сообщение помощника и service.log.");
    }
}

internal static class PipeMessages
{
    public static async Task<string> ReadAsync(Stream stream,int limit,CancellationToken token)
    {
        var header=new byte[4]; await stream.ReadExactlyAsync(header,token);
        int size=System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header);
        if(size<2||size>limit) throw new InvalidDataException("Размер сообщения вне допустимых границ.");
        var bytes=new byte[size]; await stream.ReadExactlyAsync(bytes,token);
        return new UTF8Encoding(false,true).GetString(bytes);
    }
    public static async Task WriteAsync(Stream stream,string message,CancellationToken token)
    {
        var bytes=Encoding.UTF8.GetBytes(message);
        if(bytes.Length>4*1024*1024) throw new InvalidDataException("Ответ слишком большой.");
        var header=new byte[4]; System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(header,bytes.Length);
        await stream.WriteAsync(header,token); await stream.WriteAsync(bytes,token); await stream.FlushAsync(token);
    }
}

internal sealed class ServiceRpc(MonitorEngine engine,string ownerSid,string pipeName=AppPaths.PipeName)
{
    public async Task RunAsync(CancellationToken token)
    {
        var acl=new PipeSecurity();
        acl.SetAccessRuleProtection(true,false);
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),PipeAccessRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(ownerSid),PipeAccessRights.ReadWrite,AccessControlType.Allow));
        // The fixed first instance prevents silently attaching to an already occupied control pipe.
        using var pipe=NamedPipeServerStreamAcl.Create(pipeName,PipeDirection.InOut,1,PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous|PipeOptions.FirstPipeInstance,16384,16384,acl);
        while(!token.IsCancellationRequested)
        {
            bool accepted=false;
            try
            {
                await pipe.WaitForConnectionAsync(token);
                accepted=true;
                using var timeout=CancellationTokenSource.CreateLinkedTokenSource(token); timeout.CancelAfter(TimeSpan.FromSeconds(150));
                RpcReply reply;
                bool reportOnly=false;
                try
                {
                    var request=JsonData.Decode<RpcRequest>(await PipeMessages.ReadAsync(pipe,1024*1024,timeout.Token));
                    reportOnly=request.Command is "history" or "share-candidates";
                    if(reportOnly)
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(3));
                        reply=request.Command=="history"
                            ?new RpcReply {Ok=true,History=engine.GetHistory(request.History??throw new ArgumentException("Не указан период графика."))}
                            :new RpcReply {Ok=true,Share=engine.GetShareCandidates()};
                    }
                    else
                    {
                        switch(request.Command)
                        {
                            case "status": break;
                            case "configure": await engine.ConfigureAsync(request.Config??throw new ArgumentException("Отсутствует конфигурация.")); break;
                            case "enable": await engine.SetManagementAsync(true); break;
                            case "disable": await engine.SetManagementAsync(false); break;
                            case "select": await engine.SetPreferredAsync(request.ProfileId); break;
                            case "probe": await engine.RequestProbeAsync(); break;
                            case "pause": await engine.SetMonitoringAsync(false); break;
                            case "resume": await engine.SetMonitoringAsync(true); break;
                            default: throw new ArgumentException("Неизвестная команда.");
                        }
                        reply=new RpcReply {Ok=true,Status=engine.GetStatus()};
                    }
                }
                catch(Exception ex) { reply=new RpcReply {Ok=false,Error=ex.Message,Status=reportOnly?null:engine.GetStatus()}; }
                await PipeMessages.WriteAsync(pipe,JsonData.Encode(reply),timeout.Token);
                // DisconnectNamedPipe discards unread buffered bytes on Windows.
                // Existing clients close their one-request connection after reading the
                // complete reply. Wait asynchronously for that EOF before disconnecting.
                using var delivered=CancellationTokenSource.CreateLinkedTokenSource(token);
                delivered.CancelAfter(TimeSpan.FromSeconds(5));
                var acknowledgement=new byte[1];
                int received=await pipe.ReadAsync(acknowledgement,delivered.Token);
                if(received!=0 && acknowledgement[0]!=1) throw new InvalidDataException("Некорректное подтверждение ответа службы.");
            }
            catch(OperationCanceledException) when(token.IsCancellationRequested) { break; }
            catch(Exception ex) when(ex is IOException or OperationCanceledException or System.Text.Json.JsonException or ArgumentException)
            { NativeServiceHost.Log("RPC: "+ex.Message); }
            // A broken read sets IsConnected=false, but the server still needs
            // Disconnect to accept another client instead of spinning on the old one.
            finally { if(accepted) pipe.Disconnect(); }
        }
    }
}

public sealed class InstallReceipt
{
    public string OwnerSid { get; set; } = "";
    [System.Text.Json.Serialization.JsonRequired]
    public string Version { get; set; } = AppVersion.Current;
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}

internal static class ServiceInstaller
{
    private static readonly string[] PackageFiles = ["RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json"];
    public static bool IsAdministrator=>new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);
    public static async Task InstallAsync(string sid,bool forceRestart=false)
    {
        RequireAdmin(); sid=new SecurityIdentifier(sid).Value;
        // Snapshot and validate the entire source before stopping a working service.
        // No later copy reopens a user-writable source file.
        var package=ReadPackage(AppContext.BaseDirectory);
        bool installed=ServiceClient.IsInstalled;
        VerifyStateDirectory();
        if(installed) VerifyOwnedService();
        if(Directory.Exists(AppPaths.InstallDirectory)) VerifyTrustedObject(AppPaths.InstallDirectory,true);
        foreach(var file in PackageFiles)
        {
            var path=Path.Combine(AppPaths.InstallDirectory,file);
            if(File.Exists(path)||Directory.Exists(path)) VerifyTrustedObject(path,false);
        }
        var previous=AtomicStore.Read<InstallReceipt>(Path.Combine(AppPaths.ServiceData,"install.json"));
        if(installed && previous is null) throw new InvalidDataException("Нет защищённой записи о владельце установленной службы.");
        var dll=Path.Combine(AppPaths.InstallDirectory,"RtlineDnsPilot.dll");
        if(!File.Exists(dll)) dll=Path.Combine(AppPaths.InstallDirectory,"NetPilot.dll");
        if(File.Exists(dll)) VerifyTrustedObject(dll,false);
        var installedVersion=File.Exists(dll)?ReadManagedVersion(File.ReadAllBytes(dll),"Установленная DLL",allowLegacy:true):null;
        bool sameFiles=PackageMatchesInstalled(package,AppPaths.InstallDirectory);
        bool needsUpdate=PackageNeedsUpdate(previous,sid,sameFiles,installedVersion);
        if(installed && !needsUpdate && !forceRestart)
        {
            // This also covers launching the helper from the installed directory:
            // never stop or copy it over itself when all four files and the receipt match.
            if(previous!.Version!=AppVersion.Current) WriteReceipt(previous,sid);
            await ConfigureAndStartAsync(); return;
        }
        if(installed) await RestoreAsync(false);
        SecureDirectory(AppPaths.InstallDirectory);
        SecureDirectory(AppPaths.ServiceData);
        VerifyStateDirectory();
        // An earlier interrupted session must be restored, never overwritten by installation.
        var manager=new DnsSessionManager(AppPaths.ServiceData,protectFile:ProtectFileOwner);
        if(manager.HasJournal) await manager.RestoreAsync();
        var config=AtomicStore.Read<AppConfig>(Path.Combine(AppPaths.ServiceData,"config.json"))??AppConfig.Defaults();
        config.ManagementRequested=false; config.Validate(); AtomicStore.Write(Path.Combine(AppPaths.ServiceData,"config.json"),config,ProtectFileOwner);
        ProtectFileOwner(Path.Combine(AppPaths.ServiceData,"config.json"));
        if(File.Exists(Path.Combine(AppPaths.ServiceData,"config.json.bak"))) ProtectFileOwner(Path.Combine(AppPaths.ServiceData,"config.json.bak"));
        if(!sameFiles) WritePackage(package);
        WriteReceipt(previous,sid);
        if(!installed) await ScAsync("create",AppPaths.ServiceName,"binPath=","\""+AppPaths.InstalledExe+"\" --service","start=","auto","DisplayName=","RtlineDnsPilot DNS");
        await ConfigureAndStartAsync();
    }

    internal static bool PackageNeedsUpdate(InstallReceipt? previous,string ownerSid,bool sameFiles,Version? installedBinaryVersion=null)
    {
        var current=NormalizeVersion(Version.Parse(AppVersion.Current));
        bool olderReceipt=false;
        if(previous is not null)
        {
            if(!string.Equals(previous.OwnerSid,ownerSid,StringComparison.Ordinal))
                throw new UnauthorizedAccessException("Служба или сохранённые данные NetPilot принадлежат другому пользователю. Они не изменены.");
            if(!Version.TryParse(previous.Version,out var previousVersion))
                throw new InvalidDataException("Не определена версия установленной службы. Автоматическая замена заблокирована.");
            if(NormalizeVersion(previousVersion)>current)
                throw new InvalidOperationException("Установлена более новая версия NetPilot. Понижение версии заблокировано.");
            olderReceipt=NormalizeVersion(previousVersion)<current;
        }
        if(installedBinaryVersion is not null && NormalizeVersion(installedBinaryVersion)>current)
            throw new InvalidOperationException("Установленный RtlineDnsPilot.dll новее этого пакета. Понижение версии заблокировано.");
        return olderReceipt || !sameFiles;
    }

    private static Version NormalizeVersion(Version version)=>new(version.Major,version.Minor,Math.Max(0,version.Build),Math.Max(0,version.Revision));

    internal static Dictionary<string,byte[]> ReadPackage(string sourceDirectory,string? expectedVersion=null)
    {
        expectedVersion??=AppVersion.Current;
        if(!Version.TryParse(expectedVersion,out var wanted)) throw new InvalidDataException("Некорректная ожидаемая версия пакета.");
        var package=new Dictionary<string,byte[]>(StringComparer.Ordinal);
        foreach(var file in PackageFiles)
        {
            var path=Path.Combine(sourceDirectory,file);
            if(!File.Exists(path)||(File.GetAttributes(path)&(FileAttributes.ReparsePoint|FileAttributes.Directory))!=0)
                throw new IOException("Отсутствует обычный файл пакета: "+file);
            var bytes=File.ReadAllBytes(path);
            if(bytes.Length==0) throw new InvalidDataException("Файл пакета пуст: "+file);
            package.Add(file,bytes);
        }
        ValidateRuntimeConfig(package["RtlineDnsPilot.runtimeconfig.json"]);
        ValidateDependencies(package["RtlineDnsPilot.deps.json"],expectedVersion);
        if(NormalizeVersion(ReadManagedVersion(package["RtlineDnsPilot.dll"],"RtlineDnsPilot.dll"))!=NormalizeVersion(wanted))
            throw new InvalidDataException("RtlineDnsPilot.dll не соответствует ожидаемой версии "+expectedVersion+". Пакет не изменён.");
        try
        {
            using var stream=new MemoryStream(package["RtlineDnsPilot.exe"],false);
            using var image=new PEReader(stream);
            if(image.PEHeaders.PEHeader is null || image.PEHeaders.PEHeader.AddressOfEntryPoint==0
                || image.PEHeaders.SectionHeaders.Length==0 || (image.PEHeaders.CoffHeader.Characteristics&Characteristics.ExecutableImage)==0
                || image.PEHeaders.SectionHeaders.Any(s=>(long)s.PointerToRawData+s.SizeOfRawData>stream.Length))
                throw new BadImageFormatException();
        }
        catch(BadImageFormatException ex) { throw new InvalidDataException("Повреждён исполняемый файл пакета RtlineDnsPilot.exe.",ex); }
        return package;
    }

    internal static Version ReadManagedVersion(byte[] bytes,string file,bool allowLegacy=false)
    {
        try
        {
            using var stream=new MemoryStream(bytes,false);
            using var image=new PEReader(stream);
            if(!image.HasMetadata || image.PEHeaders.CorHeader is null || image.PEHeaders.CorHeader.EntryPointTokenOrRelativeVirtualAddress==0)
                throw new BadImageFormatException();
            var metadata=image.GetMetadataReader();
            if(!metadata.IsAssembly) throw new BadImageFormatException();
            var assembly=metadata.GetAssemblyDefinition();
            var assemblyName=metadata.GetString(assembly.Name);
            if(assemblyName!="RtlineDnsPilot" && !(allowLegacy && assemblyName=="NetPilot")) throw new BadImageFormatException();
            return assembly.Version;
        }
        catch(Exception ex) when(ex is BadImageFormatException or InvalidOperationException or ArgumentException)
        { throw new InvalidDataException("Не удалось проверить версию или формат "+file+".",ex); }
    }

    private static void ValidateRuntimeConfig(byte[] bytes)
    {
        try
        {
            using var json=JsonDocument.Parse(bytes);
            var options=json.RootElement.GetProperty("runtimeOptions");
            var frameworks=options.GetProperty("frameworks").EnumerateArray().ToArray();
            bool HasFramework(string name)=>frameworks.Any(f=>f.GetProperty("name").GetString()==name
                && Version.TryParse(f.GetProperty("version").GetString(),out var version) && version.Major==10);
            if(options.GetProperty("tfm").GetString()!="net10.0" || !HasFramework("Microsoft.NETCore.App") || !HasFramework("Microsoft.WindowsDesktop.App"))
                throw new InvalidDataException("Некорректный RtlineDnsPilot.runtimeconfig.json: нужны .NET 10 и Windows Desktop.");
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Повреждён файл пакета RtlineDnsPilot.runtimeconfig.json.",ex); }
    }

    private static void ValidateDependencies(byte[] bytes,string expectedVersion)
    {
        try
        {
            using var json=JsonDocument.Parse(bytes);
            var root=json.RootElement;
            var target=root.GetProperty("runtimeTarget").GetProperty("name").GetString();
            var library="RtlineDnsPilot/"+expectedVersion;
            if(string.IsNullOrEmpty(target) || !root.GetProperty("libraries").TryGetProperty(library,out _)
                || !root.GetProperty("targets").GetProperty(target).GetProperty(library).GetProperty("runtime").TryGetProperty("RtlineDnsPilot.dll",out _))
                throw new InvalidDataException("RtlineDnsPilot.deps.json не соответствует версии пакета "+expectedVersion+".");
        }
        catch(Exception ex) when(ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("Повреждён файл пакета RtlineDnsPilot.deps.json.",ex); }
    }

    internal static bool PackageMatchesInstalled(IReadOnlyDictionary<string,byte[]> package,string directory)
    {
        foreach(var file in PackageFiles)
        {
            var path=Path.Combine(directory,file);
            if(!File.Exists(path)||!File.ReadAllBytes(path).AsSpan().SequenceEqual(package[file])) return false;
        }
        return true;
    }

    private static void WritePackage(IReadOnlyDictionary<string,byte[]> package)
    {
        // The service remains disabled until all files and the receipt are committed.
        foreach(var file in PackageFiles)
        {
            var destination=Path.Combine(AppPaths.InstallDirectory,file);
            var temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                File.WriteAllBytes(temporary,package[file]);
                ProtectFileOwner(temporary);
                File.Move(temporary,destination,true);
                VerifyTrustedObject(destination,false);
            }
            finally { if(File.Exists(temporary)) File.Delete(temporary); }
        }
        if(!PackageMatchesInstalled(package,AppPaths.InstallDirectory)) throw new IOException("Не подтверждена полная запись пакета. Служба оставлена выключенной.");
    }

    private static void WriteReceipt(InstallReceipt? previous,string sid)
    {
        var receipt=new InstallReceipt
        {
            OwnerSid=previous?.OwnerSid??sid,Version=AppVersion.Current,
            InstalledAt=previous?.InstalledAt??DateTimeOffset.UtcNow
        };
        var path=Path.Combine(AppPaths.ServiceData,"install.json");
        AtomicStore.Write(path,receipt,ProtectFileOwner);
        if(File.Exists(path+".bak")) ProtectFileOwner(path+".bak");
    }

    private static async Task ConfigureAndStartAsync()
    {
        // Also repairs installation interrupted after sc create, and explicitly reenables
        // a service disabled by the manual recovery helper.
        await ScAsync(ServiceConfigurationArguments());
        await ScAsync("description",AppPaths.ServiceName,"Local DNS relay with measured failover and DNS restoration.");
        await ScAsync("failure",AppPaths.ServiceName,"reset=","86400","actions=","restart/10000/restart/30000/restart/60000");
        await ScAsync("failureflag",AppPaths.ServiceName,"1");
        await ScAsync("start",AppPaths.ServiceName);
    }
    internal static string[] ServiceConfigurationArguments() =>
        ["config",AppPaths.ServiceName,"binPath=","\""+AppPaths.InstalledExe+"\" --service","start=","auto","DisplayName=","RtlineDnsPilot DNS"];

    public static async Task RestoreAsync(bool uninstall)
    {
        RequireAdmin();
        VerifyStateDirectory();
        if(ServiceClient.IsInstalled)
        {
            VerifyOwnedService();
            // A queued SCM restart is not cancelled by 'stop'. Disabled start mode is
            // necessary to keep a second recovery process out of this journal.
            await ScAsync("config",AppPaths.ServiceName,"start=","disabled");
            // Stop waits for the service's own restoration first. A failed service is handled by the durable journal below.
            await ScAsync("stop",AppPaths.ServiceName,allowAlreadyStopped:true);
            bool stopped=false;
            for(int i=0;i<240;i++)
            {
                if(!ServiceProcessRunning()) { stopped=true; break; }
                await Task.Delay(500);
            }
            if(!stopped) throw new InvalidOperationException("Служба ещё работает; ручное восстановление не начато, чтобы не создать гонку.");
        }
        var manager=new DnsSessionManager(AppPaths.ServiceData,protectFile:ProtectFileOwner);
        if(manager.HasJournal) await manager.RestoreAsync();
        var path=Path.Combine(AppPaths.ServiceData,"config.json");
        if(AtomicStore.Read<AppConfig>(path) is AppConfig config)
        {
            config.ManagementRequested=false; AtomicStore.Write(path,config,ProtectFileOwner); ProtectFileOwner(path);
            if(File.Exists(path+".bak")) ProtectFileOwner(path+".bak");
        }
        if(uninstall && ServiceClient.IsInstalled) await ScAsync("delete",AppPaths.ServiceName);
        NativeServiceHost.Log(uninstall?"Служба удалена; DNS восстановлен. История сохранена.":"Ручное восстановление DNS выполнено; управление выключено.");
    }

    public static async Task ResetAutomaticDnsAsync(string adapterId,string ownerSid)
    {
        RequireAdmin();
        if(!Guid.TryParse(adapterId,out var id)) throw new ArgumentException("Некорректный GUID выбранного адаптера.",nameof(adapterId));
        VerifyStateDirectory();
        bool installed=ServiceClient.IsInstalled;
        var receipt=AtomicStore.Read<InstallReceipt>(Path.Combine(AppPaths.ServiceData,"install.json"));
        ValidateAutomaticResetOwner(receipt,ownerSid,installed);
        if(installed) VerifyOwnedService();
        // Validate the selected physical GUID before stopping a service or
        // changing any protected state. No adapter is inferred from its index.
        _=WindowsAdapters.Resolve(id.ToString("D"));
        // Disables SCM restart, waits for the owned service, restores its exact
        // journal without forcing conflicts, then persists management=false.
        await RestoreAsync(false);
        SecureDirectory(AppPaths.ServiceData);
        VerifyStateDirectory();
        var manager=new DnsSessionManager(AppPaths.ServiceData,protectFile:ProtectFileOwner);
        await manager.ResetAutomaticAsync(id.ToString("D"));
        NativeServiceHost.Log("DNS выбранного адаптера переведён в автоматический режим. Управление выключено; служба остановлена.");
    }

    internal static void ValidateAutomaticResetOwner(InstallReceipt? receipt,string ownerSid,bool installed)
    {
        ownerSid=new SecurityIdentifier(ownerSid).Value;
        if(installed && receipt is null) throw new InvalidDataException("Нет защищённой записи о владельце службы. Автоматический DNS не применён.");
        if(receipt is not null && !string.Equals(receipt.OwnerSid,ownerSid,StringComparison.Ordinal))
            throw new UnauthorizedAccessException("Служба или сохранённые данные NetPilot принадлежат другому пользователю. Они не изменены.");
    }

    private static bool ServiceProcessRunning()
    {
        var scm=OpenSCManagerW(null,null,1);
        if(scm==IntPtr.Zero) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        try
        {
            var service=OpenServiceW(scm,AppPaths.ServiceName,4);
            if(service==IntPtr.Zero)
            { int error=Marshal.GetLastWin32Error(); if(error==1060) return false; throw new System.ComponentModel.Win32Exception(error); }
            try
            {
                if(!QueryServiceStatus(service,out var status)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
                return status.State!=1;
            }
            finally { CloseServiceHandle(service); }
        }
        finally { CloseServiceHandle(scm); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct ScmStatus { public uint Type,State,Accepted,Win32Exit,ServiceExit,Checkpoint,WaitHint; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenSCManagerW(string? machine,string? database,uint access);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr OpenServiceW(IntPtr manager,string name,uint access);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool QueryServiceStatus(IntPtr service,out ScmStatus status);
    [DllImport("advapi32.dll")] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CloseServiceHandle(IntPtr handle);

    private static void SecureDirectory(string path)
    {
        if(Directory.Exists(path)) VerifyTrustedObject(path,true);
        else if(!CreateDirectoryW(path,IntPtr.Zero)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error(),"Не удалось безопасно создать каталог программы.");
        var security=new DirectorySecurity(); security.SetAccessRuleProtection(true,false);
        var inheritance=InheritanceFlags.ContainerInherit|InheritanceFlags.ObjectInherit;
        var admins=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        security.SetOwner(admins);
        security.AddAccessRule(new FileSystemAccessRule(admins,FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,inheritance,PropagationFlags.None,AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,inheritance,PropagationFlags.None,AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    internal static void VerifyStateDirectory()
    {
        if(!Directory.Exists(AppPaths.ServiceData)) return;
        VerifyTrustedObject(AppPaths.ServiceData,true);
        foreach(var entry in Directory.EnumerateFileSystemEntries(AppPaths.ServiceData))
        {
            if(Directory.Exists(entry)) throw new IOException("В каталоге состояния обнаружен неожиданный подкаталог. Автоматическое восстановление заблокировано.");
            VerifyTrustedObject(entry,false);
        }
    }
    private static void VerifyOwnedService()
    {
        if(!File.Exists(Path.Combine(AppPaths.ServiceData,"install.json")))
            throw new InvalidOperationException("Нет защищённой записи о владельце NetPilot. Существующая служба не изменена.");
        using var key=Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+AppPaths.ServiceName);
        var executable=OwnedServiceExecutable(key?.GetValue("ImagePath") as string);
        VerifyTrustedObject(AppPaths.InstallDirectory,true); VerifyTrustedObject(executable,false);
    }
    internal static string OwnedServiceExecutable(string? imagePath)
    {
        foreach(var executable in new[] {AppPaths.InstalledExe,AppPaths.LegacyInstalledExe})
            if(string.Equals(imagePath,"\""+executable+"\" --service",StringComparison.OrdinalIgnoreCase)) return executable;
        throw new InvalidOperationException("Имя NetPilotDns принадлежит другой конфигурации службы. Она не изменена.");
    }
    private static void VerifyTrustedObject(string path,bool directory)
    {
        var attributes=File.GetAttributes(path);
        if((attributes&FileAttributes.ReparsePoint)!=0 || ((attributes&FileAttributes.Directory)!=0)!=directory)
            throw new IOException("Ссылка или неожиданный тип защищённого пути: "+path);
        FileSystemSecurity acl=directory?new DirectoryInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access)
            :new FileInfo(path).GetAccessControl(AccessControlSections.Owner|AccessControlSections.Access);
        if(!IsTrustedAcl(acl)) throw new UnauthorizedAccessException("Путь доступен для изменения без прав администратора: "+path+". Привилегированное чтение и восстановление заблокированы.");
    }
    private static void ProtectFileOwner(string path)
    {
        // Do not inherit a writable source-file ACL when copying a user-owned package
        // into the elevated service's installation directory.
        var acl=new FileSecurity(); acl.SetAccessRuleProtection(true,false);
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        acl.SetOwner(admin);
        acl.AddAccessRule(new FileSystemAccessRule(admin,FileSystemRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null),FileSystemRights.FullControl,AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null),FileSystemRights.ReadAndExecute,AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(acl);
    }
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool CreateDirectoryW(string path,IntPtr attributes);
    internal static bool IsTrustedAcl(FileSystemSecurity security)
    {
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        bool Privileged(IdentityReference? identity)=>identity is SecurityIdentifier sid&&(sid==admin||sid==system);
        if(!Privileged(security.GetOwner(typeof(SecurityIdentifier)))) return false;
        const FileSystemRights writes=FileSystemRights.Write|FileSystemRights.Delete|FileSystemRights.DeleteSubdirectoriesAndFiles
            |FileSystemRights.ChangePermissions|FileSystemRights.TakeOwnership;
        foreach(FileSystemAccessRule rule in security.GetAccessRules(true,true,typeof(SecurityIdentifier)))
            if(rule.AccessControlType==AccessControlType.Allow&&(rule.FileSystemRights&writes)!=0&&!Privileged(rule.IdentityReference)) return false;
        return true;
    }
    private static void RequireAdmin() { if(!IsAdministrator) throw new UnauthorizedAccessException("Требуются права администратора Windows."); }
    private static Task ScAsync(string command,string service,string? a=null,string? b=null,bool allowAlreadyStopped=false)
    {
        var args=new List<string>{command,service}; if(a!=null) args.Add(a); if(b!=null) args.Add(b);
        return ScCoreAsync(args.ToArray(),allowAlreadyStopped);
    }
    private static Task ScAsync(params string[] args)=>ScCoreAsync(args,false);
    private static async Task ScCoreAsync(string[] args,bool allowAlreadyStopped)
    {
        var info=new ProcessStartInfo(Path.Combine(Environment.SystemDirectory,"sc.exe")) {UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true};
        foreach(var arg in args) info.ArgumentList.Add(arg);
        using var p=Process.Start(info)??throw new IOException("sc.exe не запущен.");
        var stdout=p.StandardOutput.ReadToEndAsync(); var stderr=p.StandardError.ReadToEndAsync();
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(40));
        try { await p.WaitForExitAsync(timeout.Token); }
        catch(OperationCanceledException) { try { p.Kill(); } catch(InvalidOperationException) {} throw new TimeoutException("Windows не завершила команду службы."); }
        string output=(await stdout)+(await stderr);
        if(p.ExitCode!=0 && !(allowAlreadyStopped && p.ExitCode is 1062 or 1060) && !(args[0]=="start"&&p.ExitCode==1056))
            throw new InvalidOperationException("sc.exe "+args[0]+" завершился с кодом "+p.ExitCode+": "+output.Trim());
    }
}
