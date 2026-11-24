using System.Net;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Text;

namespace NetPilot;

public sealed class DnsUpstreamDiagnostic
{
    public string Server { get; set; } = "";
    public int Port { get; set; }
    public string Transport { get; set; } = "";
    public string Stage { get; set; } = "";
    public string Outcome { get; set; } = "";
    public string? ErrorCode { get; set; }
    public int? ResponseCode { get; set; }
    public int? HttpStatus { get; set; }
    public bool TcpFallback { get; set; }
    public double ElapsedMs { get; set; }
}

public sealed class RelayRequestDiagnostic
{
    public long RequestId { get; set; }
    public DateTimeOffset At { get; set; }
    public DateTimeOffset CompletedAt { get; set; }
    public int TransactionId { get; set; }
    public int QueryType { get; set; }
    public int QueryClass { get; set; }
    public string ClientTransport { get; set; } = "";
    public string LocalFamily { get; set; } = "";
    public string? ProfileId { get; set; }
    public string? ProfileContext { get; set; }
    public List<DnsUpstreamDiagnostic> UpstreamAttempts { get; set; } = [];
    public int? ReturnedCode { get; set; }
    public string Stage { get; set; } = "received";
    public string Outcome { get; set; } = "";
    public string? ErrorCode { get; set; }
    public bool ResponseQueued { get; set; }
    public double ElapsedMs { get; set; }
}

public sealed class RelayDiagnosticsSnapshot
{
    public string ServiceVersion { get; set; } = "";
    public string SessionId { get; set; } = "";
    public DateTimeOffset CapturedAt { get; set; }
    public bool ServiceMode { get; set; }
    public bool RelayRunning { get; set; }
    public bool SystemDnsManaged { get; set; }
    public bool RecoveryPending { get; set; }
    public string? ActiveProfileId { get; set; }
    public long Received { get; set; }
    public long Completed { get; set; }
    public long ResponsesQueued { get; set; }
    public long NegativeOrError { get; set; }
    public long InFlight { get; set; }
    public int RetentionSeconds { get; set; } = RelayDiagnosticsStore.RetentionSeconds;
    public List<RelayRequestDiagnostic> Requests { get; set; } = [];
}

/// <summary>Bounded process-local metadata only. Query names and packets never enter this store.</summary>
internal sealed class RelayDiagnosticsStore(Func<DateTimeOffset>? clock = null)
{
    internal const int MaximumLatest = 32;
    internal const int MaximumErrors = 32;
    internal const int RetentionSeconds = 300;
    private readonly object _sync = new();
    private readonly Queue<RelayRequestDiagnostic> _latest = new();
    private readonly Queue<RelayRequestDiagnostic> _errors = new();
    private readonly Func<DateTimeOffset> _clock = clock ?? (() => DateTimeOffset.UtcNow);
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private long _received, _completed, _queued, _negative, _inFlight;

    internal RelayRequestTrace Begin(byte[] query, string transport, AddressFamily family)
    {
        var metadata = DnsWire.QueryMetadata(query);
        lock (_sync)
        {
            _received++; _inFlight++;
            return new(this, new RelayRequestDiagnostic
            {
                RequestId = _received, At = _clock(), TransactionId = metadata.Id,
                QueryType = metadata.Type, QueryClass = metadata.Class,
                ClientTransport = transport == "TCP" ? "TCP" : "UDP",
                LocalFamily = family == AddressFamily.InterNetworkV6 ? "IPv6" : "IPv4"
            });
        }
    }

    internal void Complete(RelayRequestDiagnostic request)
    {
        lock (_sync)
        {
            request.CompletedAt = _clock();
            _completed++; _inFlight--; if (request.ResponseQueued) _queued++;
            _latest.Enqueue(RelayDiagnostics.Copy(request));
            while (_latest.Count > MaximumLatest) _latest.Dequeue();
            if (!request.ResponseQueued || request.ReturnedCode != 0)
            {
                _negative++; _errors.Enqueue(RelayDiagnostics.Copy(request));
                while (_errors.Count > MaximumErrors) _errors.Dequeue();
            }
            Prune(_clock());
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var queue in new[] { _latest, _errors })
            while (queue.TryPeek(out var row) && (now - row.CompletedAt).TotalSeconds > RetentionSeconds) queue.Dequeue();
    }

    internal RelayDiagnosticsSnapshot Snapshot()
    {
        lock (_sync)
        {
            var now = _clock(); Prune(now);
            return new()
            {
                SessionId = _sessionId, CapturedAt = now, Received = _received, Completed = _completed,
                ResponsesQueued = _queued, NegativeOrError = _negative, InFlight = _inFlight,
                Requests = _latest.Concat(_errors).GroupBy(r => r.RequestId).Select(g => g.First())
                    .OrderBy(r => r.RequestId).Select(RelayDiagnostics.Copy).ToList()
            };
        }
    }
}

internal sealed class RelayRequestTrace(RelayDiagnosticsStore store, RelayRequestDiagnostic data)
{
    private readonly System.Diagnostics.Stopwatch _timer = System.Diagnostics.Stopwatch.StartNew();
    private bool _finished;
    internal bool HasResponse => data.ReturnedCode is not null;
    internal void Profile(ChannelProfile profile)
    {
        data.ProfileId = profile.Id; data.ProfileContext = RelayDiagnostics.Context(profile); data.Stage = "upstream";
        data.ErrorCode = null;
        data.UpstreamAttempts.Clear();
    }
    internal void Upstream(DnsUpstreamDiagnostic attempt)
    { if (!_finished && data.UpstreamAttempts.Count < 8) data.UpstreamAttempts.Add(RelayDiagnostics.Copy(attempt)); }
    internal void Response(byte[] response)
    { data.ReturnedCode = DnsWire.ResponseCode(response); if (data.ErrorCode is null) data.Stage = "local-send"; }
    internal void Failure(string stage, string code)
    { data.Stage = stage; data.ErrorCode = code; }
    internal void Finish(bool queued)
    {
        if (_finished) return;
        _finished = true; data.ResponseQueued = queued; data.ElapsedMs = _timer.Elapsed.TotalMilliseconds;
        data.Outcome = queued ? data.ReturnedCode == 0 ? "response-queued" : "negative-response-queued" : "not-queued";
        store.Complete(data);
    }
}

internal static class RelayDiagnostics
{
    internal static void Validate(RelayDiagnosticsSnapshot value)
    {
        static bool Code(int? code) => code is null or (>= 0 and <= 15);
        static bool Time(double elapsed) => double.IsFinite(elapsed) && elapsed >= 0;
        static bool ErrorCode(string? error) => error is null || System.Text.RegularExpressions.Regex.IsMatch(error, "^[A-Z_0-9-]{1,48}$");
        static bool Identity(string? id) => id is null || id.Length <= 68 && Guid.TryParse(id, out _);
        static bool Hash(string? key) => key is null || key.Length == 64 && key.All(Uri.IsHexDigit);
        string[] stages = ["starting", "udp-connect", "udp-send", "udp-receive", "udp-validate", "tcp-connect", "tcp-send",
            "tcp-receive-length", "tcp-receive-body", "tcp-validate", "doh-request", "doh-headers", "doh-body", "doh-validate"];
        if (!Version.TryParse(value.ServiceVersion, out _) || !Guid.TryParseExact(value.SessionId, "N", out _)
            || value.CapturedAt == default || !Identity(value.ActiveProfileId) || value.Received < 0 || value.Completed < 0
            || value.Completed > value.Received || value.InFlight != value.Received - value.Completed
            || value.ResponsesQueued < 0 || value.ResponsesQueued > value.Completed || value.NegativeOrError < 0
            || value.NegativeOrError > value.Completed || value.RetentionSeconds != RelayDiagnosticsStore.RetentionSeconds
            || value.Requests is null || value.Requests.Count > 64)
            throw new IOException("Некорректный или слишком большой снимок диагностики посредника.");
        var ids = new HashSet<long>();
        foreach (var row in value.Requests)
        {
            if (row is null || row.RequestId < 1 || row.RequestId > value.Received || !ids.Add(row.RequestId)
                || row.At == default || row.CompletedAt == default || row.TransactionId is < 0 or > 65535
                || row.QueryType is < 0 or > 65535 || row.QueryClass is < 0 or > 65535
                || row.ClientTransport is not ("UDP" or "TCP") || row.LocalFamily is not ("IPv4" or "IPv6")
                || !Identity(row.ProfileId) || !Hash(row.ProfileContext) || !Code(row.ReturnedCode)
                || !Time(row.ElapsedMs) || !ErrorCode(row.ErrorCode)
                || row.Stage is not ("received" or "upstream" or "local-send" or "selection" or "capacity")
                || row.Outcome is not ("response-queued" or "negative-response-queued" or "not-queued")
                || row.UpstreamAttempts is null || row.UpstreamAttempts.Count > 8)
                throw new IOException("Некорректные метаданные запроса посредника.");
            var expectedOutcome = row.ResponseQueued ? row.ReturnedCode == 0 ? "response-queued" : "negative-response-queued" : "not-queued";
            if (row.Outcome != expectedOutcome || row.ResponseQueued && row.ReturnedCode is null)
                throw new IOException("Результат передачи ответа посредника противоречит его метаданным.");
            foreach (var upstream in row.UpstreamAttempts)
                if (upstream is null || !IPAddress.TryParse(upstream.Server, out _) || upstream.Server.Length > 80
                    || upstream.Port is < 1 or > 65535 || upstream.Transport is not ("UDP" or "TCP" or "DoH")
                    || !stages.Contains(upstream.Stage, StringComparer.Ordinal)
                    || upstream.Outcome is not ("response" or "negative-response" or "error" or "interrupted")
                    || !Code(upstream.ResponseCode) || upstream.HttpStatus is not (null or (>= 100 and <= 599))
                    || !Time(upstream.ElapsedMs) || !ErrorCode(upstream.ErrorCode))
                    throw new IOException("Некорректные метаданные upstream-попытки.");
        }
    }

    internal static string Context(ChannelProfile profile) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        profile.Mode + "|" + profile.DnsPort + "|" + string.Join(",", profile.Servers.Select(s => IPAddress.Parse(s).ToString()))
        + "|" + (profile.Mode == DnsMode.Doh ? new Uri(profile.DohUrl!).AbsoluteUri : ""))));

    internal static string Error(Exception error, bool cancelled = false)
    {
        var depth = 0;
        for (Exception? current = error; current is not null && depth < 8; current = current.InnerException, depth++)
        {
            if (current is SocketException socket) return "SOCKET_" + socket.NativeErrorCode;
            if (current is AuthenticationException) return "TLS_AUTHENTICATION";
            if (current is OperationCanceledException) return cancelled ? "CANCELLED" : "TIMEOUT";
            if (current is HttpRequestException { StatusCode: { } code }) return "HTTP_" + (int)code;
            if (current is InvalidDataException) return "INVALID_DNS_RESPONSE";
            if (current is ObjectDisposedException) return "DISPOSED";
        }
        return error is ArgumentException ? "INVALID_ARGUMENT" : error is EndOfStreamException ? "EOF" : "IO";
    }

    internal static DnsUpstreamDiagnostic Copy(DnsUpstreamDiagnostic value) => new()
    {
        Server = value.Server, Port = value.Port, Transport = value.Transport, Stage = value.Stage, Outcome = value.Outcome,
        ErrorCode = value.ErrorCode, ResponseCode = value.ResponseCode, HttpStatus = value.HttpStatus,
        TcpFallback = value.TcpFallback, ElapsedMs = value.ElapsedMs
    };
    internal static RelayRequestDiagnostic Copy(RelayRequestDiagnostic value) => new()
    {
        RequestId = value.RequestId, At = value.At, CompletedAt = value.CompletedAt,
        TransactionId = value.TransactionId, QueryType = value.QueryType, QueryClass = value.QueryClass,
        ClientTransport = value.ClientTransport, LocalFamily = value.LocalFamily,
        ProfileId = value.ProfileId, ProfileContext = value.ProfileContext,
        UpstreamAttempts = value.UpstreamAttempts.Take(8).Select(Copy).ToList(), ReturnedCode = value.ReturnedCode,
        Stage = value.Stage, Outcome = value.Outcome, ErrorCode = value.ErrorCode,
        ResponseQueued = value.ResponseQueued, ElapsedMs = value.ElapsedMs
    };

    internal static string Format(RelayDiagnosticsSnapshot snapshot)
    {
        var text = new StringBuilder();
        text.AppendLine("Наблюдение запросов локального DNS-посредника");
        text.AppendLine("Служба: " + snapshot.ServiceVersion + "; снимок: " + snapshot.CapturedAt.ToString("O"));
        text.AppendLine("Посредник: " + (snapshot.RelayRunning ? "запущен" : "не запущен")
            + "; управление DNS: " + snapshot.SystemDnsManaged + "; нужно восстановление: " + snapshot.RecoveryPending);
        text.AppendLine("Активный профиль: " + (snapshot.ActiveProfileId ?? "нет") + "; сеанс: " + snapshot.SessionId);
        text.AppendLine($"За сеанс принято валидных запросов {snapshot.Received}, завершено {snapshot.Completed}, ответов передано ОС {snapshot.ResponsesQueued}, негативных/ошибок {snapshot.NegativeOrError}, выполняется {snapshot.InFlight}.");
        text.AppendLine("Только память: последние 32 завершения и 32 негативных/ошибочных результата, не более 5 минут. Имена сайтов и пакеты не записываются.");
        text.AppendLine("Это наблюдение фактических запросов relay. Windows getaddrinfo, получение ответа приложением и авторизованный Codex здесь не проверяются. NXDOMAIN может быть корректным отрицательным ответом upstream.");
        text.AppendLine("RCODE — четыре бита DNS-заголовка; расширенный код EDNS отдельно не декодируется.");
        if (!snapshot.ServiceMode) text.AppendLine("Локальное наблюдение: системный DNS этой копией программы не управляется.");
        if (snapshot.Requests.Count == 0) text.AppendLine("За окно хранения завершённые запросы не зафиксированы; это не доказывает исправность или отказ Windows DNS.");
        foreach (var row in snapshot.Requests.Take(64))
        {
            text.AppendLine();
            text.AppendLine($"#{row.RequestId} {row.At:O} → {row.CompletedAt:O}; {row.ClientTransport}/{row.LocalFamily}; ID={row.TransactionId}; QTYPE={Type(row.QueryType)}; CLASS={row.QueryClass}");
            text.AppendLine("Профиль: " + (row.ProfileId ?? "не зафиксирован") + "; контекст: " + (row.ProfileContext ?? "нет"));
            foreach (var upstream in row.UpstreamAttempts.Take(8))
                text.AppendLine($"  upstream [{upstream.Server}]:{upstream.Port} {upstream.Transport}: {upstream.Stage}, {upstream.Outcome}, RCODE={Code(upstream.ResponseCode)}, HTTP={upstream.HttpStatus?.ToString() ?? "—"}, ошибка={upstream.ErrorCode ?? "нет"}, TCP fallback={upstream.TcpFallback}, {upstream.ElapsedMs:0} мс");
            text.AppendLine($"relay: {row.Stage}, {row.Outcome}, ответ RCODE={Code(row.ReturnedCode)}, ошибка={row.ErrorCode ?? "нет"}, передан ОС={row.ResponseQueued}, {row.ElapsedMs:0} мс");
        }
        return text.Length <= 64000 ? text.ToString() : text.ToString(0, 64000) + "\r\n[Отчёт ограничен по размеру.]";
    }
    private static string Type(int type) => type switch { 1 => "A(1)", 28 => "AAAA(28)", 64 => "SVCB(64)", 65 => "HTTPS(65)", _ => type.ToString() };
    private static string Code(int? code) => code switch { null => "—", 0 => "NOERROR(0)", 2 => "SERVFAIL(2)", 3 => "NXDOMAIN(3)", 5 => "REFUSED(5)", _ => code.ToString()! };
}
