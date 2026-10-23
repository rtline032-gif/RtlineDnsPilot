using System.Text;

namespace NetPilot.Tests;

public static class RpcProjectionTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, Action test)
        {
            try { test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }

        Check("RPC projection retains every numeric sample, context and rolling counter", () =>
        {
            var config = Configuration(1, 2, false); var source = Measurements(config, 120, false);
            var projected = ProjectStatistics(source); var expected = source.Single(); var actual = projected.Single();
            Require(actual.ProfileId == expected.ProfileId && actual.TotalChecks == expected.TotalChecks && actual.TotalSuccesses == expected.TotalSuccesses
                && actual.ConsecutiveFailures == expected.ConsecutiveFailures && actual.ConsecutiveSuccesses == expected.ConsecutiveSuccesses,
                "RPC projection changed historical counters or current streaks.");
            Require(actual.Samples.Count == 120 && actual.SuccessPercent == expected.SuccessPercent && actual.P95Ms == expected.P95Ms && actual.MeanMs == expected.MeanMs,
                "RPC projection shortened the chart window or changed rolling metrics.");
            for (var index = 0; index < 120; index++) NumericSame(expected.Samples[index], actual.Samples[index]);
        });

        Check("RPC latest sample keeps every service result without changing its success or latency", () =>
        {
            var config = Configuration(1, 9, false); var source = Measurements(config, 120, false);
            var raw = source[0].Last!; var projected = ProjectStatistics(source)[0].Last!;
            Require(!projected.ServiceDetailsOmitted && projected.Services.Count == 9, "Current per-service results were omitted.");
            for (var index = 0; index < 9; index++)
            {
                var expected = raw.Services[index]; var actual = projected.Services[index];
                Require(actual.ServiceId == expected.ServiceId && actual.At == expected.At && actual.Success == expected.Success
                    && actual.HttpStatus == expected.HttpStatus && actual.DnsMs == expected.DnsMs && actual.HttpsMs == expected.HttpsMs,
                    "A current per-service status or measurement changed.");
                Require(MonitoringTargets.Service(projected, actual.ServiceId) == actual, "The latest service column cannot resolve its current result.");
            }
        });

        Check("omitted historical details cannot masquerade as a legacy ChatGPT result", () =>
        {
            var source = new List<ProfileStats> { new() { ProfileId = Id(1), Samples = [new ProbeResult { ProfileId = Id(1), Success = true }, new ProbeResult { ProfileId = Id(1), Success = false }] } };
            var projected = ProjectStatistics(source)[0];
            Require(projected.Samples[0].ServiceDetailsOmitted && projected.Samples[0].Services.Count == 0, "Historic metadata omission is not explicit.");
            Require(MonitoringTargets.Service(projected.Samples[0], MonitoringTargets.ChatGpt) is null, "An aggregate old result was mislabeled ChatGPT after projection.");
            Require(!source[0].Samples[0].ServiceDetailsOmitted, "Projection changed raw legacy history.");
        });

        Check("RPC projection owns its lists and mutable rows without modifying raw history", () =>
        {
            var config = Configuration(1, 2, false); var source = Measurements(config, 120, false);
            var before = JsonData.Encode(source); var projected = ProjectStatistics(source);
            var rawLast = source[0].Last!; var projectedLast = projected[0].Last!;
            Require(!ReferenceEquals(source, projected) && !ReferenceEquals(source[0], projected[0]) && !ReferenceEquals(source[0].Samples, projected[0].Samples)
                && !ReferenceEquals(rawLast, projectedLast) && !ReferenceEquals(rawLast.Services, projectedLast.Services)
                && !ReferenceEquals(rawLast.Services[0], projectedLast.Services[0]), "The RPC view aliases raw mutable statistics.");
            projectedLast.Services[0].Detail = "Changed by consumer"; projectedLast.DnsMs = 123456; projected[0].TotalChecks = 1;
            projected[0].Samples.RemoveAt(0);
            Require(JsonData.Encode(source) == before, "A mutation of the RPC view changed full persisted history.");
        });

        Check("RPC event projection owns rows, preserves chronology and bounds diagnostic text", () =>
        {
            var events = Events(200); var before = JsonData.Encode(events); var projected = ProjectEvents(events);
            Require(projected.Count == 200 && projected.Select(e => e.At).SequenceEqual(events.Select(e => e.At)) && projected.Select(e => e.Kind).SequenceEqual(events.Select(e => e.Kind)),
                "Event projection reordered, dropped or reclassified events.");
            Require(projected.All(e => e.Message.Length <= 128) && !ReferenceEquals(events, projected) && !ReferenceEquals(events[0], projected[0]), "RPC event details are unbounded or alias raw history.");
            projected[0].Message = "Changed by consumer";
            Require(JsonData.Encode(events) == before, "RPC event clipping modified disk-history data.");
        });

        Check("RPC diagnostic clipping preserves valid Unicode and bounds stage and details", () =>
        {
            var sample = new ProbeResult { ProfileId = Id(1), Stage = new string('Ж', 63) + "😀" + new string('Ж', 1000), Detail = new string('Ж', 159) + "😀" + new string('Ж', 1000),
                Services = [new ServiceProbeResult { ServiceId = MonitoringTargets.ChatGpt, Stage = new string('Ж', 63) + "😀" + new string('Ж', 1000), Detail = new string('Ж', 127) + "😀" + new string('Ж', 1000) }] };
            var projected = ProjectStatistics([new ProfileStats { ProfileId = Id(1), Samples = [sample] }])[0].Last!;
            Require(projected.Stage.Length <= 64 && projected.Detail.Length <= 160 && projected.Services[0].Stage.Length <= 64 && projected.Services[0].Detail.Length <= 128,
                "Projected diagnostics exceed their bounded field sizes.");
            Require(ValidUnicode(projected.Stage) && ValidUnicode(projected.Detail) && ValidUnicode(projected.Services[0].Stage) && ValidUnicode(projected.Services[0].Detail),
                "Diagnostic clipping split a UTF16 surrogate pair.");
            _ = JsonData.Encode(projected);
        });

        Check("reprojecting an omitted last result never invents per-service metadata", () =>
        {
            var source = new List<ProfileStats> { new() { ProfileId = Id(1), Samples = [new ProbeResult { ProfileId = Id(1), ServiceDetailsOmitted = true, Success = true }] } };
            var latest = ProjectStatistics(ProjectStatistics(source))[0].Last!;
            Require(latest.ServiceDetailsOmitted && MonitoringTargets.Service(latest, MonitoringTargets.ChatGpt) is null, "Repeated status projection invented a service result.");
        });

        Check("maximum valid RPC configuration and measured history stay below the 4MiB frame limit", () =>
        {
            var config = Configuration(32, 9, true); config.Validate();
            var source = Measurements(config, 120, true); var events = Events(200);
            var reply = new RpcReply { Ok = true, Status = new AppStatus { ServiceVersion = AppVersion.Current, ServiceConnected = true, Monitoring = true,
                Config = config, Statistics = ProjectStatistics(source), Events = ProjectEvents(events) } };
            var bytes = Encoding.UTF8.GetByteCount(JsonData.Encode(reply));
            var requestBytes=Encoding.UTF8.GetByteCount(JsonData.Encode(new RpcRequest {Command="configure",Config=config}));
            Require(requestBytes<1024*1024,"A maximum valid configuration exceeds the service request frame limit: "+requestBytes+" bytes.");
            Require(reply.Status.Statistics.All(s => s.Samples.Count == 120 && s.Last!.Services.Count == 9), "Size control discarded chart points or current service results.");
            Require(bytes < 4 * 1024 * 1024, "Maximum valid RPC response exceeds its frame limit: " + bytes + " bytes.");
        });
    }

    private static List<ProfileStats> ProjectStatistics(IReadOnlyList<ProfileStats> source) => StatusProjection.Statistics(source);
    private static List<LogEntry> ProjectEvents(IReadOnlyList<LogEntry> source) => StatusProjection.Events(source);
    private static void NumericSame(ProbeResult expected, ProbeResult actual)
    {
        Require(actual.MonitoringKey == expected.MonitoringKey && actual.ProfileId == expected.ProfileId && actual.At == expected.At
            && actual.Success == expected.Success && actual.DnsMs == expected.DnsMs && actual.HttpsMs == expected.HttpsMs && actual.HttpStatus == expected.HttpStatus,
            "Projection changed numeric evidence or its monitoring context.");
    }
    private static bool ValidUnicode(string value)
    {
        try { _ = new UnicodeEncoding(false, false, true).GetByteCount(value); return true; }
        catch (EncoderFallbackException) { return false; }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static string Id(int number) => new Guid(number, 0, 0, new byte[8]).ToString("N");
    private static string Url(string host, int length)
    { var prefix = "https://" + host + "/"; return prefix + new string('я', length - prefix.Length); }
    private static AppConfig Configuration(int profiles, int targets, bool maximum)
    {
        var config = new AppConfig { MonitorChatGpt = true, MonitorGemini = targets > 1, MonitorTailscale = targets > 2,
            ProbeUrl = maximum ? Url("probe.example.com", 2048) : MonitoringTargets.DefaultChatGptUrl };
        for (var index = 0; index < profiles; index++) config.Profiles.Add(new ChannelProfile
        {
            Id = maximum ? Guid.Parse(Id(index + 1)).ToString("X") : Id(index + 1), Name = maximum ? new string('К', 98) + index.ToString("D2") : "Канал " + index,
            Mode = DnsMode.Doh, DohUrl = maximum ? Url("dns" + index + ".example.com", 2048) : "https://dns" + index + ".example.com/dns-query",
            Servers = maximum ? Enumerable.Range(1, 8).Select(server => "2001:0db8:0000:0000:0000:0000:" + (index + 1).ToString("x4") + ":" + server.ToString("x4")).ToArray() : ["192.0.2." + (index + 1)],
            Note = maximum ? new string('П', 2000) : ""
        });
        for (var index = 0; index < targets - 3; index++) config.AdditionalTargets.Add(new MonitoringEndpoint
        { Id = maximum ? Guid.Parse(Id(100 + index)).ToString("X") : Id(100 + index), Name = maximum ? new string('С', 98) + index.ToString("D2") : "Сервис " + index,
            Url = maximum ? Url("service" + index + ".example.com", 2048) : "https://service" + index + ".example.com/" });
        if (maximum)
        {
            config.AdapterId = Guid.Parse(Id(10000)).ToString("X"); config.PinnedProfileId = config.Profiles[0].Id;
            config.ManualProfileId = config.Profiles[0].Id; config.ManualUntil = new DateTimeOffset(2026, 10, 23, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
            config.IntervalSeconds = 3600; config.TimeoutSeconds = 30;
        }
        config.Validate(); return config;
    }
    private static List<ProfileStats> Measurements(AppConfig config, int sampleCount, bool maximum)
    {
        var selected = MonitoringTargets.Selected(config); var result = new List<ProfileStats>();
        var at = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567);
        foreach (var profile in config.Profiles)
        {
            var row = new ProfileStats { ProfileId = profile.Id };
            for (var index = 0; index < sampleCount; index++)
            {
                var services = selected.Select((target, ordinal) => new ServiceProbeResult
                { ServiceId = target.Id, At = at.AddSeconds(index * 20), Success = ordinal != selected.Length - 1 || index % 5 == 0,
                    DnsMs = maximum ? 14990.123456789012 + ordinal : 17.125 + ordinal, HttpsMs = maximum ? 14990.987654321098 + ordinal : 501.375 + ordinal,
                    HttpStatus = ordinal == selected.Length - 1 && index % 5 != 0 ? 403 : 200,
                    Stage = "http", Detail = new string('Ж', maximum ? 256 : 400), CountryCode="NL",CountrySource=ConnectionCountrySource.ClientCountryHeader }).ToArray();
                row.Add(MonitoringTargets.Combine(profile.Id, config, services));
            }
            row.TotalChecks += 10000; row.TotalSuccesses += 7000;
            result.Add(row);
        }
        return result;
    }
    private static List<LogEntry> Events(int count) => Enumerable.Range(0, count).Select(index => new LogEntry
    { At = new DateTimeOffset(2026, 10, 22, 12, 0, 0, TimeSpan.Zero).AddTicks(1234567).AddSeconds(index), Kind = "error", Message = new string('Ж', 2000) }).ToList();
}
