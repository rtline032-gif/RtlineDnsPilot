using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace NetPilot;

public sealed class DnsQueryDiagnostic
{
    public string QueryType { get; set; } = "";
    public string State { get; set; } = "not-started";
    public double ElapsedMs { get; set; }
    public int? ResponseCode { get; set; }
    public List<string> Addresses { get; set; } = [];
    public string Detail { get; set; } = "";
}

public sealed class ProbePhaseDiagnostic
{
    public string State { get; set; } = "not-started";
    public double? ElapsedMs { get; set; }
    public string Detail { get; set; } = "";
}

public sealed class HttpsAttemptDiagnostic
{
    public string Address { get; set; } = "";
    public string Family { get; set; } = "";
    public int TcpAttempts { get; set; }
    public string Outcome { get; set; } = "not-started";
    public string Detail { get; set; } = "";
    public ProbePhaseDiagnostic Tcp { get; set; } = new();
    public ProbePhaseDiagnostic Tls { get; set; } = new();
    public ProbePhaseDiagnostic Http { get; set; } = new();
    public int? HttpStatus { get; set; }
    public string? HttpVersion { get; set; }
}

/// <summary>Bounded, latest-measurement metadata; never used to decide reachability.</summary>
public sealed class ProbeDiagnostics
{
    public List<DnsQueryDiagnostic> Dns { get; set; } = [];
    public List<HttpsAttemptDiagnostic> Attempts { get; set; } = [];

    public ProbeDiagnostics BoundedCopy() => new()
    {
        Dns = (Dns ?? []).Take(2).Where(x => x is not null).Select(x => new DnsQueryDiagnostic
        {
            QueryType = x.QueryType is "A" or "AAAA" ? x.QueryType : "?", State = KnownState(x.State), ElapsedMs = Time(x.ElapsedMs) ?? 0,
            ResponseCode = x.ResponseCode is >= 0 and <= 15 ? x.ResponseCode : null,
            Addresses = (x.Addresses ?? []).Take(64).Where(a => IPAddress.TryParse(a, out _)).Select(a => IPAddress.Parse(a).ToString()).ToList(), Detail = Clip(x.Detail, 256)
        }).ToList(),
        Attempts = (Attempts ?? []).Take(64).Where(x => x is not null).Select(x => new HttpsAttemptDiagnostic
        {
            Address = IPAddress.TryParse(x.Address, out var ip) ? ip.ToString() : "?", Family = x.Family is "IPv4" or "IPv6" ? x.Family : "?", TcpAttempts = Math.Clamp(x.TcpAttempts, 0, 1024),
            Outcome = x.Outcome == "http-response" ? "http-response" : KnownState(x.Outcome), Detail = Clip(x.Detail, 256), Tcp = Copy(x.Tcp), Tls = Copy(x.Tls), Http = Copy(x.Http),
            HttpStatus = x.HttpStatus is >= 100 and <= 599 ? x.HttpStatus : null,
            HttpVersion = x.HttpVersion is "1.0" or "1.1" or "2.0" or "3.0" ? x.HttpVersion : null
        }).ToList()
    };

    private static ProbePhaseDiagnostic Copy(ProbePhaseDiagnostic? phase) => phase is null ? new() : new()
    { State = KnownState(phase.State), ElapsedMs = Time(phase.ElapsedMs), Detail = Clip(phase.Detail, 160) };
    private static string KnownState(string? value) => value is "not-started" or "running" or "success" or "empty" or "rejected" or "error" or "timeout" ? value : "unknown";
    private static double? Time(double? value) => value is { } number && double.IsFinite(number) && number >= 0 ? number : null;
    internal static string Clip(string? value, int maximum)
    {
        if (value is null) return "";
        if (value.Length <= maximum) return value;
        if (maximum > 0 && char.IsHighSurrogate(value[maximum - 1])) maximum--;
        return value[..maximum];
    }
    internal string FailureSummary()
    {
        // Keep both ends of the observed sequence ahead of the verbose exception chain.
        // The bounded ordinary service status must not hide IPv4 TLS behind a final IPv6 TCP error.
        var failures = BoundedCopy().Attempts.Select(attempt =>
        {
            var (name, phase) = attempt.Http.State is "error" or "timeout" ? ("HTTP", attempt.Http)
                : attempt.Tls.State is "error" or "timeout" ? ("TLS", attempt.Tls) : ("TCP", attempt.Tcp);
            if (phase.State is not ("error" or "timeout") || attempt.Family == "?" || attempt.Address == "?") return "";
            return attempt.Family + " " + name + (phase.State == "timeout" ? " тайм-аут" : " ошибка") + " (" + attempt.Address + ")";
        }).Where(text => text.Length > 0).ToArray();
        if (failures.Length == 0) return "";
        var summary = failures[0];
        if (failures.Length > 1) summary += "; " + failures[^1];
        if (failures.Length > 2) summary += "; ещё " + (failures.Length - 2) + " попыток — в диагностике";
        return Clip(summary, 128);
    }
    public static string Format(ProbeDiagnostics value)
    {
        var data = value.BoundedCopy();
        var text = new StringBuilder("DNS\r\n");
        foreach (var dns in data.Dns)
        {
            text.Append(dns.QueryType).Append(": ").Append(State(dns.State)).Append(" · ").Append(Milliseconds(dns.ElapsedMs));
            if (dns.ResponseCode is { } code) text.Append(" · RCODE ").Append(code);
            text.AppendLine().Append("Адреса: ").AppendLine(dns.Addresses.Count == 0 ? "нет" : string.Join(", ", dns.Addresses));
            if (dns.Detail.Length > 0) text.AppendLine(dns.Detail);
        }
        if (data.Dns.Count == 0) text.AppendLine("DNS-запросы не наблюдались.");
        text.AppendLine().AppendLine("Подключения к целевым IP");
        foreach (var attempt in data.Attempts)
        {
            text.Append(attempt.Family).Append(" · ").Append(attempt.Address);
            if (attempt.TcpAttempts > 1) text.Append(" · TCP-попыток: ").Append(attempt.TcpAttempts);
            text.AppendLine();
            Phase("TCP", attempt.Tcp); Phase("TLS", attempt.Tls); Phase("HTTP-заголовки", attempt.Http);
            if (attempt.HttpStatus is { } code) text.Append("Ответ: HTTP ").Append(code).Append(" · версия ").AppendLine(attempt.HttpVersion ?? "не определена");
            if (attempt.Detail.Length > 0) text.AppendLine(attempt.Detail);
            text.AppendLine();
        }
        if (data.Attempts.Count == 0) text.AppendLine("Попыток HTTPS не было.");
        text.Append("Показаны выполненные попытки. После первого HTTP-ответа остальные IP не проверяются.\r\nTLS фиксируется после штатного рукопожатия и проверки сертификата. Получение заголовков не подтверждает вход, тело ответа или генерацию.");
        return text.ToString();

        void Phase(string name, ProbePhaseDiagnostic phase)
        {
            text.Append(name).Append(": ").Append(State(phase.State));
            if (phase.ElapsedMs is { } ms) text.Append(" · ").Append(Milliseconds(ms));
            if (phase.Detail.Length > 0) text.Append(" · ").Append(phase.Detail);
            text.AppendLine();
        }
    }
    private static string Milliseconds(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + " мс";
    private static string State(string value) => value switch
    {
        "success" => "успешно", "empty" => "корректный пустой ответ", "rejected" => "непригодные адреса",
        "error" => "ошибка", "timeout" => "тайм-аут", "running" => "не завершено",
        "not-started" => "не начиналось", _ => "нет наблюдения"
    };
}

// Hooks observe the existing handler; they neither authenticate TLS nor read/write the HTTP stream.
internal sealed class ProbeAttemptTrace
{
    private readonly object _gate = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _phaseStarted;
    private string _phase = "unknown";
    private bool _finished;
    internal HttpsAttemptDiagnostic Data { get; }
    internal ProbeAttemptTrace(IPAddress address)
    {
        Data = new() { Address = address.ToString(), Family = address.AddressFamily == AddressFamily.InterNetwork ? "IPv4" : "IPv6" };
    }
    internal void TcpStarted()
    {
        lock (_gate)
        {
            if (_finished) return;
            Data.TcpAttempts++;
            Data.Tcp = new() { State = "running" }; Data.Tls = new(); Data.Http = new();
            Data.Outcome = "running"; _phase = "tcp"; _phaseStarted = _clock.Elapsed.TotalMilliseconds;
        }
    }
    internal void TcpConnected()
    {
        lock (_gate)
        {
            if (_finished) return;
            double now = _clock.Elapsed.TotalMilliseconds;
            Data.Tcp.State = "success"; Data.Tcp.ElapsedMs = Math.Max(0, now - _phaseStarted);
            Data.Tls.State = "running"; _phase = "tls"; _phaseStarted = now;
        }
    }
    internal void TlsCompleted(Version version)
    {
        lock (_gate)
        {
            if (_finished) return;
            double now = _clock.Elapsed.TotalMilliseconds;
            Data.Tls.State = "success"; Data.Tls.ElapsedMs = Math.Max(0, now - _phaseStarted);
            Data.Http.State = "running"; Data.HttpVersion = version.ToString(); _phase = "http"; _phaseStarted = now;
        }
    }
    internal void HttpReceived(HttpResponseMessage response)
    {
        lock (_gate)
        {
            if (_finished) return;
            if (_phase == "unknown") { Data.Tcp.State = "unknown"; Data.Tls.State = "unknown"; }
            Data.Http.State = "success";
            Data.Http.ElapsedMs = _phase == "http" ? Math.Max(0, _clock.Elapsed.TotalMilliseconds - _phaseStarted) : null;
            Data.HttpStatus = (int)response.StatusCode; Data.HttpVersion = response.Version.ToString();
            Data.Http.Detail = "Получены заголовки ответа."; Data.Outcome = "http-response"; _finished = true;
        }
    }
    internal void Failed(Exception error)
    {
        lock (_gate)
        {
            if (_finished) return;
            string state = IsTimeout(error) ? "timeout" : "error";
            string detail = ProbeDiagnostics.Clip(DnsTransport.DescribeFailure(error), 256);
            ProbePhaseDiagnostic? phase = _phase switch { "tcp" => Data.Tcp, "tls" => Data.Tls, "http" => Data.Http, _ => null };
            if (phase is not null)
            { phase.State = state; phase.ElapsedMs = Math.Max(0, _clock.Elapsed.TotalMilliseconds - _phaseStarted); phase.Detail = ProbeDiagnostics.Clip(detail, 160); }
            else { Data.Tcp.State = "unknown"; Data.Tls.State = "unknown"; }
            Data.Outcome = state; Data.Detail = detail; _finished = true;
        }
    }
    internal static bool IsTimeout(Exception error)
    {
        for (Exception? current = error; current is not null; current = current.InnerException)
            if (current is OperationCanceledException or TimeoutException || current is SocketException { SocketErrorCode: SocketError.TimedOut }) return true;
        return false;
    }
}
