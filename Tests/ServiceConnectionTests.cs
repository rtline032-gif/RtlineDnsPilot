namespace NetPilot.Tests;

public static class ServiceConnectionTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","connection-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {await test(dir);report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}
        }
        void Require(bool value,string message){if(!value)throw new Exception(message);}
        AppConfig Config(){var c=AppConfig.Defaults();foreach(var p in c.Profiles)p.Enabled=false;return c;}
        await Check("an unreachable installed service never starts a duplicate automatic observer",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config){Offline=true};
            await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();
            Require(backend.DnsStateUnknown&&!backend.Status.Monitoring,"A second automatic monitor was started while the service might still be running.");
            Require(!backend.Status.SystemDnsManaged&&backend.Status.ActiveProfileId is null,"An unconfirmed local choice was shown as applied DNS.");
        });
        await Check("failed status attempts use bounded backoff instead of retrying every UI refresh",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config){Offline=true};
            var now=DateTimeOffset.UtcNow;
            await using var backend=new UiBackend(dir,service,clock:()=>now);
            await backend.RefreshAsync();await backend.RefreshAsync();await backend.RefreshAsync();
            Require(service.StatusCalls==1&&backend.NextServiceRetry==now.AddSeconds(2),"Background polling ignored the first retry delay.");
            now=now.AddSeconds(2);await backend.RefreshAsync();
            Require(service.StatusCalls==2&&backend.NextServiceRetry==now.AddSeconds(5),"Retry delay did not grow after another failure.");
            now=now.AddSeconds(5);await backend.RefreshAsync();
            Require(service.StatusCalls==3&&backend.NextServiceRetry==now.AddSeconds(10),"Repeated status failures did not back off.");
        });
        await Check("successful service attachment records its contact time",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config);
            var now=DateTimeOffset.UtcNow;
            await using var backend=new UiBackend(dir,service,clock:()=>now);
            await backend.RefreshAsync();
            Require(backend.ServiceConnected&&backend.LastServiceContact==now&&backend.NextServiceRetry is null,"Contact state was not confirmed by a successful reply.");
        });
        await Check("explicit reconnect bypasses backoff and never replays a mutation",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config){Offline=true};
            var now=DateTimeOffset.UtcNow;
            await using var backend=new UiBackend(dir,service,clock:()=>now);
            await backend.RefreshAsync();service.Offline=false;
            await backend.ReconnectAsync();
            Require(service.StatusCalls==2&&backend.ServiceConnected&&!backend.DnsStateUnknown&&backend.NextServiceRetry is null,"Explicit reconnect waited for a timer or failed to confirm recovery.");
        });
        await Check("connection loss preserves history and clears the unconfirmed active channel",async dir=>
        {
            var config=Config();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config);
            var now=DateTimeOffset.UtcNow;
            await using var backend=new UiBackend(dir,service,clock:()=>now);
            await backend.RefreshAsync();service.Offline=true;now=now.AddSeconds(1);
            await backend.RefreshAsync();
            Require(!backend.ServiceConnected&&backend.DnsStateUnknown&&!backend.Status.Monitoring&&backend.Status.ActiveProfileId is null,"A disconnected service was presented as current.");
            Require(backend.LastServiceContact==now.AddSeconds(-1)&&backend.Status.Statistics.Count==1,"Last confirmed contact or service history was lost.");
        });
    }
    private sealed class Connection(AppConfig config):IUiServiceConnection
    {
        public bool IsInstalled=>true;
        public bool Offline {get;set;}
        public int StatusCalls {get;private set;}
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            if(request.Command!="status")throw new Exception("A status refresh attempted a mutation.");
            StatusCalls++;if(Offline)throw new TimeoutException("simulated timeout");
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(config),ServiceVersion=AppVersion.Current,
                ServiceConnected=true,Monitoring=true,SystemDnsManaged=true,ActiveProfileId=config.Profiles[0].Id,
                Statistics=[new ProfileStats {ProfileId=config.Profiles[0].Id,Samples=[new ProbeResult {ProfileId=config.Profiles[0].Id,Success=true}]}]}});
        }
        public Task InstallAsync()=>throw new Exception("No installation in status tests.");
        public Task RecoverAsync()=>throw new Exception("No recovery in status tests.");
    }
}
