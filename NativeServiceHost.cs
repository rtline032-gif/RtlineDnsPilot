using System.Runtime.InteropServices;

namespace NetPilot;

internal static class NativeServiceHost
{
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate void ServiceMainDelegate(int count,IntPtr args);
    [UnmanagedFunctionPointer(CallingConvention.Winapi)] private delegate int ServiceHandler(int control,int eventType,IntPtr eventData,IntPtr context);
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] private struct ServiceTable { [MarshalAs(UnmanagedType.LPWStr)] public string? Name; public ServiceMainDelegate? Main; }
    [StructLayout(LayoutKind.Sequential)] private struct ServiceStatus { public int Type,State,Accepted,Win32Exit,ServiceExit,Checkpoint,WaitHint; }
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool StartServiceCtrlDispatcherW([In] ServiceTable[] table);
    [DllImport("advapi32.dll",CharSet=CharSet.Unicode,SetLastError=true)] private static extern IntPtr RegisterServiceCtrlHandlerExW(string name,ServiceHandler handler,IntPtr context);
    [DllImport("advapi32.dll",SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)] private static extern bool SetServiceStatus(IntPtr handle,ref ServiceStatus status);
    private static readonly ServiceMainDelegate MainDelegate=ServiceMain;
    private static readonly ServiceHandler Handler=Handle;
    private static readonly CancellationTokenSource Stop=new();
    private static IntPtr _handle;
    private static ServiceStatus _status;
    private static readonly object StatusGate=new();

    public static int Run()
    {
        if(!StartServiceCtrlDispatcherW([new ServiceTable {Name=AppPaths.ServiceName,Main=MainDelegate},new ServiceTable()]))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        return 0;
    }
    private static void ServiceMain(int count,IntPtr args)
    {
        _handle=RegisterServiceCtrlHandlerExW(AppPaths.ServiceName,Handler,IntPtr.Zero);
        if(_handle==IntPtr.Zero) return;
        SetState(2,0,120000);
        try { RunWorkerAsync().GetAwaiter().GetResult(); SetState(1,0,0); }
        catch(Exception ex) { Log(ex.ToString()); SetState(1,1066,0,1); }
    }
    private static async Task RunWorkerAsync()
    {
        ServiceInstaller.VerifyStateDirectory();
        var recovery=new DnsSessionManager(AppPaths.ServiceData);
        if(recovery.HasJournal) await recovery.RestoreAsync();
        var receipt=AtomicStore.Read<InstallReceipt>(Path.Combine(AppPaths.ServiceData,"install.json"))??throw new InvalidDataException("Нет записи о владельце службы.");
        var config=AtomicStore.Read<AppConfig>(Path.Combine(AppPaths.ServiceData,"config.json"))??throw new InvalidDataException("Нет конфигурации службы.");
        config.Validate();
        await using var engine=new MonitorEngine(config,AppPaths.ServiceData,true);
        await engine.StartAsync();
        using var linked=CancellationTokenSource.CreateLinkedTokenSource(Stop.Token);
        var rpc=new ServiceRpc(engine,receipt.OwnerSid);
        var task=rpc.RunAsync(linked.Token);
        SetState(4,0,0);
        try { await task; }
        finally
        {
            linked.Cancel(); SetState(3,0,120000);
            await engine.StopAsync();
        }
    }
    private static int Handle(int control,int eventType,IntPtr data,IntPtr context)
    {
        if(control is 1 or 5) { SetState(3,0,120000); Stop.Cancel(); }
        else if(control==4) { lock(StatusGate) SetServiceStatus(_handle,ref _status); }
        return 0;
    }
    private static void SetState(int state,int win32,int wait,int serviceExit=0)
    {
        lock(StatusGate)
        {
            _status=new ServiceStatus {Type=0x10,State=state,Accepted=state==4?1|4:0,Win32Exit=win32,ServiceExit=serviceExit,WaitHint=wait,Checkpoint=state is 2 or 3?1:0};
            if(_handle!=IntPtr.Zero) SetServiceStatus(_handle,ref _status);
        }
    }
    public static void Log(string message)
    {
        try
        {
            if(!Directory.Exists(AppPaths.ServiceData)) return;
            ServiceInstaller.VerifyStateDirectory();
            var path=Path.Combine(AppPaths.ServiceData,"service.log");
            if(File.Exists(path)&&new FileInfo(path).Length>1024*1024) File.Move(path,path+".previous",true);
            File.AppendAllText(path,DateTimeOffset.Now.ToString("O")+" "+message+Environment.NewLine);
        }
        catch(Exception ex) when(ex is IOException or UnauthorizedAccessException) { }
    }
}
