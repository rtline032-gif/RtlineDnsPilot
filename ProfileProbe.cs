using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace NetPilot;

/// <summary>A public HTTPS reachability measurement, without account credentials or Codex inference.</summary>
public sealed class ProfileProbe
{
    private readonly DnsTransport _transport;
    public ProfileProbe(DnsTransport transport) => _transport = transport ?? throw new ArgumentNullException(nameof(transport));

    public async Task<ProbeResult> CheckAsync(ChannelProfile profile, string url, TimeSpan timeout, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (timeout <= TimeSpan.Zero || timeout > TimeSpan.FromMinutes(1)) throw new ArgumentOutOfRangeException(nameof(timeout));
        var result = new ProbeResult { ProfileId = profile.Id, At = DateTimeOffset.UtcNow, Stage = "dns" };
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
            async Task<(IReadOnlyList<IPAddress> Addresses, string? Error)> Resolve(ushort type)
            {
                try
                {
                    var query = DnsWire.Query(uri.IdnHost, type);
                    var response = await _transport.ExchangeAsync(profile, query, dnsDeadline.Token).ConfigureAwait(false);
                    if (DnsWire.ResponseCode(response) != 0) return ([], "DNS RCODE " + DnsWire.ResponseCode(response));
                    var family = type == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                    return (DnsWire.Addresses(response).Where(ip => ip.AddressFamily == family).ToArray(), null);
                }
                catch (Exception ex) when (ex is IOException or SocketException or HttpRequestException or InvalidDataException or ArgumentException or OperationCanceledException)
                {
                    return ([], ex is OperationCanceledException ? "Истёк бюджет DNS запроса" : ex.GetBaseException().Message);
                }
            }
            var answers = await Task.WhenAll(Resolve(1), Resolve(28)).ConfigureAwait(false);
            result.DnsMs = timer.Elapsed.TotalMilliseconds;
            deadline.Token.ThrowIfCancellationRequested();
            var addresses = answers.SelectMany(answer => answer.Addresses).Distinct().Take(64).ToArray();
            if (addresses.Length == 0)
            {
                result.Detail = "Профиль не вернул пригодных A/AAAA адресов. " + string.Join("; ", answers.Select(x => x.Error).Where(x => !string.IsNullOrEmpty(x)).Distinct());
                return result;
            }
            result.Stage = "https"; timer.Restart();
            using var client = DnsTransport.CreatePinnedClient(uri, addresses);
            using var request = new HttpRequestMessage(HttpMethod.Get, uri)
            {
                Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionOrLower
            };
            request.Headers.UserAgent.ParseAdd("NetPilot/0.1");
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
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
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is OperationCanceledException or HttpRequestException or IOException or SocketException or InvalidDataException or ArgumentException)
        {
            if (result.Stage == "dns") result.DnsMs = timer.Elapsed.TotalMilliseconds;
            else result.HttpsMs = timer.Elapsed.TotalMilliseconds;
            if (ex is OperationCanceledException) { result.Stage = "timeout"; result.Detail = "Истёк общий срок проверки публичного HTTPS."; }
            else result.Detail = (result.Stage == "dns" ? "Ошибка DNS: " : "Ошибка HTTPS/TLS: ") + ex.GetBaseException().Message;
            return result;
        }
    }
}
