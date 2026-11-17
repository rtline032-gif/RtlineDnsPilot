using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetPilot;

/// <summary>A public HTTPS reachability measurement, without account credentials or Codex inference.</summary>
public sealed class ProfileProbe
{
    private readonly DnsTransport _transport;
    private readonly Func<Uri, IReadOnlyList<IPAddress>, ProbeAttemptTrace, HttpClient> _createClient;
    public ProfileProbe(DnsTransport transport) : this(transport, (uri, addresses, trace) => DnsTransport.CreatePinnedClient(uri, addresses, trace)) { }
    internal ProfileProbe(DnsTransport transport, Func<Uri, IReadOnlyList<IPAddress>, HttpClient> createClient)
        : this(transport, (uri, addresses, _) => createClient(uri, addresses)) { ArgumentNullException.ThrowIfNull(createClient); }
    internal ProfileProbe(DnsTransport transport, Func<Uri, IReadOnlyList<IPAddress>, ProbeAttemptTrace, HttpClient> createClient)
    {
        _transport = transport ?? throw new ArgumentNullException(nameof(transport));
        _createClient = createClient ?? throw new ArgumentNullException(nameof(createClient));
    }

    public async Task<ProbeResult> CheckAsync(ChannelProfile profile, string url, TimeSpan timeout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var result = new ProbeResult { ProfileId = profile.Id, At = DateTimeOffset.UtcNow, Stage = "dns", Diagnostics = new() };
        ProbeAttemptTrace? activeTrace = null;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps
            || uri.HostNameType != UriHostNameType.Dns || uri.UserInfo.Length > 0 || uri.Fragment.Length > 0 || uri.Query.Length > 0)
        {
            result.Stage = "input"; result.Detail = "Для проверки нужен HTTPS URL с именем узла, без паролей, параметров и фрагмента.";
            return result;
        }
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(timeout);
        var timer = Stopwatch.StartNew();
        try
        {
            // Bound DNS to half the total budget so an unanswered family cannot consume the HTTPS deadline.
            using var dnsDeadline = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
            dnsDeadline.CancelAfter(TimeSpan.FromTicks(timeout.Ticks / 2));
            async Task<(IReadOnlyList<IPAddress> Addresses, string? Error, DnsQueryDiagnostic Diagnostic)> Resolve(ushort type)
            {
                var elapsed = Stopwatch.StartNew();
                var diagnostic = new DnsQueryDiagnostic { QueryType = type == 1 ? "A" : "AAAA" };
                try
                {
                    var query = DnsWire.Query(uri.IdnHost, type);
                    var response = await _transport.ExchangeAsync(profile, query, dnsDeadline.Token).ConfigureAwait(false);
                    diagnostic.ResponseCode = DnsWire.ResponseCode(response);
                    if (diagnostic.ResponseCode != 0)
                    {
                        diagnostic.State = "error"; diagnostic.Detail = "DNS RCODE " + diagnostic.ResponseCode;
                        return ([], diagnostic.QueryType + ": " + diagnostic.Detail, diagnostic);
                    }
                    var family = type == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                    var addresses = DnsWire.Addresses(response).Where(ip => ip.AddressFamily == family).ToArray();
                    diagnostic.Addresses = addresses.Take(64).Select(ip => ip.ToString()).ToList();
                    diagnostic.State = addresses.Length == 0 ? "empty" : addresses.All(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) ? "rejected" : "success";
                    if (addresses.Any(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
                        diagnostic.Detail = "Неопределённые адреса 0.0.0.0/:: не используются для HTTPS.";
                    return (addresses, null, diagnostic);
                }
                catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or InvalidDataException or ArgumentException or OperationCanceledException)
                {
                    diagnostic.State = ProbeAttemptTrace.IsTimeout(ex) ? "timeout" : "error";
                    diagnostic.Detail = ProbeDiagnostics.Clip(DnsTransport.DescribeFailure(ex), 256);
                    return ([], diagnostic.QueryType + ": " + DnsTransport.DescribeFailure(ex), diagnostic);
                }
                finally { diagnostic.ElapsedMs = elapsed.Elapsed.TotalMilliseconds; }
            }
            var answers = await Task.WhenAll(Resolve(1), Resolve(28)).ConfigureAwait(false);
            result.Diagnostics.Dns = answers.Select(answer => answer.Diagnostic).ToList();
            result.DnsMs = timer.Elapsed.TotalMilliseconds;
            deadline.Token.ThrowIfCancellationRequested();
            var addresses = answers.SelectMany(answer => answer.Addresses).Distinct().Take(64).ToArray();
            var unspecified = addresses.Where(ip => ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)).ToArray();
            addresses = addresses.Except(unspecified).ToArray();
            if (addresses.Length == 0)
            {
                result.Detail = "Профиль не вернул пригодных A/AAAA адресов. " + string.Join("; ", answers.Select(x => x.Error).Where(x => !string.IsNullOrEmpty(x)).Distinct());
                if (unspecified.Length > 0) result.Detail += " Отвергнуты неопределённые адреса DNS: " + string.Join(", ", unspecified.Select(ip => ip.ToString())) + ".";
                return result;
            }
            var httpsBudget = timeout - timer.Elapsed;
            result.Stage = "https"; timer.Restart();
            Exception? last = null;
            for (var index = 0; index < addresses.Length; index++)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                // Reserve time for every remaining address, including TLS and headers.
                // A single address retains the original shared deadline.
                if (addresses.Length > 1)
                    attempt.CancelAfter(TimeSpan.FromTicks(Math.Max(1, (httpsBudget - timer.Elapsed).Ticks / (addresses.Length - index))));
                HttpResponseMessage response;
                activeTrace = new ProbeAttemptTrace(addresses[index]);
                result.Diagnostics.Attempts.Add(activeTrace.Data);
                using var client = _createClient(uri, [addresses[index]], activeTrace);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, uri)
                    {
                        Version = RuntimeCompatibility.PreferredHttpVersion, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
                    };
                    request.Headers.UserAgent.ParseAdd("NetPilot/0.1");
                    response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, attempt.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
                catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or SocketException)
                {
                    activeTrace.Failed(ex);
                    deadline.Token.ThrowIfCancellationRequested();
                    last = new IOException("HTTPS, адрес " + (index + 1) + "/" + addresses.Length + ": " + DnsTransport.DescribeFailure(ex), ex);
                    continue;
                }
                using (response)
                {
                    activeTrace.HttpReceived(response);
                    // A received HTTP response ends the attempt, even for a refusal or redirect.
                    result.HttpsMs = timer.Elapsed.TotalMilliseconds;
                    result.HttpStatus = (int)response.StatusCode;
                    result.Success = result.HttpStatus is >= 200 and < 400;
                    result.Stage = "http";
                    result.Detail = result.Success
                        ? "Общедоступный HTTPS отвечает (HTTP " + result.HttpStatus + "). Работа авторизованной сессии Codex не проверялась."
                        : result.HttpStatus switch
                        {
                            401 => "HTTP 401: сервер требует авторизацию; проверка доступности неуспешна.",
                            403 => "HTTP 403: сервер запретил запрос; проверка доступности неуспешна.",
                            429 => "HTTP 429: сервер ограничил частоту запросов; проверка доступности неуспешна.",
                            >= 500 => "HTTP " + result.HttpStatus + ": ошибка сервера.",
                            _ => "HTTP " + result.HttpStatus + ": проверка доступности неуспешна."
                        };
                    try
                    {
                        // Country is optional metadata from this very response. It never
                        // changes reachability or the header latency recorded above.
                        if(await ConnectionCountry.ReadAsync(uri,response,deadline.Token).ConfigureAwait(false) is { } country)
                        {result.CountryCode=country.Code;result.CountrySource=country.Source;}
                    }
                    catch(OperationCanceledException) when(!token.IsCancellationRequested) { }
                    token.ThrowIfCancellationRequested();
                    return result;
                }
            }
            deadline.Token.ThrowIfCancellationRequested();
            throw last ?? new IOException("Не удалось HTTPS-подключение к адресам профиля.");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or SocketException or InvalidDataException or ArgumentException)
        {
            activeTrace?.Failed(ex);
            if (result.Stage == "dns") result.DnsMs = timer.Elapsed.TotalMilliseconds;
            else result.HttpsMs = timer.Elapsed.TotalMilliseconds;
            if (ex is OperationCanceledException) { result.Stage = "timeout"; result.Detail = "Истёк общий срок проверки публичного HTTPS."; }
            else result.Detail = (result.Stage == "dns" ? "Ошибка DNS: " : "Ошибка HTTPS/TLS: ") + DnsTransport.DescribeFailure(ex);
            if (result.Diagnostics?.FailureSummary() is { Length: > 0 } summary) result.Detail = summary + ". " + result.Detail;
            return result;
        }
    }
}
