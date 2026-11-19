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

    public Task<byte[]> ExchangeAsync(ChannelProfile profile, byte[] query, CancellationToken cancellationToken)
        => ExchangeCoreAsync(profile, query, cancellationToken, null);

    internal Task<byte[]> ExchangeObservedAsync(ChannelProfile profile, byte[] query, CancellationToken cancellationToken,
        Action<DnsUpstreamDiagnostic> observe) => ExchangeCoreAsync(profile, query, cancellationToken, observe);

    private async Task<byte[]> ExchangeCoreAsync(ChannelProfile profile, byte[] query, CancellationToken cancellationToken,
        Action<DnsUpstreamDiagnostic>? observe)
    {
        ArgumentNullException.ThrowIfNull(profile);
        DnsWire.ValidateQuery(query);
        lock (_sync) { if (_disposed) throw new ObjectDisposedException(GetType().FullName); }
        if (!Enum.IsDefined(typeof(DnsMode), profile.Mode) || profile.DnsPort is < 1 or > 65535 || profile.Servers is null || profile.Servers.Length is < 1 or > 8)
            throw new ArgumentException("Некорректный DNS профиль.", nameof(profile));
        var addresses = profile.Servers.Select(value => IPAddress.TryParse(value, out var ip) ? ip : throw new ArgumentException("Требуется явный IP DNS сервера.")).ToArray();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(8));
        var upstream = profile.Mode == DnsMode.Doh ? "DoH" : "UDP/TCP DNS";
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
            for (var index = 0; index < addresses.Length; index++)
            {
                var address = addresses[index];
                upstream = (dohUri != null ? "DoH" : "UDP/TCP DNS") + ", сервер " + (index + 1) + "/" + addresses.Length;
                deadline.Token.ThrowIfCancellationRequested();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                var addressBudgetMs = dohUri != null && addresses.Length == 1 ? 8000 : Math.Clamp(8000 / addresses.Length, 750, 3000);
                attempt.CancelAfter(TimeSpan.FromMilliseconds(addressBudgetMs));
                var trace = observe is null ? null : new DnsUpstreamDiagnostic
                { Server = address.ToString(), Port = dohUri?.Port ?? profile.DnsPort, Transport = dohUri is null ? "UDP" : "DoH", Stage = "starting" };
                var traceTimer = trace is null ? null : System.Diagnostics.Stopwatch.StartNew();
                try
                {
                    // Pin the entire DoH exchange to one bootstrap address. TLS, HTTP, body validation,
                    // and DNS RCODE failures must all permit a secondary, not just TCP connect failures.
                    var response = dohUri != null
                        ? await ExchangeDohAsync(dohUri, address, query, attempt.Token, trace).ConfigureAwait(false)
                        : await ExchangeUdpAsync(new IPEndPoint(address, profile.DnsPort), query, attempt.Token, trace).ConfigureAwait(false);
                    if (trace is not null)
                    {
                        trace.ResponseCode = DnsWire.ResponseCode(response);
                        trace.Outcome = trace.ResponseCode == 0 ? "response" : "negative-response";
                    }
                    if (DnsWire.ResponseCode(response) is 2 or 5) { dnsFailure = response; continue; }
                    return response; // Includes valid negative answers such as NXDOMAIN.
                }
                catch (Exception ex) when (ex is SocketException or IOException or InvalidDataException or HttpRequestException or AuthenticationException
                    || (ex is OperationCanceledException && !deadline.IsCancellationRequested))
                {
                    if (trace is not null) { trace.Outcome = "error"; trace.ErrorCode = RelayDiagnostics.Error(ex, cancellationToken.IsCancellationRequested); }
                    last = new IOException(upstream + ": " + DescribeFailure(ex), ex);
                }
                finally
                {
                    if (trace is not null)
                    {
                        trace.ElapsedMs = traceTimer!.Elapsed.TotalMilliseconds;
                        if (trace.Outcome.Length == 0)
                        {
                            trace.Outcome = "interrupted";
                            trace.ErrorCode = cancellationToken.IsCancellationRequested ? "CANCELLED" : deadline.IsCancellationRequested ? "TIMEOUT" : "NOT_COMPLETED";
                        }
                        // An observer cannot change DNS response or fallback semantics.
                        try { observe!(trace); }
                        catch (Exception ex) when (ex is not OutOfMemoryException && ex is not StackOverflowException) { }
                    }
                }
            }
            if (dnsFailure != null) return dnsFailure;
            throw new IOException("DNS-профиль не ответил корректно. " + last?.Message, last);
        }
        catch (OperationCanceledException ex)
        {
            if (cancellationToken.IsCancellationRequested)
                throw new OperationCanceledException(upstream + ": запрос DNS прерван — тайм-аут или отмена.", ex, cancellationToken);
            throw new IOException(upstream + ": истёк срок ожидания ответа DNS (не более 8 с).", ex);
        }
    }

    // Keep the transport layer and socket/TLS reason together. GetBaseException()
    // alone discards these phases and often leaves only "operation was canceled".
    // Never include query packets, DNS names, stack traces or local file paths.
    internal static string DescribeFailure(Exception error)
    {
        var messages = new List<string>();
        for (Exception? item = error; item is not null && messages.Count < 4; item = item.InnerException)
        {
            var text = item switch
            {
                SocketException socket => "TCP/UDP " + socket.SocketErrorCode + " (" + socket.NativeErrorCode + "): " + socket.Message,
                AuthenticationException => "TLS/сертификат: " + item.Message,
                OperationCanceledException => "Тайм-аут/отмена: " + item.Message,
                _ => item.Message
            };
            if (!string.IsNullOrWhiteSpace(text) && !messages.Contains(text, StringComparer.Ordinal)) messages.Add(text);
        }
        var detail = string.Join(" → ", messages);
        return detail[..Math.Min(detail.Length, 900)];
    }

    private static async Task<byte[]> ExchangeUdpAsync(IPEndPoint endpoint, byte[] query, CancellationToken token, DnsUpstreamDiagnostic? trace = null)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
        if (trace is not null) trace.Stage = "udp-connect";
        await socket.ConnectAsync(endpoint, token).ConfigureAwait(false); // Connected UDP filters foreign source endpoints.
        if (trace is not null) trace.Stage = "udp-send";
        var sent = await socket.SendAsync(query, SocketFlags.None, token).ConfigureAwait(false);
        if (sent != query.Length) throw new IOException("Неполная отправка DNS запроса.");
        var buffer = new byte[DnsWire.MaxMessage];
        if (trace is not null) trace.Stage = "udp-receive";
        var count = await socket.ReceiveAsync(buffer, SocketFlags.None, token).ConfigureAwait(false);
        var response = buffer[..count];
        if (trace is not null) trace.Stage = "udp-validate";
        DnsWire.ValidateResponse(query, response);
        if (DnsWire.IsTruncated(response))
        {
            if (trace is not null) { trace.TcpFallback = true; trace.Transport = "TCP"; }
            return await ExchangeTcpAsync(endpoint, query, token, trace).ConfigureAwait(false);
        }
        return response;
    }

    private static async Task<byte[]> ExchangeTcpAsync(IPEndPoint endpoint, byte[] query, CancellationToken token, DnsUpstreamDiagnostic? trace = null)
    {
        using var socket = new Socket(endpoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        if (trace is not null) trace.Stage = "tcp-connect";
        await socket.ConnectAsync(endpoint, token).ConfigureAwait(false);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var prefix = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)query.Length);
        if (trace is not null) trace.Stage = "tcp-send";
        await stream.WriteAsync(prefix, token).ConfigureAwait(false);
        await stream.WriteAsync(query, token).ConfigureAwait(false);
        if (trace is not null) trace.Stage = "tcp-receive-length";
        await stream.ReadExactlyAsync(prefix, token).ConfigureAwait(false);
        var length = BinaryPrimitives.ReadUInt16BigEndian(prefix);
        if (length < 12) throw new InvalidDataException("Некорректная длина DNS TCP ответа.");
        if (trace is not null) trace.Stage = "tcp-receive-body";
        var response = new byte[length]; await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (trace is not null) trace.Stage = "tcp-validate";
        DnsWire.ValidateResponse(query, response);
        if (DnsWire.IsTruncated(response)) throw new InvalidDataException("Оборванный DNS ответ после TCP fallback.");
        return response;
    }

    private async Task<byte[]> ExchangeDohAsync(Uri uri, IPAddress address, byte[] query, CancellationToken token, DnsUpstreamDiagnostic? trace = null)
    {
        var key = uri.AbsoluteUri + "|" + address;
        HttpClient client; var owned = false;
        lock (_sync)
        {
            if (_disposed) throw new ObjectDisposedException(GetType().FullName);
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
                Version = RuntimeCompatibility.PreferredHttpVersion,
                VersionPolicy = HttpVersionPolicy.RequestVersionOrLower,
                Content = new ByteArrayContent(query)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/dns-message");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/dns-message"));
            if (trace is not null) trace.Stage = "doh-request";
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            if (trace is not null) { trace.Stage = "doh-headers"; trace.HttpStatus = (int)response.StatusCode; }
            if (!response.IsSuccessStatusCode) throw new HttpRequestException("DoH HTTP " + (int)response.StatusCode + ".", null, response.StatusCode);
            if (!string.Equals(response.Content.Headers.ContentType?.MediaType, "application/dns-message", StringComparison.OrdinalIgnoreCase)
                || response.Content.Headers.ContentEncoding.Count > 0)
                throw new InvalidDataException("DoH вернул неподдерживаемый формат ответа.");
            if (response.Content.Headers.ContentLength is > DnsWire.MaxMessage) throw new InvalidDataException("Ответ DoH превышает 65535 байт.");
            if (trace is not null) trace.Stage = "doh-body";
            using var body = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            using var bytes = new MemoryStream(); var buffer = new byte[4096];
            while (true)
            {
                var count = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, DnsWire.MaxMessage + 1 - (int)bytes.Length)), token).ConfigureAwait(false);
                if (count == 0) break;
                bytes.Write(buffer, 0, count);
                if (bytes.Length > DnsWire.MaxMessage) throw new InvalidDataException("Ответ DoH превышает 65535 байт.");
            }
            if (trace is not null) trace.Stage = "doh-validate";
            var packet = bytes.ToArray(); DnsWire.ValidateResponse(query, packet);
            if (DnsWire.IsTruncated(packet)) throw new InvalidDataException("DoH вернул усечённый DNS ответ.");
            return packet;
        }
        finally { if (owned) client.Dispose(); }
    }

    internal static HttpClient CreatePinnedClient(Uri uri, IReadOnlyList<IPAddress> addresses)
        => CreatePinnedClient(uri, addresses, null);

    internal static HttpClient CreatePinnedClient(Uri uri, IReadOnlyList<IPAddress> addresses, ProbeAttemptTrace? trace)
    {
        var handler = new SocketsHttpHandler
        {
            UseProxy = false, AllowAutoRedirect = false, UseCookies = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 16,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5), PooledConnectionIdleTimeout = TimeSpan.FromMinutes(1),
            MaxResponseHeadersLength = 64, MaxResponseDrainSize = 0,
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
                        trace?.TcpStarted();
                        await socket.ConnectAsync(new IPEndPoint(address, uri.Port), attempt.Token).ConfigureAwait(false);
                        trace?.TcpConnected();
                        return new NetworkStream(socket, ownsSocket: true);
                    }
                    catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                    {
                        trace?.Failed(ex);
                        socket.Dispose(); if (token.IsCancellationRequested) throw;
                        last = new IOException("TCP-подключение: " + DescribeFailure(ex), ex);
                    }
                }
                throw new IOException("Не удалось TCP-подключение к закреплённым адресам HTTPS. " + last?.Message, last);
            }
            // Standard SslOptions defaults preserve hostname/SNI and certificate-chain validation.
        };
        // Observe only the target probe, after the handler's standard TLS authentication.
        // Returning the identical stream neither replaces validation nor reads HTTP bytes.
        if (trace is not null) handler.PlaintextStreamFilter = (context, _) =>
        {
            trace.TlsCompleted(context.NegotiatedHttpVersion);
            return new ValueTask<Stream>(context.PlaintextStream);
        };
        RuntimeCompatibility.ConfigurePinnedHttpsHandler(handler, RuntimeCompatibility.IsWindows7);
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
