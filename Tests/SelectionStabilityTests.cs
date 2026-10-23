namespace NetPilot.Tests;

public static class SelectionStabilityTests
{
    public static void Run(Action<string,bool,string> report)
    {
        void Check(string name,Action test)
        {try{test();report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}}
        static void Require(bool condition,string message){if(!condition)throw new Exception(message);}
        var now=new DateTimeOffset(2026,10,22,12,0,0,TimeSpan.Zero);
        var config=AppConfig.Defaults();var key=MonitoringTargets.Key(config);
        string a=config.Profiles[0].Id,b=config.Profiles[1].Id,c=config.Profiles[2].Id;
        ProfileStats Stats(string id,bool[] successes,double ms=100,int gapSeconds=20)
        {
            var value=new ProfileStats {ProfileId=id};
            for(int i=0;i<successes.Length;i++)value.Add(new ProbeResult {ProfileId=id,MonitoringKey=key,
                At=now.AddSeconds(-(successes.Length-1-i)*gapSeconds),Success=successes[i],DnsMs=ms/4,HttpsMs=ms*3/4});
            return value;
        }
        bool[] Successes(int count)=>Enumerable.Repeat(true,count).ToArray();
        ProfileStats Flaky(string id,double ms=100)=>Stats(id,[false,false,true,true,true,true,true,true,true,true],ms);
        SelectionDecision Choose(ProfileStats[] statistics,string? current=null,int dwellSeconds=600,AppConfig? selected=null)
            =>Selection.Choose(selected??config,statistics,current,now.AddSeconds(-dwellSeconds),now);

        Check("initial auto choice ranks observed stability ahead of latency",()=>
        {
            Require(Choose([Flaky(a,20),Stats(b,Successes(5),300)]).ProfileId==b,"A fast flaky channel outranked a measured stable channel at initial choice.");
        });
        Check("healthy auto selection moves to a more stable channel even when it is slower",()=>
        {
            var decision=Choose([Flaky(a),Stats(b,Successes(5),120)],a);
            Require(decision.ProfileId==b&&decision.Reason.Contains("стабил",StringComparison.OrdinalIgnoreCase),
                "A latency requirement prevented a confirmed stability improvement.");
        });
        Check("optimization chooses the highest observed stability among confirmed alternatives",()=>
        {
            var lessStable=Stats(b,[false,true,true,true,true,true,true,true,true,true],30);
            Require(Choose([Flaky(a),lessStable,Stats(c,Successes(5),300)],a).ProfileId==c,
                "A faster but less reliable alternative displaced the most stable confirmed candidate.");
        });
        Check("equal stability still requires the established twenty percent latency improvement",()=>
        {
            Require(Choose([Stats(a,Successes(8)),Stats(b,Successes(5),80)],a).ProfileId==b,"An exact twenty-percent improvement was ignored.");
            Require(Choose([Stats(a,Successes(8)),Stats(b,Successes(5),81)],a).ProfileId==a,"A smaller latency gain caused a switch with equal stability.");
        });
        Check("faster but less stable alternatives never win normal optimization",()=>
        {
            Require(Choose([Stats(a,Successes(8)),Flaky(b,5)],a).ProfileId==a,"Latency was prioritized over measured reliability.");
        });
        Check("stability optimization honors the five minute minimum dwell",()=>
        {
            Require(Choose([Flaky(a),Stats(b,Successes(5),120)],a,299).ProfileId==a,"A reliability comparison bypassed anti-oscillation dwell.");
            Require(Choose([Flaky(a),Stats(b,Successes(5),120)],a,300).ProfileId==b,"A confirmed stable channel was not eligible at the dwell boundary.");
        });
        Check("stability optimization requires five consecutive successful rounds",()=>
        {
            Require(Choose([Flaky(a),Stats(b,Successes(4),120)],a).ProfileId==a,"Four successes were treated as five confirmed rounds.");
            Require(Choose([Flaky(a),Stats(b,[true,false,true,true,true,true],120)],a).ProfileId==a,
                "A recent failed round was ignored when counting the candidate streak.");
        });
        Check("regular successful rounds remain eligible when the oldest is outside latest-result freshness",()=>
        {
            var candidate=Stats(b,Successes(5),120);
            Require(now-candidate.Samples[0].At>TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config)),"The cadence fixture did not span beyond latest-result freshness.");
            Require(Choose([Flaky(a),candidate],a).ProfileId==b,"Requiring all five timestamps inside one freshness window made normal optimization impossible.");
        });
        Check("a new success after a monitoring gap cannot reuse an old optimization streak",()=>
        {
            var candidate=Stats(b,Successes(5),120);
            foreach(var sample in candidate.Samples.Take(4))sample.At=sample.At.AddHours(-1);
            Require(Choose([Flaky(a),candidate],a).ProfileId==a,"Old pre-gap successes were counted toward a fresh candidate confirmation.");
        });
        Check("future or nonmonotonic samples cannot complete a stability streak",()=>
        {
            foreach(bool future in new[] {true,false})
            {
                var candidate=Stats(b,Successes(5),120);
                candidate.Samples[1].At=future?now.AddSeconds(1):candidate.Samples[2].At.AddSeconds(1);
                Require(Choose([Flaky(a),candidate],a).ProfileId==a,"A future or backward timestamp counted toward the five-round confirmation.");
            }
        });
        Check("streak counters cannot substitute for missing measured samples",()=>
        {
            var candidate=Stats(b,Successes(1),120);candidate.ConsecutiveSuccesses=5;
            Require(Choose([Flaky(a),candidate],a).ProfileId==a,"A stale counter fabricated four unrecorded successful rounds.");
        });
        Check("one and two current failures hold the channel while the third triggers emergency failover",()=>
        {
            var candidate=Stats(b,Successes(5),300);
            for(int failures=1;failures<=3;failures++)
            {
                var active=Stats(a,Successes(5).Concat(Enumerable.Repeat(false,failures)).ToArray());
                Require(Choose([active,candidate],a,1).ProfileId==(failures<3?a:b),"The three-failure emergency threshold or dwell bypass changed.");
            }
        });
        Check("emergency failover ranks reliability first and needs two fresh adjacent successful rounds",()=>
        {
            var active=Stats(a,[true,false,false,false]);var stable=Stats(b,Successes(2),300);
            Require(Choose([active,stable,Flaky(c,10)],a,1).ProfileId==b,"An emergency alternative was ranked only by speed.");
            stable.Samples[0].At=now.AddHours(-1);
            Require(Choose([active,stable],a,1).ProfileId==a,"A single new response after a long gap counted as two fresh reserve successes.");
            stable.Samples[0].At=now.AddSeconds(-20);
            Require(Choose([active,stable],a,1).ProfileId==b,"Two valid adjacent reserve rounds no longer permit emergency failover.");
        });
        Check("disabled stale future and other-context alternatives remain ineligible",()=>
        {
            foreach(string kind in new[] {"disabled","stale","future","context"})
            {
                var selected=JsonData.Clone(config);var candidate=Stats(b,Successes(5),120);
                if(kind=="disabled")selected.Profiles[1].Enabled=false;
                if(kind=="stale")foreach(var sample in candidate.Samples)sample.At=sample.At.AddHours(-1);
                if(kind=="future")candidate.Samples[^1].At=now.AddSeconds(1);
                if(kind=="context")foreach(var sample in candidate.Samples)sample.MonitoringKey="another-context";
                Require(Choose([Flaky(a),candidate],a,selected:selected).ProfileId==a,"Ineligible "+kind+" evidence caused a stability switch.");
            }
        });
        Check("an incomplete aggregate cannot become a stable channel",()=>
        {
            var selected=JsonData.Clone(config);selected.MonitorGemini=true;var active=Flaky(a);
            foreach(var sample in active.Samples)sample.MonitoringKey=MonitoringTargets.Key(selected);
            var incomplete=new ProfileStats {ProfileId=b};
            for(int i=0;i<5;i++)incomplete.Add(MonitoringTargets.Combine(b,selected,[new ServiceProbeResult
                {ServiceId=MonitoringTargets.ChatGpt,At=now.AddSeconds(-(4-i)*20),Success=true,HttpStatus=200}]));
            Require(!incomplete.Last!.Success&&Choose([active,incomplete],a,selected:selected).ProfileId==a,
                "Missing selected-service checks were promoted into stable reachability.");
        });
        Check("explicit preference and fixation keep their documented override semantics",()=>
        {
            var selected=JsonData.Clone(config);selected.PinnedProfileId=a;
            Require(Choose([Flaky(a),Stats(b,Successes(5),120)],a,selected:selected).ProfileId==a,
                "Automatic stability optimization overrode an available explicit preferred channel.");
            selected.ManualProfileId=a;
            Require(Choose([Stats(a,[false,false,false]),Stats(b,Successes(5),120)],a,selected:selected).ProfileId==a,
                "Strict manual fixation silently failed over.");
            selected.ManualUntil=now;selected.PinnedProfileId=null;
            Require(Choose([Stats(a,[false,false,false]),Stats(b,Successes(5),120)],a,selected:selected).ProfileId==b,
                "Expired manual fixation prevented automatic recovery.");
        });
        Check("stability comparison does not mutate raw samples counters or context",()=>
        {
            var statistics=new[] {Flaky(a),Stats(b,Successes(5),120)};var before=JsonData.Encode(statistics);
            _=Choose(statistics,a);Require(JsonData.Encode(statistics)==before,"Selection altered the evidence used by persistence or charts.");
        });
    }
}
