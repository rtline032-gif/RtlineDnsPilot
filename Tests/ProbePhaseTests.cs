using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
#if NET6_0
using TcpListener = NetPilot.Tests.LegacyTestTcpListener;
#endif

namespace NetPilot.Tests;

public static class ProbePhaseTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        { try { await test(); report(name, true, ""); } catch (Exception ex) { report(name, false, ex.ToString()); } }
        await Check("phase observation retains the standard TLS validator and transport bounds", () =>
        {
            var trace = new ProbeAttemptTrace(IPAddress.Loopback);
            using var client = DnsTransport.CreatePinnedClient(new Uri("https://phase.netpilot.invalid/"), [IPAddress.Loopback], trace);
            var handler = Handler(client);
            Require(handler.PlaintextStreamFilter is not null && handler.SslOptions.RemoteCertificateValidationCallback is null
                && !handler.UseProxy && !handler.AllowAutoRedirect && !handler.UseCookies && handler.MaxResponseHeadersLength == 64,
                "Phase instrumentation changed HTTPS security or transport bounds.");
            return Task.CompletedTask;
        });
        await Check("IPv6 route failure remains TCP failure without TLS or certificate rejection", () =>
        {
            var trace = new ProbeAttemptTrace(IPAddress.Parse("2001:db8::1")); trace.TcpStarted();
            trace.Failed(new SocketException((int)SocketError.NetworkUnreachable));
            Require(trace.Data.Family == "IPv6" && trace.Data.Tcp.State == "error" && trace.Data.Tls.State == "not-started"
                && trace.Data.Http.State == "not-started", "An IPv6 route error was relabelled as TLS or HTTP.");
            return Task.CompletedTask;
        });
        await Check("post-handshake read reset keeps completed TCP TLS and a separate HTTP error", () =>
        {
            var trace = new ProbeAttemptTrace(IPAddress.Parse("192.0.2.1"));
            trace.TcpStarted(); trace.TcpConnected(); trace.TlsCompleted(HttpVersion.Version20);
            trace.Failed(new HttpRequestException("read reset", new SocketException((int)SocketError.ConnectionReset)));
            Require(trace.Data.Tcp.State == "success" && trace.Data.Tls.State == "success" && trace.Data.Http.State == "error"
                && trace.Data.HttpStatus is null && trace.Data.HttpVersion == "2.0", "A read reset erased successful TCP/TLS or invented a status.");
            return Task.CompletedTask;
        });
        await Check("late callbacks cannot mutate a completed diagnostic record", () =>
        {
            var trace = new ProbeAttemptTrace(IPAddress.Loopback); trace.TcpStarted(); trace.TcpConnected();
            trace.Failed(new OperationCanceledException());
            var before = JsonData.Encode(trace.Data); trace.TlsCompleted(HttpVersion.Version20); trace.TcpConnected();
            using var response = new HttpResponseMessage(HttpStatusCode.OK); trace.HttpReceived(response);
            Require(JsonData.Encode(trace.Data) == before, "A late callback changed a returned timeout.");
            return Task.CompletedTask;
        });
        await Check("latest diagnostics retention preserves numeric history and ordinary RPC bounds", () =>
        {
            var old = new ProbeResult { Success = true, DnsMs = 12, HttpsMs = 34, MonitoringKey = "same", Diagnostics = new(),
                Services = [new ServiceProbeResult { ServiceId = "chatgpt", Diagnostics = new() }] };
            var stats = new ProfileStats(); stats.Add(old);
            var next = new ProbeResult { Success = false, DnsMs = 17, HttpsMs = 39, MonitoringKey = "same", Diagnostics = new(),
                Services = [new ServiceProbeResult { ServiceId = "chatgpt", Diagnostics = new() }] };
            stats.Add(next);
            Require(stats.Samples.Count == 2 && stats.TotalChecks == 2 && stats.TotalSuccesses == 1 && stats.SuccessPercent == 50
                && old.DnsMs == 12 && old.HttpsMs == 34 && old.Diagnostics is null && old.Services[0].Diagnostics is null
                && next.Diagnostics is not null && next.Services[0].Diagnostics is not null, "Diagnostics changed numeric evidence or retained old arrays.");
            var projected = StatusProjection.Statistics([stats]);
            Require(projected.All(s => s.Samples.All(r => r.Diagnostics is null && r.Services.All(t => t.Diagnostics is null))),
                "Routine status contains full phase records.");
            return Task.CompletedTask;
        });
        await Check("loopback TLS observes TCP TLS and negotiated HTTP version without reading bodies", async () =>
        {
            var result = await TlsFixture(stallHeaders: false);
            var attempt = result.Diagnostics!.Attempts.Single();
            Require(result.Success && result.Diagnostics.Dns.Count == 2 && result.Diagnostics.Dns[1].State == "empty"
                && attempt.Tcp.State == "success" && attempt.Tls.State == "success" && attempt.Http.State == "success"
                && attempt.Tcp.ElapsedMs >= 0 && attempt.Tls.ElapsedMs >= 0 && attempt.Http.ElapsedMs >= 0
                && attempt.HttpStatus == 200 && attempt.HttpVersion == "1.1", "Observed TLS/HTTP boundaries or version were lost.");
        });
        await Check("a TLS peer stalling headers is not reported as a TCP or TLS timeout", async () =>
        {
            var result = await TlsFixture(stallHeaders: true);
            var attempt = result.Diagnostics!.Attempts.Single();
            Require(!result.Success && attempt.Tcp.State == "success" && attempt.Tls.State == "success" && attempt.Http.State == "timeout"
                && attempt.HttpStatus is null, "A header stall became an earlier connection-phase failure.");
        });
    }

    private static async Task<ProbeResult> TlsFixture(bool stallHeaders)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        using var dns = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=phase.netpilot.invalid", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder(); names.AddDnsName("phase.netpilot.invalid"); request.CertificateExtensions.Add(names.Build());
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
#if NET6_0
        using var certificate = new X509Certificate2(generated.Export(X509ContentType.Pfx), (string?)null, X509KeyStorageFlags.Exportable);
#else
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null, X509KeyStorageFlags.Exportable);
#endif
        var serveDns = Task.Run(async () =>
        {
            for (int i = 0; i < 2; i++)
            {
                var query = await dns.ReceiveAsync(deadline.Token);
                await dns.SendAsync(Answer(query.Buffer), query.RemoteEndPoint, deadline.Token);
            }
        });
        var serveTls = Task.Run(async () =>
        {
            try
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var tls = new SslStream(socket.GetStream(), false);
                await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                { ServerCertificate = certificate, ApplicationProtocols = [SslApplicationProtocol.Http11] }, deadline.Token);
                var bytes = new byte[4096]; await tls.ReadAtLeastAsync(bytes, 1, false, deadline.Token);
                if (stallHeaders) await Task.Delay(Timeout.Infinite, deadline.Token);
                else await tls.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"), deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        });
        using var transport = new DnsTransport();
        var probe = new ProfileProbe(transport, (uri, addresses, trace) =>
        {
            var client = DnsTransport.CreatePinnedClient(uri, addresses, trace);
            // Only this fixture accepts its exact generated certificate; the trust store is untouched.
            Handler(client).SslOptions.RemoteCertificateValidationCallback = (_, cert, _, _) => cert?.GetCertHashString() == certificate.GetCertHashString();
            return client;
        });
        try
        {
            return await probe.CheckAsync(new ChannelProfile { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = ((IPEndPoint)dns.Client.LocalEndPoint!).Port },
                "https://phase.netpilot.invalid:" + ((IPEndPoint)listener.LocalEndpoint).Port + "/", TimeSpan.FromSeconds(3), deadline.Token);
        }
        finally { deadline.Cancel(); await Task.WhenAll(serveDns, serveTls); }
    }
    private static byte[] Answer(byte[] query)
    {
        bool a = query[^3] == 1; var answer = new byte[query.Length + (a ? 16 : 0)]; query.CopyTo(answer, 0);
        answer[2] = 0x81; answer[3] = 0x80; BinaryPrimitives.WriteUInt16BigEndian(answer.AsSpan(6), a ? (ushort)1 : (ushort)0);
        if (a)
        {
            var record = answer.AsSpan(query.Length); record[0] = 0xc0; record[1] = 0x0c;
            BinaryPrimitives.WriteUInt16BigEndian(record[2..], 1); BinaryPrimitives.WriteUInt16BigEndian(record[4..], 1);
            BinaryPrimitives.WriteUInt32BigEndian(record[6..], 30); BinaryPrimitives.WriteUInt16BigEndian(record[10..], 4);
            IPAddress.Loopback.GetAddressBytes().CopyTo(record[12..]);
        }
        return answer;
    }
    private static SocketsHttpHandler Handler(HttpClient client) => (SocketsHttpHandler)typeof(HttpMessageInvoker)
        .GetField("_handler", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(client)!;
    private static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
}
