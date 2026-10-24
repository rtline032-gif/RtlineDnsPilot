namespace NetPilot.Tests;

public static class CoreTests
{
    public static async Task<int> Main(string[] args)
    {
        int failures = 0;
        void Report(string name,bool ok,string message) { Console.WriteLine((ok ? "PASS " : "FAIL ") + name + (message.Length == 0 ? "" : ": " + message)); if (!ok) failures++; }
        if(args.Contains("--rpc-only")) { await RpcTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--transactions-only")) { await TransactionTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--dns-recovery-only")) { await TransactionTests.RunAsync(Report); await DnsRecoveryCommandTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--application-update-only")) { await ApplicationUpdateClientTests.RunAsync(Report); await ApplicationUpdateLauncherTests.RunAsync(Report); await ApplicationUpdateUiTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--ui-sync-only")) { await UiPersistenceTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--monitoring-only")) { await MonitoringTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--manual-only")) { await ManualSelectionTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--automatic-dns-only")) { await AutomaticDnsTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--monitoring-control-only")) { await MonitoringControlTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--connection-only")) { await ServiceConnectionTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--rpc-projection-only")) { RpcProjectionTests.Run(Report); return failures==0?0:1; }
        if(args.Contains("--country-only")) { await CountryTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--history-only")) { ChartHistoryTests.Run(Report); await ChartHistoryIntegrationTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--sharing-integration-only")) { await SharingIntegrationTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--tailscale-only")) { await TailscaleMonitoringTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--sharing-only")) { DnsSharingTests.Run(Report); await DnsShareClientTests.RunAsync(Report); await SharingIntegrationTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--selection-stability-only")) { SelectionStabilityTests.Run(Report); return failures==0?0:1; }
        if(args.Contains("--auto-mode-only")) { await AutoSelectionModeTests.RunAsync(Report); return failures==0?0:1; }
        if(args.Contains("--one-click-only")) { await OneClickDnsShareTests.RunAsync(Report); return failures==0?0:1; }
        Run(Report);
        await TransactionTests.RunAsync(Report);
        await DnsRecoveryCommandTests.RunAsync(Report);
        await ProtocolTests.RunAsync(Report);
        await EngineTests.RunAsync(Report);
        InstallSafetyTests.Run(Report);
        await RpcTests.RunAsync(Report);
        ProfileConfigurationTests.Run(Report);
        await UiPersistenceTests.RunAsync(Report);
        ServiceUpgradeTests.Run(Report);
        await BundledChannelTests.RunAsync(Report);
        await MonitoringTests.RunAsync(Report);
        await ManualSelectionTests.RunAsync(Report);
        await AutomaticDnsTests.RunAsync(Report);
        BrandingUpgradeTests.Run(Report);
        await MonitoringControlTests.RunAsync(Report);
        await ServiceConnectionTests.RunAsync(Report);
        RpcProjectionTests.Run(Report);
        await CountryTests.RunAsync(Report);
        ChartHistoryTests.Run(Report);
        await ChartHistoryIntegrationTests.RunAsync(Report);
        await SharingIntegrationTests.RunAsync(Report);
        await TailscaleMonitoringTests.RunAsync(Report);
        DnsSharingTests.Run(Report);
        await DnsShareClientTests.RunAsync(Report);
        SelectionStabilityTests.Run(Report);
        await AutoSelectionModeTests.RunAsync(Report);
        await OneClickDnsShareTests.RunAsync(Report);
        await ApplicationUpdateClientTests.RunAsync(Report);
        await ApplicationUpdateLauncherTests.RunAsync(Report);
        await ApplicationUpdateUiTests.RunAsync(Report);
        return failures == 0 ? 0 : 1;
    }
    public static void Run(Action<string,bool,string> report)
    {
        void Check(string name, Action test) { try { test(); report(name,true,""); } catch(Exception e) { report(name,false,e.Message); } }
        void Require(bool v, string reason) { if(!v) throw new Exception(reason); }
        void Reject(Action f) { try { f(); } catch(ArgumentException) { return; } throw new Exception("Invalid input was accepted."); }
        var config = AppConfig.Defaults();
        Check("default provider profiles validate", config.Validate);
        Check("DoH rejects plaintext endpoint", () => { var p=JsonData.Clone(config.Profiles[0]); p.DohUrl="http://xbox-dns.ru/dns-query"; Reject(p.Validate); });
        Check("DNS recursion to loopback is rejected", () => { var p=JsonData.Clone(config.Profiles[0]); p.Servers=["127.0.0.1"]; Reject(p.Validate); });
        Check("duplicate profile IDs rejected", () => { var c=JsonData.Clone(config); c.Profiles[1].Id=c.Profiles[0].Id; Reject(c.Validate); });
        var now = DateTimeOffset.UtcNow;
        ProfileStats Stats(string id,int successes,double ms,int failures=0) { var s=new ProfileStats { ProfileId=id }; for(int i=0;i<successes;i++) s.Add(new ProbeResult { ProfileId=id,At=now,Success=true,HttpsMs=ms }); for(int i=0;i<failures;i++) s.Add(new ProbeResult { ProfileId=id,At=now,Success=false,Detail="timeout" }); return s; }
        var a=config.Profiles[0].Id; var b=config.Profiles[1].Id;
        Check("initial choice requires actual successful measurements", () => Require(Selection.Choose(config,[],null,now,now).ProfileId==null,"Unknown channels selected without evidence."));
        Check("initial healthy profile selected", () => Require(Selection.Choose(config,[Stats(a,1,90)],null,now,now).ProfileId==a,"No healthy initial selection."));
        Check("single failure does not flap", () => Require(Selection.Choose(config,[Stats(a,8,100,1),Stats(b,8,70)],a,now.AddMinutes(-10),now).ProfileId==a,"Single failure caused a switch."));
        Check("three failures bypass dwell for healthy alternative", () => Require(Selection.Choose(config,[Stats(a,4,100,3),Stats(b,2,80)],a,now.AddSeconds(-10),now).ProfileId==b,"Emergency failover did not happen."));
        Check("one lucky alternative response insufficient for failover", () => Require(Selection.Choose(config,[Stats(a,2,100,3),Stats(b,1,20)],a,now.AddMinutes(-10),now).ProfileId==a,"Unstable alternative selected."));
        Check("small speed improvement does not switch", () => Require(Selection.Choose(config,[Stats(a,8,100),Stats(b,8,90)],a,now.AddMinutes(-10),now).ProfileId==a,"Small speed improvement switched channel."));
        Check("20 percent improvement switches after five minute dwell", () => Require(Selection.Choose(config,[Stats(a,8,100),Stats(b,8,75)],a,now.AddMinutes(-10),now).ProfileId==b,"Clear measured improvement ignored."));
        Check("dwell blocks speed-only switch", () => Require(Selection.Choose(config,[Stats(a,8,100),Stats(b,8,20)],a,now.AddSeconds(-60),now).ProfileId==a,"Dwell was ignored."));
        Check("stale success is never evidence", () => { var s=Stats(b,8,20); foreach(var x in s.Samples) x.At=now.AddHours(-1); Require(Selection.Choose(config,[Stats(a,4,100,3),s],a,now.AddMinutes(-10),now).ProfileId==a,"Stale alternative selected."); });
        Check("all unavailable reported without destroying selection", () => { var d=Selection.Choose(config,[Stats(a,0,100,3),Stats(b,0,100,3),Stats(config.Profiles[2].Id,0,100,3)],a,now.AddMinutes(-10),now); Require(d.AllUnavailable && d.ProfileId==a,"All-down state lost or active profile discarded."); });
        Check("unknown channel does not prove all are unavailable", () => Require(!Selection.Choose(config,[Stats(a,0,100,3),Stats(b,0,100,3)],a,now,now).AllUnavailable,"Missing evidence called a total outage."));
        Check("preferred profile does not suppress emergency failover", () => { var c=JsonData.Clone(config); c.PinnedProfileId=a; Require(Selection.Choose(c,[Stats(a,4,100,3),Stats(b,2,80)],a,now,now).ProfileId==b,"Preferred profile trapped failed connectivity."); });
        Check("successful-check percentage and sample window", () => { var s=new ProfileStats(); for(int i=0;i<130;i++) s.Add(new ProbeResult { Success=i>=70,HttpsMs=50 }); Require(s.TotalChecks==130 && s.Samples.Count==120 && Math.Abs(s.SuccessPercent-50)<.001,"Window denominator is wrong."); });
    }
}
