using System.Text.Json.Nodes;

namespace NetPilot.Tests;

public static class MonitoringTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        void Check(string name,Action test) { try { test(); report(name,true,""); } catch(Exception ex) {report(name,false,ex.Message);} }
        void Require(bool condition,string message) {if(!condition) throw new Exception(message);}
        void Reject(Action action) {try {action();} catch(ArgumentException) {return;} throw new Exception("Invalid monitoring settings accepted.");}
        AppConfig Config(bool chatgpt,bool gemini,string? manual=null,DateTimeOffset? until=null)
        {
            var node=JsonNode.Parse(JsonData.Encode(AppConfig.Defaults()))!;
            node["MonitorChatGpt"]=chatgpt;node["MonitorGemini"]=gemini;
            node["ManualProfileId"]=manual;node["ManualUntil"]=until?.ToString("O");
            return JsonData.Decode<AppConfig>(node.ToJsonString());
        }
        Check("monitoring rejects a configuration with no selected service",()=>Reject(Config(false,false).Validate));
        Check("Gemini-only monitoring survives JSON storage",()=>
        {
            var encoded=JsonNode.Parse(JsonData.Encode(Config(false,true)))!;
            Require(encoded["MonitorGemini"]?.GetValue<bool>()==true && encoded["MonitorChatGpt"]?.GetValue<bool>()==false,"Selected services disappeared during serialization.");
        });
        Check("manual fixation rejects an unknown channel",()=>Reject(Config(true,false,Guid.NewGuid().ToString("N")).Validate));

        async Task CheckAsync(string name,Func<Task> test)
        {
            try {await test();report(name,true,"");} catch(Exception ex) {report(name,false,ex.Message);}
        }
        MonitoringEndpoint Custom(string name="Custom service",string url="https://service.example/status",bool enabled=true) =>
            new() {Name=name,Url=url,Enabled=enabled};
        ServiceProbeResult Service(string id,bool success=true,int status=200) => new()
            {ServiceId=id,Success=success,HttpStatus=status,DnsMs=12,HttpsMs=40,Stage="http",Detail="Public HTTPS response"};

        Check("legacy configuration defaults to ChatGPT only",()=>
        {
            var node=JsonNode.Parse(JsonData.Encode(AppConfig.Defaults()))!;
            node.AsObject().Remove("MonitorChatGpt");node.AsObject().Remove("MonitorGemini");node.AsObject().Remove("AdditionalTargets");
            var config=JsonData.Decode<AppConfig>(node.ToJsonString());config.Validate();
            Require(MonitoringTargets.Selected(config) is [{Id:MonitoringTargets.ChatGpt}],"Legacy monitoring settings changed.");
            Require(config.AdditionalTargets.Count==0,"A legacy configuration gained custom services.");
        });
        Check("custom-only monitoring survives JSON storage",()=>
        {
            var config=Config(false,false);config.AdditionalTargets=[Custom()];
            var restored=JsonData.Clone(config);restored.Validate();
            Require(MonitoringTargets.Selected(restored) is [var target] && target.Name=="Custom service" && target.Url==config.AdditionalTargets[0].Url,
                "An enabled custom service was not selected.");
        });
        Check("disabled custom services do not satisfy minimum monitoring selection",()=>
        {
            var config=Config(false,false);config.AdditionalTargets=[Custom(enabled:false)];Reject(config.Validate);
        });
        Check("monitoring supports six custom services and rejects a seventh",()=>
        {
            var config=Config(true,true);
            config.AdditionalTargets=Enumerable.Range(1,6).Select(i=>Custom("Service "+i,$"https://service{i}.example/")).ToList();
            config.Validate();Require(MonitoringTargets.Selected(config).Length==8,"Six custom services were not selected alongside the built-ins.");
            config.AdditionalTargets.Add(Custom("Seventh","https://seventh.example/"));Reject(config.Validate);
        });
        Check("monitoring rejects malformed or unsafe custom HTTPS URLs",()=>
        {
            foreach(var url in new[] {"", "http://service.example/", "https://127.0.0.1/", "https://[::1]/", "https://user:secret@service.example/",
                "https://service.example/?token=test", "https://service.example/#fragment", "https://service.example/%zz", "https://service.example/%2", "https://service.example/bad path"})
            {
                var config=Config(false,false);config.AdditionalTargets=[Custom(url:url)];Reject(config.Validate);
            }
        });
        Check("monitoring applies the same URL rules to the editable ChatGPT endpoint",()=>
        {
            var config=Config(true,false);config.ProbeUrl="https://chatgpt.com/%broken";Reject(config.Validate);
        });
        Check("monitoring rejects duplicate custom IDs names and URLs",()=>
        {
            var first=Custom();
            var duplicateId=Custom("Other service","https://other.example/");duplicateId.Id=Guid.Parse(first.Id).ToString("D").ToUpperInvariant();
            var cases=new[] {duplicateId,Custom(" custom service ","https://other.example/"),Custom("Other service","https://SERVICE.example:443/status")};
            foreach(var duplicate in cases)
            {
                var config=Config(true,false);config.AdditionalTargets=[first,duplicate];Reject(config.Validate);
            }
        });
        Check("monitoring reserves built-in identities and endpoints even when unchecked",()=>
        {
            foreach(var name in new[] {"ChatGPT", "chatgpt / codex", "Codex", " GEMINI "})
            {
                var config=Config(false,false);config.AdditionalTargets=[Custom(name)];Reject(config.Validate);
            }
            foreach(var id in new[] {MonitoringTargets.ChatGpt,MonitoringTargets.Gemini,"bad-id"})
            {
                var config=Config(false,false);var target=Custom();target.Id=id;config.AdditionalTargets=[target];Reject(config.Validate);
            }
            foreach(var url in new[] {MonitoringTargets.DefaultChatGptUrl,MonitoringTargets.GeminiUrl})
            {
                var config=Config(false,false);config.AdditionalTargets=[Custom(url:url)];Reject(config.Validate);
            }
        });
        Check("monitoring rejects null or unnamed custom records",()=>
        {
            var config=Config(true,false);config.AdditionalTargets=null!;Reject(config.Validate);
            config.AdditionalTargets=[null!];Reject(config.Validate);
            foreach(var name in new[] {"", "   ",new string('x',101)}) {config.AdditionalTargets=[Custom(name)];Reject(config.Validate);}
        });
        Check("aggregate requires every selected service to pass",()=>
        {
            var config=Config(true,true);
            var success=MonitoringTargets.Combine("profile",config,[Service(MonitoringTargets.ChatGpt),Service(MonitoringTargets.Gemini)]);
            Require(success.Success && success.Services.Count==2 && success.DnsMs==24 && success.HttpsMs==80,"Complete successful checks did not aggregate.");
            var failed=MonitoringTargets.Combine("profile",config,[Service(MonitoringTargets.ChatGpt),Service(MonitoringTargets.Gemini,false,403)]);
            Require(!failed.Success && failed.Stage=="gemini/http" && failed.Services[1].HttpStatus==403,"A blocked Gemini response became a healthy channel.");
            Require(failed.Detail.Contains("Gemini") && failed.Detail.Contains("403"),"The blocked service lost its result detail.");
        });
        Check("missing duplicate and unexpected service results cannot form a complete aggregate",()=>
        {
            var config=Config(true,true);
            foreach(var results in new[] {Array.Empty<ServiceProbeResult>(),[Service(MonitoringTargets.ChatGpt)],
                [Service(MonitoringTargets.ChatGpt),Service(MonitoringTargets.ChatGpt)],
                [Service(MonitoringTargets.ChatGpt),Service("unexpected")]})
            {
                var aggregate=MonitoringTargets.Combine("profile",config,results);
                Require(!aggregate.Success && aggregate.Stage=="incomplete","An incomplete aggregate was reported as successful.");
                Require(aggregate.Services.Count==results.Length,"Raw per-service results were discarded.");
            }
            Require(!MonitoringTargets.Combine("profile",Config(false,false),[]).Success,"An empty target list produced a successful aggregate.");
        });
        Check("custom result detail uses the configured service name",()=>
        {
            var config=Config(false,false);var custom=Custom("My service");config.AdditionalTargets=[custom];
            var aggregate=MonitoringTargets.Combine("profile",config,[Service(custom.Id)]);
            Require(aggregate.Detail.StartsWith("My service:") && !aggregate.Detail.Contains(custom.Id),"The custom service was displayed as an opaque GUID.");
            Require(MonitoringTargets.Service(aggregate,custom.Id)?.HttpStatus==200,"The custom per-service result was not preserved.");
        });
        Check("legacy ChatGPT results expose only the ChatGPT service",()=>
        {
            var legacy=new ProbeResult {Success=true,HttpStatus=200,DnsMs=17,HttpsMs=90};
            Require(MonitoringTargets.Service(legacy,MonitoringTargets.ChatGpt) is {Success:true,HttpStatus:200,DnsMs:17},"Legacy ChatGPT results were lost.");
            Require(MonitoringTargets.Service(legacy,MonitoringTargets.Gemini) is null,"A legacy ChatGPT result was relabelled as Gemini.");
        });
        Check("monitoring key ignores labels and ordering but includes identity and URL",()=>
        {
            var config=Config(true,true);config.AdditionalTargets=[Custom("First","https://first.example/"),Custom("Second","https://second.example/")];
            var key=MonitoringTargets.Key(config);
            config.AdditionalTargets[0].Name="Renamed";config.AdditionalTargets.Reverse();
            Require(MonitoringTargets.Key(config)==key,"Renaming or reordering services invalidated equivalent measurements.");
            config.AdditionalTargets[0].Url="https://changed.example/";
            Require(MonitoringTargets.Key(config)!=key,"A different endpoint kept the old context key.");
            config.AdditionalTargets[0].Url="https://second.example/";config.AdditionalTargets[0].Id=Guid.NewGuid().ToString("N");
            Require(MonitoringTargets.Key(config)!=key,"A different service identity kept the old context key.");
        });
        Check("changed selection does not reuse ChatGPT-only evidence and preserves history",()=>
        {
            var oldConfig=Config(true,false);var current=Config(true,true);
            var stats=new ProfileStats {ProfileId="profile"};
            stats.Add(new ProbeResult {ProfileId="profile",Success=true});
            stats.Add(MonitoringTargets.Combine("profile",oldConfig,[Service(MonitoringTargets.ChatGpt)]));
            var filtered=MonitoringTargets.View(stats,current)!;
            Require(filtered.Samples.Count==0 && filtered.ConsecutiveSuccesses==0,"A new selection reused old successful evidence.");
            Require(stats.Samples.Count==2 && stats.ConsecutiveSuccesses==1,"Filtering mutated the stored history.");
            stats.Add(MonitoringTargets.Combine("profile",current,[Service(MonitoringTargets.ChatGpt),Service(MonitoringTargets.Gemini)]));
            Require(stats.Samples.Count==3 && stats.ConsecutiveSuccesses==1 && MonitoringTargets.View(stats,current)!.Samples.Count==1,
                "The new monitoring context did not restart its streak while retaining history.");
            var customOnly=Config(false,false);customOnly.AdditionalTargets=[Custom()];
            Require(!MonitoringTargets.Matches(stats.Samples[0],customOnly),"Legacy ChatGPT evidence matched custom-only monitoring.");
            oldConfig.ProbeUrl="https://different.example/";
            Require(!MonitoringTargets.Matches(stats.Samples[0],oldConfig),"An edited ChatGPT endpoint reused unknown legacy evidence.");
        });
        Check("freshness covers a bounded round of all selected services",()=>
        {
            var config=Config(true,true);config.IntervalSeconds=10;config.TimeoutSeconds=30;
            config.AdditionalTargets=Enumerable.Range(1,6).Select(i=>Custom("Service "+i,$"https://service{i}.example/")).ToList();
            Require(MonitoringTargets.FreshnessSeconds(config)==190,"The round freshness does not cover 24 probes in four slots and the interval.");
            config.Profiles[0].Enabled=false;
            Require(MonitoringTargets.FreshnessSeconds(config)==130,"Disabled channels inflated the round freshness.");
            config=Config(true,false);Require(MonitoringTargets.FreshnessSeconds(config)==60,"Default freshness is no longer at least 60 seconds.");
            config.IntervalSeconds=50;Require(MonitoringTargets.FreshnessSeconds(config)==150,"The existing three-interval freshness floor was lost.");
        });
        Check("freshness handles no enabled channels and the maximum supported round",()=>
        {
            var config=Config(true,true);config.IntervalSeconds=10;config.TimeoutSeconds=30;
            config.Profiles=Enumerable.Range(1,32).Select(i=>new ChannelProfile {Name="Channel "+i,Servers=[$"198.51.100.{i}"]}).ToList();
            config.AdditionalTargets=Enumerable.Range(1,6).Select(i=>Custom("Service "+i,$"https://service{i}.example/")).ToList();
            config.Validate();
            Require(MonitoringTargets.FreshnessSeconds(config)==1930,"The maximum 256-request round is not covered.");
            foreach(var profile in config.Profiles) profile.Enabled=false;
            Require(MonitoringTargets.FreshnessSeconds(config)==60,"A round with no enabled channels lost the 60-second floor.");
            config.Profiles=[];
            Require(MonitoringTargets.FreshnessSeconds(config)==60,"An empty UI draft could not calculate freshness.");
        });
        await CheckAsync("coordinator checks only enabled channels and enabled custom targets",async()=>
        {
            var config=Config(false,false);var enabled=Custom();config.AdditionalTargets=[enabled,Custom("Disabled","https://disabled.example/",false)];
            config.Profiles[1].Enabled=false;config.Profiles[2].Enabled=false;
            var calls=new List<(string Profile,string Url)>();
            var results=await MonitoringTargets.CheckAsync(config,(profile,url,timeout,token)=>
            {
                calls.Add((profile.Id,url));
                Require(timeout==TimeSpan.FromSeconds(config.TimeoutSeconds),"The configured timeout was not forwarded.");
                return Task.FromResult(new ProbeResult {ProfileId=profile.Id,Success=true,HttpStatus=200,DnsMs=7,HttpsMs=21,Detail="Checked custom service"});
            },CancellationToken.None);
            Require(calls.Count==1 && calls[0]==(config.Profiles[0].Id,enabled.Url),"Unchecked services or disabled channels were probed.");
            Require(results is [{Success:true}] && results[0].Services is [{ServiceId:var id,DnsMs:7,HttpsMs:21}] && id==enabled.Id,
                "The selected custom service result was not retained.");
        });
        await CheckAsync("coordinator applies a single four-request concurrency limit across services",async()=>
        {
            var config=Config(true,true);config.AdditionalTargets=[Custom()];
            var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var saturated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active=0;var maximum=0;var calls=0;
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var run=MonitoringTargets.CheckAsync(config,async(profile,url,timeout,token)=>
            {
                var current=Interlocked.Increment(ref active);Interlocked.Increment(ref calls);
                lock(release) {maximum=Math.Max(maximum,current);if(current==4) saturated.TrySetResult();}
                try {await release.Task.WaitAsync(token);return new ProbeResult {ProfileId=profile.Id,Success=true,HttpStatus=200};}
                finally {Interlocked.Decrement(ref active);}
            },stop.Token);
            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Require(calls==4 && active==4,"The coordinator exceeded four in-flight requests while the first four were blocked.");
            release.SetResult();var results=await run;
            Require(maximum==4 && calls==9 && active==0,"Service pairs bypassed the shared four-request limit or were skipped.");
            Require(results.Count==3 && results.All(r=>r.Success && r.Services.Count==3),"The complete round did not retain every per-service check.");
        });
        await CheckAsync("cancellation stops active and queued monitoring requests",async()=>
        {
            var config=Config(true,true);
            var saturated=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var active=0;var calls=0;
            using var stop=new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var run=MonitoringTargets.CheckAsync(config,async(profile,url,timeout,token)=>
            {
                Interlocked.Increment(ref active);if(Interlocked.Increment(ref calls)==4) saturated.TrySetResult();
                try {await Task.Delay(Timeout.Infinite,token);return new ProbeResult();}
                finally {Interlocked.Decrement(ref active);}
            },stop.Token);
            await saturated.Task.WaitAsync(TimeSpan.FromSeconds(2));stop.Cancel();
            try {await run;throw new Exception("Cancellation was swallowed.");} catch(OperationCanceledException) { }
            Require(calls==4 && active==0,"Cancellation started queued work or left an active request behind.");
        });
        await CheckAsync("an already cancelled round performs no network work",async()=>
        {
            var calls=0;using var stop=new CancellationTokenSource();stop.Cancel();
            try
            {
                await MonitoringTargets.CheckAsync(Config(true,true),(profile,url,timeout,token)=>{Interlocked.Increment(ref calls);return Task.FromResult(new ProbeResult());},stop.Token);
                throw new Exception("An already cancelled round completed successfully.");
            }
            catch(OperationCanceledException) { }
            Require(calls==0,"An already cancelled round invoked the network probe.");
        });
    }
}
