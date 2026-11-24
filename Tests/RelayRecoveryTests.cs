using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace NetPilot.Tests;

public static class RelayRecoveryTests
{
    private const string AdapterId="9c470ddd-3de5-411e-9b22-c6782cde48f1";

    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            string directory=Path.Combine(Path.GetTempPath(),"NetPilot-relay-fake-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try {await test(directory);report(name,true,"");}
            catch(Exception ex) {report(name,false,ex.Message);}
            finally
            {
                string path=Path.GetFullPath(directory);
                string tempRoot=Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath()))+Path.DirectorySeparatorChar;
                if(!path.StartsWith(tempRoot,StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(path).StartsWith("NetPilot-relay-fake-",StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the generated relay fixture directory.");
                try {Directory.Delete(path,true);} catch(IOException) { }
            }
        }

        await Check("paused management preserves a healthy relay without probes or restoration",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            int checks=fixture.Checks, writes=fixture.Backend.Writes, flushes=fixture.Flushes;
            string? active=fixture.Engine.GetStatus().ActiveProfileId;
            await Task.Delay(650);
            var status=fixture.Engine.GetStatus();
            Require(!status.Monitoring && status.Config.MonitoringPaused && status.SystemDnsManaged && !status.DnsRecoveryPending,
                "A healthy paused service lost its independent DNS-management state.");
            Require(fixture.LatestRelay.IsRunning && fixture.LatestRelay.DisposeCalls==0 && !fixture.LatestRelay.StartToken.CanBeCanceled,
                "Pause ended the relay lifetime or tied it to a probe token.");
            Require(fixture.Checks==checks && fixture.Backend.Writes==writes && fixture.Flushes==flushes && status.ActiveProfileId==active,
                "Paused safety polling probed, selected a channel or rewrote DNS.");
        });
        await Check("fatal relay failure restores both DNS families while checks remain paused",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            int checks=fixture.Checks;
            var before=fixture.Engine.GetStatus();
            fixture.LatestRelay.Fail();
            await UntilAsync(()=>!fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1);
            var after=fixture.Engine.GetStatus();
            Require(fixture.Backend.IsOriginal && !after.SystemDnsManaged && !after.DnsRecoveryPending,
                "Fatal failure left one DNS family on loopback or claimed incomplete restoration as success.");
            Require(after.Config.MonitoringPaused && !after.Monitoring && after.ActiveProfileId==before.ActiveProfileId
                && fixture.Checks==checks && fixture.FactoryCalls==1,"Safety recovery changed pause/selection or started probes/another relay.");
            Require(after.Error?.Contains("исходный DNS восстановлен",StringComparison.Ordinal)==true
                && after.Error.Contains("ConnectionAborted",StringComparison.Ordinal),"The confirmed recovery lost its bounded listener-failure cause.");
            Require(fixture.Trace.ToArray().Last()=="dispose-after-restore" || fixture.Trace.ToArray().Last()=="flush",
                "Unexpected effect followed recovery.");
        });
        await Check("fatal signal cancels an executing round before paused safety recovery",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            string? active=fixture.Engine.GetStatus().ActiveProfileId;
            int checks=fixture.Checks;
            fixture.BlockChecks=true;
            Task round=fixture.Engine.ProbeNowAsync();
            await fixture.CheckEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            fixture.LatestRelay.Fail();
            await RejectCancellationAsync(round);
            await UntilAsync(()=>!fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1);
            var status=fixture.Engine.GetStatus();
            Require(fixture.Checks==checks+1 && status.ActiveProfileId==active && !status.Monitoring
                && status.Statistics.Single().TotalChecks==1,"The cancelled round altered selection, completed a fake measurement or launched more checks.");
        });
        await Check("a newly requested round performs relay safety recovery before probing",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            fixture.Trace.Clear();
            fixture.LatestRelay.Fail(signal:false);
            await fixture.Engine.ProbeNowAsync();
            string[] trace=fixture.Trace.ToArray();
            Require(Array.IndexOf(trace,"dispose-after-restore")>=0 && Array.IndexOf(trace,"probe")>Array.IndexOf(trace,"dispose-after-restore"),
                "A new round ran before the dead relay's owned DNS was restored.");
            Require(fixture.Engine.GetStatus().Config.MonitoringPaused,
                "The safety gate resumed automatic monitoring.");
        });
        await Check("fatal relay recovery also runs during the long interval between rounds",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            await fixture.Engine.SetMonitoringAsync(true);
            await UntilAsync(()=>fixture.Engine.GetStatus().Statistics.Single().TotalChecks>=2);
            int checks=fixture.Checks;
            fixture.LatestRelay.Fail();
            await UntilAsync(()=>!fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1);
            Require(fixture.Backend.IsOriginal && fixture.Engine.GetStatus().Monitoring && fixture.Checks==checks && fixture.FactoryCalls==1,
                "Recovery waited for another scheduled round or triggered extra probes/activation.");
        });
        await Check("unpaused fatal recovery persists disabled management through manual and later scheduled probes",async directory=>
        {
            await using var fixture=new Fixture(directory,intervalSeconds:10);
            await fixture.StartManagedAsync();
            await fixture.Engine.SetMonitoringAsync(true);
            await UntilAsync(()=>fixture.Engine.GetStatus().Statistics.Single().TotalChecks>=2);
            string? active=fixture.Engine.GetStatus().ActiveProfileId;
            fixture.LatestRelay.Fail();
            await UntilAsync(()=>!fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1);
            var recovered=fixture.Engine.GetStatus();
            var saved=AtomicStore.Read<AppConfig>(Path.Combine(directory,"config.json"))!;
            Require(!recovered.Config.ManagementRequested && !saved.ManagementRequested && recovered.Monitoring && !saved.MonitoringPaused,
                "Fatal recovery retained or failed to persist automatic management intent.");
            int writes=fixture.Backend.Writes;
            long completed=recovered.Statistics.Single().TotalChecks;
            await fixture.Engine.ProbeNowAsync();
            await UntilAsync(()=>fixture.Engine.GetStatus().Statistics.Single().TotalChecks>=completed+2,timeoutSeconds:14);
            var after=fixture.Engine.GetStatus();
            Require(after.Monitoring && !after.Config.MonitoringPaused && after.ActiveProfileId==active && !after.Config.ManagementRequested
                && !after.SystemDnsManaged && !after.DnsRecoveryPending && fixture.Backend.IsOriginal && !fixture.Session.HasJournal
                && fixture.Backend.Writes==writes && fixture.FactoryCalls==1,
                "A later explicit or normally scheduled probe implicitly reactivated DNS after fatal recovery.");
            Require(!AtomicStore.Read<AppConfig>(Path.Combine(directory,"config.json"))!.ManagementRequested,
                "A later probe restored the persisted automatic-management intent.");
        });
        await Check("a failed config save does not skip DNS safety restoration and reports its unsaved intent",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            string configPath=Path.Combine(directory,"config.json");
            using(var lockedConfig=new FileStream(configPath,FileMode.Open,FileAccess.Read,FileShare.Read))
            {
                fixture.LatestRelay.Fail();
                await UntilAsync(()=>!fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1
                    && fixture.Engine.GetStatus().Error?.Contains("Выключение управления не сохранено",StringComparison.Ordinal)==true);
                var status=fixture.Engine.GetStatus();
                Require(fixture.Backend.IsOriginal && !status.SystemDnsManaged && !status.DnsRecoveryPending && !status.Config.ManagementRequested,
                    "A failed intent save suppressed the DNS restoration attempt or retained in-memory automatic activation.");
                Require(status.Error?.Contains("Выключение управления не сохранено",StringComparison.Ordinal)==true
                    && status.Error.Contains("Перед перезапуском",StringComparison.Ordinal),"The failed intent save was reported as durably disabled.");
                Require(AtomicStore.Read<AppConfig>(configPath)!.ManagementRequested,"The locked-file fixture did not actually prevent replacing the saved config.");
            }
            await fixture.Engine.SetManagementAsync(false);
            Require(!AtomicStore.Read<AppConfig>(configPath)!.ManagementRequested,"Explicit retry did not save disabled management after the config lock was removed.");
        });
        await Check("failed safety recovery stays pending without repeated writes and permits manual retry",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            fixture.Backend.FailRestoration=true;
            fixture.LatestRelay.Fail();
            await UntilAsync(()=>fixture.Engine.GetStatus().Error?.Contains("восстановление DNS не завершено",StringComparison.Ordinal)==true);
            var failed=fixture.Engine.GetStatus();
            Require(failed.DnsRecoveryPending && fixture.Session.HasJournal && !fixture.Session.Managed && fixture.LatestRelay.DisposeCalls==0,
                "Failed recovery lost its journal/Pending state or disposed before successful restoration.");
            int attempts=fixture.Backend.RestoreAttempts, checks=fixture.Checks;
            await Task.Delay(800);
            Require(fixture.Backend.RestoreAttempts==attempts && fixture.Checks==checks,"A pending recovery created an automatic retry/probe loop.");
            await fixture.Engine.ProbeNowAsync();
            Require(fixture.Checks==checks+1 && fixture.Engine.GetStatus().DnsRecoveryPending && fixture.Backend.RestoreAttempts==attempts
                && fixture.FactoryCalls==1,"An explicit diagnostic probe retried restoration or reactivated a pending relay.");
            await RejectInvalidOperationAsync(()=>fixture.Engine.SetManagementAsync(true));
            fixture.Backend.FailRestoration=false;
            await fixture.Engine.SetManagementAsync(false);
            var restored=fixture.Engine.GetStatus();
            Require(fixture.Backend.IsOriginal && !fixture.Session.HasJournal && !restored.DnsRecoveryPending
                && !restored.Config.ManagementRequested && restored.Config.MonitoringPaused && fixture.LatestRelay.DisposeCalls==1,
                "The existing manual Restore/Disable did not finish and clear the latched recovery.");
        });
        await Check("recovery serializes configuration and manual restore behind the current safety operation",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            fixture.Backend.RestoreGate=new(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.LatestRelay.Fail();
            await fixture.Backend.RestoreEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(!fixture.Engine.GetStatus().Config.ManagementRequested
                && !AtomicStore.Read<AppConfig>(Path.Combine(directory,"config.json"))!.ManagementRequested,
                "Safety recovery began before disabling and durably saving its automatic-management intent.");
            var config=fixture.Engine.GetStatus().Config;
            config.IntervalSeconds=299;
            Task configure=fixture.Engine.ConfigureAsync(config);
            Task restore=fixture.Engine.SetManagementAsync(false);
            await Task.Delay(100);
            Require(!configure.IsCompleted && !restore.IsCompleted && fixture.LatestRelay.DisposeCalls==0,
                "A concurrent operation bypassed the safety operation semaphore.");
            fixture.Backend.RestoreGate.TrySetResult();
            await Task.WhenAll(configure,restore).WaitAsync(TimeSpan.FromSeconds(3));
            Require(fixture.Backend.IsOriginal && !fixture.Session.HasJournal && fixture.LatestRelay.DisposeCalls==1
                && fixture.FactoryCalls==1 && fixture.Engine.GetStatus().Config.IntervalSeconds==299,
                "Serialized operations lost the restored state or recreated a relay.");
        });
        await Check("lost ownership journal stays visibly pending and is never replaced with guessed DNS",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync();
            File.Delete(Path.Combine(directory,"dns-session.json"));
            File.Delete(Path.Combine(directory,"dns-session.json.bak"));
            int writes=fixture.Backend.Writes;
            fixture.LatestRelay.Fail();
            await UntilAsync(()=>fixture.Engine.GetStatus().Error?.Contains("Журнал активного DNS отсутствует",StringComparison.Ordinal)==true);
            var status=fixture.Engine.GetStatus();
            Require(status.DnsRecoveryPending && status.State=="Восстановление DNS не завершено" && fixture.LatestRelay.DisposeCalls==0
                && fixture.Backend.Writes==writes,"A missing journal was treated as a successful no-op restoration.");
            // Restore the exact fake-session journal for isolated fixture cleanup, not a new DNS setting.
            File.WriteAllBytes(Path.Combine(directory,"dns-session.json"),fixture.SavedJournal!);
        });
        await Check("an unmanaged observer performs no relay recovery or privileged effects",async directory=>
        {
            var config=Config();
            await using var engine=new MonitorEngine(config,directory,false,(_,_,_,_)=>throw new InvalidOperationException("No paused observer probes expected."));
            await engine.StartAsync();
            await Task.Delay(300);
            Require(!engine.GetStatus().SystemDnsManaged && !engine.GetStatus().DnsRecoveryPending
                && !File.Exists(Path.Combine(directory,"dns-session.json")),"An observer created a DNS ownership transaction.");
        });
        await Check("Windows DNS failure with a successful website probe is retried after flushing the cache",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false);
            await fixture.StartManagedAsync(startLoop:false);
            int flushes=fixture.Flushes, writes=fixture.Backend.Writes;
            fixture.Lookup=(host,_)=>
            {
                Require(host=="chatgpt.com","System recovery probed a host outside the enabled monitoring targets.");
                return fixture.Flushes==flushes?HostNotFoundAsync():ResolvedAsync();
            };

            await fixture.Engine.ProbeNowAsync();

            var status=fixture.Engine.GetStatus();
            Require(fixture.SystemLookups==2 && fixture.Flushes==flushes+1,
                "The green direct probe masked the Windows DNS failure, or the retry did not use a flushed cache.");
            Require(status.SystemDnsManaged && status.ActiveProfileId==fixture.PrimaryId && fixture.Backend.Writes==writes
                && status.Events.Any(e=>e.Kind=="recovered" && e.Message.Contains("Windows",StringComparison.Ordinal)),
                "A DNS cache recovery changed channels, rewrote adapter DNS or went unrecorded.");
        });
        await Check("persistent Windows DNS failure switches to a confirmed reserve and verifies it",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false,preferPrimary:true);
            await fixture.StartManagedAsync(startLoop:false);
            int writes=fixture.Backend.Writes, flushes=fixture.Flushes;
            fixture.Lookup=(_,_)=>fixture.Engine.GetStatus().ActiveProfileId==fixture.PrimaryId?HostNotFoundAsync():ResolvedAsync();

            await fixture.Engine.ProbeNowAsync();

            var switched=fixture.Engine.GetStatus();
            Require(switched.SystemDnsManaged && switched.ActiveProfileId==fixture.ReserveId
                && fixture.SystemLookups==3 && fixture.Flushes==flushes+2 && fixture.Backend.Writes==writes,
                "A confirmed reserve was not tried through Windows or the switch rewrote the adapter unnecessarily.");
            Require(switched.Statistics.All(s=>s.Last?.Success==true)
                && switched.Events.Any(e=>e.Kind=="switch" && e.Message.Contains("Windows",StringComparison.Ordinal)),
                "The switch falsified the direct measurements or was not recorded as a Windows DNS recovery.");
            await fixture.Engine.ProbeNowAsync();
            Require(fixture.Engine.GetStatus().ActiveProfileId==fixture.ReserveId && fixture.SystemLookups==4
                && fixture.Flushes==flushes+2,
                "The next green direct check immediately selected the failed preferred channel again or flushed a healthy cache.");
        });
        await Check("a failed Windows DNS retry and reserve restore the original DNS without automatic reactivation",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false,preferPrimary:true);
            await fixture.StartManagedAsync(startLoop:false);
            fixture.Lookup=(_,_)=>HostNotFoundAsync();

            await fixture.Engine.ProbeNowAsync();

            var status=fixture.Engine.GetStatus();
            Require(fixture.SystemLookups==3 && fixture.LatestRelay.DisposeCalls==1 && fixture.Backend.IsOriginal
                && !fixture.Session.HasJournal && !status.DnsRecoveryPending && !status.SystemDnsManaged,
                "A running relay kept the adapter pointed at loopback even though Windows could not resolve the monitored host.");
            Require(status.Statistics.All(s=>s.Last?.Success==true) && !status.Config.ManagementRequested
                && !AtomicStore.Read<AppConfig>(Path.Combine(directory,"config.json"))!.ManagementRequested
                && status.Error?.Contains("11001",StringComparison.Ordinal)==true,
                "Recovery lost the concrete DNS error, falsified successful website probes or did not save disabled management.");

            int writes=fixture.Backend.Writes;
            await fixture.Engine.ProbeNowAsync();
            Require(fixture.Backend.Writes==writes && fixture.FactoryCalls==1 && fixture.SystemLookups==3,
                "Another green probe silently reactivated DNS or ran system recovery in observation mode.");
        });
        await Check("persistent Windows DNS failure restores the original DNS when there is no reserve",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false);
            await fixture.StartManagedAsync(startLoop:false);
            fixture.Lookup=(_,_)=>HostNotFoundAsync();

            await fixture.Engine.ProbeNowAsync();

            Require(fixture.SystemLookups==2 && fixture.Backend.IsOriginal && fixture.LatestRelay.DisposeCalls==1
                && !fixture.Engine.GetStatus().Config.ManagementRequested,"A single green channel kept Windows on a failed DNS path.");
        });
        await Check("pausing during a system lookup cancels recovery without changing DNS",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false);
            await fixture.StartManagedAsync(startLoop:false);
            var lookupEntered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Lookup=async (_,token)=>
            {
                lookupEntered.TrySetResult();
                await Task.Delay(Timeout.Infinite,token);
                return [];
            };
            int writes=fixture.Backend.Writes, flushes=fixture.Flushes;
            var round=fixture.Engine.ProbeNowAsync();
            await lookupEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));

            await fixture.Engine.SetMonitoringAsync(false);
            await RejectCancellationAsync(round);

            var status=fixture.Engine.GetStatus();
            Require(fixture.SystemLookups==1 && status.Config.MonitoringPaused && status.SystemDnsManaged
                && fixture.Backend.Writes==writes && fixture.Flushes==flushes,"A cancelled system lookup was treated as a DNS failure.");
        });
        await Check("one-off measurements while paused do not trigger Windows DNS recovery",async directory=>
        {
            await using var fixture=new Fixture(directory);
            await fixture.StartManagedAsync(startLoop:false);
            int writes=fixture.Backend.Writes, flushes=fixture.Flushes;
            fixture.Lookup=(_,_)=>HostNotFoundAsync();
            await fixture.Engine.ProbeNowAsync();
            Require(fixture.Checks==2 && fixture.SystemLookups==0 && fixture.Backend.Writes==writes
                && fixture.Flushes==flushes && fixture.Engine.GetStatus().SystemDnsManaged,
                "A paused diagnostic probe changed the running DNS session.");
        });
        await Check("manual fixation still suppresses automatic Windows DNS recovery",async directory=>
        {
            await using var fixture=new Fixture(directory,paused:false,preferPrimary:true);
            await fixture.StartManagedAsync(startLoop:false);
            var config=fixture.Engine.GetStatus().Config;
            config.ManualProfileId=fixture.PrimaryId;
            await fixture.Engine.ConfigureAsync(config);
            int writes=fixture.Backend.Writes, flushes=fixture.Flushes;
            fixture.Lookup=(_,_)=>HostNotFoundAsync();
            await fixture.Engine.ProbeNowAsync();
            Require(fixture.SystemLookups==0 && fixture.Backend.Writes==writes && fixture.Flushes==flushes
                && fixture.Engine.GetStatus().ActiveProfileId==fixture.PrimaryId,
                "Automatic Windows recovery ignored the explicit manual fixation.");
        });
    }

    private static Task<IPAddress[]> HostNotFoundAsync() =>
        Task.FromException<IPAddress[]>(new SocketException((int)SocketError.HostNotFound));
    private static Task<IPAddress[]> ResolvedAsync() => Task.FromResult(new[] { IPAddress.Parse("192.0.2.55") });

    private static AppConfig Config()
    {
        var config=AppConfig.Defaults();
        config.Profiles=config.Profiles.Take(1).ToList();config.Profiles[0].Enabled=true;
        config.AdapterId=AdapterId;config.MonitoringPaused=true;config.IntervalSeconds=300;
        config.MonitorChatGpt=true;config.MonitorGemini=false;config.MonitorTailscale=false;config.AdditionalTargets=[];
        return config;
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public readonly ConcurrentQueue<string> Trace=new();
        public readonly FakeBackend Backend;
        public readonly DnsSessionManager Session;
        public readonly MonitorEngine Engine;
        public readonly TaskCompletionSource CheckEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly List<FakeRelay> _relays=[];
        private int _checks,_flushes,_factoryCalls,_systemLookups;
        public int Checks=>Volatile.Read(ref _checks);
        public int Flushes=>Volatile.Read(ref _flushes);
        public int FactoryCalls=>Volatile.Read(ref _factoryCalls);
        public int SystemLookups=>Volatile.Read(ref _systemLookups);
        public string PrimaryId {get;}
        public string? ReserveId {get;}
        public Func<string,CancellationToken,Task<IPAddress[]>> Lookup {get;set;}=(_,_)=>ResolvedAsync();
        public volatile bool BlockChecks;
        public FakeRelay LatestRelay=>_relays.Last();
        public byte[]? SavedJournal;
        private readonly string _directory;
        public Fixture(string directory,int intervalSeconds=300,bool paused=true,bool preferPrimary=false)
        {
            _directory=directory;
            Backend=new FakeBackend(Trace);Session=new DnsSessionManager(directory,Backend);
            var config=Config();config.IntervalSeconds=intervalSeconds;config.MonitoringPaused=paused;
            PrimaryId=config.Profiles.Single().Id;
            if(preferPrimary)
            {
                var reserve=AppConfig.Defaults().Profiles[1];reserve.Enabled=true;
                config.Profiles.Add(reserve);ReserveId=reserve.Id;config.PinnedProfileId=PrimaryId;
            }
            Engine=new MonitorEngine(config,directory,true,CheckAsync,Session,CreateRelay,
                ()=>{Interlocked.Increment(ref _flushes);Trace.Enqueue("flush");},ResolveSystemDnsAsync);
        }
        private Task<IPAddress[]> ResolveSystemDnsAsync(string host,CancellationToken token)
        {
            Interlocked.Increment(ref _systemLookups);
            return Lookup(host,token);
        }
        private IMonitorDnsRelay CreateRelay()
        {
            Interlocked.Increment(ref _factoryCalls);
            var relay=new FakeRelay(Trace,()=>Backend.IsOriginal&&!Session.HasJournal);
            _relays.Add(relay);return relay;
        }
        private async Task<ProbeResult> CheckAsync(ChannelProfile profile,string url,TimeSpan timeout,CancellationToken token)
        {
            Interlocked.Increment(ref _checks);Trace.Enqueue("probe");
            if(BlockChecks) {CheckEntered.TrySetResult();await Task.Delay(Timeout.Infinite,token);}
            token.ThrowIfCancellationRequested();
            return new ProbeResult {ProfileId=profile.Id,At=DateTimeOffset.UtcNow,Success=true,DnsMs=1,HttpsMs=2,HttpStatus=200,Stage="http",Detail="fake"};
        }
        public async Task StartManagedAsync(bool startLoop=true)
        {
            if(startLoop) await Engine.StartAsync();
            await Engine.SetManagementAsync(true);
            SavedJournal=File.ReadAllBytes(Path.Combine(_directory,"dns-session.json"));
            Require(Session.Managed && Session.HasJournal && LatestRelay.IsRunning,"The fake managed session was not activated.");
        }
        public async ValueTask DisposeAsync()
        {
            Backend.FailRestoration=false;Backend.RestoreGate?.TrySetResult();
            if(Session.Managed&&!Session.HasJournal&&SavedJournal is not null)
                File.WriteAllBytes(Path.Combine(_directory,"dns-session.json"),SavedJournal);
            await Engine.DisposeAsync();
        }
    }
    private sealed class FakeRelay(ConcurrentQueue<string> trace,Func<bool> restorationComplete) : IMonitorDnsRelay
    {
        private volatile bool _running;
        private bool _disposed;
        private int _disposeCalls;
        public bool IsRunning=>_running;
        public string? LastFailure {get;private set;}
        public event Action? ListenerFailed;
        public CancellationToken StartToken {get;private set;}
        public int DisposeCalls=>Volatile.Read(ref _disposeCalls);
        public Task StartAsync(int port,bool ipv6,CancellationToken token)
        {
            token.ThrowIfCancellationRequested();StartToken=token;_running=true;trace.Enqueue("relay-start");return Task.CompletedTask;
        }
        public void Fail(bool signal=true)
        {
            LastFailure="TCP InterNetwork: ConnectionAborted (10053)";_running=false;
            if(signal) ListenerFailed?.Invoke();
        }
        public ValueTask DisposeAsync()
        {
            if(_disposed) return ValueTask.CompletedTask;
            Require(restorationComplete(),"Relay disposal occurred before verified original DNS restoration/journal retirement.");
            _disposed=true;_running=false;Interlocked.Increment(ref _disposeCalls);trace.Enqueue("dispose-after-restore");
            return ValueTask.CompletedTask;
        }
    }
    private sealed class FakeBackend : IDnsBackend
    {
        private readonly ConcurrentQueue<string> _trace;
        private readonly AdapterDnsSnapshot _original;
        private AdapterDnsSnapshot _current;
        private readonly object _sync=new();
        private int _writes,_restoreAttempts;
        public volatile bool FailRestoration;
        public TaskCompletionSource? RestoreGate;
        public readonly TaskCompletionSource RestoreEntered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Writes=>Volatile.Read(ref _writes);
        public int RestoreAttempts=>Volatile.Read(ref _restoreAttempts);
        public bool IsOriginal {get {lock(_sync) return JsonData.Encode(_current)==JsonData.Encode(_original);}}
        public FakeBackend(ConcurrentQueue<string> trace)
        {
            _trace=trace;
            _original=new AdapterDnsSnapshot {AdapterId=AdapterId,AdapterName="Fake",Up=true,
                IPv4=Family(DnsFamily.IPv4,"192.0.2.53","198.51.100.53"),IPv6=Family(DnsFamily.IPv6,"2001:db8::53","2001:db8:1::53")};
            _current=JsonData.Clone(_original);
        }
        public Task<AdapterDnsSnapshot> ReadAsync(string adapterId)
        {
            Require(adapterId==AdapterId,"The fake backend was asked for another adapter.");
            lock(_sync) return Task.FromResult(JsonData.Clone(_current));
        }
        public async Task ApplyAsync(string adapterId,DnsFamilySnapshot expected,DnsFamilySnapshot desired)
        {
            Require(adapterId==AdapterId,"A DNS write targeted another adapter.");
            bool restoring=!desired.Servers.Any(x=>IPAddress.IsLoopback(IPAddress.Parse(x)));
            if(restoring)
            {
                Interlocked.Increment(ref _restoreAttempts);RestoreEntered.TrySetResult();
                if(RestoreGate is not null) await RestoreGate.Task;
                if(FailRestoration) throw new IOException("Injected restoration failure.");
            }
            lock(_sync)
            {
                Require(DnsState.SameConfiguration(DnsState.Family(_current,desired.Family),expected),"The fake backend rejected a stale expected DNS state.");
                var written=JsonData.Clone(desired);written.EffectiveServers=written.Servers.ToArray();
                if(desired.Family==DnsFamily.IPv4) _current.IPv4=written;else _current.IPv6=written;
            }
            Interlocked.Increment(ref _writes);_trace.Enqueue(restoring?"restore-"+desired.Family:"loopback-"+desired.Family);
        }
        private static DnsFamilySnapshot Family(DnsFamily family,params string[] servers)=>new()
        {Family=family,Enabled=true,Source=DnsSource.Static,Servers=servers,EffectiveServers=servers.ToArray()};
    }
    private static async Task UntilAsync(Func<bool> condition,int timeoutSeconds=4)
    {
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        while(!condition()) await Task.Delay(20,timeout.Token);
    }
    private static async Task RejectCancellationAsync(Task task)
    {
        try {await task.WaitAsync(TimeSpan.FromSeconds(3));}
        catch(OperationCanceledException) {return;}
        throw new InvalidOperationException("The listener-failure signal did not cancel the active probe round.");
    }
    private static async Task RejectInvalidOperationAsync(Func<Task> action)
    {
        try {await action();}
        catch(InvalidOperationException) {return;}
        throw new InvalidOperationException("Pending recovery allowed management to be enabled.");
    }
    private static void Require(bool condition,string message) {if(!condition) throw new InvalidOperationException(message);}
}
