using System.Security.Cryptography;
using System.Security.Principal;
#if LEGACY_WINDOWS
using JsonRequiredAttribute = NetPilot.LegacyJsonRequiredAttribute;
#else
using JsonRequiredAttribute = System.Text.Json.Serialization.JsonRequiredAttribute;
#endif

namespace NetPilot;

// Only a completed elevated rollback with an unconfirmed final live status permits the
// unprivileged update worker to offer recovery from its already verified current package.
internal sealed class ServiceRollbackConfirmationException(string message) : IOException(message);
internal sealed class ServiceRollbackNotRequiredException(string message) : IOException(message);

internal sealed class ServiceRollbackSnapshot
{
    [JsonRequired] public int Protocol {get;set;}=1;
    [JsonRequired] public string Id {get;set;}="";
    [JsonRequired] public string OwnerSid {get;set;}="";
    [JsonRequired] public string SourceVersion {get;set;}="";
    [JsonRequired] public string DestinationVersion {get;set;}="";
    [JsonRequired] public string RuntimePlatform {get;set;}="";
    [JsonRequired] public DateTimeOffset CreatedUtc {get;set;}
    [JsonRequired] public InstallReceipt? SourceReceipt {get;set;}
    [JsonRequired] public Dictionary<string,byte[]> Package {get;set;}=new(StringComparer.Ordinal);
    [JsonRequired] public Dictionary<string,string> SourceHashes {get;set;}=new(StringComparer.Ordinal);
    [JsonRequired] public Dictionary<string,string> DestinationHashes {get;set;}=new(StringComparer.Ordinal);
}

internal sealed record ServiceRollbackState(InstallReceipt Receipt,Dictionary<string,byte[]> Package);
internal interface IServiceRollbackPlatform
{
    void RevalidateCurrent(ServiceRollbackState expected);
    Task RestoreAndStopAsync();
    void PreserveStatistics();
    void WritePackage(IReadOnlyDictionary<string,byte[]> package);
    void WriteReceipt(InstallReceipt receipt);
    Task StartAsync();
    Task ConfirmAsync(Version version);
}

public static partial class ServiceClient
{
    internal static async Task<bool> RollbackPackageAsync(string previousVersion,CancellationToken token=default)
    {
        var previous=ServiceInstaller.ParseRollbackPreviousVersion(previousVersion);
        token.ThrowIfCancellationRequested();
        if(!IsInstalled) return false;
        RpcReply? before=null;
        try {before=await SendAsync(new RpcRequest {Command="status"},token);}
        catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException)
        {token.ThrowIfCancellationRequested();}
        if(before is {Ok:true,Status:{ } status})
        {
            if(HasExpectedVersion(status,previous))
            {
                if(!DnsIsOff(status)) throw new InvalidOperationException("Служба уже предыдущей версии, но управление или восстановление DNS ещё включено. Сначала восстановите DNS.");
                return false;
            }
            if(!HasExpectedVersion(status,Version.Parse(AppVersion.Current)))
                throw new InvalidOperationException("Версия установленной службы отличается от текущего обновления. Откат другого выпуска не разрешён.");
        }
        string sid=WindowsIdentity.GetCurrent().User?.Value??throw new InvalidOperationException("Не определён пользователь Windows.");
        token.ThrowIfCancellationRequested();
        int exit=await RunRollbackHelperAsync(sid,previousVersion);
        if(exit==10)
        {
            if(!IsInstalled || await ConfirmRollbackVersionAsync(previous)) return false;
            throw new IOException("Служба не заменялась. Её состояние после отката не подтверждено.");
        }
        // The helper may already have replaced the protected package. Caller cancellation
        // is no longer evidence that this operation was never started.
        try { if(await ConfirmRollbackVersionAsync(previous)) return true; }
        catch(Exception ex) when(ex is UnauthorizedAccessException or InvalidDataException or System.Text.Json.JsonException)
        { throw new ServiceRollbackConfirmationException("Помощник отката завершился успешно, но получен некорректный ответ службы. "+ex.Message); }
        throw new ServiceRollbackConfirmationException("Помощник отката завершился успешно, но точная версия службы "+previousVersion+" и выключенное управление DNS не подтверждены.");
    }

    private static async Task<int> RunRollbackHelperAsync(string sid,string previousVersion)
    {
        var start=new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory,"RtlineDnsPilot.exe"))
        {UseShellExecute=true,Verb="runas",WindowStyle=System.Diagnostics.ProcessWindowStyle.Hidden};
        foreach(string arg in new[] {"--rollback-service","--previous-version",previousVersion,"--owner-sid",sid}) start.ArgumentList.Add(arg);
        using var process=System.Diagnostics.Process.Start(start)??throw new InvalidOperationException("Не удалось открыть окно UAC для отката службы.");
        await process.WaitForExitAsync();
        if(process.ExitCode is not (0 or 10)) throw new IOException("Помощник отката службы завершился с ошибкой. Проверьте его сообщение и service.log; текущий пакет не переустанавливался автоматически.");
        return process.ExitCode;
    }

    internal static bool DnsIsOff(AppStatus status)=>!status.SystemDnsManaged && !status.DnsRecoveryPending && !status.Config.ManagementRequested;
    internal static async Task<bool> ConfirmRollbackVersionAsync(Version expected)
    {
        for(int attempt=0;attempt<20;attempt++)
        {
            try
            {
                var reply=await SendAsync(new RpcRequest {Command="status"});
                if(reply.Ok && reply.Status is { } status && HasExpectedVersion(status,expected) && DnsIsOff(status)) return true;
            }
            catch(Exception ex) when(ex is IOException or TimeoutException or OperationCanceledException) { }
            if(attempt<19) await Task.Delay(500);
        }
        return false;
    }
}

internal static partial class ServiceInstaller
{
    internal const int MaximumRuntimeFileBytes=16*1024*1024;
    private const int MaximumRuntimePackageBytes=32*1024*1024,MaximumRollbackSnapshotBytes=48*1024*1024;
    private const int MaximumStatisticsCopyBytes=32*1024*1024;
    private const string RollbackSnapshotFile="service-package.previous.json";
    private sealed record UpgradeRollbackPlan(ServiceRollbackState Current,ServiceRollbackSnapshot Snapshot);

    internal static Version ParseRollbackPreviousVersion(string value)
    {
        if(string.IsNullOrWhiteSpace(value) || value.Length>64 || !Version.TryParse(value,out var version))
            throw new ArgumentException("Некорректная предыдущая версия службы.",nameof(value));
        version=NormalizeVersion(version);
        if(version>=NormalizeVersion(Version.Parse(AppVersion.Current)))
            throw new InvalidOperationException("Откат разрешён только к версии до текущего обновления.");
        return version;
    }

    internal static bool IsRollbackAlreadyApplied(InstallReceipt receipt,string previousVersion)
    {
        var previous=ParseRollbackPreviousVersion(previousVersion);
        if(!Version.TryParse(receipt.Version,out var installed)) throw new InvalidDataException("Не определена защищённая версия службы.");
        installed=NormalizeVersion(installed);
        if(installed==previous) return true;
        if(installed!=NormalizeVersion(Version.Parse(AppVersion.Current)))
            throw new InvalidOperationException("Установлена другая версия службы. Откат разрешён только для последней замены текущего выпуска.");
        return false;
    }

    internal static ServiceRollbackSnapshot CreateRollbackSnapshot(InstallReceipt previous,IReadOnlyDictionary<string,byte[]> source,
        string destinationVersion,IReadOnlyDictionary<string,byte[]> destination)
    {
        if(!Version.TryParse(previous.Version,out var oldVersion) || !Version.TryParse(destinationVersion,out var newVersion)
            || NormalizeVersion(oldVersion)>=NormalizeVersion(newVersion)) throw new InvalidOperationException("Снимок отката создаётся только при обновлении проверенной более старой службы.");
        string sid=new SecurityIdentifier(previous.OwnerSid).Value;
        ValidatePackageBytes(source,previous.Version);ValidatePackageBytes(destination,destinationVersion);
        return new ServiceRollbackSnapshot {Id=Guid.NewGuid().ToString("N"),OwnerSid=sid,SourceVersion=previous.Version,DestinationVersion=destinationVersion,
            RuntimePlatform=RuntimeCompatibility.PackagePlatform,CreatedUtc=DateTimeOffset.UtcNow,SourceReceipt=JsonData.Clone(previous),
            Package=source.ToDictionary(x=>x.Key,x=>x.Value.ToArray(),StringComparer.Ordinal),SourceHashes=Hashes(source),DestinationHashes=Hashes(destination)};
    }

    internal static void ValidateRollbackSnapshot(ServiceRollbackSnapshot? snapshot,string ownerSid,InstallReceipt current,string previousVersion,
        IReadOnlyDictionary<string,byte[]> currentPackage)
    {
        ownerSid=new SecurityIdentifier(ownerSid).Value;
        if(current.OwnerSid!=ownerSid) throw new UnauthorizedAccessException("Служба принадлежит другому пользователю. Откат не выполнен.");
        if(snapshot is null || snapshot.Protocol!=1 || !Guid.TryParseExact(snapshot.Id,"N",out _)
            || snapshot.Id!=current.RollbackSnapshotId || snapshot.SourceReceipt is null || snapshot.CreatedUtc==default
            || snapshot.RuntimePlatform!=RuntimeCompatibility.PackagePlatform)
            throw new InvalidDataException("Нет действительного защищённого снимка перед последней заменой этой службы. Откат не выполнен.");
        if(snapshot.OwnerSid!=ownerSid || snapshot.SourceReceipt.OwnerSid!=ownerSid)
            throw new UnauthorizedAccessException("Снимок отката принадлежит другому пользователю.");
        var wanted=ParseRollbackPreviousVersion(previousVersion);
        if(!Version.TryParse(snapshot.SourceVersion,out var sourceVersion) || !Version.TryParse(snapshot.DestinationVersion,out var destinationVersion)
            || !Version.TryParse(current.Version,out var installedVersion) || !Version.TryParse(snapshot.SourceReceipt.Version,out var receiptVersion))
            throw new InvalidDataException("Версии защищённого снимка отката повреждены.");
        if(NormalizeVersion(sourceVersion)!=wanted || NormalizeVersion(receiptVersion)!=wanted
            || NormalizeVersion(destinationVersion)!=NormalizeVersion(installedVersion)
            || NormalizeVersion(destinationVersion)!=NormalizeVersion(Version.Parse(AppVersion.Current))
            || NormalizeVersion(sourceVersion)>=NormalizeVersion(destinationVersion))
            throw new InvalidOperationException("Сохранённый снимок относится к другой паре версий. Откат не выполнен.");
        ValidatePackageBounds(snapshot.Package);
        ValidateHashes(snapshot.SourceHashes,snapshot.Package,"предыдущего пакета");
        ValidateHashes(snapshot.DestinationHashes,currentPackage,"текущего пакета");
        ValidatePackageBytes(snapshot.Package,snapshot.SourceVersion);
        ValidatePackageBytes(currentPackage,current.Version);
    }

    internal static ServiceRollbackSnapshot RetainRollbackSnapshot(ServiceRollbackSnapshot snapshot,string ownerSid,InstallReceipt current,
        IReadOnlyDictionary<string,byte[]> before,IReadOnlyDictionary<string,byte[]> replacement)
    {
        ValidateRollbackSnapshot(snapshot,ownerSid,current,snapshot.SourceVersion,before);
        ValidatePackageBytes(replacement,current.Version);
        var retained=JsonData.Clone(snapshot);retained.DestinationHashes=Hashes(replacement);return retained;
    }

    private static Dictionary<string,string> Hashes(IReadOnlyDictionary<string,byte[]> package)
        =>PackageFiles.ToDictionary(name=>name,name=>Convert.ToHexString(SHA256.HashData(package[name])),StringComparer.Ordinal);

    private static void ValidateHashes(IReadOnlyDictionary<string,string>? expected,IReadOnlyDictionary<string,byte[]> package,string description)
    {
        ValidatePackageBounds(package);
        if(expected is null || expected.Count!=PackageFiles.Length || PackageFiles.Any(name=>!expected.TryGetValue(name,out var hash)
            || hash is null || hash.Length!=64 || !hash.All(Uri.IsHexDigit) || !string.Equals(hash,Convert.ToHexString(SHA256.HashData(package[name])),StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Не подтверждена целостность "+description+". Откат не выполнен.");
    }

    private static void ValidatePackageBounds(IReadOnlyDictionary<string,byte[]> package)
    {
        if(package is null || package.Count!=PackageFiles.Length || PackageFiles.Any(name=>!package.TryGetValue(name,out var data)
            || data is null || data.Length is <=0 or >MaximumRuntimeFileBytes) || package.Values.Sum(x=>(long)x.Length)>MaximumRuntimePackageBytes)
            throw new InvalidDataException("Пакет должен содержать только четыре непустых runtime-файла допустимого размера.");
    }

    private static UpgradeRollbackPlan? PrepareUpgradeRollback(InstallReceipt current,IReadOnlyDictionary<string,byte[]> destination)
    {
        // Legacy or damaged installations remain repairable, but cannot authorize a guessed backup.
        if(!UsesModernServiceExecutable() || PackageFiles.Any(name=>!File.Exists(Path.Combine(AppPaths.InstallDirectory,name)))) return null;
        Dictionary<string,byte[]> installed;
        try {installed=ReadProtectedPackage(current.Version);}
        catch(InvalidDataException) {return null;}
        var state=new ServiceRollbackState(JsonData.Clone(current),installed);
        if(NormalizeVersion(Version.Parse(current.Version))<NormalizeVersion(Version.Parse(AppVersion.Current)))
            return new(state,CreateRollbackSnapshot(current,installed,AppVersion.Current,destination));
        if(current.RollbackSnapshotId is null) return null;
        try
        {
            var existing=ReadRollbackSnapshot();
            return existing is null?null:new(state,RetainRollbackSnapshot(existing,current.OwnerSid,current,installed,destination));
        }
        catch(Exception ex) when(ex is InvalidDataException or InvalidOperationException or System.Text.Json.JsonException) {return null;}
    }

    private static Dictionary<string,byte[]> ReadProtectedPackage(string version)
    {
        VerifyNoReparseAncestors(AppPaths.InstallDirectory);
        VerifyTrustedObject(AppPaths.InstallDirectory,true);
        foreach(string name in PackageFiles) VerifyTrustedObject(Path.Combine(AppPaths.InstallDirectory,name),false);
        return ReadPackage(AppPaths.InstallDirectory,version);
    }

    private static void RevalidateProtectedPackage(ServiceRollbackState expected)
    {
        VerifyStateDirectory();
        var receipt=ReadProtectedReceipt();
        if(JsonData.Encode(receipt)!=JsonData.Encode(expected.Receipt)
            || !SamePackage(expected.Package,ReadProtectedPackage(expected.Receipt.Version)))
            throw new IOException("Установленный пакет или запись владельца изменились после проверки. Замена не выполнена.");
    }
    private static bool SamePackage(IReadOnlyDictionary<string,byte[]> expected,IReadOnlyDictionary<string,byte[]> actual)
        =>PackageFiles.All(name=>expected[name].AsSpan().SequenceEqual(actual[name]));

    private static InstallReceipt ReadProtectedReceipt()
    {
        string path=Path.Combine(AppPaths.ServiceData,"install.json");
        _=ReadProtectedBytes(path,128*1024);
        return AtomicStore.Read<InstallReceipt>(path)??throw new InvalidDataException("Нет защищённой записи о владельце службы.");
    }
    private static ServiceRollbackSnapshot? ReadRollbackSnapshot()
    {
        string path=Path.Combine(AppPaths.ServiceData,RollbackSnapshotFile);
        if(!File.Exists(path)) return null;
        var bytes=ReadProtectedBytes(path,MaximumRollbackSnapshotBytes);
        return JsonData.Decode<ServiceRollbackSnapshot>(System.Text.Encoding.UTF8.GetString(bytes));
    }
    private static void SaveRollbackSnapshot(ServiceRollbackSnapshot snapshot)
    {
        VerifyStateDirectory();
        string path=Path.Combine(AppPaths.ServiceData,RollbackSnapshotFile);
        var encoded=System.Text.Encoding.UTF8.GetBytes(JsonData.Encode(snapshot));
        if(encoded.Length>MaximumRollbackSnapshotBytes) throw new InvalidDataException("Снимок предыдущего пакета превышает допустимый размер.");
        AtomicStore.Write(path,snapshot,ProtectFileOwner);
        if(File.Exists(path+".bak")) ProtectFileOwner(path+".bak");
        if(!ReadProtectedBytes(path,MaximumRollbackSnapshotBytes).AsSpan().SequenceEqual(encoded))
            throw new IOException("Не подтверждена защищённая запись снимка предыдущей службы.");
    }
    private static byte[] ReadProtectedBytes(string path,int maximum)
    {
        VerifyNoReparseAncestors(Path.GetDirectoryName(path)!);
        VerifyTrustedObject(path,false);
        using var input=new FileStream(path,FileMode.Open,FileAccess.Read,FileShare.Read);
        if(input.Length is <=0 || input.Length>maximum) throw new InvalidDataException("Размер защищённого файла вне допустимых границ: "+Path.GetFileName(path));
        var bytes=new byte[checked((int)input.Length)];int offset=0;
        while(offset<bytes.Length)
        {
            int read=input.Read(bytes,offset,bytes.Length-offset);
            if(read==0)throw new EndOfStreamException("Защищённый файл обрезан во время чтения.");
            offset+=read;
        }
        return bytes;
    }
    private static void VerifyNoReparseAncestors(string path)
    {
        for(var directory=new DirectoryInfo(Path.GetFullPath(path));directory is not null;directory=directory.Parent)
            if(directory.Exists && (directory.Attributes&FileAttributes.ReparsePoint)!=0) throw new IOException("Ссылка в защищённом пути. Привилегированная замена заблокирована.");
    }
    private static bool UsesModernServiceExecutable()
    {
        using var key=Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\"+AppPaths.ServiceName);
        return string.Equals(OwnedServiceExecutable(key?.GetValue("ImagePath") as string),AppPaths.InstalledExe,StringComparison.OrdinalIgnoreCase);
    }

    internal static async Task RollbackAsync(string ownerSid,string previousVersion)
    {
        RequireAdmin();ownerSid=new SecurityIdentifier(ownerSid).Value;
        _=ParseRollbackPreviousVersion(previousVersion);
        VerifyStateDirectory();
        if(!ServiceClient.IsInstalled) throw new ServiceRollbackNotRequiredException("Установленной службы нет; откат службы не требуется.");
        VerifyOwnedService();
        if(!UsesModernServiceExecutable()) throw new InvalidOperationException("Служба использует прежнюю структуру NetPilot. Защищённый откат этого пакета не разрешён.");
        var receipt=ReadProtectedReceipt();
        if(receipt.OwnerSid!=ownerSid) throw new UnauthorizedAccessException("Служба принадлежит другому пользователю. Откат не выполнен.");
        bool already=IsRollbackAlreadyApplied(receipt,previousVersion);
        var current=new ServiceRollbackState(receipt,ReadProtectedPackage(receipt.Version));
        if(already)
        {
            if(IsPipeOwner(ownerSid) && !await ServiceClient.ConfirmRollbackVersionAsync(ParseRollbackPreviousVersion(previousVersion)))
                throw new IOException("Служба уже предыдущей версии, но её состояние с выключенным DNS не подтверждено.");
            throw new ServiceRollbackNotRequiredException("Служба уже предыдущей версии; DNS и управление выключены.");
        }
        var snapshot=ReadRollbackSnapshot();
        ValidateRollbackSnapshot(snapshot,ownerSid,receipt,previousVersion,current.Package);
        await ExecuteRollbackAsync(current,snapshot!,new NativeServiceRollbackPlatform(ownerSid));
        NativeServiceHost.Log("Выполнен явный откат службы к "+previousVersion+"; управление DNS выключено. Пользовательские данные сохранены.");
    }

    internal static async Task ExecuteRollbackAsync(ServiceRollbackState current,ServiceRollbackSnapshot snapshot,IServiceRollbackPlatform platform)
    {
        ValidateRollbackSnapshot(snapshot,current.Receipt.OwnerSid,current.Receipt,snapshot.SourceVersion,current.Package);
        platform.RevalidateCurrent(current);
        bool stopped=false,replacementStarted=false;
        try
        {
            await platform.RestoreAndStopAsync();stopped=true;
            platform.RevalidateCurrent(current);
            platform.PreserveStatistics();
            replacementStarted=true;
            platform.WritePackage(snapshot.Package);
            var previous=JsonData.Clone(snapshot.SourceReceipt!);previous.RollbackSnapshotId=null;
            platform.WriteReceipt(previous);
            await platform.StartAsync();
            await platform.ConfirmAsync(Version.Parse(snapshot.SourceVersion));
        }
        catch(Exception rollbackError)
        {
            if(!stopped) throw;
            try
            {
                if(replacementStarted)
                {
                    await platform.RestoreAndStopAsync();
                    platform.WritePackage(current.Package);
                    platform.WriteReceipt(current.Receipt);
                }
                else platform.RevalidateCurrent(current);
                await platform.StartAsync();
                await platform.ConfirmAsync(Version.Parse(current.Receipt.Version));
            }
            catch(Exception restoreError)
            {
                throw new AggregateException("Откат службы не завершён; восстановление текущего проверенного пакета тоже не подтверждено. Управление DNS должно оставаться выключенным; требуется ручное восстановление.",rollbackError,restoreError);
            }
            throw new IOException("Откат службы не выполнен. Текущий проверенный пакет и запись владельца восстановлены; служба запущена с выключенным управлением DNS.",rollbackError);
        }
    }

    private static bool IsPipeOwner(string ownerSid)=>WindowsIdentity.GetCurrent().User?.Value==ownerSid;

    private sealed class NativeServiceRollbackPlatform(string ownerSid) : IServiceRollbackPlatform
    {
        public void RevalidateCurrent(ServiceRollbackState expected)=>RevalidateProtectedPackage(expected);
        public Task RestoreAndStopAsync()=>RestoreCoreAsync(false,forServiceUpdate:true);
        public void PreserveStatistics()
        {
            VerifyStateDirectory();
            string source=Path.Combine(AppPaths.ServiceData,"statistics.json");
            if(!File.Exists(source)) return;
            byte[] bytes=ReadProtectedBytes(source,MaximumStatisticsCopyBytes);
            string destination=Path.Combine(AppPaths.ServiceData,"statistics.pre-rollback.json");
            if(File.Exists(destination)||Directory.Exists(destination)) VerifyTrustedObject(destination,false);
            string temporary=destination+"."+Guid.NewGuid().ToString("N")+".tmp";
            try
            {
                using(var output=new FileStream(temporary,FileMode.CreateNew,FileAccess.Write,FileShare.None,4096,FileOptions.WriteThrough))
                {output.Write(bytes);output.Flush(true);}
                ProtectFileOwner(temporary);File.Move(temporary,destination,true);
                if(!ReadProtectedBytes(destination,MaximumStatisticsCopyBytes).AsSpan().SequenceEqual(bytes)) throw new IOException("Не подтверждена копия статистики перед откатом.");
            }
            finally {if(File.Exists(temporary))File.Delete(temporary);}
        }
        public void WritePackage(IReadOnlyDictionary<string,byte[]> package)
        {
            VerifyStateDirectory();VerifyNoReparseAncestors(AppPaths.InstallDirectory);VerifyTrustedObject(AppPaths.InstallDirectory,true);
            foreach(string name in PackageFiles)
            {
                string path=Path.Combine(AppPaths.InstallDirectory,name);
                if(File.Exists(path)||Directory.Exists(path))VerifyTrustedObject(path,false);
            }
            ServiceInstaller.WritePackage(package);
        }
        public void WriteReceipt(InstallReceipt receipt) {VerifyStateDirectory();WriteExactReceipt(receipt);}
        public Task StartAsync()=>ConfigureAndStartAsync();
        public async Task ConfirmAsync(Version version)
        {
            // A UAC helper started with another administrator's credentials
            // cannot connect to the owner's pipe. The calling worker confirms it.
            if(IsPipeOwner(ownerSid) && !await ServiceClient.ConfirmRollbackVersionAsync(version))
                throw new IOException("Не подтверждены версия службы "+version+" и выключенное управление/восстановление DNS.");
        }
    }
}
