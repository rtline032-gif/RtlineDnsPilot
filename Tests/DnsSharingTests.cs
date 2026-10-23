using System.Net;
using System.Text;

namespace NetPilot.Tests;

public static class DnsSharingTests
{
    public static void Run(Action<string,bool,string> report)
    {
        void Check(string name, Action test) { try { test(); report(name,true,""); } catch(Exception e) { report(name,false,e.Message); } }
        static void Require(bool condition,string reason) { if(!condition) throw new Exception(reason); }
        static void Reject(Action action) { try { action(); } catch(ArgumentException) {return;} throw new Exception("Invalid shared DNS was accepted."); }
        var now=DateTimeOffset.Parse("2026-10-22T12:00:00Z");
        AppConfig Config() => new() {Profiles=[new ChannelProfile {Name="PRIVATE owner and machine",Servers=["8.8.8.8"],Note="PRIVATE note",Mode=DnsMode.Udp}]};
        ProfileStats Stats(AppConfig c, params ProbeResult[] samples) => new() {ProfileId=c.Profiles[0].Id,Samples=samples.ToList()};
        ProbeResult Sample(AppConfig c,bool success=true,string? key=null) => new() {ProfileId=c.Profiles[0].Id,At=now,Success=success,
            MonitoringKey=key??MonitoringTargets.Key(c),Services=[new ServiceProbeResult {ServiceId=MonitoringTargets.ChatGpt,At=now,Success=success}]};
        DnsShareCandidate Candidate(string ip="8.8.8.8") => new() {Mode=DnsMode.Udp,Servers=[ip],WorkedServices=["chatgpt"]};
        Check("sharing excludes all 15 built-in effective configurations",()=>
        {
            var bases=AppConfig.Defaults().Profiles.Concat(BundledChannels.Additions()).ToArray();
            Require(bases.Length==15,"Bundled baseline changed without receiver update.");
            foreach(var p in bases)
            {
                var c=Config(); c.Profiles=[JsonData.Clone(p)];c.Profiles[0].Id=Guid.NewGuid().ToString("N");c.Profiles[0].Name="Renamed";
                c.Profiles[0].Servers=c.Profiles[0].Servers.Reverse().ToArray();
                Require(DnsSharing.Select(c,[Stats(c,Sample(c))],now).Profiles.Count==0,"Built-in escaped by ID/name/address order: "+p.Name);
            }
        });
        Check("sharing sends only public configuration and worked official sites",()=>
        {
            var c=Config();var sample=Sample(c,false); sample.Services.Add(new() {ServiceId=MonitoringTargets.Gemini,At=now,Success=true,Detail="PRIVATE detail",CountryCode="NL"});
            var value=DnsSharing.Select(c,[Stats(c,sample)],now);
            Require(value.Profiles.Count==1 && value.Profiles[0].WorkedServices.SequenceEqual(["gemini"]),"A successful individual official target was ignored.");
            var json=JsonData.Encode(value.Profiles);
            foreach(var forbidden in new[]{"PRIVATE",c.Profiles[0].Id,"Name","Note","Country","At","Detail","ProfileId","MonitoringKey"})
                Require(!json.Contains(forbidden,StringComparison.Ordinal),"Local evidence/private field leaked: "+forbidden);
        });
        Check("sharing rejects stale future missing or projected success evidence",()=>
        {
            var c=Config();
            foreach(var kind in new[]{"stale","future","projected","failed","mismatch"})
            {
                var s=Sample(c);
                if(kind=="stale") s.At=s.Services[0].At=now.AddHours(-24).AddTicks(-1);
                if(kind=="future") s.At=s.Services[0].At=now.AddTicks(1);
                if(kind=="projected") s.ServiceDetailsOmitted=true;
                if(kind=="failed") s.Services[0].Success=false;
                if(kind=="mismatch") s.ProfileId=Guid.NewGuid().ToString("N");
                Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==0,"Unproven evidence accepted: "+kind);
            }
            Require(DnsSharing.Select(c,[],now).Profiles.Count==0,"Unmeasured profile offered.");
            var boundary=Sample(c);boundary.At=boundary.Services[0].At=now.AddHours(-24);
            Require(DnsSharing.Select(c,[Stats(c,boundary)],now).Profiles.Count==1,"The inclusive 24h boundary was rejected.");
        });
        Check("sharing checks each site timestamp and ignores custom successes",()=>
        {
            var c=Config();var s=Sample(c);s.Services[0].At=now.AddHours(-25);
            s.Services.Add(new() {ServiceId=Guid.NewGuid().ToString("N"),At=now,Success=true});
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==0,"Stale official or custom success was promoted.");
        });
        Check("sharing validates ChatGPT provenance and fixed Gemini historical evidence",()=>
        {
            var c=Config();c.ProbeUrl="https://private.example.org/check";var s=Sample(c);
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==0,"A custom URL labeled ChatGPT was shared as official evidence.");
            s.MonitoringKey=MonitoringTargets.Key(new AppConfig());
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==1,"Known default ChatGPT context was lost.");
            s.MonitoringKey="unrecognized-context";s.Services=[new() {ServiceId=MonitoringTargets.Gemini,At=now,Success=true}];
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles[0].WorkedServices.SequenceEqual(["gemini"]),"Fixed official Gemini was lost after context change.");
            c=Config();c.ProbeUrl="https://chatgpt.com/";s=Sample(c);
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==1,"Current official ChatGPT URL context was rejected.");
            c.ProbeUrl="https://chatgpt.com.attacker.example.org/check";s=Sample(c);
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==0,"A suffix lookalike was official.");
        });
        Check("sharing accepts legacy aggregate only in known default GPT context",()=>
        {
            var c=Config();var s=Sample(c);s.Services=[];s.MonitoringKey="";
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==1,"Legacy default GPT evidence lost.");
            c.MonitorGemini=true;
            Require(DnsSharing.Select(c,[Stats(c,s)],now).Profiles.Count==0,"Unknown legacy aggregate assumed to include selected Gemini.");
        });
        Check("sharing excludes private reserved documentation and transition IP ranges",()=>
        {
            foreach(var value in new[]{"0.1.2.3","10.1.2.3","100.64.0.1","100.127.255.1","127.1.2.3","169.254.1.2","172.16.0.1","172.31.255.1",
                "192.0.0.9","192.0.2.1","192.88.99.1","192.168.1.1","198.18.1.1","198.19.255.1","198.51.100.1","203.0.113.1","224.1.2.3","255.255.255.255",
                "::1","::ffff:8.8.8.8","fe80::1","fc00::1","ff02::1","2001::1","2001:2::1","2001:db8::1","2002::1","3fff::1"})
                Reject(()=>DnsSharing.NormalizeCandidate(Candidate(value)));
            foreach(var value in new[]{"8.8.8.8","1.1.1.1","100.63.255.1","100.128.0.1","172.15.0.1","172.32.0.1","2001:4860:4860::8888","2606:4700:4700::1111"})
                DnsSharing.NormalizeCandidate(Candidate(value));
        });
        Check("sharing canonicalizes IPv6 and stable configuration keys",()=>
        {
            var p=Candidate("2001:4860:4860::8888");p.Servers=["2001:4860:4860::8888","8.8.8.8"];
            var key=DnsSharing.Key(p);var canonical=DnsSharing.NormalizeCandidate(p);
            Require(canonical.Servers.Contains("2001:4860:4860:0000:0000:0000:0000:8888"),"IPv6 wire form is not strict eight-hextet.");
            p.Servers=p.Servers.Reverse().ToArray();p.WorkedServices=["gemini"];
            Require(DnsSharing.Key(p)==key,"Settings key depends on evidence/address order.");
            p.DnsPort=5353;Require(DnsSharing.Key(p)!=key,"UDP port missing from dedupe identity.");
        });
        Check("sharing candidate schema rejects duplicate or unsupported evidence",()=>
        {
            var p=Candidate();p.Servers=["8.8.8.8","8.8.8.8"];Reject(()=>DnsSharing.NormalizeCandidate(p));
            p=Candidate();p.WorkedServices=["chatgpt","chatgpt"];Reject(()=>DnsSharing.NormalizeCandidate(p));
            p=Candidate();p.WorkedServices=["tailscale"];Reject(()=>DnsSharing.NormalizeCandidate(p));
            p=Candidate();p.Mode=(DnsMode)123;Reject(()=>DnsSharing.NormalizeCandidate(p));
            p=Candidate();p.DohUrl="https://dns.google/dns-query";Reject(()=>DnsSharing.NormalizeCandidate(p));
        });
        Check("sharing validates DoH and canonicalizes irrelevant DoH port",()=>
        {
            var p=Candidate();p.Mode=DnsMode.Doh;p.DohUrl="https://DNS.GOOGLE:443/dns-query";p.DnsPort=9953;
            var normalized=DnsSharing.NormalizeCandidate(p);
            Require(normalized.DohUrl=="https://dns.google/dns-query" && normalized.DnsPort==53,"DoH canonical settings differ from receiver protocol.");
            foreach(var value in new[]{"http://dns.google/dns-query","https://user:pass@dns.google/dns-query","https://dns.google/dns-query?token=secret",
                "https://dns.google/dns-query#secret","https://127.0.0.1/dns-query","https://machine.local/dns-query"})
            {p.DohUrl=value;Reject(()=>DnsSharing.NormalizeCandidate(p));}
        });
        Check("sharing merges duplicate settings and leaves original config statistics untouched",()=>
        {
            var c=Config();var p=JsonData.Clone(c.Profiles[0]);p.Id=Guid.NewGuid().ToString("N");c.Profiles.Add(p);
            var a=Sample(c);var b=Sample(c);b.ProfileId=p.Id;b.Services=[new() {ServiceId="gemini",At=now,Success=true}];
            var stats=new[]{Stats(c,a),new ProfileStats {ProfileId=p.Id,Samples=[b]}};
            var before=JsonData.Encode(new {c,stats});var offer=DnsSharing.Select(c,stats,now);
            Require(offer.Profiles.Count==1 && offer.Profiles[0].WorkedServices.SequenceEqual(["chatgpt","gemini"]),"Duplicate settings/evidence were not merged.");
            offer.Profiles[0].Servers[0]="1.1.1.1";
            Require(before==JsonData.Encode(new {c,stats}),"Offering/upload preview mutated original configuration or statistics.");
        });
        Check("sharing maximum whitelisted offer fits bounded POST",()=>
        {
            var list=Enumerable.Range(1,32).Select(i=>new DnsShareCandidate {Mode=DnsMode.Doh,Servers=Enumerable.Range(1,8).Select(n=>"8.8."+i+"."+n).ToArray(),
                DohUrl="https://dns.google/"+new string('a',2000),WorkedServices=["chatgpt","gemini"]}).ToList();
            var json=JsonData.Encode(new {Protocol=1,InviteCode=new string('a',64),Profiles=list});
            Require(Encoding.UTF8.GetByteCount(json)<96*1024,"Maximum valid shared profile payload exceeds receiver body cap.");
        });
    }
}
