using System.Text.Json.Nodes;

namespace NetPilot.Tests;

public static class MonitoringControlTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","control-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {await test(dir);report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}
        }
        void Require(bool value,string message) {if(!value)throw new Exception(message);}
        AppConfig NoNetwork() {var c=AppConfig.Defaults();foreach(var p in c.Profiles)p.Enabled=false;return c;}
        await Check("pause and resume reach the connected service without disabling DNS management",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config);
            await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();
            await backend.SetMonitoringAsync(false);
            Require(!backend.Status.Monitoring&&backend.Status.SystemDnsManaged&&service.Pauses==1,"Pause did not stop service checks independently of DNS management.");
            await backend.SetMonitoringAsync(true);
            Require(backend.Status.Monitoring&&backend.Status.SystemDnsManaged&&service.Resumes==1,"Resume did not restart service checks.");
        });
        await Check("local pause is persisted and survives reopening",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {Installed=false};
            await using(var backend=new UiBackend(dir,service))
            {
                await backend.RefreshAsync();await backend.SetMonitoringAsync(false);
                var saved=JsonNode.Parse(File.ReadAllText(Path.Combine(dir,"ui-config.json")))!;
                Require(saved["MonitoringPaused"]?.GetValue<bool>()==true,"Pause was not saved.");
            }
            await using var reopened=new UiBackend(dir,service);
            await reopened.RefreshAsync();
            Require(!reopened.Status.Monitoring,"Reopening silently restarted paused checks.");
        });
        await Check("engine pause persists without restoring or creating an adapter transaction",async dir=>
        {
            var config=NoNetwork();
            await using var engine=new MonitorEngine(config,dir);
            await engine.StartAsync();
            await engine.SetMonitoringAsync(false);
            var saved=AtomicStore.Read<AppConfig>(Path.Combine(dir,"config.json"))!;
            Require(!engine.GetStatus().Monitoring&&saved.MonitoringPaused,"Engine did not enter a persistent pause.");
            Require(!File.Exists(Path.Combine(dir,"dns-session.json")),"Observer pause created a DNS transaction.");
            await engine.RequestProbeAsync();
            Require(!engine.GetStatus().Monitoring,"A one-off check silently resumed automatic checks.");
            await engine.SetMonitoringAsync(true);
            Require(engine.GetStatus().Monitoring&&!engine.GetStatus().Config.MonitoringPaused,"Engine did not resume.");
        });
        await Check("a paused engine stays paused after a restart",async dir=>
        {
            var config=NoNetwork();config.MonitoringPaused=true;
            await using var engine=new MonitorEngine(config,dir);
            await engine.StartAsync();
            Require(!engine.GetStatus().Monitoring,"Service startup ignored the saved pause.");
        });
        await Check("service-owned pause does not keep acknowledged profile edits pending",dir=>
        {
            var desired=NoNetwork();var applied=JsonData.Clone(desired);applied.MonitoringPaused=true;
            Require(ProfileSynchronization.SameConfig(desired,applied),"Service pause made an otherwise acknowledged configuration look unapplied.");
            var draft=ProfileSynchronization.WithDraft(new AppStatus {Config=applied},desired);
            Require(draft.Config.MonitoringPaused,"Draft settings hid the service's authoritative pause.");
            return Task.CompletedTask;
        });
    }
    private sealed class Connection(AppConfig config):IUiServiceConnection
    {
        public bool Installed {get;set;}=true;
        public bool IsInstalled=>Installed;
        public int Pauses {get;private set;}
        public int Resumes {get;private set;}
        private bool _monitoring=true;
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            if(request.Command=="pause"){Pauses++;_monitoring=false;}
            else if(request.Command=="resume"){Resumes++;_monitoring=true;}
            else if(request.Command!="status")throw new Exception("Unexpected mutation: "+request.Command);
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(config),ServiceVersion=AppVersion.Current,ServiceConnected=true,SystemDnsManaged=true,Monitoring=_monitoring}});
        }
        public Task InstallAsync()=>throw new Exception("No service installation in tests.");
        public Task RecoverAsync()=>throw new Exception("No DNS recovery in tests.");
    }
}
