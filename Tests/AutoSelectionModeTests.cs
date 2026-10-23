using System.Text.Json.Nodes;

namespace NetPilot.Tests;

public static class AutoSelectionModeTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        void Check(string name,Action test)
        {try{test();report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}}
        async Task CheckAsync(string name,Func<string,Task> test)
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","auto-mode-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);
            try{await test(dir);report(name,true,"");}catch(Exception ex){report(name,false,ex.ToString());}
        }
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        static void Reject(Action action)
        {try{action();}catch(ArgumentException){return;}throw new Exception("An undefined automatic mode was accepted.");}
        var now=DateTimeOffset.UtcNow.AddMilliseconds(-10);var config=AppConfig.Defaults();var key=MonitoringTargets.Key(config);
        string a=config.Profiles[0].Id,b=config.Profiles[1].Id,c=config.Profiles[2].Id;
        ProfileStats Stats(string id,bool[] successes,double ms=100)
        {
            var value=new ProfileStats {ProfileId=id};
            for(int i=0;i<successes.Length;i++)value.Add(new ProbeResult {ProfileId=id,MonitoringKey=key,
                At=now.AddSeconds(-(successes.Length-1-i)*20),Success=successes[i],DnsMs=ms/4,HttpsMs=ms*3/4});
            return value;
        }
        bool[] Successes(int count)=>Enumerable.Repeat(true,count).ToArray();
        ProfileStats Flaky(string id,double ms=100)=>Stats(id,[false,false,true,true,true,true,true,true,true,true],ms);
        SelectionDecision Choose(AutoSelectionMode mode,ProfileStats[] statistics,string? current=null,int dwellSeconds=600,AppConfig? selected=null)
        {
            var desired=JsonData.Clone(selected??config);desired.AutoSelection=mode;
            return Selection.Choose(desired,statistics,current,now.AddSeconds(-dwellSeconds),now);
        }
        static AppConfig NoNetwork(AppConfig source)
        {var copy=JsonData.Clone(source);foreach(var profile in copy.Profiles)profile.Enabled=false;return copy;}

        Check("legacy settings default to stable auto selection without changing measurement context",()=>
        {
            var node=JsonNode.Parse(JsonData.Encode(config))!.AsObject();node.Remove("AutoSelection");
            var restored=JsonData.Decode<AppConfig>(node.ToJsonString());restored.Validate();
            Require(restored.AutoSelection==AutoSelectionMode.Stable&&MonitoringTargets.Key(restored)==key,
                "Existing users changed priority or measurement context during migration.");
        });
        Check("Fast auto selection survives JSON round trips and undefined enum values are rejected",()=>
        {
            var selected=JsonData.Clone(config);selected.AutoSelection=AutoSelectionMode.Fast;
            Require(JsonData.Clone(selected).AutoSelection==AutoSelectionMode.Fast,"The chosen mode was not serialized.");
            foreach(int undefined in new[] {-1,2,int.MaxValue}){selected.AutoSelection=(AutoSelectionMode)undefined;Reject(selected.Validate);}
        });
        Check("initial Stable and Fast choices use their different primary ranking",()=>
        {
            var statistics=new[] {Stats(a,Successes(8),150),Flaky(b,50)};
            Require(Choose(AutoSelectionMode.Stable,statistics).ProfileId==a,"Stable did not prioritize observed success share.");
            Require(Choose(AutoSelectionMode.Fast,statistics).ProfileId==b,"Fast did not prioritize measured response time.");
        });
        Check("Fast uses observed stability to break equal latency ties",()=>
        {
            Require(Choose(AutoSelectionMode.Fast,[Flaky(a,50),Stats(b,Successes(5),50)]).ProfileId==b,
                "Equal-speed candidates were not ordered by observed success share.");
        });
        Check("emergency failover follows the selected primary ranking while bypassing dwell",()=>
        {
            var statistics=new[] {Stats(a,[true,false,false,false]),Flaky(b,25),Stats(c,Successes(2),150)};
            Require(Choose(AutoSelectionMode.Fast,statistics,a,1).ProfileId==b,"Fast emergency ignored its fastest confirmed reserve.");
            Require(Choose(AutoSelectionMode.Stable,statistics,a,1).ProfileId==c,"Stable emergency ignored its most reliable confirmed reserve.");
        });
        Check("Fast permits a confirmed faster candidate with lower historical success share",()=>
        {
            var statistics=new[] {Stats(a,Successes(8),100),Flaky(b,75)};
            Require(Choose(AutoSelectionMode.Fast,statistics,a).ProfileId==b,"A no-worse-success restriction silently turned Fast into Stable.");
            Require(Choose(AutoSelectionMode.Stable,statistics,a).ProfileId==a,"Stable inherited Fast's lower-reliability optimization.");
        });
        Check("Fast does not move to a slower candidate solely for better stability",()=>
        {
            var statistics=new[] {Flaky(a,100),Stats(b,Successes(5),120)};
            Require(Choose(AutoSelectionMode.Fast,statistics,a).ProfileId==a,"Fast optimized a metric that the user did not select.");
            Require(Choose(AutoSelectionMode.Stable,statistics,a).ProfileId==b,"Stable lost its stability-first behavior.");
        });
        Check("Fast optimization keeps the twenty percent gain five rounds and five minute protections",()=>
        {
            var active=Stats(a,Successes(8));
            Require(Choose(AutoSelectionMode.Fast,[active,Stats(b,Successes(5),81)],a).ProfileId==a,"A gain below twenty percent switched channels.");
            Require(Choose(AutoSelectionMode.Fast,[active,Stats(b,Successes(4),50)],a).ProfileId==a,"Four successes passed five-round confirmation.");
            var candidate=Stats(b,Successes(5),80);
            Require(Choose(AutoSelectionMode.Fast,[active,candidate],a,299).ProfileId==a,"Selecting Fast bypassed dwell.");
            Require(Choose(AutoSelectionMode.Fast,[active,candidate],a,300).ProfileId==b,"The exact thresholds did not permit a confirmed speed improvement.");
        });
        Check("Fast healthy optimization ranks speed before success share among eligible reserves",()=>
        {
            Require(Choose(AutoSelectionMode.Fast,[Stats(a,Successes(8)),Flaky(b,50),Stats(c,Successes(5),75)],a).ProfileId==b,
                "Fast kept stability as the primary candidate order.");
        });
        Check("Fast rejects stale future discontinuous and other-context reserve evidence",()=>
        {
            foreach(string kind in new[] {"stale","future","gap","context"})
            {
                var candidate=Stats(b,Successes(5),50);
                if(kind=="stale")foreach(var sample in candidate.Samples)sample.At=sample.At.AddHours(-1);
                if(kind=="future")candidate.Samples[1].At=now.AddSeconds(1);
                if(kind=="gap")foreach(var sample in candidate.Samples.Take(4))sample.At=sample.At.AddHours(-1);
                if(kind=="context")foreach(var sample in candidate.Samples)sample.MonitoringKey="another-context";
                Require(Choose(AutoSelectionMode.Fast,[Stats(a,Successes(8)),candidate],a).ProfileId==a,"Fast accepted "+kind+" evidence.");
            }
        });
        Check("Fast retains a channel for two failures and requires two confirmed emergency successes",()=>
        {
            var reserve=Stats(b,Successes(2),50);
            for(int failures=1;failures<=3;failures++)
            {
                var active=Stats(a,Successes(5).Concat(Enumerable.Repeat(false,failures)).ToArray());
                Require(Choose(AutoSelectionMode.Fast,[active,reserve],a,1).ProfileId==(failures<3?a:b),"Fast altered the three-failure emergency threshold.");
            }
            Require(Choose(AutoSelectionMode.Fast,[Stats(a,[false,false,false]),Stats(b,Successes(1),1)],a,1).ProfileId==a,
                "One lucky reserve response caused an emergency switch.");
        });
        Check("Fast requires every selected monitored object to succeed",()=>
        {
            var desired=JsonData.Clone(config);desired.MonitorGemini=true;var active=Stats(a,Successes(8));
            foreach(var sample in active.Samples)sample.MonitoringKey=MonitoringTargets.Key(desired);
            var incomplete=new ProfileStats {ProfileId=b};
            for(int i=0;i<5;i++)incomplete.Add(MonitoringTargets.Combine(b,desired,[new ServiceProbeResult
                {ServiceId=MonitoringTargets.ChatGpt,At=now.AddSeconds(-(4-i)*20),Success=true,DnsMs=1,HttpsMs=1}]));
            Require(Choose(AutoSelectionMode.Fast,[active,incomplete],a,selected:desired).ProfileId==a,"Fast selected a quick but incomplete aggregate.");
        });
        Check("manual fixation and explicit preference override both automatic priorities",()=>
        {
            foreach(var mode in new[] {AutoSelectionMode.Stable,AutoSelectionMode.Fast})
            {
                var selected=JsonData.Clone(config);selected.PinnedProfileId=a;
                Require(Choose(mode,[Flaky(a),Stats(b,Successes(5),25)],a,selected:selected).ProfileId==a,"An automatic mode ignored explicit preference.");
                selected.ManualProfileId=a;selected.ManualUntil=now.AddMinutes(1);
                Require(Choose(mode,[Stats(a,[false,false,false]),Stats(b,Successes(5),25)],a,selected:selected).ProfileId==a,
                    "An automatic mode ignored strict manual fixation.");
            }
        });
        Check("changing automatic priority preserves monitoring and chart contexts and displayed samples",()=>
        {
            var fast=JsonData.Clone(config);fast.AutoSelection=AutoSelectionMode.Fast;
            var statistics=Stats(a,Successes(5));var before=JsonData.Encode(statistics);
            var draft=ProfileSynchronization.WithDraft(new AppStatus {Config=config,Statistics=[statistics]},fast);
            Require(MonitoringTargets.Key(fast)==key&&ChartHistoryArchive.ContextKey(fast.Profiles[0],key)==ChartHistoryArchive.ContextKey(config.Profiles[0],key)
                &&draft.Statistics.Count==1&&JsonData.Encode(statistics)==before&&!ProfileSynchronization.SameConfig(config,fast),
                "Priority selection reset measurement context/history or failed to mark a real config change.");
        });
        await CheckAsync("engine priority configuration persists without clearing raw or aggregated history",async dir=>
        {
            var selected=NoNetwork(config);var measured=Stats(a,Successes(5));
            AtomicStore.Write(Path.Combine(dir,"statistics.json"),new[] {measured});
            await using var engine=new MonitorEngine(selected,dir);
            var rawBefore=JsonData.Encode(engine.GetStatus().Statistics);var countBefore=engine.GetHistory(new HistoryQuery()).Channels.Sum(s=>s.TotalChecks);
            selected.AutoSelection=AutoSelectionMode.Fast;await engine.ConfigureAsync(selected);
            Require(JsonData.Encode(engine.GetStatus().Statistics)==rawBefore&&engine.GetHistory(new HistoryQuery()).Channels.Sum(s=>s.TotalChecks)==countBefore
                &&AtomicStore.Read<AppConfig>(Path.Combine(dir,"config.json"))!.AutoSelection==AutoSelectionMode.Fast,
                "Applying a priority reset history/counters or did not persist the mode.");
            Require(!File.Exists(Path.Combine(dir,"dns-session.json")),"Changing an observer's priority created a DNS transaction.");
        });
        await CheckAsync("SelectAutomatic selects its priority clears overrides and sends only configuration",async dir=>
        {
            var selected=JsonData.Clone(config);selected.ManualProfileId=a;selected.ManualUntil=DateTimeOffset.UtcNow.AddMinutes(5);selected.PinnedProfileId=b;
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),selected);var service=new Connection(selected);
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();await backend.SelectAutomaticAsync(AutoSelectionMode.Fast);
            Require(backend.Status.Config.AutoSelection==AutoSelectionMode.Fast&&backend.Status.Config.ManualProfileId is null
                &&backend.Status.Config.ManualUntil is null&&backend.Status.Config.PinnedProfileId is null&&service.Commands.SequenceEqual(new[] {"status","configure"}),
                "Choosing automatic priority did not clear overrides or sent an unrelated command.");
        });
        await CheckAsync("returning to auto keeps the last chosen priority",async dir=>
        {
            var selected=JsonData.Clone(config);selected.AutoSelection=AutoSelectionMode.Fast;selected.ManualProfileId=a;selected.PinnedProfileId=b;
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),selected);var service=new Connection(selected);
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();await backend.SelectAsync(null);
            Require(backend.Status.Config.AutoSelection==AutoSelectionMode.Fast&&backend.Status.Config.ManualProfileId is null&&backend.Status.Config.PinnedProfileId is null,
                "Return to auto reset the previously selected priority.");
        });
        await CheckAsync("services older than 016 cannot silently apply Fast and preserve the pending choice",async dir=>
        {
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);var service=new Connection(config) {Version="0.1.5"};
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();bool blocked=false;
            try{await backend.SelectAutomaticAsync(AutoSelectionMode.Fast);}catch(InvalidOperationException ex){blocked=ex.Message.Contains("Обновить службу");}
            Require(blocked&&backend.PendingServiceConfiguration&&AtomicStore.Read<AppConfig>(Path.Combine(dir,"ui-config.json"))!.AutoSelection==AutoSelectionMode.Fast
                &&service.Config.AutoSelection==AutoSelectionMode.Stable&&service.Commands.SequenceEqual(new[] {"status"}),
                "An old service silently ignored Fast or the saved pending priority was lost.");
        });
        await CheckAsync("a lost configure reply retains pending priority until a read-only acknowledgement",async dir=>
        {
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);var service=new Connection(config) {LoseConfigureReply=true};
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();bool lost=false;
            try{await backend.SelectAutomaticAsync(AutoSelectionMode.Fast);}catch(IOException){lost=true;}
            Require(lost&&backend.PendingServiceConfiguration&&backend.Status.Config.AutoSelection==AutoSelectionMode.Fast,"A lost reply discarded the pending priority.");
            await backend.RefreshAsync();
            Require(!backend.PendingServiceConfiguration&&backend.Status.Config.AutoSelection==AutoSelectionMode.Fast
                &&service.Commands.SequenceEqual(new[] {"status","configure","status"}),"Refresh replayed a mutation or failed to acknowledge the saved priority.");
        });
        await CheckAsync("undefined and unknown-source priority changes are rejected without sending controls",async dir=>
        {
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);var service=new Connection(config) {Offline=true};
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();Reject(()=>backend.SelectAutomaticAsync((AutoSelectionMode)99));
            bool blocked=false;try{await backend.SelectAutomaticAsync(AutoSelectionMode.Fast);}catch(InvalidOperationException){blocked=true;}
            Require(blocked&&AtomicStore.Read<AppConfig>(Path.Combine(dir,"ui-config.json"))!.AutoSelection==AutoSelectionMode.Stable
                &&service.Commands.SequenceEqual(new[] {"status"}),"An unknown service was given a locally acknowledged mode or an invalid mode changed config.");
        });
        await CheckAsync("a local priority survives reopening without enabling system management",async dir=>
        {
            var selected=NoNetwork(config);AtomicStore.Write(Path.Combine(dir,"ui-config.json"),selected);var service=new Connection(selected) {Installed=false};
            await using(var backend=new UiBackend(dir,service))
            {await backend.RefreshAsync();await backend.SelectAutomaticAsync(AutoSelectionMode.Fast);}
            await using var reopened=new UiBackend(dir,service);await reopened.RefreshAsync();
            Require(reopened.Status.Config.AutoSelection==AutoSelectionMode.Fast&&!reopened.Status.SystemDnsManaged&&service.Commands.Count==0,
                "Local priority was lost or unexpectedly contacted system management.");
        });
    }

    private sealed class Connection(AppConfig config):IUiServiceConnection
    {
        public AppConfig Config {get;private set;}=JsonData.Clone(config);
        public bool Installed {get;set;}=true;
        public bool IsInstalled=>Installed;
        public bool Offline {get;set;}
        public bool LoseConfigureReply {get;set;}
        public string Version {get;set;}="0.1.6";
        public List<string> Commands {get;}=[];
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            Commands.Add(request.Command);if(Offline)throw new TimeoutException("Simulated unavailable service.");
            if(request.Command=="configure")
            {
                Config=JsonData.Clone(request.Config!);if(LoseConfigureReply)throw new IOException("Simulated lost acknowledgement.");
            }
            else if(request.Command!="status")throw new Exception("Unexpected automatic-priority mutation: "+request.Command);
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(Config),ServiceVersion=Version,ServiceConnected=true}});
        }
        public Task InstallAsync()=>throw new Exception("No service installation in automatic-priority tests.");
        public Task RecoverAsync()=>throw new Exception("No DNS recovery in automatic-priority tests.");
    }
}
