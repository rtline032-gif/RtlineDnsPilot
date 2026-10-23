namespace NetPilot.Tests;

public static class ManualSelectionTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        void Check(string name,Action test) {try {test();report(name,true,"");} catch(Exception ex) {report(name,false,ex.Message);}}
        async Task AsyncCheck(string name,Func<string,Task> test)
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","manual-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try {await test(dir);report(name,true,"");} catch(Exception ex) {report(name,false,ex.ToString());}
        }
        void Require(bool condition,string message) {if(!condition) throw new Exception(message);}
        var now=DateTimeOffset.UtcNow;
        var config=AppConfig.Defaults();
        var a=config.Profiles[0].Id;var b=config.Profiles[1].Id;
        ProfileStats Stats(string id,bool success,int count)
        {
            var stats=new ProfileStats {ProfileId=id};
            for(int i=0;i<count;i++) stats.Add(new ProbeResult {ProfileId=id,At=now,Success=success,DnsMs=10,HttpsMs=20});
            return stats;
        }
        var samples=new[] {Stats(a,false,3),Stats(b,true,5)};
        Check("permanent manual selection stays on a failed channel despite a healthy alternative",()=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;
            var decision=Selection.Choose(selected,samples,a,now.AddMinutes(-10),now);
            Require(decision.ProfileId==a&&!decision.AllUnavailable,"Manual channel silently failed over or marked the healthy alternative down.");
        });
        Check("timed manual selection holds before its deadline",()=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;selected.ManualUntil=now.AddMinutes(5);
            Require(Selection.Choose(selected,samples,a,now.AddMinutes(-10),now).ProfileId==a,"Timed fixation was ignored.");
        });
        Check("timed manual selection returns to automatic failover at its deadline",()=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;selected.ManualUntil=now;
            Require(Selection.Choose(selected,samples,a,now.AddMinutes(-10),now).ProfileId==b,"An expired fixation trapped the failed channel.");
        });
        Check("manual fixation overrides the soft preferred channel",()=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;selected.PinnedProfileId=b;
            Require(Selection.Choose(selected,samples,b,now.AddMinutes(-10),now).ProfileId==a,"Soft preference overrode explicit fixation.");
        });
        Check("manual fixation rejects a disabled channel and a deadline without a channel",()=>
        {
            var selected=JsonData.Clone(config);selected.Profiles[0].Enabled=false;selected.ManualProfileId=a;
            bool disabled=false;try {selected.Validate();} catch(ArgumentException) {disabled=true;}
            selected=JsonData.Clone(config);selected.ManualUntil=now.AddMinutes(1);
            bool noChannel=false;try {selected.Validate();} catch(ArgumentException) {noChannel=true;}
            Require(disabled&&noChannel,"Invalid manual references accepted.");
        });
        await AsyncCheck("manual configure selects immediately and persists the UTC deadline",async dir=>
        {
            await using var engine=new MonitorEngine(config,dir);
            var selected=JsonData.Clone(config);selected.ManualProfileId=b;selected.ManualUntil=now.AddMinutes(15);
            await engine.ConfigureAsync(selected);
            var state=engine.GetStatus();var saved=AtomicStore.Read<AppConfig>(Path.Combine(dir,"config.json"))!;
            Require(state.ActiveProfileId==b&&saved.ManualProfileId==b&&saved.ManualUntil==selected.ManualUntil,"Manual choice or deadline was lost.");
            Require(!state.SystemDnsManaged&&!File.Exists(Path.Combine(dir,"dns-session.json")),"Manual monitoring choice mutated adapter DNS.");
        });
        await AsyncCheck("returning to automatic mode clears both fixation and preference",async dir=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;selected.ManualUntil=now.AddMinutes(15);selected.PinnedProfileId=b;
            await using var engine=new MonitorEngine(selected,dir);
            await engine.SetPreferredAsync(null);
            var state=engine.GetStatus();
            Require(state.Config.ManualProfileId is null&&state.Config.ManualUntil is null&&state.Config.PinnedProfileId is null,"Automatic mode retained a manual override.");
        });
        await AsyncCheck("changing monitored services preserves history without reusing its availability",async dir=>
        {
            var original=JsonData.Clone(config);foreach(var p in original.Profiles)p.Enabled=false;
            AtomicStore.Write(Path.Combine(dir,"statistics.json"),new List<ProfileStats> {Stats(a,true,3)});
            await using var engine=new MonitorEngine(original,dir);
            var changed=JsonData.Clone(original);changed.MonitorGemini=true;
            await engine.ConfigureAsync(changed);
            var raw=engine.GetStatus().Statistics;
            Require(raw.Count==1&&raw[0].Samples.Count==3,"Historical ChatGPT measurements were deleted.");
            Require(MonitoringTargets.View(raw[0],changed)!.Samples.Count==0,"ChatGPT-only evidence was reused for Gemini.");
        });
        await AsyncCheck("new features are saved locally without configuring an older service",async dir=>
        {
            var original=JsonData.Clone(config);foreach(var p in original.Profiles)p.Enabled=false;
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),original);
            var service=new FakeConnection(original,"0.1.2");
            await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();
            var changed=JsonData.Clone(backend.Status.Config);changed.MonitorGemini=true;
            bool rejected=false;try {await backend.ConfigureAsync(changed);} catch(InvalidOperationException) {rejected=true;}
            var saved=AtomicStore.Read<AppConfig>(Path.Combine(dir,"ui-config.json"))!;
            Require(rejected&&saved.MonitorGemini&&service.ConfigureCalls==0,"Unsupported configuration was silently sent to an old service.");
        });
        await AsyncCheck("manual UI commands survive acknowledgement and automatic mode removes the deadline",async dir=>
        {
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new FakeConnection(config,AppVersion.Current);
            await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();
            await backend.FixChannelAsync(b,5);
            Require(backend.Status.Config.ManualProfileId==b&&backend.Status.Config.ManualUntil>now,"Manual UI command was not committed.");
            await backend.SelectAsync(null);
            Require(backend.Status.Config.ManualProfileId is null&&backend.Status.Config.ManualUntil is null&&service.ConfigureCalls==2,"Automatic UI command did not clear manual state.");
        });
        Check("local service reconciliation remaps a manually fixed equivalent channel",()=>
        {
            var local=JsonData.Clone(config);local.ManualProfileId=a;local.ManualUntil=now.AddMinutes(5);
            var service=AppConfig.Defaults();
            var merged=ProfileSynchronization.ForConnection(local,service,true);
            Require(merged.ManualProfileId==service.Profiles[0].Id&&merged.ManualUntil==local.ManualUntil,"Service handoff left a stale manual channel ID.");
        });
        Check("a pending target change cannot hide an edited active DNS endpoint",()=>
        {
            var desired=JsonData.Clone(config);desired.MonitorGemini=true;
            desired.Profiles[0].Servers=["192.0.2.53"];
            var state=new AppStatus {Config=config,ActiveProfileId=a,Statistics=[Stats(a,true,2)]};
            var draft=ProfileSynchronization.WithDraft(state,desired);
            Require(draft.ActiveProfileId is null&&draft.Statistics.Count==0,"Pending settings presented an edited endpoint as the service's applied channel.");
        });
    }
    private sealed class FakeConnection(AppConfig config,string version) : IUiServiceConnection
    {
        private AppConfig _config=JsonData.Clone(config);
        public int ConfigureCalls {get;private set;}
        public bool IsInstalled=>true;
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            if(request.Command=="configure") {ConfigureCalls++;_config=JsonData.Clone(request.Config!);}
            else if(request.Command!="status") throw new Exception("Unexpected command: "+request.Command);
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(_config),ServiceVersion=version,ServiceConnected=true}});
        }
        public Task InstallAsync()=>throw new Exception("No installation authorized in tests.");
        public Task RecoverAsync()=>throw new Exception("No recovery authorized in tests.");
    }
}
