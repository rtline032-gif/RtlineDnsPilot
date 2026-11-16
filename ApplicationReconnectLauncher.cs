using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;

namespace NetPilot;

internal enum ApplicationReconnectOutcome { Completed, NoConnections, Partial, Ipv6Remaining, Cancelled }
internal sealed record ApplicationReconnectRequest(ApplicationReconnectTargets Targets, int CallerId, long CallerStartUtcTicks);

internal sealed class ApplicationReconnectLauncher
{
    internal const string Command = "--reconnect-applications";
    private readonly Func<ProcessStartInfo, Task<int>> _launch;
    public ApplicationReconnectLauncher() : this(LaunchAsync) { }
    internal ApplicationReconnectLauncher(Func<ProcessStartInfo, Task<int>> launch) => _launch = launch;

    internal static ProcessStartInfo StartInfo(string executable, ApplicationReconnectRequest request)
    {
        Validate(request);
        var info = new ProcessStartInfo(Path.GetFullPath(executable))
        { UseShellExecute = true, Verb = "runas", WindowStyle = ProcessWindowStyle.Hidden };
        foreach (var argument in new[] { Command, "--targets", ((int)request.Targets).ToString(CultureInfo.InvariantCulture),
            "--caller-pid", request.CallerId.ToString(CultureInfo.InvariantCulture),
            "--caller-start", request.CallerStartUtcTicks.ToString(CultureInfo.InvariantCulture) })
            info.ArgumentList.Add(argument);
        return info;
    }

    internal static ApplicationReconnectRequest Parse(string[] args)
    {
        if (args.Length != 7 || args[0] != Command || args[1] != "--targets" || args[3] != "--caller-pid" || args[5] != "--caller-start"
            || !int.TryParse(args[2], NumberStyles.None, CultureInfo.InvariantCulture, out var targets)
            || !int.TryParse(args[4], NumberStyles.None, CultureInfo.InvariantCulture, out var pid)
            || !long.TryParse(args[6], NumberStyles.None, CultureInfo.InvariantCulture, out var started))
            throw new ArgumentException("Некорректный запрос переподключения приложений.");
        var request = new ApplicationReconnectRequest((ApplicationReconnectTargets)targets, pid, started);
        Validate(request);
        return request;
    }

    private static void Validate(ApplicationReconnectRequest request)
    {
        if (request.Targets == ApplicationReconnectTargets.None
            || (request.Targets & ~(ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex)) != 0
            || request.CallerId <= 0 || request.CallerStartUtcTicks <= 0 || request.CallerStartUtcTicks > DateTime.MaxValue.Ticks)
            throw new ArgumentException("Выберите Chrome или Codex для переподключения.");
    }

    internal async Task<ApplicationReconnectOutcome> RunAsync(ApplicationReconnectTargets targets)
    {
        using var caller = Process.GetCurrentProcess();
        var info = StartInfo(Path.Combine(AppContext.BaseDirectory, "RtlineDnsPilot.exe"),
            new(targets, caller.Id, caller.StartTime.ToUniversalTime().Ticks));
        int code;
        try { code = await _launch(info); }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { return ApplicationReconnectOutcome.Cancelled; }
        return code switch
        {
            0 => ApplicationReconnectOutcome.Completed,
            10 => ApplicationReconnectOutcome.NoConnections,
            11 => ApplicationReconnectOutcome.Partial,
            12 => ApplicationReconnectOutcome.Ipv6Remaining,
            _ => throw new IOException("Переподключение не завершено. Подробности показаны в окне помощника; повторный сброс автоматически не выполнялся.")
        };
    }

    private static async Task<int> LaunchAsync(ProcessStartInfo info)
    {
        using var child = Process.Start(info) ?? throw new IOException("Не удалось запустить помощник переподключения.");
        await child.WaitForExitAsync();
        return child.ExitCode;
    }

    internal static int ResultCode(ApplicationReconnectResult result) => result.Failed > 0 ? 11
        : result.SkippedIpv6 > 0 ? 12 : result.Disconnected == 0 ? 10 : 0;

    internal static string ResultMessage(ApplicationReconnectResult result) =>
        "Системный DNS-кэш очищен.\r\n\r\n"
        + $"Найдено процессов выбранных приложений: {result.MatchedProcesses}.\r\n"
        + $"Сброшено TCP/IPv4-соединений HTTPS: {result.Disconnected}.\r\n"
        + $"Уже закрыты или изменились: {result.AlreadyClosed}. Ошибок: {result.Failed}.\r\n"
        + $"Соединений IPv6 оставлено без изменений: {result.SkippedIpv6}.\r\n\r\n"
        + "Окна приложений не закрывались. Повторите запрос или обновите страницу.\r\n"
        + "QUIC и внутренний DNS-кэш приложений не сбрасываются. Если переподключение не помогло, может потребоваться полный перезапуск приложения.";
}
