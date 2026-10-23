using System.Text;

namespace NetPilot.Tests;

public static class ChartHistoryTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, Action test) { try { test(); report(name, true, ""); } catch (Exception e) { report(name, false, e.Message); } }
        static void Require(bool value, string reason) { if (!value) throw new Exception(reason); }
        static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new Exception("Invalid graph query accepted."); }
        var now = DateTimeOffset.Parse("2026-10-22T15:17:33.1234567Z");
        var config = AppConfig.Defaults();
        var id = config.Profiles[0].Id;
        ProbeResult Sample(DateTimeOffset at, bool success = true, double dns = 10, double https = 90) =>
            new() { ProfileId = id, At = at, Success = success, DnsMs = dns, HttpsMs = https, MonitoringKey = MonitoringTargets.Key(config) };
        ChannelHistorySeries Data(ChartHistoryArchive archive, int period = 60, int bucket = 5) =>
            archive.Query(config, new() { PeriodMinutes = period, BucketMinutes = bucket }, now).Channels[0];

        Check("history queries bound ranges and aligned bar count", () =>
        {
            new HistoryQuery { PeriodMinutes = 1440, BucketMinutes = 5 }.Validate();
            new HistoryQuery { PeriodMinutes = 299, BucketMinutes = 1 }.Validate();
            Reject(() => new HistoryQuery { PeriodMinutes = 300, BucketMinutes = 1 }.Validate());
            Reject(() => new HistoryQuery { PeriodMinutes = 0, BucketMinutes = 1 }.Validate());
            Reject(() => new HistoryQuery { PeriodMinutes = 1441, BucketMinutes = 5 }.Validate());
            Reject(() => new HistoryQuery { PeriodMinutes = 5, BucketMinutes = 6 }.Validate());
            Require(new HistoryQuery() == new HistoryQuery(), "Graph queries do not compare by value.");
        });
        Check("history minute aggregation counts every check and keeps true gaps", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now.AddMinutes(-16)), Sample(now.AddMinutes(-15).AddSeconds(-10), false), Sample(now.AddMinutes(-1))], now);
            var data = Data(archive);
            Require(data.TotalChecks == 3 && data.TotalSuccesses == 2 && data.Bars.Count == 2,
                "Measured checks or empty intervals were fabricated/lost.");
            Require(data.Bars[0].Count == 2 && data.Bars[0].FailedCount == 1 && data.Bars[0].LastSuccess == false,
                "A failed check or latest outcome was lost inside the aggregated bar.");
            Require(data.Bars[1].From - data.Bars[0].To >= TimeSpan.FromMinutes(5), "The unobserved gap was filled.");
        });
        Check("history mean latency and success share are weighted by checks", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now.AddMinutes(-11), true, 2, 8), Sample(now.AddMinutes(-6), true, 20, 80),
                Sample(now.AddMinutes(-6).AddSeconds(1), false, 20, 80), Sample(now.AddMinutes(-6).AddSeconds(2), false, 20, 80)], now);
            var data = Data(archive);
            Require(data.TotalChecks == 4 && data.TotalSuccesses == 2 && data.SuccessPercent == 50,
                "Averaged bucket percentages were used.");
            Require(Math.Abs(data.MeanMs - 77.5) < .0001, "Bucket means were averaged without their measurement weights.");
            Require(data.Bars.Sum(b => b.FailedCount) == 2, "Failed measured durations were omitted.");
        });
        Check("history range boundaries use UTC minutes and clip current partial bar", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now.AddMinutes(-60).AddSeconds(-20)), Sample(now.AddMinutes(-59)), Sample(now)], now);
            var reportValue = archive.Query(config, new() { PeriodMinutes = 60, BucketMinutes = 5 }, now.ToOffset(TimeSpan.FromHours(3)));
            var data = reportValue.Channels[0];
            Require(reportValue.From == DateTimeOffset.Parse("2026-10-22T14:17:00Z") && reportValue.To == now,
                "The actual rounded range was not reported.");
            Require(data.TotalChecks == 3 && data.Bars[0].From == reportValue.From && data.Bars[^1].To == now,
                "Partial first/current bars were not clipped to their report.");
            Require(data.Bars[^1].From == DateTimeOffset.Parse("2026-10-22T15:15:00Z"), "Buckets are not UTC aligned.");
        });
        Check("history minute retention keeps explicit partial oldest minute only", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now.AddHours(-24).AddMinutes(-1)), Sample(now.AddHours(-24).AddSeconds(-20)),
                Sample(now.AddHours(-24).AddSeconds(1)), Sample(now)], now);
            var reportValue = archive.Query(config, new() { PeriodMinutes = 1440, BucketMinutes = 5 }, now);
            Require(reportValue.Channels[0].TotalChecks == 3 && reportValue.From == DateTimeOffset.Parse("2026-10-21T15:17:00Z"),
                "Retention did not expose its minute-rounded boundary.");
            archive.Record(config, [], now.AddMinutes(1));
            Require(archive.Series.Single().Minutes.Count == 1, "The expired oldest minute was retained.");
        });
        Check("history refuses future samples without inflating measured coverage", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now.AddTicks(1)), Sample(now.AddMinutes(1)), Sample(now)], now);
            Require(Data(archive).TotalChecks == 1, "Future measurements were accepted.");
            var before = archive.Query(config, new(), now.AddSeconds(-1));
            Require(before.Channels[0].TotalChecks == 0, "A future current-minute aggregate was used after clock rollback.");
        });
        Check("history separates monitoring contexts and DNS configurations", () =>
        {
            var archive = new ChartHistoryArchive(); archive.Record(config, [Sample(now)], now);
            var alternate = JsonData.Clone(config); alternate.MonitorGemini = true;
            Require(archive.Query(alternate, new(), now).Channels[0].TotalChecks == 0, "Other monitored targets leaked into a report.");
            alternate = JsonData.Clone(config); alternate.Profiles[0].Servers = ["8.8.4.4"];
            Require(archive.Query(alternate, new(), now).Channels[0].TotalChecks == 0, "Old DNS settings leaked into a report.");
            Require(Data(archive).TotalChecks == 1, "Changing the context destroyed the original history.");
            alternate.MonitorGemini = true;
            archive.Record(config, [new ProbeResult { ProfileId = id, At = now, MonitoringKey = MonitoringTargets.Key(alternate) }], now);
            Require(Data(archive).TotalChecks == 1, "A result for another target context was recorded under the current context.");
        });
        Check("history context canonicalizes effective addresses and ignores labels", () =>
        {
            var profile = JsonData.Clone(config.Profiles[0]);
            var key = ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config));
            profile.Servers = profile.Servers.Reverse().ToArray(); profile.Name = "Other"; profile.Note = "Note"; profile.Enabled = false; profile.DnsPort = 9953;
            Require(key == ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config)), "Irrelevant DoH or label changes reset history.");
            profile.DohUrl = "https://other.example/dns-query";
            Require(key != ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config)), "DoH URL changes did not isolate history.");
            profile.Mode = DnsMode.Udp; profile.Servers = ["2001:db8::1"];
            key = ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config));
            profile.Servers = ["2001:0db8:0:0:0:0:0:1"]; profile.DohUrl = "https://ignored.example/dns-query";
            Require(key == ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config)), "Canonical IPv6/plain DNS settings changed context.");
            profile.DnsPort++;
            Require(key != ChartHistoryArchive.ContextKey(profile, MonitoringTargets.Key(config)), "UDP port changes did not isolate history.");
        });
        Check("history first seed is idempotent and leaves raw selection window untouched", () =>
        {
            var stats = new ProfileStats { ProfileId = id };
            stats.Add(Sample(now.AddMinutes(-15))); stats.Add(Sample(now.AddMinutes(-10), false));
            var raw = JsonData.Encode(stats);
            var archive = new ChartHistoryArchive(); archive.Seed(config, [stats], now); archive.Seed(config, [stats], now);
            Require(Data(archive).TotalChecks == 2 && raw == JsonData.Encode(stats), "Seeding replayed samples or mutated selection statistics.");
            archive.Record(config, [], now.AddHours(25)); archive.Seed(config, [stats], now.AddHours(25));
            Require(archive.Series.Count == 0 && archive.InitialSeedCompleted, "An empty retained archive triggered another migration.");
        });
        Check("history seed accepts legacy ChatGPT samples only in the legacy context", () =>
        {
            var stats = new ProfileStats { ProfileId = id, Samples = [new() { ProfileId = id, At = now, Success = true }] };
            var archive = new ChartHistoryArchive(); archive.Seed(config, [stats], now);
            Require(Data(archive).TotalChecks == 1, "Known legacy samples could not be migrated.");
            var alternate = JsonData.Clone(config); alternate.MonitorGemini = true;
            archive = new(); archive.Seed(alternate, [stats], now);
            Require(archive.Query(alternate, new(), now).Channels[0].TotalChecks == 0, "Legacy samples were assumed to include Gemini.");
        });
        Check("history normalizes bad timings but retains failure counts", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, [Sample(now, false, double.NaN, double.PositiveInfinity), Sample(now, true, -3, 5)], now);
            archive.Validate(); var data = Data(archive);
            Require(data.TotalChecks == 2 && data.TotalSuccesses == 1 && data.MeanMs == 2.5, "Invalid durations corrupted or discarded measured outcomes.");
            JsonData.Decode<HistoryReport>(JsonData.Encode(archive.Query(config, new(), now)));
        });
        Check("history loaded documents reject corruption before trimming", () =>
        {
            var archive = new ChartHistoryArchive(); archive.Record(config, [Sample(now)], now);
            var invalid = JsonData.Clone(archive); invalid.Version = 17;
            static void Corrupt(Action action) { try { action(); } catch (InvalidDataException) { return; } throw new Exception("Corrupt archive accepted."); }
            Corrupt(() => invalid.NormalizeLoaded(now));
            invalid = JsonData.Clone(archive); invalid.Series[0].Minutes[0].DnsTotalMs = double.NaN;
            Corrupt(() => invalid.NormalizeLoaded(now.AddHours(25)));
            Require(invalid.Series.Count == 1, "Corruption was silently trimmed into an empty archive.");
            invalid = JsonData.Clone(archive); invalid.Series[0].Minutes.Add(JsonData.Clone(invalid.Series[0].Minutes[0]));
            Corrupt(invalid.Validate);
            invalid = JsonData.Clone(archive); invalid.Series[0].Minutes[0].SuccessCount = 2;
            Corrupt(invalid.Validate);
        });
        Check("history loaded documents require explicit archive and numeric fields", () =>
        {
            static void Incomplete(string json)
            {
                try { JsonData.Decode<ChartHistoryArchive>(json); }
                catch (System.Text.Json.JsonException) { return; }
                throw new Exception("A missing archive/measurement field became valid default data.");
            }
            Incomplete("{}");
            var archive = new ChartHistoryArchive(); archive.Record(config, [Sample(now)], now);
            var json = JsonData.Encode(archive);
            Incomplete(json.Replace("\"n\":1,", "", StringComparison.Ordinal));
        });
        Check("history JSON roundtrip and detached reports preserve all aggregates", () =>
        {
            var archive = new ChartHistoryArchive(); archive.Record(config, [Sample(now.AddMinutes(-6)), Sample(now, false, 25, 75)], now);
            var saved = JsonData.Encode(archive); var loaded = JsonData.Decode<ChartHistoryArchive>(saved); loaded.NormalizeLoaded(now);
            var query = new HistoryQuery(); var result = loaded.Query(config, query, now);
            Require(JsonData.Encode(result) == JsonData.Encode(archive.Query(config, query, now)), "JSON changed aggregate values.");
            query.BucketMinutes = 10; result.Query.PeriodMinutes = 5; result.Channels[0].Bars[0].Count = 77;
            Require(JsonData.Encode(loaded) == saved && loaded.Query(config, new(), now).Channels[0].TotalChecks == 2,
                "The query/report shares mutable archive state.");
            Require(!saved.Contains("Services") && !saved.Contains("Country") && !saved.Contains("Detail"), "Diagnostic or country data entered the compact archive.");
        });
        Check("history stable ordering uses success then count then latency", () =>
        {
            var profiles = Enumerable.Range(0, 7).Select(i => new ChannelProfile { Name = i.ToString() }).ToArray();
            profiles[0].Enabled = false;
            var reportValue = new HistoryReport { Channels = profiles.Select((p, i) => new ChannelHistorySeries { ProfileId = p.Id,
                Bars = i == 1 ? [] : [new HistoryBar { Count = i is 2 or 3 ? 2 : 4, SuccessCount = i == 6 ? 3 : i is 2 or 3 ? 2 : 4,
                    DnsTotalMs = i == 3 ? 10 : 20, HttpsTotalMs = 0 }] }).ToList() };
            var order = ChartHistoryArchive.SortProfiles(profiles, reportValue).Select(p => p.Name).ToArray();
            Require(string.Join(",", order) == "4,5,3,2,6,0,1", "Stability ordering or stable ties are wrong: " + string.Join(",", order));
        });
        Check("history longest range remains below 300 measured bars per channel", () =>
        {
            var archive = new ChartHistoryArchive();
            archive.Record(config, Enumerable.Range(0, 1441).Select(i => Sample(now.AddSeconds(-33).AddMinutes(-i))), now);
            var bars = Data(archive, 1440, 5).Bars;
            Require(bars.Count == 289 && bars.Sum(b => b.Count) == 1441, "The day view lost minutes or exceeded the bounded query.");
        });
        Check("history maximum standalone 32 by 300 reply fits 4 MiB frame", () =>
        {
            var reportValue = new HistoryReport { From = now.AddHours(-24), To = now, MonitoringKey = new string('A', 64) };
            for (var p = 0; p < 32; p++)
            {
                var series = new ChannelHistorySeries { ProfileId = Guid.NewGuid().ToString("X"), ContextKey = new string('B', 64) };
                for (var i = 0; i < 300; i++) series.Bars.Add(new HistoryBar { From = now.AddMinutes(-300 + i),
                    To = now.AddMinutes(-299 + i), LastAt = now.AddMinutes(-300 + i).AddSeconds(1), Count = int.MaxValue,
                    SuccessCount = int.MaxValue - 1, DnsTotalMs = 123456789.12345678, HttpsTotalMs = 987654321.87654321, LastSuccess = false });
                reportValue.Channels.Add(series);
            }
            var bytes = Encoding.UTF8.GetByteCount("{\"Ok\":true,\"History\":" + JsonData.Encode(reportValue) + "}");
            Require(bytes < 4 * 1024 * 1024, "Standalone graph reply exceeds frame: " + bytes);
        });
    }
}
