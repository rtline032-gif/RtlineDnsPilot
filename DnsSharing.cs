using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace NetPilot;

public sealed class DnsShareCandidate
{
    public DnsMode Mode { get; set; }
    public string[] Servers { get; set; } = [];
    public int DnsPort { get; set; } = 53;
    public string DohUrl { get; set; } = "";
    public string[] WorkedServices { get; set; } = [];
}

public sealed class DnsShareOffer
{
    public List<DnsShareCandidate> Profiles { get; set; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] public string? Warning { get; set; }
}

public static class DnsSharing
{
    private static readonly ChannelProfile[] Baseline = AppConfig.Defaults().Profiles.Concat(BundledChannels.Additions()).ToArray();
    private static readonly string DefaultGptKey = MonitoringTargets.Key(new AppConfig());

    public static DnsShareOffer Select(AppConfig config, IEnumerable<ProfileStats> statistics, DateTimeOffset now)
    {
        var result = new DnsShareOffer();
        var currentKey = MonitoringTargets.Key(config);
        bool officialCurrentGpt = Uri.TryCreate(config.ProbeUrl, UriKind.Absolute, out var probe)
            && probe.Scheme == "https" && probe.IdnHost.Equals("chatgpt.com", StringComparison.OrdinalIgnoreCase) && probe.IsDefaultPort;
        bool legacyCurrentGpt = currentKey == DefaultGptKey;
        var byId = statistics.Where(s => s is not null && Guid.TryParse(s.ProfileId, out _))
            .GroupBy(s => Guid.Parse(s.ProfileId)).ToDictionary(g => g.Key, g => g.SelectMany(s => s.Samples));
        var unique = new Dictionary<string, DnsShareCandidate>(StringComparer.Ordinal);
        foreach (var profile in config.Profiles)
        {
            if (!Guid.TryParse(profile.Id, out var id) || !byId.TryGetValue(id, out var samples)) continue;
            var worked = new HashSet<string>(StringComparer.Ordinal);
            foreach (var sample in samples)
            {
                if (sample is null || sample.ServiceDetailsOmitted || !Guid.TryParse(sample.ProfileId, out var sampleId)
                    || sampleId != id || !Fresh(sample.At, now)) continue;
                bool knownGpt = sample.MonitoringKey == DefaultGptKey
                    || (officialCurrentGpt && sample.MonitoringKey == currentKey)
                    || (sample.MonitoringKey.Length == 0 && legacyCurrentGpt);
                if (sample.Services.Count == 0)
                {
                    if (sample.Success && knownGpt) worked.Add(MonitoringTargets.ChatGpt);
                    continue;
                }
                foreach (var service in sample.Services)
                {
                    if (!service.Success || !Fresh(service.At, now)) continue;
                    if (service.ServiceId == MonitoringTargets.Gemini) worked.Add(MonitoringTargets.Gemini);
                    else if (service.ServiceId == MonitoringTargets.ChatGpt && knownGpt) worked.Add(MonitoringTargets.ChatGpt);
                }
            }
            if (worked.Count == 0) continue;
            try
            {
                var candidate = NormalizeCandidate(new DnsShareCandidate { Mode = profile.Mode, Servers = profile.Servers,
                    DnsPort = profile.DnsPort, DohUrl = profile.Mode == DnsMode.Doh ? profile.DohUrl : "",
                    WorkedServices = worked.Order(StringComparer.Ordinal).ToArray() });
                if (IsBaseline(candidate)) continue;
                var key = Key(candidate);
                if (unique.TryGetValue(key, out var existing)) existing.WorkedServices = existing.WorkedServices.Concat(candidate.WorkedServices)
                    .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                else { unique.Add(key, candidate); result.Profiles.Add(candidate); }
            }
            catch (ArgumentException) { /* Invalid/private settings never become an upload candidate. */ }
        }
        return result;
    }

    public static DnsShareCandidate NormalizeCandidate(DnsShareCandidate source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!Enum.IsDefined(source.Mode) || source.Servers is null || source.Servers.Length is < 1 or > 8
            || source.DnsPort is < 1 or > 65535 || source.WorkedServices is null || source.WorkedServices.Length is < 1 or > 2)
            throw new ArgumentException("Некорректный канал для отправки.");
        var servers = source.Servers.Select(value =>
        {
            if (value is null || value.Length > 128 || !IPAddress.TryParse(value, out var ip) || !IsPublicAddress(ip))
                throw new ArgumentException("Можно отправлять только публичные DNS-адреса.");
            if (ip.AddressFamily == AddressFamily.InterNetwork) return ip.ToString();
            var bytes = ip.GetAddressBytes();
            return string.Join(":", Enumerable.Range(0, 8).Select(i => ((bytes[i * 2] << 8) | bytes[i * 2 + 1]).ToString("x4", CultureInfo.InvariantCulture)));
        }).ToArray();
        if (servers.Distinct(StringComparer.Ordinal).Count() != servers.Length
            || source.WorkedServices.Any(s => s is not (MonitoringTargets.ChatGpt or MonitoringTargets.Gemini))
            || source.WorkedServices.Distinct(StringComparer.Ordinal).Count() != source.WorkedServices.Length)
            throw new ArgumentException("В канале повторяются адреса или результаты проверок.");
        string doh = "";
        if (source.Mode == DnsMode.Doh)
        {
            if ((source.DohUrl?.Length ?? 0) > 2048 || !Uri.TryCreate(source.DohUrl, UriKind.Absolute, out var uri)
                || uri.Scheme != "https" || !uri.IsWellFormedOriginalString() || uri.HostNameType != UriHostNameType.Dns
                || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || uri.IdnHost.Length > 253
                || !PublicHost(uri.IdnHost)) throw new ArgumentException("Для DoH нужен публичный HTTPS-адрес.");
            doh = "https://" + uri.IdnHost.ToLowerInvariant() + (uri.IsDefaultPort ? "" : ":" + uri.Port.ToString(CultureInfo.InvariantCulture)) + uri.AbsolutePath;
            if (doh.Length > 2048) throw new ArgumentException("Адрес DoH слишком длинный.");
        }
        else if (!string.IsNullOrEmpty(source.DohUrl)) throw new ArgumentException("Обычный DNS не содержит адрес DoH.");
        return new DnsShareCandidate { Mode = source.Mode, Servers = servers.Order(StringComparer.Ordinal).ToArray(),
            DnsPort = source.Mode == DnsMode.Doh ? 53 : source.DnsPort, DohUrl = doh,
            WorkedServices = source.WorkedServices.Order(StringComparer.Ordinal).ToArray() };
    }

    public static bool IsBaseline(DnsShareCandidate candidate)
    {
        var normalized = NormalizeCandidate(candidate);
        var profile = new ChannelProfile { Mode = normalized.Mode, Servers = normalized.Servers,
            DnsPort = normalized.DnsPort, DohUrl = normalized.DohUrl };
        return Baseline.Any(baseProfile => AppConfig.SameEffectiveConfiguration(profile, baseProfile));
    }

    public static string Key(DnsShareCandidate candidate)
    {
        var value = NormalizeCandidate(candidate);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Mode + "\n" + string.Join(",", value.Servers)
            + "\n" + value.DnsPort.ToString(CultureInfo.InvariantCulture) + "\n" + value.DohUrl))).ToLowerInvariant();
    }

    internal static bool IsPublicAddress(IPAddress ip)
    {
        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                || b[0] == 100 && b[1] is >= 64 and <= 127
                || b[0] == 169 && b[1] == 254 || b[0] == 172 && b[1] is >= 16 and <= 31
                || b[0] == 192 && (b[1] == 0 && b[2] is 0 or 2 || b[1] == 88 && b[2] == 99 || b[1] == 168)
                || b[0] == 198 && (b[1] is 18 or 19 || b[1] == 51 && b[2] == 100)
                || b[0] == 203 && b[1] == 0 && b[2] == 113);
        }
        if (ip.AddressFamily != AddressFamily.InterNetworkV6 || ip.ScopeId != 0 || ip.IsIPv4MappedToIPv6) return false;
        var bytes = ip.GetAddressBytes();
        // Only global unicast; omit IETF special-purpose, transition and documentation prefixes.
        return (bytes[0] & 0xe0) == 0x20 && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] < 2)
            && !(bytes[0] == 0x20 && bytes[1] == 0x01 && bytes[2] == 0x0d && bytes[3] == 0xb8)
            && !(bytes[0] == 0x20 && bytes[1] == 0x02) && !(bytes[0] == 0x3f && bytes[1] == 0xff);
    }

    private static bool Fresh(DateTimeOffset at, DateTimeOffset now) => at <= now && at >= now.AddHours(-24);
    private static bool PublicHost(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        return labels.Length >= 2 && labels.All(label => label.Length is >= 1 and <= 63 && char.IsAsciiLetterOrDigit(label[0])
            && char.IsAsciiLetterOrDigit(label[^1]) && label.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            && labels[^1].Any(char.IsAsciiLetter) && labels[^1] is not ("local" or "localhost" or "internal" or "lan" or "home" or "invalid" or "test" or "example");
    }
}
