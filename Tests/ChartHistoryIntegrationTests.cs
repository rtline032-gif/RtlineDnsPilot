using System.IO.Pipes;
using System.Security.Principal;
using System.Text.Json;

namespace NetPilot.Tests;

public static class ChartHistoryIntegrationTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            var directory=Path.Combine(AppContext.BaseDirectory,"test-data","chart-integration-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {await test(directory);report(name,true,"");}catch(Exception ex){report(name,false,ex.ToString());}
        }
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        static AppConfig NoNetwork()
        {var config=AppConfig.Defaults();foreach(var profile in config.Profiles)profile.Enabled=false;return config;}
        static List<ProfileStats> Statistics(AppConfig config,int count)
        {
            var stats=new ProfileStats {ProfileId=config.Profiles[0].Id};
            var now=DateTimeOffset.UtcNow.AddSeconds(-5);
            for(int i=0;i<count;i++)stats.Add(new ProbeResult {ProfileId=stats.ProfileId,MonitoringKey=MonitoringTargets.Key(config),
                At=now.AddMilliseconds(-i),Success=true,DnsMs=5,HttpsMs=20});
            return [stats];
        }
        static long Count(HistoryReport value)=>value.Channels.Sum(series=>series.TotalChecks);
        static async Task<string> Rejection(Func<Task> action)
        {try{await action();}catch(InvalidOperationException ex){return ex.Message;}throw new Exception("Expected a clear rejection.");}

        await Check("chart migration seeds existing measured samples only once across restarts",async dir=>
        {
            var config=NoNetwork();var query=new HistoryQuery();
            AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config,120));
            await using(var engine=new MonitorEngine(config,dir))
            {
                Require(Count(engine.GetHistory(query))==120,"Initial chart did not retain the 120 actual measurements.");
                var raw=engine.GetStatus().Statistics.Single();
                Require(raw.Samples.Count==120&&raw.TotalChecks==120,"Chart migration changed the selection window or counters.");
            }
            await using var restarted=new MonitorEngine(config,dir);
            Require(Count(restarted.GetHistory(query))==120,"Reopening duplicated the seed or lost the archive.");
            Require(AtomicStore.Read<ChartHistoryArchive>(Path.Combine(dir,"chart-history.json"))!.InitialSeedCompleted,
                "The seed completion was not persisted.");
        });
        await Check("an existing empty chart archive is not reseeded from raw statistics",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config,2));
            AtomicStore.Write(Path.Combine(dir,"chart-history.json"),new ChartHistoryArchive());
            await using var engine=new MonitorEngine(config,dir);
            Require(Count(engine.GetHistory(new HistoryQuery()))==0,"An existing archive was backfilled again.");
        });
        await Check("completed synthetic probes persist beyond the 120 sample selection window",async dir=>
        {
            var config=NoNetwork();config.Profiles[0].Enabled=true;int checks=0;
            var now=DateTimeOffset.UtcNow.AddSeconds(-5);
            Task<ProbeResult> Probe(ChannelProfile profile,string url,TimeSpan budget,CancellationToken token)
            {
                token.ThrowIfCancellationRequested();checks++;
                return Task.FromResult(new ProbeResult {ProfileId=profile.Id,At=now.AddMilliseconds(checks),Success=checks!=5,DnsMs=2,HttpsMs=8});
            }
            await using(var engine=new MonitorEngine(config,dir,false,Probe))
            {
                for(int i=0;i<125;i++)await engine.ProbeNowAsync();
                var raw=engine.GetStatus().Statistics.Single();var history=engine.GetHistory(new HistoryQuery());
                Require(checks==125&&Count(history)==125&&history.Channels[0].TotalSuccesses==124,"Completed probes were omitted or counted twice.");
                Require(raw.Samples.Count==120&&raw.TotalChecks==125,"Chart collection changed the selection window.");
                Require(!File.Exists(Path.Combine(dir,"dns-session.json")),"An observer created a system DNS transaction.");
            }
            await using var reopened=new MonitorEngine(config,dir,false,Probe);
            Require(Count(reopened.GetHistory(new HistoryQuery()))==125&&checks==125,"Restart lost history or triggered a new probe.");
        });
        await Check("history reads return detached reports and never trigger a probe",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config,1));
            await using var engine=new MonitorEngine(config,dir,false,(_,_,_,_)=>throw new Exception("History triggered a network probe."));
            var query=new HistoryQuery();var first=engine.GetHistory(query);
            query.PeriodMinutes=30;first.Query.BucketMinutes=10;first.Channels[0].Bars[0].Count=99;first.Channels.Clear();
            var second=engine.GetHistory(new HistoryQuery());
            Require(Count(second)==1&&second.Query.BucketMinutes==5,"A caller could mutate the stored archive or query.");
        });
        await Check("a corrupt optional chart archive is preserved while monitoring keeps working",async dir=>
        {
            var config=NoNetwork();config.Profiles[0].Enabled=true;
            var path=Path.Combine(dir,"chart-history.json");const string broken="{invalid chart";File.WriteAllText(path,broken);
            await using(var engine=new MonitorEngine(config,dir,false,(profile,_,_,_)=>Task.FromResult(
                new ProbeResult {ProfileId=profile.Id,At=DateTimeOffset.UtcNow.AddMilliseconds(-1),Success=true,DnsMs=3,HttpsMs=4})))
            {
                await engine.ProbeNowAsync();var history=engine.GetHistory(new HistoryQuery());
                Require(Count(history)==1&&!string.IsNullOrWhiteSpace(history.Warning),"New in-memory history or chart warning was lost.");
                Require(engine.GetStatus().Statistics.Single().TotalChecks==1&&engine.GetStatus().Error is null,
                    "The chart failure blocked normal monitoring or became a DNS error.");
            }
            Require(File.ReadAllText(path)==broken,"The corrupt archive was overwritten.");
        });
        await Check("an optional chart file missing its schema is preserved instead of reseeded",async dir=>
        {
            var config=NoNetwork();var path=Path.Combine(dir,"chart-history.json");File.WriteAllText(path,"{}");
            AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config,2));
            await using(var engine=new MonitorEngine(config,dir))
            {
                var history=engine.GetHistory(new HistoryQuery());
                Require(Count(history)==0&&!string.IsNullOrWhiteSpace(history.Warning),"A schema-less chart file was accepted or silently reseeded.");
            }
            Require(File.ReadAllText(path)=="{}","A schema-less archive was overwritten.");
        });
        await Check("chart write failure retains measurements in memory without breaking normal persistence",async dir=>
        {
            var config=NoNetwork();config.Profiles[0].Enabled=true;
            Directory.CreateDirectory(Path.Combine(dir,"chart-history.json"));
            await using var engine=new MonitorEngine(config,dir,false,(profile,_,_,_)=>Task.FromResult(
                new ProbeResult {ProfileId=profile.Id,At=DateTimeOffset.UtcNow.AddMilliseconds(-1),Success=true,DnsMs=1,HttpsMs=2}));
            await engine.ProbeNowAsync();var history=engine.GetHistory(new HistoryQuery());
            Require(Count(history)==1&&!string.IsNullOrWhiteSpace(history.Warning),"Failed disk writes discarded chart measurements or their warning.");
            Require(AtomicStore.Read<List<ProfileStats>>(Path.Combine(dir,"statistics.json"))!.Single().TotalChecks==1,
                "A chart write failure prevented ordinary measurements from being saved.");
        });
        await Check("history RPC contains only the requested report and rejects an invalid range",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"statistics.json"),Statistics(config,3));
            await using var engine=new MonitorEngine(config,dir);
            string pipeName="RtlineDnsPilot.Chart.Test."+Guid.NewGuid().ToString("N");
            using var lifetime=new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var server=new ServiceRpc(engine,WindowsIdentity.GetCurrent().User!.Value,pipeName).RunAsync(lifetime.Token);
            try
            {
                async Task<(RpcReply Reply,string Json)> Send(RpcRequest request)
                {
                    using var client=new NamedPipeClientStream(".",pipeName,PipeDirection.InOut,PipeOptions.Asynchronous);
                    await client.ConnectAsync(2000,lifetime.Token);await PipeMessages.WriteAsync(client,JsonData.Encode(request),lifetime.Token);
                    var json=await PipeMessages.ReadAsync(client,4*1024*1024,lifetime.Token);
                    return (JsonData.Decode<RpcReply>(json),json);
                }
                var success=await Send(new RpcRequest {Command="history",History=new HistoryQuery {PeriodMinutes=15,BucketMinutes=5}});
                using var document=JsonDocument.Parse(success.Json);
                Require(success.Reply.Ok&&success.Reply.History is not null&&Count(success.Reply.History)==3
                    &&success.Reply.History.Query.PeriodMinutes==15&&!document.RootElement.TryGetProperty("Status",out _),
                    "History RPC attached a full status or returned the wrong measurements.");
                var invalid=await Send(new RpcRequest {Command="history",History=new HistoryQuery {PeriodMinutes=1440,BucketMinutes=1}});
                Require(!invalid.Reply.Ok&&invalid.Reply.Status is null&&invalid.Reply.History is null&&!string.IsNullOrWhiteSpace(invalid.Reply.Error),
                    "An invalid history request was accepted or returned an unrelated full status.");
                var status=await Send(new RpcRequest());
                Require(status.Reply.Ok&&status.Reply.Status?.Statistics.Single().TotalChecks==3,
                    "History reads mutated monitoring or prevented later status reads.");
            }
            finally {lifetime.Cancel();try{await server;}catch(OperationCanceledException){}}
        });
        await Check("connected backend reads detached service history without sending a control action",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config);await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var history=await backend.ReadHistoryAsync(new HistoryQuery());
            Require(Count(history)==1&&service.Commands.SequenceEqual(new[] {"status","history"}),"A history read used the wrong source or sent a mutation.");
            history.Channels[0].Bars[0].Count=99;
            Require(service.SharedReport.Channels[0].Bars[0].Count==1,"Backend returned an attached RPC object.");
        });
        await Check("old services receive a clear history update instruction without an unsupported request",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {Version="0.1.4"};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadHistoryAsync(new HistoryQuery()));
            Require(error.Contains("Обновить службу")&&service.Commands.SequenceEqual(new[] {"status"}),"An older service was sent history or had no update guidance.");
        });
        await Check("an unknown service never substitutes local observer history",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {Offline=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadHistoryAsync(new HistoryQuery()));
            Require(error.Contains("Проверить связь")&&backend.DnsStateUnknown&&!backend.Status.Monitoring
                &&service.Commands.SequenceEqual(new[] {"status"}),"Unknown service history was replaced with local data or caused a duplicate monitor.");
        });
        await Check("a real local observation source can read its own archive without service requests",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var observer=Path.Combine(dir,"observer");Directory.CreateDirectory(observer);
            AtomicStore.Write(Path.Combine(observer,"statistics.json"),Statistics(config,2));
            var service=new Connection(config) {Installed=false};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var history=await backend.ReadHistoryAsync(new HistoryQuery());
            Require(Count(history)==2&&service.Commands.Count==0&&!backend.ServiceConnected,"Local history unexpectedly came from the service.");
        });
        await Check("confirmed offline recovery permits the actual local observation history",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var observer=Path.Combine(dir,"observer");Directory.CreateDirectory(observer);
            AtomicStore.Write(Path.Combine(observer,"statistics.json"),Statistics(config,2));
            var service=new Connection(config) {Offline=true,AllowFakeRecovery=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();await backend.RestoreAsync();
            var callsBeforeRead=service.Commands.Count;var history=await backend.ReadHistoryAsync(new HistoryQuery());
            Require(backend.RecoveryConfirmed&&!backend.DnsStateUnknown&&Count(history)==2&&service.Recoveries==1
                &&service.Commands.Count==callsBeforeRead,"Confirmed local observation was mistaken for an unknown service or sent a service request.");
        });
        await Check("unapplied DNS edits cannot be mislabeled as service history",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.Profiles[0].Servers=["1.1.1.2"];
            await Rejection(()=>backend.ConfigureAsync(draft));
            var error=await Rejection(()=>backend.ReadHistoryAsync(new HistoryQuery()));
            Require(error.Contains("Применить настройки")&&!service.Commands.Contains("history"),"An unapplied DNS profile was shown with the service's old history.");
        });
        await Check("unapplied monitored targets cannot reuse a previous service history context",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.MonitorGemini=true;
            await Rejection(()=>backend.ConfigureAsync(draft));
            var error=await Rejection(()=>backend.ReadHistoryAsync(new HistoryQuery()));
            Require(error.Contains("Применить настройки")&&!service.Commands.Contains("history"),"An unapplied target set reused old service history.");
        });
        await Check("unapplied names and intervals do not hide valid measurement context",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {RejectConfigure=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var draft=JsonData.Clone(config);draft.Profiles[0].Name+=" renamed";draft.IntervalSeconds=30;
            await Rejection(()=>backend.ConfigureAsync(draft));
            Require(Count(await backend.ReadHistoryAsync(new HistoryQuery()))==1,"Metadata-only edits incorrectly invalidated measured history.");
        });
        await Check("canceling an optional history read promptly releases the backend control gate",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {DelayHistory=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();using var cancel=new CancellationTokenSource(TimeSpan.FromMilliseconds(40));
            bool canceled=false;try{await backend.ReadHistoryAsync(new HistoryQuery(),cancel.Token);}catch(OperationCanceledException){canceled=true;}
            await backend.RefreshAsync();
            Require(canceled&&backend.ServiceConnected&&service.Commands.SequenceEqual(new[] {"status","history","status"}),
                "An optional read prevented later controls/status or changed connection state.");
        });
        await Check("a history deadline reports a clear timeout and leaves the backend gate usable",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {DelayHistory=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();bool timedOut=false;
            try{await backend.ReadHistoryAsync(new HistoryQuery());}catch(TimeoutException ex){timedOut=ex.Message.Contains("3 секунды");}
            await backend.RefreshAsync();
            Require(timedOut&&backend.ServiceConnected&&service.Commands.SequenceEqual(new[] {"status","history","status"}),
                "A timed-out optional read was silent or kept the control gate locked.");
        });
        await Check("a mismatched service history context is rejected without adopting unrelated status",async dir=>
        {
            var config=NoNetwork();AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);
            var service=new Connection(config) {WrongContext=true};await using var backend=new UiBackend(dir,service);
            await backend.RefreshAsync();var error=await Rejection(()=>backend.ReadHistoryAsync(new HistoryQuery()));
            Require(error.Contains("Обновите состояние")&&backend.ServiceConnected,"A response from another context was accepted or changed DNS/connection state.");
        });
    }

    private sealed class Connection : IUiServiceConnection
    {
        private readonly AppConfig _config;
        public Connection(AppConfig config)
        {
            _config=JsonData.Clone(config);
            var archive=new ChartHistoryArchive();var at=DateTimeOffset.UtcNow.AddSeconds(-1);
            archive.Record(config,[new ProbeResult {ProfileId=config.Profiles[0].Id,MonitoringKey=MonitoringTargets.Key(config),At=at,Success=true,DnsMs=1,HttpsMs=2}],DateTimeOffset.UtcNow);
            SharedReport=archive.Query(config,new HistoryQuery(),DateTimeOffset.UtcNow);
        }
        public bool Installed {get;set;}=true;
        public bool IsInstalled=>Installed;
        public bool Offline {get;set;}
        public bool RejectConfigure {get;set;}
        public bool DelayHistory {get;set;}
        public bool WrongContext {get;set;}
        public bool AllowFakeRecovery {get;set;}
        public int Recoveries {get;private set;}
        public string Version {get;set;}="0.1.5";
        public List<string> Commands {get;}=[];
        public HistoryReport SharedReport {get;}
        public async Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            Commands.Add(request.Command);if(Offline)throw new TimeoutException("Simulated disconnected service.");
            if(request.Command=="history")
            {
                if(DelayHistory)await Task.Delay(Timeout.Infinite,token);
                if(WrongContext)SharedReport.MonitoringKey="different-monitoring-context";
                return new RpcReply {Ok=true,History=SharedReport};
            }
            if(request.Command=="configure"&&RejectConfigure)throw new InvalidOperationException("Simulated rejected edit.");
            if(request.Command!="status")throw new Exception("Unexpected mutation: "+request.Command);
            return new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(_config),ServiceVersion=Version,ServiceConnected=true,Monitoring=true}};
        }
        public Task InstallAsync()=>throw new Exception("No service installation in history tests.");
        public Task RecoverAsync()
        {
            if(!AllowFakeRecovery)throw new Exception("No DNS recovery in history tests.");
            Recoveries++;return Task.CompletedTask;
        }
    }
}
