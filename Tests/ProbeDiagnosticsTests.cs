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
