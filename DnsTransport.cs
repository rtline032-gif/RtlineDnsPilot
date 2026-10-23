using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Authentication;

namespace NetPilot;

/// <summary>Explicit-IP DNS upstreams; there is no proxy, OS resolver, or plaintext DoH fallback.</summary>
public sealed class DnsTransport : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, HttpClient> _dohClients = new(StringComparer.Ordinal);
    private bool _disposed;

    public async Task<byte[]> ExchangeAsync(ChannelProfile profile, byte[] query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        DnsWire.ValidateQuery(query);
        lock (_sync) ObjectDisposedException.ThrowIf(_disposed, this);
        if (!Enum.IsDefined(profile.Mode) || profile.DnsPort is < 1 or > 65535 || profile.Servers is null || profile.Servers.Length is < 1 or > 8)
            throw new ArgumentException("Некорректный DNS профиль.", nameof(profile));
        var addresses = profile.Servers.Select(value => IPAddress.TryParse(value, out var ip) ? ip : throw new ArgumentException("Требуется явный IP DNS сервера.")).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        try
        {
            Uri? dohUri = null;
            if (profile.Mode == DnsMode.Doh)
            {
                if (!Uri.TryCreate(profile.DohUrl, UriKind.Absolute, out dohUri) || dohUri.Scheme != Uri.UriSchemeHttps
                    || dohUri.HostNameType != UriHostNameType.Dns || dohUri.UserInfo.Length > 0 || dohUri.Fragment.Length > 0 || dohUri.Query.Length > 0)
                    throw new ArgumentException("Для DoH нужен HTTPS URL с именем сервера.");
            }
            Exception? last = null; byte[]? dnsFailure = null;
            foreach (var address in addresses)
            {
                deadline.Token.ThrowIfCancellationRequested();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var addressBudgetMs = dohUri != null && addresses.Length == 1 ? 8000 : Math.Clamp(8000 / addresses.Length, 750, 3000);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(addressBudgetMs));
                try
                {
                    // Pin the entire DoH exchange to one bootstrap address. TLS, HTTP, body validation,
                    // and DNS RCODE failures must all permit a secondary, not just TCP connect failures.
                    var response = dohUri != null
                        ? await ExchangeDohAsync(dohUri, address, query, attempt.Token).ConfigureAwait(false)
                        : await ExchangeUdpAsync(new IPEndPoint(address, profile.DnsPort), query, attempt.Token).ConfigureAwait(false);
                    if (DnsWire.ResponseCode(response) is 2 or 5) { dnsFailure = response; continue; }
                    return response; // Includes valid negative answers such as NXDOMAIN.
                }
                catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or HttpRequestException or AuthenticationException
                    || (ex is OperationCanceledException && !deadline.IsCancellationRequested)) { last = ex; }
            }
            if (dnsFailure != null) return dnsFailure;
            throw new IOException("Все явные DNS серверы профиля недоступны или вернули некорректный ответ.", last);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Истёк срок ожидания ответа DNS (не более 8 с).", ex);
        }
    }

    private static async Task<byte[]> ExchangeUdpAsync(IPEndPoint endpoint, byte[] query, CancellationToken token)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        await socket.ConnectAsync(endpoint, token).ConfigureAwait(false); // Connected UDP filters foreign source endpoints.
        var sent = await socket.SendAsync(query, SocketFlags.None, token).ConfigureAwait(false);
        if (sent != query.Length) throw new IOException("Неполная отправка DNS запроса.");
        var buffer = new byte[DnsWire.MaxMessage];
        var count = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
        var response = buffer[..count];
        DnsWire.ValidateResponse(query, response);
        if (DnsWire.IsTruncated(response)) return await ExchangeTcpAsync(endpoint, query, token).ConfigureAwait(false);
        return response;
    }

    private static async Task<byte[]> ExchangeTcpAsync(IPEndPoint endpoint, byte[] query, CancellationToken token)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var prefix = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)query.Length);
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(query, token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length < 12) throw new InvalidDataException("Некорректная длина DNS TCP ответа.");
        var response = new byte[length]; await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        DnsWire.ValidateResponse(query, response);
        if (DnsWire.IsTruncated(response)) throw new InvalidDataException("Оборванный DNS ответ после TCP fallback.");
        return response;
    }

    private async Task<byte[]> ExchangeDohAsync(Uri uri, IPAddress address, byte[] query, CancellationToken token)
    {
        var key = uri.AbsoluteUri + "|" + address;
        HttpClient client; var owned = false;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_dohClients.TryGetValue(key, out client!))
            {
                client = CreatePinnedClient(uri, [address]);
                if (_dohClients.Count < 32) _dohClients.Add(key, client);
                else owned = true; // Editing profiles cannot grow the long-lived cache indefinitely.
            }
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri)
            {
                Version = HttpVersion.Version20,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = new ByteArrayContent(query)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("DoH HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("DoH вернул неподдерживаемый формат ответа.");
            if (response.Content.Headers.ContentLength is > DnsWire.MaxMessage) throw new InvalidDataException("Ответ DoH превышает 65535 байт.");
            using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, DnsWire.MaxMessage + 1 - (int)bytes.Length)), token).ConfigureAwait(false);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                if (bytes.Length > DnsWire.MaxMessage) throw new InvalidDataException("Ответ DoH превышает 65535 байт.");
            }
            var packet = bytes.ToArray(); DnsWire.ValidateResponse(query, packet);
            if (DnsWire.IsTruncated(packet)) throw new InvalidDataException("DoH вернул усечённый DNS ответ.");
            return packet;
        }
        finally { if (owned) client.Dispose(); }
    }

    internal static HttpClient CreatePinnedClient(Uri uri, IReadOnlyList<IPAddress> addresses)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxResponseHeadersLength = 16, MaxResponseDrainSize = 0,
            ConnectCallback = async (context, token) =>
            {
                if (!string.Equals(context.DnsEndPoint.Host, uri.IdnHost, StringComparison.OrdinalIgnoreCase) || context.DnsEndPoint.Port != uri.Port)
                    throw new IOException("Попытка соединения вне закреплённого HTTPS узла.");
                Exception? last = null;
                foreach (var address in addresses)
                {
                    token.ThrowIfCancellationRequested();
                    using var attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attempt.CancelAfter(TimeSpan.FromMilliseconds(Math.Clamp(5000 / Math.Max(addresses.Count, 1), 300, 2000)));
                    var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
                    try
                    {
                        await socket.ConnectAsync(new IPEndPoint(address, uri.Port), attempt.Token).ConfigureAwait(false);
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        socket.Dispose(); if (token.IsCancellationRequested) throw; last = ex;
                    }
                }
                throw new IOException("Ни один закреплённый IP не доступен для HTTPS.", last);
            }
            // Standard SslOptions defaults preserve hostname/SNI and certificate-chain validation.
        };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return; _disposed = true;
            foreach (var client in _dohClients.Values) client.Dispose();
            _dohClients.Clear();
        }
    }
}
