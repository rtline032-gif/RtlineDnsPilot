using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace NetPilot;

public sealed record DnsShareResult(int Accepted, int Duplicates)
{
    public int Unconfirmed { get; init; }
}

public sealed class DnsShareException : Exception
{
    public bool MayHaveBeenSent { get; }
    public DnsShareException(string message, bool mayHaveBeenSent) : base(message) => MayHaveBeenSent = mayHaveBeenSent;
}

public sealed class DnsShareSettings
{
    public string Endpoint { get; set; } = "";
    public string InviteCode { get; set; } = "";
}

/// <summary>Receiver credentials are protected for the current Windows user, outside distributable files.</summary>
public static class DnsShareSettingsStore
{
    internal const string FileName = "dns-sharing.dpapi";
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("RtlineDnsPilot.DnsShare.Settings.v1");
    private sealed class Envelope { public Envelope() { } public int Version { get; set; } = 1; public string Protected { get; set; } = ""; }
    public static DnsShareSettings Load(string directory)
    {
        var path = Path.Combine(directory, FileName);
        if (!File.Exists(path)) return new();
        byte[]? plaintext = null;
        try
        {
            if (new FileInfo(path).Length > 8192) throw new InvalidDataException();
            var envelope = AtomicStore.Read<Envelope>(path);
            if (envelope is not { Version: 1 } || envelope.Protected.Length > 7000) throw new InvalidDataException();
            plaintext = ProtectedData.Unprotect(Convert.FromBase64String(envelope.Protected), Entropy, DataProtectionScope.CurrentUser);
            var settings = JsonData.Decode<DnsShareSettings>(Encoding.UTF8.GetString(plaintext));
            return DnsShareClient.ValidateSettings(settings.Endpoint, settings.InviteCode);
        }
        catch (Exception ex) when (ex is CryptographicException or InvalidDataException or FormatException or JsonException or ArgumentException or IOException or UnauthorizedAccessException)
        { throw new InvalidDataException("Сохранённые параметры приёмника не удалось прочитать. Введите их заново; исходный файл сохранён."); }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }
    public static void Save(string directory, DnsShareSettings settings)
    {
        var valid = DnsShareClient.ValidateSettings(settings.Endpoint, settings.InviteCode);
        var plaintext = Encoding.UTF8.GetBytes(JsonData.Encode(valid));
        try
        {
            var encrypted = ProtectedData.Protect(plaintext, Entropy, DataProtectionScope.CurrentUser);
            AtomicStore.Write(Path.Combine(directory, FileName), new Envelope { Protected = Convert.ToBase64String(encrypted) });
        }
        finally { CryptographicOperations.ZeroMemory(plaintext); }
    }
}

/// <summary>One explicit bounded upload. Redirects retrieve Google's response with GET and never repeat the POST.</summary>
public sealed class DnsShareClient : IDisposable
{
    public const int MaxRequestBytes = 96 * 1024;
    public const int MaxResponseBytes = 16 * 1024;
    private readonly HttpClient _http;
    private readonly TimeSpan _deadline;
    private readonly string _ownerEndpoint;
    private readonly SemaphoreSlim _ownerGate = new(1, 1);
    private readonly HashSet<string> _confirmedOwnerProfiles = new(StringComparer.Ordinal);
    private readonly HashSet<string> _uncertainOwnerProfiles = new(StringComparer.Ordinal);
    public DnsShareClient() : this(CreateHandler(), TimeSpan.FromSeconds(15)) { }
    internal DnsShareClient(HttpMessageHandler handler, TimeSpan? deadline = null, string? ownerEndpointForTests = null)
    {
        _deadline = deadline ?? TimeSpan.FromSeconds(15);
        if (_deadline <= TimeSpan.Zero || _deadline > TimeSpan.FromSeconds(15)) throw new ArgumentOutOfRangeException(nameof(deadline));
        _ownerEndpoint = ownerEndpointForTests ?? DnsShareDestination.Endpoint;
        _http = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }
    private static HttpClientHandler CreateHandler() => new()
    {
        AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false, Credentials = null
    };

    public static DnsShareSettings ValidateSettings(string endpoint, string inviteCode)
    {
        var address = ValidateEndpoint(endpoint);
        var invite = (inviteCode ?? "").Trim();
        if (invite.Length != 64 || invite.Any(c => c is not (>= 'a' and <= 'f') and not (>= '0' and <= '9')))
            throw new ArgumentException("Укажите персональный код приглашения: 64 символа 0–9 и a–f, полученные от владельца приёмника.");
        return new DnsShareSettings { Endpoint = address, InviteCode = invite };
    }

    private static string ValidateEndpoint(string endpoint)
    {
        var address = (endpoint ?? "").Trim();
        if (address.Length is < 1 or > 1024 || !address.StartsWith("https://script.google.com/", StringComparison.OrdinalIgnoreCase)
            || !Uri.TryCreate(address, UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString()
            || uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Authority, "script.google.com", StringComparison.OrdinalIgnoreCase)
            || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0
            || !Regex.IsMatch(uri.AbsolutePath, "^/macros/s/[A-Za-z0-9_-]{1,256}/exec$", RegexOptions.CultureInvariant))
            throw new ArgumentException("Укажите HTTPS-адрес приёмника Google Apps Script: https://script.google.com/macros/s/ID/exec, без параметров и порта.");
        return uri.AbsoluteUri;
    }

    internal static DnsShareCandidate[] PrepareProfiles(IReadOnlyCollection<DnsShareCandidate> profiles)
    {
        try
        {
            if (profiles is null || profiles.Count is < 1 or > 32) throw new ArgumentException();
            var prepared = profiles.Select(DnsSharing.NormalizeCandidate).ToArray();
            if (prepared.Any(DnsSharing.IsBaseline) || prepared.Select(DnsSharing.Key).Distinct(StringComparer.Ordinal).Count() != prepared.Length)
                throw new ArgumentException();
            return prepared;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidDataException or FormatException)
        { throw new ArgumentException("Можно отправить от 1 до 32 различных публичных DNS-каналов вне базового списка, с подтверждённой публичной проверкой ChatGPT/Gemini."); }
    }

    public async Task<DnsShareResult> SendAsync(string endpoint, string inviteCode, IReadOnlyCollection<DnsShareCandidate> profiles, CancellationToken token = default)
    {
        var settings = ValidateSettings(endpoint, inviteCode);
        var prepared = PrepareProfiles(profiles);
        // Explicit projection is the public schema; never serialize a local profile or offer object here.
        var payload = Encoding.UTF8.GetBytes(JsonData.Encode(new
        {
            Protocol = 1, settings.InviteCode,
            Profiles = prepared.Select(p => new { p.Mode, p.Servers, p.DnsPort, p.DohUrl, p.WorkedServices }).ToArray()
        }));
        return await SendPayloadAsync(settings.Endpoint, settings.InviteCode, payload, prepared.Length, 1, token).ConfigureAwait(false);
    }

    /// <summary>One-click product path: the destination never comes from user settings or a saved DPAPI file.</summary>
    public async Task<DnsShareResult> SendToOwnerAsync(IReadOnlyCollection<DnsShareCandidate> profiles, CancellationToken token = default)
    {
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count == 0) return new DnsShareResult(0, 0);
        var prepared = PrepareProfiles(profiles);
        await _ownerGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            static string AttemptKey(DnsShareCandidate profile) => DnsSharing.Key(profile);
            var unsent = prepared.Where(p => !_confirmedOwnerProfiles.Contains(AttemptKey(p)) && !_uncertainOwnerProfiles.Contains(AttemptKey(p))).ToArray();
            var unconfirmed = prepared.Count(p => _uncertainOwnerProfiles.Contains(AttemptKey(p)));
            var known = prepared.Length - unsent.Length - unconfirmed;
            if (unsent.Length == 0) return new DnsShareResult(0, known) { Unconfirmed = unconfirmed };
            if (string.IsNullOrWhiteSpace(_ownerEndpoint))
                throw new DnsShareException("DNS: " + DnsShareDestination.RecipientName + " ещё не подключён. Ничего не отправлено.", false);
            var endpoint = ValidateEndpoint(_ownerEndpoint);
            // Only this projection is uploaded. No invite, local settings, IDs, names, observations or account data.
            var payload = Encoding.UTF8.GetBytes(JsonData.Encode(new
            {
                Protocol = 2,
                Profiles = unsent.Select(p => new { p.Mode, p.Servers, p.DnsPort, p.DohUrl, p.WorkedServices }).ToArray()
            }));
            try
            {
                var reply = await SendPayloadAsync(endpoint, "", payload, unsent.Length, 2, token).ConfigureAwait(false);
                foreach (var profile in unsent) _confirmedOwnerProfiles.Add(AttemptKey(profile));
                return new DnsShareResult(reply.Accepted, reply.Duplicates + known) { Unconfirmed = unconfirmed };
            }
            catch (DnsShareException ex) when (ex.MayHaveBeenSent)
            {
                // The receiver might already have written the profiles. A later click cannot replay them in this session.
                foreach (var profile in unsent) _uncertainOwnerProfiles.Add(AttemptKey(profile));
                throw;
            }
        }
        finally { _ownerGate.Release(); }
    }

    private async Task<DnsShareResult> SendPayloadAsync(string endpoint, string invite, byte[] payload, int sentCount, int protocol, CancellationToken token)
    {
        if (payload.Length > MaxRequestBytes) { CryptographicOperations.ZeroMemory(payload); throw new ArgumentException("Список DNS превышает допустимый размер отправки."); }
        var started = false;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(_deadline);
        try
        {
            deadline.Token.ThrowIfCancellationRequested();
            var uri = new Uri(endpoint);
            for (var redirects = 0; ; redirects++)
            {
                using var request = new HttpRequestMessage(redirects == 0 ? HttpMethod.Post : HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (redirects == 0)
                {
                    request.Content = new ByteArrayContent(payload);
                    request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }
                started = true;
                using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
                if (Redirect(response.StatusCode))
                {
                    if (redirects >= 2 || !SafeResponseRedirect(response.Headers.Location, invite))
                        throw new DnsShareException("Приёмник вернул неподдерживаемое перенаправление. Результат отправки не подтверждён; повтор автоматически не выполнялся.", true);
                    uri = response.Headers.Location!;
                    continue;
                }
                if (!response.IsSuccessStatusCode)
                    throw new DnsShareException("Приёмник не вернул подтверждение отправки. Результат не определён; повтор автоматически не выполнялся.", true);
                var body = await ReadBoundedAsync(response.Content, deadline.Token).ConfigureAwait(false);
                try { return ParseReply(body, sentCount, protocol); }
                finally { CryptographicOperations.ZeroMemory(body); }
            }
        }
        catch (DnsShareException) { throw; }
        catch (OperationCanceledException)
        {
            throw new DnsShareException(started
                ? "Ожидание ответа завершено или отменено. Каналы могли быть приняты, но результат не подтверждён. Повтор автоматически не выполнялся."
                : "Отправка отменена до начала запроса.", started);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or JsonException or InvalidDataException or InvalidOperationException)
        { throw new DnsShareException("Не удалось получить достоверный ответ приёмника. Каналы могли быть приняты; повтор автоматически не выполнялся.", started); }
        finally { CryptographicOperations.ZeroMemory(payload); }
    }

    private static bool Redirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
        or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static bool SafeResponseRedirect(Uri? uri, string invite) => uri is { IsAbsoluteUri: true } && uri.Scheme == Uri.UriSchemeHttps
        && uri.OriginalString.StartsWith("https://script.googleusercontent.com/", StringComparison.OrdinalIgnoreCase)
        && string.Equals(uri.Authority, "script.googleusercontent.com", StringComparison.OrdinalIgnoreCase) && uri.UserInfo.Length == 0
        && uri.AbsolutePath == "/macros/echo" && uri.Fragment.Length == 0 && uri.AbsoluteUri.Length <= 4096
        && (invite.Length == 0 || !uri.AbsoluteUri.Contains(invite, StringComparison.Ordinal));

    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, CancellationToken token)
    {
        if (content.Headers.ContentLength > MaxResponseBytes) throw new InvalidDataException();
        await using var source = await content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var chunk = new byte[4096];
        while (true)
        {
            var count = await source.ReadAsync(chunk.AsMemory(0, Math.Min(chunk.Length, MaxResponseBytes + 1 - (int)buffer.Length)), token).ConfigureAwait(false);
            if (count == 0) break;
            buffer.Write(chunk, 0, count);
            if (buffer.Length > MaxResponseBytes) throw new InvalidDataException();
        }
        return buffer.ToArray();
    }

    private static DnsShareResult ParseReply(byte[] body, int sentCount, int expectedProtocol)
    {
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object || root.EnumerateObject().Select(p => p.Name).Distinct(StringComparer.Ordinal).Count() != root.EnumerateObject().Count()
            || !root.TryGetProperty("Protocol", out var protocol) || !protocol.TryGetInt32(out var version) || version != expectedProtocol
            || !root.TryGetProperty("Ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)
            || !root.TryGetProperty("Accepted", out var accepted) || !accepted.TryGetInt32(out var a) || a < 0 || a > sentCount
            || !root.TryGetProperty("Duplicates", out var duplicates) || !duplicates.TryGetInt32(out var d) || d < 0 || d > sentCount || (long)a + d > sentCount)
            throw new InvalidDataException();
        if (!ok.GetBoolean())
        {
            if (a != 0 || d != 0) throw new InvalidDataException();
            var code = root.TryGetProperty("Error", out var error) && error.ValueKind == JsonValueKind.String ? error.GetString() : null;
            var message = code switch
            {
                "denied" => expectedProtocol == 1 ? "Приёмник отклонил код приглашения. Проверьте код или запросите новый у владельца." : "Каталог временно не принимает предложения. Ничего не добавлено.",
                "rate_limit" => "Лимит отправок исчерпан. Повторите позже.",
                "capacity" => "Каталог или дневной лимит добавлений заполнен. Обратитесь к владельцу приёмника.",
                "busy" => "Приёмник занят другой записью. Повторите позже.",
                "not_configured" => "Владелец ещё не настроил приёмник.",
                "invalid_request" => "Приёмник отклонил формат DNS-каналов. Проверьте совместимость версии.",
                _ => "Приёмник отклонил отправку. Обратитесь к владельцу приёмника."
            };
            throw new DnsShareException(message, false);
        }
        if ((long)a + d != sentCount) throw new InvalidDataException();
        return new DnsShareResult(a, d);
    }

    public void Dispose() => _http.Dispose();
}
