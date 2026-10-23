namespace NetPilot.Tests;

public static class EngineTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        async Task Check(string name,Func<string,Task> test)
        {
            var directory=Path.Combine(AppContext.BaseDirectory,"test-data","engine-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); report(name,true,""); } catch(Exception e) { report(name,false,e.Message); }
        }
        void Require(bool condition,string reason) { if(!condition) throw new Exception(reason); }
        AppConfig NoNetwork()
        { var c=AppConfig.Defaults(); foreach(var p in c.Profiles) p.Enabled=false; return c; }
        await Check("local observer cannot enable system management",async dir=>
        {
            await using var engine=new MonitorEngine(NoNetwork(),dir);
            bool rejected=false; try { await engine.SetManagementAsync(true); } catch(InvalidOperationException) {rejected=true;}
            Require(rejected&&!engine.GetStatus().SystemDnsManaged,"Observer mutated or accepted privileged mode.");
            Require(!File.Exists(Path.Combine(dir,"dns-session.json")),"Observer created an adapter transaction.");
        });
        await Check("persisted management flag cannot authorize local observer",async dir=>
        {
            var config=NoNetwork(); config.ManagementRequested=true;
            await using var engine=new MonitorEngine(config,dir);
            Require(!engine.GetStatus().Config.ManagementRequested,"Local config authorized a system write.");
            await engine.ConfigureAsync(config);
            Require(!engine.GetStatus().Config.ManagementRequested,"Configure bypassed observer restriction.");
        });
        await Check("observer start stop restart preserves explicit state",async dir=>
        {
            await using var engine=new MonitorEngine(NoNetwork(),dir);
            await engine.StartAsync(); Require(engine.GetStatus().Monitoring,"Start did not enter observation.");
            await engine.StopAsync(); Require(!engine.GetStatus().Monitoring,"Stop did not leave observation.");
            await engine.StartAsync(); Require(engine.GetStatus().Monitoring,"Restart did not enter observation.");
            Require(!engine.GetStatus().SystemDnsManaged,"Observer has a system DNS state.");
        });
        await Check("invalid incoming configuration is rejected without state change",async dir=>
        {
            await using var engine=new MonitorEngine(NoNetwork(),dir);
            var original=JsonData.Encode(engine.GetStatus().Config);
            var invalid=NoNetwork(); invalid.ProbeUrl="http://example.com";
            bool rejected=false; try { await engine.ConfigureAsync(invalid); } catch(ArgumentException) {rejected=true;}
            Require(rejected&&JsonData.Encode(engine.GetStatus().Config)==original,"Rejected config still changed engine state.");
        });
        await Check("atomic store rejects corrupt state instead of hiding it",dir=>
        {
            string path=Path.Combine(dir,"broken.json"); File.WriteAllText(path,"{");
            bool rejected=false; try { AtomicStore.Read<AppConfig>(path); } catch(InvalidDataException) {rejected=true;}
            Require(rejected&&File.ReadAllText(path)=="{","Corrupt state was silently replaced."); return Task.CompletedTask;
        });
        await Check("atomic store preserves previous committed version",dir=>
        {
            string path=Path.Combine(dir,"value.json"); AtomicStore.Write(path,new[] {1,2}); AtomicStore.Write(path,new[] {3,4});
            Require(AtomicStore.Read<int[]>(path)!.SequenceEqual([3,4])&&AtomicStore.Read<int[]>(path+".bak")!.SequenceEqual([1,2]),"Committed value or backup lost."); return Task.CompletedTask;
        });
        await Check("initial profile identities survive reopening without a manual edit",async dir=>
        {
            string[] first;
            await using(var backend=new UiBackend(dir)) first=backend.Status.Config.Profiles.Select(p=>p.Id).ToArray();
            await using var reopened=new UiBackend(dir);
            Require(reopened.Status.Config.Profiles.Select(p=>p.Id).SequenceEqual(first),"Default profile IDs changed; stored measurements would lose their channel identity.");
        });
    }
}
