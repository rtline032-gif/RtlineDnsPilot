using System.Security.Cryptography;
using System.Text;

namespace NetPilot;

public sealed record MonitoringTarget(string Id,string Name,string Url);

public sealed class MonitoringEndpoint
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "Новый сервис";
    public string Url { get; set; } = "";
    public bool Enabled { get; set; } = true;
}

public sealed class ServiceProbeResult
{
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public string? CountryCode { get; set; }
    [System.Text.Json.Serialization.JsonIgnore(Condition=System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingDefault)]
    public ConnectionCountrySource CountrySource { get; set; }
    public string ServiceId { get; set; } = "";
    public DateTimeOffset At { get; set; } = DateTimeOffset.UtcNow;
    public bool Success { get; set; }
    public double DnsMs { get; set; }
    public double HttpsMs { get; set; }
    public int? HttpStatus { get; set; }
    public string Stage { get; set; } = "";
    public string Detail { get; set; } = "";
}

public static class MonitoringTargets
{
    public const string ChatGpt = "chatgpt";
    public const string Gemini = "gemini";
    public const string Tailscale = "tailscale";
    public const string DefaultChatGptUrl = "https://chatgpt.com/cdn-cgi/trace";
    public const string GeminiUrl = "https://gemini.google.com/";
    public const string TailscaleUrl = "https://login.tailscale.com/";
    public const int MaxAdditionalTargets = 6;
    public static string Name(string id) => id==ChatGpt?"ChatGPT / Codex":id==Gemini?"Gemini":id==Tailscale?"Tailscale":id;
    public static MonitoringTarget[] Selected(AppConfig config) =>
        (config.MonitorChatGpt ? new[] {new MonitoringTarget(ChatGpt,Name(ChatGpt),config.ProbeUrl)} : [])
        .Concat(config.MonitorGemini ? new[] {new MonitoringTarget(Gemini,Name(Gemini),GeminiUrl)} : [])
        .Concat(config.MonitorTailscale ? new[] {new MonitoringTarget(Tailscale,Name(Tailscale),TailscaleUrl)} : [])
        .Concat(config.AdditionalTargets.Where(t=>t.Enabled).Select(t=>new MonitoringTarget(t.Id,t.Name,t.Url))).ToArray();
    public static void Validate(AppConfig config)
    {
        ValidateUrl(config.ProbeUrl);
        if(config.AdditionalTargets is null || config.AdditionalTargets.Count>MaxAdditionalTargets)
            throw new ArgumentException($"Можно добавить не более {MaxAdditionalTargets} собственных сервисов мониторинга.");
        var ids=new HashSet<Guid>();
        var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase) {Name(ChatGpt),"ChatGPT","Codex",Name(Gemini),Name(Tailscale)};
        var urls=new HashSet<string>(StringComparer.Ordinal) {new Uri(config.ProbeUrl).AbsoluteUri,new Uri(GeminiUrl).AbsoluteUri,new Uri(TailscaleUrl).AbsoluteUri};
        foreach(var endpoint in config.AdditionalTargets)
        {
            if(endpoint is null || (endpoint.Id?.Length??0)>68 || !Guid.TryParse(endpoint.Id,out var id))
                throw new ArgumentException("У сервиса мониторинга должен быть уникальный GUID.");
            if(!ids.Add(id)) throw new ArgumentException("ID сервиса мониторинга повторяется.");
            if(string.IsNullOrWhiteSpace(endpoint.Name) || endpoint.Name.Length>100)
                throw new ArgumentException("Название сервиса мониторинга должно содержать от 1 до 100 символов.");
            if(!names.Add(endpoint.Name.Trim())) throw new ArgumentException("Название сервиса мониторинга уже используется.");
            ValidateUrl(endpoint.Url);
            if(!urls.Add(new Uri(endpoint.Url).AbsoluteUri)) throw new ArgumentException("Этот HTTPS-адрес уже добавлен в мониторинг.");
        }
        if(Selected(config).Length==0) throw new ArgumentException("Выберите хотя бы один сервис для мониторинга.");
    }
    private static void ValidateUrl(string? value)
    {
        if((value?.Length??0)>2048 || !Uri.TryCreate(value,UriKind.Absolute,out var uri) || uri.Scheme!="https" || uri.HostNameType!=UriHostNameType.Dns
            || !uri.IsWellFormedOriginalString() || uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0)
            throw new ArgumentException("Проверочный адрес должен быть HTTPS URL с именем сервера, без паролей, параметров и фрагмента.");
    }
    public static string Key(AppConfig config) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        string.Join("\n",Selected(config).Select(t=>(Id:Guid.TryParse(t.Id,out var id)?id.ToString("N"):t.Id,Url:new Uri(t.Url).AbsoluteUri))
            .OrderBy(t=>t.Id,StringComparer.Ordinal).Select(t=>t.Id+"|"+t.Url)))));
    public static bool Matches(ProbeResult sample,AppConfig config) => sample.MonitoringKey.Length==0
        ? Selected(config) is [var target] && target.Id==ChatGpt && new Uri(target.Url).AbsoluteUri==DefaultChatGptUrl
        : sample.MonitoringKey==Key(config);
    public static int FreshnessSeconds(AppConfig config) => Math.Max(Math.Max(60,config.IntervalSeconds*3),
        (int)Math.Ceiling(config.Profiles.Count(p=>p.Enabled)*Selected(config).Length/4.0)*config.TimeoutSeconds+config.IntervalSeconds);
    public static bool ManualIsActive(AppConfig config,DateTimeOffset now) => config.ManualProfileId is not null
        && (config.ManualUntil is null || config.ManualUntil>now);
    public static ProfileStats? View(ProfileStats? source,AppConfig config)
    {
        if(source is null) return null;
        var samples=source.Samples.Where(s=>Matches(s,config)).ToList();
        bool lastMatches=source.Last is { } last && Matches(last,config);
        return new ProfileStats { ProfileId=source.ProfileId,Samples=samples,TotalChecks=source.TotalChecks,TotalSuccesses=source.TotalSuccesses,
            ConsecutiveFailures=lastMatches?source.ConsecutiveFailures:0,ConsecutiveSuccesses=lastMatches?source.ConsecutiveSuccesses:0 };
    }
    public static ServiceProbeResult? Service(ProbeResult? sample,string id)
    {
        if(sample is null) return null;
        if(sample.ServiceDetailsOmitted) return null;
        if(sample.Services.FirstOrDefault(s=>s.ServiceId==id) is { } service) return service;
        return id==ChatGpt && sample.Services.Count==0
            ? new ServiceProbeResult {ServiceId=id,At=sample.At,Success=sample.Success,DnsMs=sample.DnsMs,HttpsMs=sample.HttpsMs,
                HttpStatus=sample.HttpStatus,Stage=sample.Stage,Detail=sample.Detail,CountryCode=sample.CountryCode,CountrySource=sample.CountrySource} : null;
    }
    public static string ServiceSummary(ServiceProbeResult? result) => result is null ? "Нет измерений"
        : (result.Success?"Доступен":"Недоступен")+(result.HttpStatus is {} code?" · HTTP "+code:" · "+result.Stage);
    public static ProbeResult Combine(string profileId,AppConfig config,IReadOnlyList<ServiceProbeResult> services)
    {
        var selected=Selected(config);
        bool complete=selected.Length>0 && services.Count==selected.Length && selected.All(t=>services.Count(s=>s.ServiceId==t.Id)==1);
        var failed=services.FirstOrDefault(s=>!s.Success);
        return new ProbeResult {ProfileId=profileId,MonitoringKey=Key(config),Services=services.ToList(),
            At=services.Count==0?DateTimeOffset.UtcNow:services.Min(s=>s.At),Success=complete && services.All(s=>s.Success),
            DnsMs=services.Sum(s=>s.DnsMs),HttpsMs=services.Sum(s=>s.HttpsMs),
            HttpStatus=services.Count==1?services[0].HttpStatus:null,
            CountryCode=services.Count==1?services[0].CountryCode:null,
            CountrySource=services.Count==1?services[0].CountrySource:ConnectionCountrySource.None,
            Stage=!complete?"incomplete":failed is null?"http":failed.ServiceId+"/"+failed.Stage,
            Detail=string.Join("\r\n",services.Select(s=>(selected.FirstOrDefault(t=>t.Id==s.ServiceId)?.Name??Name(s.ServiceId))+": "+ServiceSummary(s)+". "+s.Detail))
                +(!complete?"\r\nНе все выбранные сервисы проверены.":"")};
    }
    public static async Task<List<ProbeResult>> CheckAsync(AppConfig config,
        Func<ChannelProfile,string,TimeSpan,CancellationToken,Task<ProbeResult>> check,CancellationToken token)
    {
        using var parallel=new SemaphoreSlim(4);
        var profiles=config.Profiles.Where(p=>p.Enabled).ToArray();
        var selected=Selected(config);
        var jobs=profiles.SelectMany(profile=>selected.Select(async target=>
        {
            await parallel.WaitAsync(token);
            try
            {
                token.ThrowIfCancellationRequested();
                var result=await check(profile,target.Url,TimeSpan.FromSeconds(config.TimeoutSeconds),token);
                return (profile.Id,Service:new ServiceProbeResult {ServiceId=target.Id,At=result.At,Success=result.Success,
                    DnsMs=result.DnsMs,HttpsMs=result.HttpsMs,HttpStatus=result.HttpStatus,Stage=result.Stage,Detail=result.Detail,
                    CountryCode=result.CountryCode,CountrySource=result.CountrySource});
            }
            finally {parallel.Release();}
        })).ToArray();
        var results=await Task.WhenAll(jobs);
        token.ThrowIfCancellationRequested();
        return profiles.Select(p=>Combine(p.Id,config,results.Where(r=>r.Id==p.Id).Select(r=>r.Service).ToArray())).ToList();
    }
}
