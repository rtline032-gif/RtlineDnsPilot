using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace NetPilot.Tests;

public static class TailscaleMonitoringTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        void Check(string name,Action test)
        {try{test();report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}}
        async Task CheckAsync(string name,Func<Task> test)
        {try{await test();report(name,true,"");}catch(Exception ex){report(name,false,ex.ToString());}}
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        static void Reject(Action action)
        {try{action();}catch(ArgumentException){return;}throw new Exception("Invalid settings were accepted.");}
        static AppConfig AllTargets()
        {
            var config=AppConfig.Defaults();config.MonitorGemini=true;config.MonitorTailscale=true;
            config.AdditionalTargets=Enumerable.Range(1,6).Select(i=>new MonitoringEndpoint {Name="Custom "+i,Url=$"https://custom{i}.example/"}).ToList();
            return config;
        }
        static string DirectoryForTest()
        {
            var dir=Path.Combine(AppContext.BaseDirectory,"test-data","tailscale-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(dir);return dir;
        }
        Check("legacy configurations keep Tailscale unchecked and preserve ChatGPT-only context",()=>
        {
            var config=AppConfig.Defaults();var key=MonitoringTargets.Key(config);
            var node=JsonNode.Parse(JsonData.Encode(config))!.AsObject();node.Remove("MonitorTailscale");
            var restored=JsonData.Decode<AppConfig>(node.ToJsonString());restored.Validate();
            Require(!restored.MonitorTailscale&&MonitoringTargets.Selected(restored) is [{Id:MonitoringTargets.ChatGpt}]
                &&MonitoringTargets.Key(restored)==key,"Adding the optional built-in changed existing defaults or keys.");
        });
        Check("Tailscale-only selection persists as a fixed public HTTPS target",()=>
        {
            var config=AppConfig.Defaults();config.MonitorChatGpt=false;config.MonitorTailscale=true;
            var restored=JsonData.Clone(config);restored.Validate();
            Require(restored.MonitorTailscale&&MonitoringTargets.Selected(restored) is [var target]
                &&target.Id==MonitoringTargets.Tailscale&&target.Name=="Tailscale"&&target.Url=="https://login.tailscale.com/",
                "Tailscale selection was lost or did not use the fixed official endpoint.");
        });
        Check("three built-ins allow exactly six additional services for nine targets",()=>
        {
            var config=AllTargets();config.Validate();Require(MonitoringTargets.Selected(config).Length==9,"The maximum target set is incomplete.");
            config.AdditionalTargets.Add(new MonitoringEndpoint {Name="Seventh custom",Url="https://seventh.example/"});Reject(config.Validate);
        });
        Check("Tailscale identity name and canonical URL are reserved even while unchecked",()=>
        {
            foreach(var name in new[] {"Tailscale"," TAILSCALE "})
            {
                var config=AppConfig.Defaults();config.AdditionalTargets=[new MonitoringEndpoint {Name=name,Url="https://custom.example/"}];Reject(config.Validate);
            }
            var duplicate=AppConfig.Defaults();duplicate.AdditionalTargets=[new MonitoringEndpoint {Name="Other",Url="https://LOGIN.TAILSCALE.COM:443/"}];Reject(duplicate.Validate);
            duplicate.AdditionalTargets=[new MonitoringEndpoint {Id=MonitoringTargets.Tailscale,Name="Other",Url="https://custom.example/"}];Reject(duplicate.Validate);
        });
        Check("enabling Tailscale changes measurement context without reusing old or legacy evidence",()=>
        {
            var config=AppConfig.Defaults();var oldKey=MonitoringTargets.Key(config);
            var old=new ProbeResult {MonitoringKey=oldKey,Success=true};var legacy=new ProbeResult {Success=true};
            config.MonitorTailscale=true;
            Require(MonitoringTargets.Key(config)!=oldKey&&!MonitoringTargets.Matches(old,config)&&!MonitoringTargets.Matches(legacy,config),
                "An unmeasured Tailscale target inherited old reachability.");
            config.MonitorTailscale=false;Require(MonitoringTargets.Key(config)==oldKey,"Unchecking Tailscale changed the existing context key.");
        });
        Check("Tailscale failure prevents an all-selected successful aggregate",()=>
        {
            var config=AppConfig.Defaults();config.MonitorTailscale=true;
            var results=new[] {new ServiceProbeResult {ServiceId=MonitoringTargets.ChatGpt,Success=true,HttpStatus=200},
                new ServiceProbeResult {ServiceId=MonitoringTargets.Tailscale,Success=false,HttpStatus=403,Stage="http"}};
            var aggregate=MonitoringTargets.Combine(config.Profiles[0].Id,config,results);
            Require(!aggregate.Success&&aggregate.Stage=="tailscale/http"&&aggregate.Detail.Contains("Tailscale")&&aggregate.Detail.Contains("403"),
                "A failed fixed target was treated as healthy or had an unreadable identity.");
            results[1].Success=true;Require(MonitoringTargets.Combine(config.Profiles[0].Id,config,results).Success,"Complete successful evidence did not recover.");
        });
        Check("freshness covers the full 32 profile nine target round in four slots",()=>
        {
            var config=AllTargets();config.IntervalSeconds=10;config.TimeoutSeconds=30;
            config.Profiles=Enumerable.Range(1,32).Select(i=>new ChannelProfile {Name="Channel "+i,Servers=[$"198.51.100.{i}"]}).ToList();config.Validate();
            Require(MonitoringTargets.FreshnessSeconds(config)==2170,"The 288-check bounded round was not covered.");
            config.MonitorTailscale=false;Require(MonitoringTargets.FreshnessSeconds(config)==1930,"Disabling the optional target still inflated freshness.");
        });
        Check("synchronization retains a Tailscale draft and clears incompatible displayed evidence",()=>
        {
            var applied=AppConfig.Defaults();var desired=JsonData.Clone(applied);desired.MonitorTailscale=true;
            var sample=new ProbeResult {ProfileId=applied.Profiles[0].Id,MonitoringKey=MonitoringTargets.Key(applied),Success=true};
            var status=new AppStatus {Config=applied,Statistics=[new ProfileStats {ProfileId=sample.ProfileId,Samples=[sample]}]};
            var draft=ProfileSynchronization.WithDraft(status,desired);
            Require(draft.Config.MonitorTailscale&&draft.Statistics.Count==0&&!ProfileSynchronization.SameConfig(applied,desired),
                "Pending Tailscale selection was ignored or shown with another context's samples.");
            Require(ProfileSynchronization.ForConnection(desired,applied,true).MonitorTailscale,"Explicit local Tailscale selection was lost at service handoff.");
        });
        await CheckAsync("an unchecked Tailscale target never creates a probe",async()=>
        {
            var config=AppConfig.Defaults();foreach(var profile in config.Profiles.Skip(1))profile.Enabled=false;
            var calls=new List<string>();
            var results=await MonitoringTargets.CheckAsync(config,(profile,url,_,_)=>
            {
                calls.Add(url);return Task.FromResult(new ProbeResult {ProfileId=profile.Id,Success=true});
            },CancellationToken.None);
            Require(calls.SequenceEqual(new[] {MonitoringTargets.DefaultChatGptUrl})&&results.Single().Services.All(s=>s.ServiceId!=MonitoringTargets.Tailscale),
                "An unchecked built-in made a network probe.");
        });
        await CheckAsync("Tailscale-only coordinator uses its fixed URL without ChatGPT or Gemini probes",async()=>
        {
            var config=AppConfig.Defaults();config.MonitorChatGpt=false;config.MonitorTailscale=true;
            foreach(var profile in config.Profiles.Skip(1))profile.Enabled=false;var calls=new List<string>();
            var results=await MonitoringTargets.CheckAsync(config,(profile,url,_,_)=>
            {
                calls.Add(url);return Task.FromResult(new ProbeResult {ProfileId=profile.Id,Success=true,HttpStatus=200,DnsMs=2,HttpsMs=4});
            },CancellationToken.None);
            Require(calls.SequenceEqual(new[] {MonitoringTargets.TailscaleUrl})&&results.Single().Success
                &&results.Single().Services is [{ServiceId:MonitoringTargets.Tailscale}],"The optional built-in probed a different service.");
        });
        await CheckAsync("all nine targets share the existing global concurrency bound of four",async()=>
        {
            var config=AllTargets();int active=0,maximum=0;var urls=new ConcurrentBag<string>();
            var results=await MonitoringTargets.CheckAsync(config,async(profile,url,_,token)=>
            {
                int current=Interlocked.Increment(ref active);
                while(true){int known=Volatile.Read(ref maximum);if(current<=known||Interlocked.CompareExchange(ref maximum,current,known)==known)break;}
                try{urls.Add(url);await Task.Delay(5,token);return new ProbeResult {ProfileId=profile.Id,Success=true,HttpStatus=200};}
                finally{Interlocked.Decrement(ref active);}
            },CancellationToken.None);
            Require(urls.Count==27&&urls.Count(url=>url==MonitoringTargets.TailscaleUrl)==3&&maximum==4
                &&results.Count==3&&results.All(result=>result.Success&&result.Services.Count==9),"Tailscale introduced an extra concurrency pool or incomplete rounds.");
        });
        await CheckAsync("legacy engine samples remain ChatGPT-only when Tailscale is selected",async()=>
        {
            var dir=DirectoryForTest();var config=AppConfig.Defaults();config.MonitorTailscale=true;
            foreach(var profile in config.Profiles)profile.Enabled=false;
            var expected=JsonData.Clone(config);expected.MonitorTailscale=false;
            AtomicStore.Write(Path.Combine(dir,"statistics.json"),new[] {new ProfileStats {ProfileId=config.Profiles[0].Id,
                Samples=[new ProbeResult {ProfileId=config.Profiles[0].Id,At=DateTimeOffset.UtcNow.AddSeconds(-1),Success=true}]}});
            await using var engine=new MonitorEngine(config,dir);
            var raw=engine.GetStatus().Statistics.Single();
            Require(raw.Samples.Single().MonitoringKey==MonitoringTargets.Key(expected)
                &&MonitoringTargets.View(raw,config)!.Samples.Count==0&&!File.Exists(Path.Combine(dir,"dns-session.json")),
                "Legacy migration labelled old evidence as a successful Tailscale check.");
        });
        await CheckAsync("an older service cannot silently acknowledge the new Tailscale checkbox",async()=>
        {
            var dir=DirectoryForTest();var config=AppConfig.Defaults();foreach(var profile in config.Profiles)profile.Enabled=false;
            AtomicStore.Write(Path.Combine(dir,"ui-config.json"),config);var service=new OldConnection(config);
            await using var backend=new UiBackend(dir,service);await backend.RefreshAsync();
            var desired=JsonData.Clone(config);desired.MonitorTailscale=true;bool rejected=false;
            try{await backend.ConfigureAsync(desired);}catch(InvalidOperationException ex){rejected=ex.Message.Contains("Обновить службу");}
            var saved=AtomicStore.Read<AppConfig>(Path.Combine(dir,"ui-config.json"))!;
            Require(rejected&&saved.MonitorTailscale&&backend.PendingServiceConfiguration&&service.Commands.SequenceEqual(new[] {"status"}),
                "The flag was lost or sent to an older service as if applied.");
        });
    }

    private sealed class OldConnection(AppConfig config):IUiServiceConnection
    {
        public bool IsInstalled=>true;
        public List<string> Commands {get;}=[];
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            Commands.Add(request.Command);if(request.Command!="status")throw new Exception("An unsupported service received a mutation.");
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(config),ServiceVersion="0.1.4",ServiceConnected=true}});
        }
        public Task InstallAsync()=>throw new Exception("No service installation in Tailscale tests.");
        public Task RecoverAsync()=>throw new Exception("No DNS recovery in Tailscale tests.");
    }
}
