using System.Text;

namespace NetPilot;

public sealed class ChannelDiagnosticsSnapshot
{
    public string ProfileId { get; set; } = "";
    public string ProfileName { get; set; } = "";
    public bool ProfileEnabled { get; set; }
    public string MonitoringKey { get; set; } = "";
    public string ContextKey { get; set; } = "";
    public string Source { get; set; } = "";
    public DateTimeOffset? At { get; set; }
    public bool? Success { get; set; }
    public List<MonitoringTarget> Targets { get; set; } = [];
    public List<ServiceProbeResult> Services { get; set; } = [];
    public string Detail { get; set; } = "";
}

internal static class ChannelDiagnostics
{
    internal const int MaximumReportCharacters = 512 * 1024;

    // The caller holds the engine's state lock. Only the requested profile's
    // latest measurement in the current endpoint context is copied.
    internal static ChannelDiagnosticsSnapshot Create(AppConfig config, IReadOnlyList<ProfileStats> statistics,
        string profileId, DateTimeOffset now, string source)
    {
        if ((profileId?.Length ?? 0) > 68 || !Guid.TryParse(profileId, out var requested))
            throw new ArgumentException("Укажите GUID канала для чтения диагностики.", nameof(profileId));
        var profile = config.Profiles.FirstOrDefault(p => Guid.TryParse(p.Id, out var id) && id == requested)
            ?? throw new ArgumentException("Канал отсутствует в текущих настройках.", nameof(profileId));
        var monitoringKey = MonitoringTargets.Key(config);
        var selected = MonitoringTargets.Selected(config);
        var sample = statistics.FirstOrDefault(s => s.ProfileId == profile.Id)?.Samples
            .LastOrDefault(s => MonitoringTargets.Matches(s, config));
        var snapshot = new ChannelDiagnosticsSnapshot
        {
            ProfileId = profile.Id, ProfileName = profile.Name, ProfileEnabled = profile.Enabled,
            MonitoringKey = monitoringKey, ContextKey = ChartHistoryArchive.ContextKey(profile, monitoringKey), Source = source,
            Targets = selected.ToList(), At = sample?.At, Success = sample?.Success
        };
        var notices = new List<string>();
        if (!profile.Enabled) notices.Add("Канал выключен. Показано только сохранённое измерение, новые проверки не запускаются.");
        if (sample is null)
            notices.Add("Нет сохранённого измерения для текущих настроек канала и выбранных объектов проверок.");
        else
        {
            if (sample.At > now)
                notices.Add("Время измерения находится в будущем. Его свежесть не подтверждена; проверьте часы.");
            else if ((now - sample.At).TotalSeconds > MonitoringTargets.FreshnessSeconds(config))
                notices.Add("Измерение устарело и не подтверждает текущую доступность канала.");
            if (sample.ServiceDetailsOmitted)
                notices.Add("Подробности этого измерения не сохранены. Дождитесь следующей штатной проверки.");
            foreach (var target in selected)
                if (MonitoringTargets.Service(sample, target.Id) is { } service)
                    snapshot.Services.Add(CopyService(service));
            if (snapshot.Services.Count < selected.Length)
                notices.Add("Не для всех выбранных объектов есть сохранённые результаты.");
        }
        notices.Add("Это чтение последнего результата. Новая сетевая проверка не выполняется. Вход в аккаунт, генерация и streaming не проверяются.");
        snapshot.Detail = string.Join("\r\n", notices);
        return JsonData.Clone(snapshot);
    }

    internal static ChannelDiagnosticsSnapshot BoundedCopy(ChannelDiagnosticsSnapshot value) => new()
    {
        ProfileId = Limit(value.ProfileId, 68), ProfileName = Limit(value.ProfileName, 100), ProfileEnabled = value.ProfileEnabled,
        MonitoringKey = Limit(value.MonitoringKey, 64), ContextKey = Limit(value.ContextKey, 64), Source = Limit(value.Source, 100),
        At = value.At, Success = value.Success, Detail = Limit(value.Detail, 2000),
        Targets = value.Targets.Take(9).Select(t => new MonitoringTarget(Limit(t.Id, 68), Limit(t.Name, 100), Limit(t.Url, 2048))).ToList(),
        Services = value.Services.Take(9).Select(CopyService).ToList()
    };

    private static ServiceProbeResult CopyService(ServiceProbeResult service) => new()
    {
        ServiceId = Limit(service.ServiceId, 68), At = service.At, Success = service.Success,
        DnsMs = service.DnsMs, HttpsMs = service.HttpsMs, HttpStatus = service.HttpStatus,
        Stage = Limit(service.Stage, 100), Detail = Limit(service.Detail, 2000),
        CountryCode = service.CountryCode is null ? null : Limit(service.CountryCode, 2), CountrySource = service.CountrySource,
        Diagnostics = service.Diagnostics?.BoundedCopy()
    };

    internal static string Format(ChannelDiagnosticsSnapshot value)
    {
        var snapshot = BoundedCopy(value);
        var text = new StringBuilder();
        text.AppendLine("Канал: " + snapshot.ProfileName);
        text.AppendLine("ID: " + snapshot.ProfileId);
        text.AppendLine("Источник: " + snapshot.Source);
        text.AppendLine("Измерение: " + (snapshot.At is { } at ? at.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz") : "нет данных"));
        text.AppendLine("Общий результат: " + (snapshot.Success is { } success ? success ? "успех" : "ошибка" : "нет данных"));
        text.AppendLine(snapshot.Detail);
        foreach (var target in snapshot.Targets)
        {
            text.AppendLine();
            text.AppendLine("=== " + target.Name + " ===");
            text.AppendLine(target.Url);
            var service = snapshot.Services.FirstOrDefault(s => s.ServiceId == target.Id);
            if (service is null) { text.AppendLine("Нет сохранённого результата этого объекта."); continue; }
            text.AppendLine("Результат: " + (service.Success ? "успех" : "ошибка") + " · этап: " + service.Stage
                + (service.HttpStatus is { } status ? " · HTTP " + status : ""));
            text.AppendLine("Время объекта: " + service.At.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz"));
            if (!string.IsNullOrWhiteSpace(service.Detail)) text.AppendLine(service.Detail);
            if (service.Diagnostics is { } diagnostics) text.AppendLine(ProbeDiagnostics.Format(diagnostics));
            else text.AppendLine("Подробные DNS-ответы и фазы TCP / TLS / HTTP для этого измерения не сохранены. Дождитесь следующей штатной проверки.");
        }
        var report = text.ToString();
        return report.Length <= MaximumReportCharacters ? report
            : report[..MaximumReportCharacters] + "\r\n[Текст диагностики ограничен по размеру.]";
    }

    private static string Limit(string? value, int length) => string.IsNullOrEmpty(value) ? "" : value.Length <= length ? value : ProbeDiagnostics.Clip(value, length - 1) + "…";
}
