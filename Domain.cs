using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPilot;

public enum DnsMode { Doh, Udp }
public enum AutoSelectionMode { Stable=0, Fast=1 }

public sealed class ChannelProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый канал";
    public bool Enabled { get; set; } = true;
    public DnsMode Mode { get; set; } = DnsMode.Udp;
    public string[] Servers { get; set; } = [];
    public int DnsPort { get; set; } = 53;
    public string DohUrl { get; set; } = "";
    public string Note { get; set; } = "";

    public void Validate()
    {
        if ((Id?.Length ?? 0)>68 || !Guid.TryParse(Id, out _) || string.IsNullOrWhiteSpace(Name) || Name.Length > 100)
            throw new ArgumentException("У канала должны быть уникальный ID и название до 100 символов.");
        if (!Enum.IsDefined(Mode) || DnsPort < 1 || DnsPort > 65535 || Servers is null || Servers.Length is < 1 or > 8)
            throw new ArgumentException("Укажите от 1 до 8 IP-адресов DNS и корректный порт.");
        var addresses = new HashSet<IPAddress>();
        foreach (var value in Servers)
        {
            if ((value?.Length ?? 0)>128 || !IPAddress.TryParse(value, out var ip) || IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)
                || ip.IsIPv6Multicast || (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && ip.GetAddressBytes()[0] >= 224))
                throw new ArgumentException("Недопустимый DNS IP: " + value + ". Локальная петля вызовет рекурсию.");
            if (!addresses.Add(ip)) throw new ArgumentException($"IP-адрес {value} повторяется в канале «{Name.Trim()}».");
        }
        if ((DohUrl?.Length ?? 0)>2048) throw new ArgumentException("Адрес DoH не должен превышать 2048 символов.");
        if (Mode == DnsMode.Doh && (!Uri.TryCreate(DohUrl, UriKind.Absolute, out var uri) || uri.Scheme != "https"
            || !uri.IsWellFormedOriginalString()
            || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment) || uri.HostNameType != UriHostNameType.Dns
            || !string.IsNullOrEmpty(uri.Query)))
            throw new ArgumentException("Для DoH нужен HTTPS URL с именем сервера, без пароля, параметров и фрагмента.");
        if ((Note?.Length ?? 0) > 2000) throw new ArgumentException("Примечание слишком длинное.");
    }
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<ChannelProfile> Profiles { get; set; } = [];
    public string ProbeUrl { get; set; } = "https://chatgpt.com/cdn-cgi/trace";
    public bool MonitorChatGpt { get; set; } = true;
    public bool MonitorGemini { get; set; }
    public bool MonitorTailscale { get; set; }
    public bool MonitoringPaused { get; set; }
    public List<MonitoringEndpoint> AdditionalTargets { get; set; } = [];
    public int IntervalSeconds { get; set; } = 20;
    public int TimeoutSeconds { get; set; } = 8;
    public AutoSelectionMode AutoSelection { get; set; } = AutoSelectionMode.Stable;
    public string AdapterId { get; set; } = "";
    public string? PinnedProfileId { get; set; }
    public string? ManualProfileId { get; set; }
    public DateTimeOffset? ManualUntil { get; set; }
    public bool ManagementRequested { get; set; }
    public void Validate()
    {
        if (Version != 1) throw new ArgumentException("Поддерживается формат конфигурации 1.");
        if (!Enum.IsDefined(AutoSelection)) throw new ArgumentException("Выберите режим автовыбора: самый стабильный или самый быстрый.");
        ValidateProfiles(Profiles);
        MonitoringTargets.Validate(this);
        if (IntervalSeconds is < 10 or > 3600 || TimeoutSeconds is < 2 or > 30) throw new ArgumentException("Интервал: 10–3600 с, тайм-аут: 2–30 с.");
        if (AdapterId.Length > 68 || (AdapterId.Length > 0 && !Guid.TryParse(AdapterId, out _))) throw new ArgumentException("Некорректный GUID адаптера.");
        if (PinnedProfileId != null && !Profiles.Any(p => p.Enabled && p.Id == PinnedProfileId)) throw new ArgumentException("Закреплённый канал отсутствует или выключен.");
        if (ManualProfileId != null && !Profiles.Any(p => p.Enabled && p.Id == ManualProfileId)) throw new ArgumentException("Канал ручной фиксации отсутствует или выключен.");
        if (ManualProfileId is null && ManualUntil is not null) throw new ArgumentException("Срок фиксации задан без выбранного канала.");
    }

    internal static void ValidateProfiles(IReadOnlyList<ChannelProfile>? profiles)
    {
        if (profiles is null || profiles.Count is < 1 or > 32) throw new ArgumentException("Поддерживается от 1 до 32 каналов.");
        foreach (var profile in profiles)
        {
            if (profile is null) throw new ArgumentException("В списке каналов есть пустая запись.");
            profile.Validate();
        }
        for (var i = 0; i < profiles.Count; i++)
        {
            var profile = profiles[i];
            for (var j = 0; j < i; j++)
            {
                var existing = profiles[j];
                if (Guid.Parse(profile.Id) == Guid.Parse(existing.Id))
                    throw new ArgumentException($"ID канала «{profile.Name.Trim()}» повторяет канал «{existing.Name.Trim()}».");
                if (StringComparer.OrdinalIgnoreCase.Equals(profile.Name.Trim(), existing.Name.Trim()))
                    throw new ArgumentException($"Название уже используется каналом «{existing.Name.Trim()}».");
                if (SameEffectiveConfiguration(profile, existing))
                    throw new ArgumentException($"Настройки уже используются каналом «{existing.Name.Trim()}».");
            }
        }
    }

    internal static bool SameEffectiveConfiguration(ChannelProfile first, ChannelProfile second)
    {
        if (first.Mode != second.Mode || !first.Servers.Select(IPAddress.Parse).ToHashSet().SetEquals(second.Servers.Select(IPAddress.Parse)))
            return false;
        return first.Mode == DnsMode.Udp
            ? first.DnsPort == second.DnsPort
            : StringComparer.Ordinal.Equals(new Uri(first.DohUrl).AbsoluteUri, new Uri(second.DohUrl).AbsoluteUri);
    }

    public static AppConfig Defaults() => new()
    {
        Profiles =
        [
            new() { Name = "Xbox DNS", Mode = DnsMode.Doh, Servers = ["111.88.96.54", "111.88.96.55"], DohUrl = "https://xbox-dns.ru/dns-query", Note = "На сайте 22.10.2026: сервис временно недоступен. Проверяйте фактический результат." },
            new() { Name = "Bezmezhau", Mode = DnsMode.Doh, Servers = ["181.215.40.49", "31.58.216.201"], DohUrl = "https://dns.bezmezhau.com/dns-query" },
            new() { Name = "DNS-AI", Mode = DnsMode.Doh, Servers = ["194.87.196.13", "45.151.102.219", "2a00:b700:5::3:62", "2a12:5940:d4ef::219"], DohUrl = "https://dns.dns-ai.ru/dns-query", Note = "Провайдер сообщает: порт 53 закрыт; используйте DoH. Без открытого DNS fallback." }
        ]
    };
}

public static class ProfileEditor
{
    public static string GetDohUrlForEditing(ChannelProfile profile) => profile.Mode == DnsMode.Doh ? profile.DohUrl ?? "" : "";

    public static ChannelProfile Build(ChannelProfile source, string name, bool enabled, string servers, string? dohUrl, int dnsPort,
        string? note, IEnumerable<ChannelProfile>? existingProfiles = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        var profile = JsonData.Clone(source);
        profile.Name = (name ?? "").Trim();
        profile.Enabled = enabled;
        profile.DohUrl = (dohUrl ?? "").Trim();
        profile.Mode = profile.DohUrl.Length == 0 ? DnsMode.Udp : DnsMode.Doh;
        profile.Servers = (servers ?? "").Split(['\r', '\n', ',', ';', ' ', '\t'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        profile.DnsPort = dnsPort;
        profile.Note = (note ?? "").Trim();
        profile.Validate();
        var id = Guid.Parse(profile.Id);
        var profiles = (existingProfiles ?? []).Where(p => p is null || !Guid.TryParse(p.Id, out var existingId) || existingId != id).ToList();
        profiles.Add(profile);
        AppConfig.ValidateProfiles(profiles);
        return profile;
    }
}

public sealed class ProbeResult
{
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryCode { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public ConnectionCountrySource CountrySource { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingDefault)]
    public bool ServiceDetailsOmitted { get; set; }
    public string MonitoringKey { get; set; } = "";
    public List<ServiceProbeResult> Services { get; set; } = [];
    public string ProfileId { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public double DnsMs { get; set; }
    public double HttpsMs { get; set; }
    public int? HttpStatus { get; set; }
    public string Stage { get; set; } = "";
    public string Detail { get; set; } = "";
    public double TotalMs => DnsMs + HttpsMs;
}

public sealed class ProfileStats
{
    public string ProfileId { get; set; } = "";
    public List<ProbeResult> Samples { get; set; } = [];
    public long TotalChecks { get; set; }
    public long TotalSuccesses { get; set; }
    public int ConsecutiveSuccesses { get; set; }
    public int ConsecutiveFailures { get; set; }
    public double SuccessPercent => Samples.Count == 0 ? 0 : 100.0 * Samples.Count(x => x.Success) / Samples.Count;
    public double MeanMs => Samples.Where(x => x.Success).Select(x => x.TotalMs).DefaultIfEmpty(double.PositiveInfinity).Average();
    public double P95Ms
    {
        get { var v = Samples.Where(x => x.Success).Select(x => x.TotalMs).Order().ToArray(); return v.Length == 0 ? 0 : v[Math.Clamp((int)Math.Ceiling(v.Length * .95) - 1, 0, v.Length - 1)]; }
    }
    public ProbeResult? Last => Samples.LastOrDefault();
    public void Add(ProbeResult sample)
    {
        if (Last is { } previous && previous.MonitoringKey != sample.MonitoringKey) { ConsecutiveFailures=0; ConsecutiveSuccesses=0; }
        Samples.Add(sample); if (Samples.Count > 120) Samples.RemoveRange(0, Samples.Count - 120);
        TotalChecks++; if (sample.Success) TotalSuccesses++;
        ConsecutiveSuccesses = sample.Success ? ConsecutiveSuccesses + 1 : 0;
        ConsecutiveFailures = sample.Success ? 0 : ConsecutiveFailures + 1;
    }
}

public sealed class LogEntry
{
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public string Kind { get; set; } = "info";
    public string Message { get; set; } = "";
}

public sealed class AdapterInfo
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Up { get; set; }
    public bool HasGateway { get; set; }
    public string[] Dns { get; set; } = [];
    public override string ToString() => Name + (Up ? " — подключён" : " — отключён");
}

public sealed class AppStatus
{
    // Empty means an older service that does not publish its version.
    public string ServiceVersion { get; set; } = "";
    public bool ServiceConnected { get; set; }
    public bool Monitoring { get; set; }
    public bool SystemDnsManaged { get; set; }
    public bool DnsRecoveryPending { get; set; }
    public bool AllUnavailable { get; set; }
    public string State { get; set; } = "Наблюдение";
    public string SelectionReason { get; set; } = "";
    public string? ActiveProfileId { get; set; }
    public string? Error { get; set; }
    public AppConfig Config { get; set; } = AppConfig.Defaults();
    public List<ProfileStats> Statistics { get; set; } = [];
    public List<LogEntry> Events { get; set; } = [];
    public List<AdapterInfo> Adapters { get; set; } = [];
}

public sealed class RpcRequest
{
    public string Command { get; set; } = "status";
    public AppConfig? Config { get; set; }
    public string? ProfileId { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public HistoryQuery? History { get; set; }
}
public sealed class RpcReply
{
    public bool Ok { get; set; }
    public string? Error { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public AppStatus? Status { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public HistoryReport? History { get; set; }
    [JsonIgnore(Condition=JsonIgnoreCondition.WhenWritingNull)]
    public DnsShareOffer? Share { get; set; }
}

public static class JsonData
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false, PropertyNameCaseInsensitive = true,
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        Converters = { new JsonStringEnumConverter() }
    };
    public static string Encode<T>(T value) => JsonSerializer.Serialize(value, Options);
    public static T Decode<T>(string json) => JsonSerializer.Deserialize<T>(json, Options) ?? throw new InvalidDataException("Пустой JSON.");
    public static T Clone<T>(T value) => Decode<T>(Encode(value));
}
