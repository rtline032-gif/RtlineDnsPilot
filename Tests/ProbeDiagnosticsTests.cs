using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
#if NET6_0
using TcpListener = NetPilot.Tests.LegacyTestTcpListener;
#endif

namespace NetPilot.Tests;

public static class ProbeDiagnosticsTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> action)
        {
            try { await action(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }
        await Check("pinned HTTPS has a bounded 64 KiB header allowance and the standard certificate validator", () =>
        {
            using var client = DnsTransport.CreatePinnedClient(new Uri("https://headers.netpilot.invalid/"), [IPAddress.Loopback]);
            var handler = Handler(client);
            Require(handler.MaxResponseHeadersLength == 64 && !handler.UseProxy && !handler.AllowAutoRedirect && !handler.UseCookies,
                "HTTPS bounds or direct connection rules changed.");
            Require(handler.SslOptions.RemoteCertificateValidationCallback is null, "Production HTTPS bypasses certificate validation.");
            return Task.CompletedTask;
        });
        await Check("pinned HTTP parser accepts a 20 KiB response header that exceeded the previous limit", async () =>
        {
            var response = await HeaderFixture(20 * 1024);
            Require(response == HttpStatusCode.OK, "A header between the former and current bounds was rejected.");
        });
        await Check("pinned HTTP parser still rejects an 80 KiB response header", async () =>
        {
            try { await HeaderFixture(80 * 1024); }
            catch (HttpRequestException) { return; }
            throw new Exception("The bounded response-header limit was removed.");
        });
        await Check("DNS timeout reports transport and both query families without logging the question", async () =>
        {
            using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var received = 0;
            var requests = Task.Run(async () =>
            {
                for (var index = 0; index < 2; index++) { await dns.ReceiveAsync(deadline.Token); received++; }
            });
            var profile = new ChannelProfile { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port };
            var result = await new ProfileProbe(transport).CheckAsync(profile, "https://question-must-not-be-logged.netpilot.invalid/", TimeSpan.FromSeconds(2), deadline.Token);
            await requests;
            Require(received == 2 && !result.Success && result.Stage == "dns" && result.HttpStatus is null, "Unanswered DNS became a successful or misclassified probe.");
            Require(result.Detail.Contains("UDP/TCP DNS", StringComparison.Ordinal) && result.Detail.Contains("A:", StringComparison.Ordinal)
                && result.Detail.Contains("AAAA:", StringComparison.Ordinal) && result.Detail.Contains("айм-аут", StringComparison.OrdinalIgnoreCase),
                "The transport, family or timeout phase was lost.");
            Require(!result.Detail.Contains("question-must-not-be-logged", StringComparison.Ordinal), "An ordinary DNS question leaked into diagnostics.");
            Require(result.Diagnostics is { Dns.Count: 2, Attempts.Count: 0 } && result.Diagnostics.Dns.All(d => d.State == "timeout"),
                "DNS timeouts were lost or invented a target TCP/TLS attempt.");
        });
        await Check("unspecified A and AAAA answers remain a DNS failure without an HTTPS attempt", async () =>
        {
            var (result, connected) = await UnspecifiedAnswersFixture(includeUsable: false);
            Require(!result.Success && result.Stage == "dns" && result.HttpStatus is null && !connected,
                "Unspecified DNS answers advanced to HTTPS: " + result.Stage + "; " + result.Detail);
            Require(result.Detail.Contains("0.0.0.0", StringComparison.Ordinal) && result.Detail.Contains("::", StringComparison.Ordinal),
                "Rejected unspecified A/AAAA answers were lost from DNS diagnostics.");
            Require(result.Diagnostics is { Dns.Count: 2, Attempts.Count: 0 } && result.Diagnostics.Dns.All(d => d.State == "rejected"),
                "Unspecified answers lost their separate family records.");
        });
        await Check("a usable address alongside unspecified DNS answers still reaches HTTPS", async () =>
        {
            var (result, connected) = await UnspecifiedAnswersFixture(includeUsable: true);
            Require(connected && result.Stage == "https" && !result.Success,
                "The usable address was discarded or HTTPS/TLS failure was misclassified: " + result.Stage + "; " + result.Detail);
        });
        await Check("a stalled TLS address permits the next address without bypassing its certificate", async () =>
        {
            var fixture = await HttpsFallbackFixture(cancelCaller: false, stallBoth: false);
            Require(fixture.FirstConnected && fixture.SecondConnected && fixture.Sni == "fallback.netpilot.invalid",
                "The complete TLS attempt did not advance to the second address with the intended SNI.");
            Require(!fixture.Result.Success && fixture.Result.Stage == "https" && fixture.Result.Detail.Contains("TLS/сертификат", StringComparison.Ordinal),
                "The second address's untrusted certificate was accepted or its failure was lost: " + fixture.Result.Stage + "; " + fixture.Result.Detail);
            Require(fixture.Result.Diagnostics is { Attempts.Count: 2 } details
                && details.Attempts[0].Address == "127.0.0.1" && details.Attempts[0].Tcp.State == "success" && details.Attempts[0].Tls.State == "timeout"
                && details.Attempts[1].Address == "127.0.0.2" && details.Attempts[1].Tcp.State == "success" && details.Attempts[1].Tls.State == "error"
                && details.Attempts.All(a => a.Http.State == "not-started"),
                "A later certificate error erased the earlier TLS timeout or invented HTTP headers.");
        });
        await Check("caller cancellation stops HTTPS without retrying the next address", async () =>
        {
            var fixture = await HttpsFallbackFixture(cancelCaller: true, stallBoth: false);
            Require(fixture.CallerCanceled && fixture.FirstConnected && !fixture.SecondConnected,
                "Caller cancellation was swallowed or started a second HTTPS attempt.");
        });
        await Check("multiple stalled TLS addresses remain bounded by the single probe deadline", async () =>
        {
            var fixture = await HttpsFallbackFixture(cancelCaller: false, stallBoth: true);
            Require(fixture.FirstConnected && fixture.SecondConnected && !fixture.Result.Success && fixture.ElapsedMs < 2500,
                "HTTPS retry lost the shared deadline or did not try the reserve.");
        });
        await Check("every received HTTP status ends HTTPS attempts without following redirects", async () =>
        {
            foreach (var status in new[] { 200, 301, 302, 401, 403, 429, 500, 503 })
            {
                var fixture = await ControlledHttpsFixture((_, _) =>
                {
                    var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent("") };
                    response.Headers.Location = new Uri("https://must-not-be-followed.netpilot.invalid/");
                    return Task.FromResult(response);
                });
                Require(fixture.Attempts.Count == 1 && fixture.Result.HttpStatus == status && fixture.Result.Stage == "http"
                    && fixture.Result.Success == (status is >= 200 and < 400), "An HTTP response retried or changed reachability: " + status);
                Require(fixture.Result.Diagnostics is { Attempts.Count: 1 } details && details.Attempts[0].HttpStatus == status
                    && details.Attempts[0].Http.State == "success" && details.Attempts[0].Tcp.State == "unknown",
                    "An HTTP status disappeared or an unobserved fake transport became measured TCP.");
            }
        });
        await Check("a failed TCP address permits a successful complete HTTPS attempt on the reserve", async () =>
        {
            var fixture = await ControlledHttpsFixture((index, _) => index == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("TCP failed", new SocketException((int)SocketError.NetworkUnreachable)))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
            Require(fixture.Result.Success && fixture.Result.HttpStatus == 200 && fixture.Attempts.SequenceEqual(new[] { "127.0.0.1", "127.0.0.2" }),
                "A TCP failure prevented reserve HTTPS or the complete attempts were not pinned individually.");
        });
        await Check("failed IPv6 connectivity permits the next IPv6 address", async () =>
        {
            var fixture = await ControlledHttpsFixture((index, _) => index == 0
                ? Task.FromException<HttpResponseMessage>(new HttpRequestException("IPv6 failed", new SocketException((int)SocketError.NetworkUnreachable)))
                : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)), [IPAddress.IPv6Loopback, IPAddress.Parse("::2")]);
            Require(fixture.Result.Success && fixture.Attempts.SequenceEqual(new[] { "::1", "::2" }), "IPv6 failure prevented reserve HTTPS.");
        });
        await Check("failure of all HTTPS addresses preserves an HTTPS failure and final attempt detail", async () =>
        {
            var fixture = await ControlledHttpsFixture((_, _) => Task.FromException<HttpResponseMessage>(new HttpRequestException("fixture handshake failure")));
            Require(!fixture.Result.Success && fixture.Result.Stage == "https" && fixture.Attempts.Count == 2
                && fixture.Result.Detail.Contains("2/2", StringComparison.Ordinal) && fixture.Result.Detail.Contains("fixture handshake failure", StringComparison.Ordinal),
                "All-address HTTPS failure or its last diagnostic was lost.");
        });
        await Check("optional country body failure does not retry or invalidate received HTTP success", async () =>
        {
            var fixture = await ControlledHttpsFixture((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new BrokenTraceStream()) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                return Task.FromResult(response);
            });
            Require(fixture.Attempts.Count == 1 && fixture.Result.Success && fixture.Result.Stage == "http" && fixture.Result.CountryCode is null,
                "Optional country metadata retried HTTPS or invalidated HTTP reachability.");
        });
        await Check("country trace remains readable after the completed attempt's former deadline", async () =>
        {
            var fixture = await ControlledHttpsFixture(async (_, token) =>
            {
                await Task.Delay(100, token);
                var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DelayedTraceStream(token)) };
                response.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain");
                return response;
            }, probeTimeout: TimeSpan.FromMilliseconds(600));
            Require(fixture.Attempts.Count == 1 && fixture.Result.Success && fixture.Result.CountryCode == "US"
                && fixture.Result.CountrySource == ConnectionCountrySource.SiteTrace,
                "A completed attempt's timer canceled optional country metadata before the overall deadline.");
        });
        await Check("DoH handshake timeout preserves DoH server phase instead of only operation-canceled text", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var transport = new DnsTransport();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            var sockets = new List<TcpClient>();
            var accepts = Task.Run(async () =>
            {
                sockets.Add(await listener.AcceptTcpClientAsync(deadline.Token));
            });
            try
            {
                var profile = new ChannelProfile { Mode = DnsMode.Doh, Servers = ["127.0.0.1"],
                    DohUrl = "https://doh.netpilot.invalid:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/dns-query" };
                var result = await new ProfileProbe(transport).CheckAsync(profile, "https://question-must-not-be-logged.netpilot.invalid/", TimeSpan.FromSeconds(2), deadline.Token);
                await accepts;
                Require(!result.Success && result.Stage == "dns" && result.Detail.Contains("DoH", StringComparison.Ordinal)
                    && result.Detail.Contains("сервер 1/1", StringComparison.Ordinal) && result.Detail.Contains("айм-аут", StringComparison.OrdinalIgnoreCase),
                    "DoH cancellation discarded the upstream phase or became success.");
                Require(!result.Detail.Contains("question-must-not-be-logged", StringComparison.Ordinal), "A DNS question leaked into DoH errors.");
            }
            finally { deadline.Cancel(); foreach (var socket in sockets) socket.Dispose(); }
        });
        await Check("pinned HTTPS still rejects an untrusted certificate and sends the intended SNI", async () =>
        {
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var key = RSA.Create(2048);
            var certificateRequest = new CertificateRequest("CN=tls.netpilot.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("tls.netpilot.invalid");
            certificateRequest.CertificateExtensions.Add(san.Build());
            using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
#if NET6_0
            using var serverCertificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));
#else
            using var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
#endif
            string? sni = null;
            string? serverError = null;
            var serve = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var tls = new SslStream(socket.GetStream(), false);
                try
                {
                    await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                        ServerCertificateSelectionCallback = (_, name) => { sni = name; return serverCertificate; }
                    }, deadline.Token);
                    await tls.ReadAtLeastAsync(new byte[256], 1, false, deadline.Token);
                }
                catch (Exception ex) when (ex is AuthenticationException or IOException) { serverError = ex.Message; }
            });
            using var client = DnsTransport.CreatePinnedClient(new Uri("https://tls.netpilot.invalid:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/"), [IPAddress.Loopback]);
            try { await client.GetAsync("https://tls.netpilot.invalid:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/", deadline.Token); }
            catch (HttpRequestException ex)
            {
                await serve;
                Require(sni == "tls.netpilot.invalid" && DnsTransport.DescribeFailure(ex).Contains("TLS/сертификат", StringComparison.Ordinal),
                    "The original SNI or certificate failure phase was lost. SNI=" + sni + "; server=" + serverError + "; " + DnsTransport.DescribeFailure(ex));
                return;
            }
            throw new Exception("The untrusted certificate was accepted.");
        });
    }

    private sealed class ControlledHandler(Func<CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(token);
    }

    private sealed class BrokenTraceStream : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new IOException("fixture trace body failure");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("fixture trace body failure"));
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class DelayedTraceStream(CancellationToken requestToken) : Stream
    {
        private bool _read;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException(); public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_read) return 0;
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestToken, cancellationToken);
            await Task.Delay(300, linked.Token);
            var trace = Encoding.UTF8.GetBytes("h=controlled.netpilot.invalid\nvisit_scheme=https\nloc=US\n");
            trace.CopyTo(buffer); _read = true; return trace.Length;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(ProbeResult Result, List<string> Attempts)> ControlledHttpsFixture(Func<int, CancellationToken, Task<HttpResponseMessage>> send, IPAddress[]? answers = null, TimeSpan? probeTimeout = null)
    {
        answers ??= [IPAddress.Loopback, IPAddress.Parse("127.0.0.2")];
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var transport = new DnsTransport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var serveDns = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await dns.ReceiveAsync(deadline.Token);
                var family = request.Buffer[^3] == 1 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
                await dns.SendAsync(AddressAnswer(request.Buffer, answers.Where(address => address.AddressFamily == family).ToArray()), request.RemoteEndPoint, deadline.Token);
            }
        });
        var attempts = new List<string>();
        var probe = new ProfileProbe(transport, (_, addresses) =>
        {
            Require(addresses.Count == 1, "A complete HTTPS attempt must be pinned to one address.");
            var index = attempts.Count; attempts.Add(addresses[0].ToString());
            return new HttpClient(new ControlledHandler(token => send(index, token))) { Timeout = Timeout.InfiniteTimeSpan };
        });
        var profile = new ChannelProfile { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port };
        var result = await probe.CheckAsync(profile, "https://controlled.netpilot.invalid/cdn-cgi/trace", probeTimeout ?? TimeSpan.FromSeconds(2), deadline.Token);
        await serveDns;
        return (result, attempts);
    }

    private sealed record FallbackFixture(ProbeResult Result, bool FirstConnected, bool SecondConnected, string? Sni, bool CallerCanceled, double ElapsedMs);

    private static async Task<FallbackFixture> HttpsFallbackFixture(bool cancelCaller, bool stallBoth)
    {
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var first = new TcpListener(IPAddress.Loopback, 0); first.Start();
        using var second = new TcpListener(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)first.LocalEndpoint).Port); second.Start();
        using var transport = new DnsTransport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var caller = new CancellationTokenSource();
        using var key = RSA.Create(2048);
        var certificateRequest = new CertificateRequest("CN=fallback.netpilot.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var san = new SubjectAlternativeNameBuilder(); san.AddDnsName("fallback.netpilot.invalid");
        certificateRequest.CertificateExtensions.Add(san.Build());
        using var certificate = certificateRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
#if NET6_0
        using var serverCertificate = new X509Certificate2(certificate.Export(X509ContentType.Pfx));
#else
        using var serverCertificate = X509CertificateLoader.LoadPkcs12(certificate.Export(X509ContentType.Pfx), null);
#endif
        bool firstConnected = false, secondConnected = false; string? sni = null;
        var serveDns = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await dns.ReceiveAsync(deadline.Token);
                var addresses = request.Buffer[^3] == 1 ? new[] { IPAddress.Loopback, IPAddress.Parse("127.0.0.2") } : Array.Empty<IPAddress>();
                await dns.SendAsync(AddressAnswer(request.Buffer, addresses), request.RemoteEndPoint, deadline.Token);
            }
        });
        async Task Serve(TcpListener listener, bool primary)
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                if (primary) firstConnected = true; else secondConnected = true;
                if (primary || stallBoth)
                {
                    if (primary && cancelCaller) caller.CancelAfter(50);
                    await Task.Delay(Timeout.Infinite, deadline.Token);
                }
                else
                {
                    using var tls = new SslStream(socket.GetStream(), false);
                    try { await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions {
                        ServerCertificateSelectionCallback = (_, name) => { sni = name; return serverCertificate; }
                    }, deadline.Token); }
                    catch (Exception ex) when (ex is AuthenticationException or IOException) { }
                }
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        }
        var serveFirst = Serve(first, true); var serveSecond = Serve(second, false);
        var timer = System.Diagnostics.Stopwatch.StartNew(); ProbeResult result = new(); bool canceled = false;
        try
        {
            var profile = new ChannelProfile { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port };
            try { result = await new ProfileProbe(transport).CheckAsync(profile, "https://fallback.netpilot.invalid:" + ((IPEndPoint)first.LocalEndpoint).Port + "/",
                TimeSpan.FromSeconds(1), caller.Token); }
            catch (OperationCanceledException) when (caller.IsCancellationRequested) { canceled = true; }
            await serveDns;
            return new(result, firstConnected, secondConnected, sni, canceled, timer.Elapsed.TotalMilliseconds);
        }
        finally { deadline.Cancel(); await Task.WhenAll(serveFirst, serveSecond, serveDns); }
    }

    private static async Task<(ProbeResult Result, bool Connected)> UnspecifiedAnswersFixture(bool includeUsable)
    {
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var https = new TcpListener(IPAddress.Loopback, 0); https.Start();
        using var transport = new DnsTransport();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var serveDns = Task.Run(async () =>
        {
            for (var index = 0; index < 2; index++)
            {
                var request = await dns.ReceiveAsync(deadline.Token);
                var ipv4 = request.Buffer[^3] == 1;
                var addresses = ipv4
                    ? (includeUsable ? new[] { IPAddress.Any, IPAddress.Loopback } : new[] { IPAddress.Any })
                    : new[] { IPAddress.IPv6Any };
                await dns.SendAsync(AddressAnswer(request.Buffer, addresses), request.RemoteEndPoint, deadline.Token);
            }
        });
        var acceptHttps = Task.Run(async () =>
        {
            try
            {
                using var socket = await https.AcceptTcpClientAsync(deadline.Token);
                // A local peer closes during TLS. No trusted certificate or external network is required.
                return true;
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { return false; }
        });
        try
        {
            var profile = new ChannelProfile { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port };
            var result = await new ProfileProbe(transport).CheckAsync(profile,
                "https://unspecified.netpilot.invalid:" + ((IPEndPoint)https.LocalEndpoint).Port + "/", TimeSpan.FromSeconds(3), deadline.Token);
            await serveDns;
            deadline.Cancel();
            return (result, await acceptHttps);
        }
        finally
        {
            deadline.Cancel();
            await Task.WhenAll(serveDns, acceptHttps);
        }
    }

    private static byte[] AddressAnswer(byte[] query, IReadOnlyList<IPAddress> addresses)
    {
        var header = (byte[])query.Clone(); header[2] = 0x81; header[3] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(6, 2), (ushort)addresses.Count);
        using var packet = new MemoryStream(); packet.Write(header);
        foreach (var address in addresses)
        {
            var bytes = address.GetAddressBytes();
            packet.Write(new byte[] { 0xc0, 12, 0, (byte)(bytes.Length == 4 ? 1 : 28), 0, 1, 0, 0, 0, 60, 0, (byte)bytes.Length });
            packet.Write(bytes);
        }
        return packet.ToArray();
    }

    private static void Require(bool value, string message) { if (!value) throw new Exception(message); }
    private static SocketsHttpHandler Handler(HttpClient client) =>
        (SocketsHttpHandler)(typeof(HttpMessageInvoker).GetField("_handler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client)!);

    private static async Task<HttpStatusCode> HeaderFixture(int length)
    {
        // The same HTTP/1 parser and configured header bound operate inside HTTPS.
        // Keep this parser fixture on loopback HTTP; TLS/SNI/trust are tested above.
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        var serve = Task.Run(async () =>
        {
            using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
            using var stream = socket.GetStream();
            var request = new byte[2048];
            await stream.ReadAtLeastAsync(request, 1, false, deadline.Token);
            var response = Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nX-Test: " + new string('x', length) + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
            try { await stream.WriteAsync(response, deadline.Token); }
            catch (IOException) { } // The oversized-header client may close immediately.
        });
        var uri = new Uri("http://headers.netpilot.invalid:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/");
        using var client = DnsTransport.CreatePinnedClient(uri, [IPAddress.Loopback]);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri) { Version = HttpVersion.Version11, VersionPolicy = HttpVersionPolicy.RequestVersionExact };
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            return response.StatusCode;
        }
        finally { await serve; }
    }
}
