using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NetPilot;

public sealed record HistoryQuery
{
    public int PeriodMinutes { get; set; } = 60;
    public int BucketMinutes { get; set; } = 5;
    public void Validate()
    {
        if (PeriodMinutes is < 1 or > 1440 || BucketMinutes < 1 || BucketMinutes > PeriodMinutes
            || (int)Math.Ceiling((double)PeriodMinutes / BucketMinutes) + 1 > 300)
            throw new ArgumentException("Период графика: 1–1440 минут; шаг не больше периода и не более 300 столбцов.");
    }
}

public sealed class HistoryBar
{
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public DateTimeOffset LastAt { get; set; }
    public int Count { get; set; }
    public int SuccessCount { get; set; }
    public double DnsTotalMs { get; set; }
    public double HttpsTotalMs { get; set; }
    public bool LastSuccess { get; set; }
    [JsonIgnore] public int FailedCount => Count - SuccessCount;
    [JsonIgnore] public double MeanDnsMs => Count == 0 ? 0 : DnsTotalMs / Count;
    [JsonIgnore] public double MeanHttpsMs => Count == 0 ? 0 : HttpsTotalMs / Count;
    [JsonIgnore] public double MeanMs => ChartHistoryArchive.SumDuration(MeanDnsMs, MeanHttpsMs);
}

public sealed class ChannelHistorySeries
{
    public string ProfileId { get; set; } = "";
    public string ContextKey { get; set; } = "";
    public List<HistoryBar> Bars { get; set; } = [];
    [JsonIgnore] public long TotalChecks => Bars.Sum(b => (long)b.Count);
    [JsonIgnore] public long TotalSuccesses => Bars.Sum(b => (long)b.SuccessCount);
    [JsonIgnore] public double SuccessPercent => TotalChecks == 0 ? 0 : 100.0 * TotalSuccesses / TotalChecks;
    [JsonIgnore] public double MeanMs => TotalChecks == 0 ? 0 : ChartHistoryArchive.SumDuration(
        Bars.Aggregate(0.0, (sum, b) => ChartHistoryArchive.SumDuration(sum, b.DnsTotalMs)) / TotalChecks,
        Bars.Aggregate(0.0, (sum, b) => ChartHistoryArchive.SumDuration(sum, b.HttpsTotalMs)) / TotalChecks);
}

public sealed class HistoryReport
{
    public HistoryQuery Query { get; set; } = new();
    public DateTimeOffset From { get; set; }
    public DateTimeOffset To { get; set; }
    public string MonitoringKey { get; set; } = "";
    public List<ChannelHistorySeries> Channels { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Warning { get; set; }
}

// Stored points contain numeric aggregates only. Raw probe responses and selection history live elsewhere.
public sealed class ChartMinute
{
    [JsonRequired, JsonPropertyName("f")]
    public DateTimeOffset From { get; set; }
    [JsonRequired, JsonPropertyName("t")]
    public DateTimeOffset LastAt { get; set; }
    [JsonRequired, JsonPropertyName("n")]
    public int Count { get; set; }
    [JsonRequired, JsonPropertyName("s")]
    public int SuccessCount { get; set; }
    [JsonRequired, JsonPropertyName("d")]
    public double DnsTotalMs { get; set; }
    [JsonRequired, JsonPropertyName("h")]
    public double HttpsTotalMs { get; set; }
    [JsonRequired, JsonPropertyName("l")]
    public bool LastSuccess { get; set; }
}

public sealed class ChartMinuteSeries
{
    [JsonRequired, JsonPropertyName("p")]
    public string ProfileId { get; set; } = "";
    [JsonRequired, JsonPropertyName("c")]
    public string ContextKey { get; set; } = "";
    [JsonRequired, JsonPropertyName("m")]
    public List<ChartMinute> Minutes { get; set; } = [];
}

public sealed class ChartHistoryArchive
{
    [JsonRequired, JsonPropertyName("v")]
    public int Version { get; set; } = 1;
    [JsonRequired, JsonPropertyName("seed")]
    public bool InitialSeedCompleted { get; set; }
    [JsonRequired, JsonPropertyName("series")]
    public List<ChartMinuteSeries> Series { get; set; } = [];

    public void Validate()
    {
        if (Version != 1 || Series is null || Series.Count > 16384)
            throw new InvalidDataException("Неподдерживаемый или повреждённый архив графиков.");
        var identities = new HashSet<(Guid, string)>();
        long totalMinutes = 0;
        foreach (var series in Series)
        {
            if (series is null || series.ProfileId is null || series.ProfileId.Length > 68
                || !Guid.TryParse(series.ProfileId, out var id) || !ValidKey(series.ContextKey)
                || !identities.Add((id, series.ContextKey)) || series.Minutes is null || series.Minutes.Count > 1441)
                throw new InvalidDataException("Повреждена серия архива графиков.");
            if ((totalMinutes += series.Minutes.Count) > 32L * 1441 * 16)
                throw new InvalidDataException("Архив графиков превышает допустимый объём.");
            var times = new HashSet<DateTimeOffset>();
            foreach (var point in series.Minutes)
            {
                if (point is null || point.From.Offset != TimeSpan.Zero || point.From != Minute(point.From)
                    || point.From > DateTimeOffset.MaxValue.AddMinutes(-1) || !times.Add(point.From)
                    || point.LastAt < point.From || point.LastAt >= point.From.AddMinutes(1)
                    || point.Count < 1 || point.SuccessCount < 0 || point.SuccessCount > point.Count
                    || !ValidDuration(point.DnsTotalMs) || !ValidDuration(point.HttpsTotalMs)
                    || (point.LastSuccess && point.SuccessCount == 0)
                    || (!point.LastSuccess && point.SuccessCount == point.Count))
                    throw new InvalidDataException("Повреждено измерение архива графиков.");
            }
        }
    }

    // Validate the entire loaded document before trimming: corruption must not become a new empty file.
    public void NormalizeLoaded(DateTimeOffset now)
    {
        Validate();
        Trim(now);
        foreach (var series in Series) series.Minutes.Sort((a, b) => a.From.CompareTo(b.From));
    }

    public void Seed(AppConfig config, IEnumerable<ProfileStats> statistics, DateTimeOffset now)
    {
        if (InitialSeedCompleted) return;
        if (Series.Count > 0) throw new InvalidOperationException("Начальное заполнение возможно только для нового архива графиков.");
        Record(config, statistics.SelectMany(s => s.Samples), now);
        InitialSeedCompleted = true;
    }

    public void Record(AppConfig config, IEnumerable<ProbeResult> results, DateTimeOffset now)
    {
        var cutoff = Minute(now.AddHours(-24));
        var profiles = config.Profiles.ToDictionary(p => Guid.Parse(p.Id));
        var monitoringKey = MonitoringTargets.Key(config);
        var lookup = Series.ToDictionary(s => (Guid.Parse(s.ProfileId), s.ContextKey));
        foreach (var sample in results)
        {
            if (sample is null || !Guid.TryParse(sample.ProfileId, out var id) || !profiles.TryGetValue(id, out var profile)
                || sample.At < cutoff || sample.At > now || !MonitoringTargets.Matches(sample, config)) continue;
            var key = ContextKey(profile, monitoringKey);
            if (!lookup.TryGetValue((id, key), out var series))
            {
                series = new ChartMinuteSeries { ProfileId = profile.Id, ContextKey = key };
                Series.Add(series); lookup.Add((id, key), series);
            }
            var from = Minute(sample.At);
            var point = series.Minutes.FirstOrDefault(m => m.From == from);
            if (point is null)
            {
                point = new ChartMinute { From = from, LastAt = sample.At, LastSuccess = sample.Success };
                series.Minutes.Add(point);
            }
            point.Count = checked(point.Count + 1);
            if (sample.Success) point.SuccessCount = checked(point.SuccessCount + 1);
            point.DnsTotalMs = SumDuration(point.DnsTotalMs, NormalizeDuration(sample.DnsMs));
            point.HttpsTotalMs = SumDuration(point.HttpsTotalMs, NormalizeDuration(sample.HttpsMs));
            if (sample.At >= point.LastAt) { point.LastAt = sample.At; point.LastSuccess = sample.Success; }
        }
        Trim(now);
    }

    public HistoryReport Query(AppConfig config, HistoryQuery query, DateTimeOffset now)
    {
        query.Validate();
        var monitoringKey = MonitoringTargets.Key(config);
        var report = new HistoryReport { Query = query with { }, From = Minute(now.AddMinutes(-query.PeriodMinutes)),
            To = now, MonitoringKey = monitoringKey };
        foreach (var profile in config.Profiles)
        {
            var context = ContextKey(profile, monitoringKey);
            var series = new ChannelHistorySeries { ProfileId = profile.Id, ContextKey = context };
            var stored = Series.FirstOrDefault(s => Guid.Parse(s.ProfileId) == Guid.Parse(profile.Id) && s.ContextKey == context);
            if (stored is not null)
            {
                foreach (var group in stored.Minutes.Where(m => m.From >= report.From && m.From <= now && m.LastAt <= now)
                    .GroupBy(m => Bucket(m.From, query.BucketMinutes)).OrderBy(g => g.Key))
                {
                    var last = group.MaxBy(m => m.LastAt)!;
                    series.Bars.Add(new HistoryBar { From = group.Key < report.From ? report.From : group.Key,
                        To = group.Key.AddMinutes(query.BucketMinutes) > now ? now : group.Key.AddMinutes(query.BucketMinutes),
                        LastAt = last.LastAt, LastSuccess = last.LastSuccess,
                        Count = checked(group.Sum(m => m.Count)), SuccessCount = checked(group.Sum(m => m.SuccessCount)),
                        DnsTotalMs = group.Aggregate(0.0, (sum, m) => SumDuration(sum, m.DnsTotalMs)),
                        HttpsTotalMs = group.Aggregate(0.0, (sum, m) => SumDuration(sum, m.HttpsTotalMs)) });
                }
            }
            report.Channels.Add(series);
        }
        return report;
    }

    public static string ContextKey(ChannelProfile profile, string monitoringKey)
    {
        var addresses = string.Join(",", profile.Servers.Select(IPAddress.Parse).Select(ip => ip.ToString()).Order(StringComparer.Ordinal));
        var endpoint = profile.Mode == DnsMode.Doh ? new Uri(profile.DohUrl).AbsoluteUri : profile.DnsPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{profile.Mode}\n{addresses}\n{endpoint}\n{monitoringKey}")));
    }

    public static ChannelProfile[] SortProfiles(IEnumerable<ChannelProfile> profiles, HistoryReport report)
    {
        var series = report.Channels.GroupBy(s => s.ProfileId, StringComparer.Ordinal).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        return profiles.Select((p, index) => (Profile: p, Index: index, Data: series.GetValueOrDefault(p.Id)))
            .OrderBy(x => x.Profile.Enabled && x.Data?.TotalChecks > 0 ? 0 : 1)
            .ThenByDescending(x => x.Profile.Enabled && x.Data?.TotalChecks > 0 ? x.Data.SuccessPercent : 0)
            .ThenByDescending(x => x.Profile.Enabled && x.Data?.TotalChecks > 0 ? x.Data.TotalChecks : 0)
            .ThenBy(x => x.Profile.Enabled && x.Data?.TotalChecks > 0 ? x.Data.MeanMs : 0)
            .ThenBy(x => x.Index).Select(x => x.Profile).ToArray();
    }

    private void Trim(DateTimeOffset now)
    {
        var cutoff = Minute(now.AddHours(-24));
        foreach (var series in Series) series.Minutes.RemoveAll(m => m.From < cutoff);
        Series.RemoveAll(s => s.Minutes.Count == 0);
    }
    private static bool ValidKey(string? value) => value is { Length: 64 } && value.All(c => char.IsAsciiHexDigit(c));
    private static bool ValidDuration(double value) => double.IsFinite(value) && value >= 0;
    private static double NormalizeDuration(double value) => ValidDuration(value) ? value : 0;
    internal static double SumDuration(double a, double b) => double.IsPositiveInfinity(a + b) ? double.MaxValue : a + b;
    private static DateTimeOffset Minute(DateTimeOffset value) => new(value.UtcTicks - value.UtcTicks % TimeSpan.TicksPerMinute, TimeSpan.Zero);
    private static DateTimeOffset Bucket(DateTimeOffset value, int minutes)
    {
        long minute = (value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMinute;
        // Correct floor division also for old imported measurements before the Unix epoch.
        long slot = Math.DivRem(minute, minutes, out var remainder);
        if (remainder < 0) slot--;
        return DateTimeOffset.UnixEpoch.AddMinutes(slot * minutes);
    }
}
