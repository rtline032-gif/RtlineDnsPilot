using System.Buffers.Binary;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace NetPilot.Tests;

public static class ProtocolTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Test(string name, Func<Task> body)
        {
            try { await body(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }
        await Test("DNS query preserves requested QTYPE and parses compressed IPv4/IPv6", () =>
        {
            var query = DnsWire.Query("example.test", 28);
            Require(query[^4] == 0 && query[^3] == 28 && query[^1] == 1, "AAAA/IN question missing");
            var response = Answer(query, IPAddress.Parse("2001:db8::7"));
            DnsWire.ValidateResponse(query, response);
            Require(DnsWire.Addresses(response).Single().Equals(IPAddress.Parse("2001:db8::7")), "IPv6 answer lost");
            Require(DnsWire.Addresses(Answer(DnsWire.Query("example.test"), IPAddress.Parse("203.0.113.7"))).Single().ToString() == "203.0.113.7", "IPv4 answer lost");
            return Task.CompletedTask;
        });
        await Test("DNS rejects forged ID, question, QR, malformed address and pointer loop", () =>
        {
            var query = DnsWire.Query("example.test");
            var good = Answer(query, IPAddress.Parse("203.0.113.7"));
            var badId = (byte[])good.Clone(); badId[0] ^= 1;
            ExpectRejected(() => DnsWire.ValidateResponse(query, badId));
            var badQuestion = (byte[])good.Clone(); badQuestion[13] = (byte)'x';
            ExpectRejected(() => DnsWire.ValidateResponse(query, badQuestion));
            var noQr = (byte[])good.Clone(); noQr[2] &= 0x7f;
            ExpectRejected(() => DnsWire.ValidateResponse(query, noQr));
            var loop = (byte[])good.Clone(); loop[12] = 0xc0; loop[13] = 12;
            ExpectRejected(() => DnsWire.ValidateResponse(query, loop));
            var shortA = good[..^1]; shortA[query.Length + 11] = 3;
            ExpectRejected(() => DnsWire.Addresses(shortA));
            ExpectRejected(() => DnsWire.Query("invalid..name"));
            return Task.CompletedTask;
        });
        await Test("DNS excludes unrelated answer injection and follows CNAME", () =>
        {
            var query = DnsWire.Query("example.test");
            using var packet = new MemoryStream(); packet.Write(Header(query, 3));
            packet.Write([0xc0, 12]); Write16(packet, 5); Write16(packet, 1); Write32(packet, 60);
            var alias = Name("alias.test"); Write16(packet, alias.Length); packet.Write(alias);
            WriteRecord(packet, Name("evil.test"), IPAddress.Parse("203.0.113.66"));
            WriteRecord(packet, alias, IPAddress.Parse("203.0.113.8"));
            var response = packet.ToArray(); DnsWire.ValidateResponse(query, response);
            Require(DnsWire.Addresses(response).Select(x => x.ToString()).SequenceEqual(["203.0.113.8"]), "Unrelated answer accepted or CNAME lost");
            return Task.CompletedTask;
        });
        await Test("SERVFAIL preserves transaction and question", () =>
        {
            var query = DnsWire.Query("example.test", 16);
            var fail = DnsWire.Failure(query); DnsWire.ValidateResponse(query, fail);
            Require((fail[3] & 15) == 2 && fail.Length == query.Length, "Invalid SERVFAIL");
            Require(DnsWire.Failure([1]).Length == 12, "Malformed input failure must remain bounded");
            return Task.CompletedTask;
        });
        await Test("Connected UDP accepts matching upstream response", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = ServeOne(upstream, p => Answer(p, IPAddress.Parse("203.0.113.9")), deadline.Token);
            using var transport = new DnsTransport();
            var response = await transport.ExchangeAsync(LocalProfile(Port(upstream)), DnsWire.Query("example.test"), deadline.Token);
            Require(DnsWire.Addresses(response).Single().ToString() == "203.0.113.9", "UDP answer incorrect");
            await serve;
        });
        await Test("UDP retries secondary after SERVFAIL and REFUSED", async () =>
        {
            foreach (var code in new[] { 2, 5 })
            {
                using var primary = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var secondary = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), Port(primary)));
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var transport = new DnsTransport();
                var first = ServeOne(primary, q => { var r = Header(q, 0); r[3] |= (byte)code; return r; }, deadline.Token);
                var second = ServeOne(secondary, q => Answer(q, IPAddress.Parse("203.0.113.11")), deadline.Token);
                try
                {
                    var profile = LocalProfile(Port(primary)); profile.Servers = ["127.0.0.1", "127.0.0.2"];
                    var response = await transport.ExchangeAsync(profile, DnsWire.Query("example.test"), deadline.Token);
                    Require((response[3] & 15) == 0 && DnsWire.Addresses(response).Single().ToString() == "203.0.113.11", "RCODE " + code + " prevented secondary failover");
                    await Task.WhenAll(first, second);
                }
                finally
                {
                    deadline.Cancel();
                    try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
                }
            }
        });
        await Test("UDP preserves authoritative NXDOMAIN without querying secondary", async () =>
        {
            using var primary = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var secondary = new UdpClient(new IPEndPoint(IPAddress.Parse("127.0.0.2"), Port(primary)));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)); using var transport = new DnsTransport();
            var first = ServeOne(primary, q => { var r = Header(q, 0); r[3] |= 3; return r; }, deadline.Token);
            var profile = LocalProfile(Port(primary)); profile.Servers = ["127.0.0.1", "127.0.0.2"];
            var response = await transport.ExchangeAsync(profile, DnsWire.Query("missing.example.test"), deadline.Token);
            Require((response[3] & 15) == 3 && secondary.Available == 0, "NXDOMAIN must remain a valid DNS result");
            await first;
        });
        await Test("Connected UDP ignores packets from another source port", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var rogue = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = Task.Run(async () =>
            {
                var request = await upstream.ReceiveAsync(deadline.Token);
                await rogue.SendAsync(Answer(request.Buffer, IPAddress.Parse("203.0.113.66")), request.RemoteEndPoint, deadline.Token);
                await Task.Delay(30, deadline.Token);
                await upstream.SendAsync(Answer(request.Buffer, IPAddress.Parse("203.0.113.9")), request.RemoteEndPoint, deadline.Token);
            });
            using var transport = new DnsTransport();
            var response = await transport.ExchangeAsync(LocalProfile(Port(upstream)), DnsWire.Query("example.test"), deadline.Token);
            Require(DnsWire.Addresses(response).Single().ToString() == "203.0.113.9", "Off-source response accepted");
            await serve;
        });
        await Test("UDP truncated incomplete answer falls back to framed TCP", async () =>
        {
            var reservation = ReserveDnsFixture();
            using var tcp = reservation.Tcp;
            using var udp = reservation.Udp;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var udpServe = ServeOne(udp, q => { var r = Header(q, 1); r[2] |= 2; return r; }, deadline.Token);
            var tcpServe = Task.Run(async () =>
            {
                using var client = await tcp.AcceptTcpClientAsync(deadline.Token);
                var stream = client.GetStream(); var q = await ReadFrame(stream, deadline.Token);
                await WriteFrame(stream, Answer(q, IPAddress.Parse("203.0.113.10")), deadline.Token);
            });
            using var transport = new DnsTransport();
            var response = await transport.ExchangeAsync(LocalProfile(Port(udp)), DnsWire.Query("example.test"), deadline.Token);
            Require(DnsWire.Addresses(response).Single().ToString() == "203.0.113.10", "TCP fallback not used");
            await Task.WhenAll(udpServe, tcpServe);
        });
        await Test("UDP rejects forged transaction from configured endpoint", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = ServeOne(upstream, q => { var r = Answer(q, IPAddress.Loopback); r[1] ^= 1; return r; }, deadline.Token);
            using var transport = new DnsTransport();
            await ExpectRejectedAsync(() => transport.ExchangeAsync(LocalProfile(Port(upstream)), DnsWire.Query("example.test"), deadline.Token));
            await serve;
        });
        await Test("Upstream UDP deadline cancellation is respected", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport();
            using var deadline = new CancellationTokenSource(TimeSpan.FromMilliseconds(120));
            try { await transport.ExchangeAsync(LocalProfile(Port(upstream)), DnsWire.Query("example.test"), deadline.Token); throw new Exception("Cancellation ignored"); }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested) { }
        });
        await Test("Relay forwards TXT over loopback UDP and sequential framed TCP", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var profile = LocalProfile(Port(upstream));
            var serve = Task.Run(async () =>
            {
                for (var i = 0; i < 3; i++) await ServeOne(upstream, TxtAnswer, deadline.Token);
            });
            await using var relay = new DnsRelay(transport, () => profile);
            var relayPort = await StartRelayFixtureAsync(relay, false, deadline.Token);
            using var local = new UdpClient(AddressFamily.InterNetwork); local.Connect(IPAddress.Loopback, relayPort);
            var query = DnsWire.Query("example.test", 16);
            await local.SendAsync(query, deadline.Token);
            var answer = await local.ReceiveAsync(deadline.Token); DnsWire.ValidateResponse(query, answer.Buffer);
            Require(answer.Buffer[^1] == (byte)'k' && (answer.Buffer[3] & 15) == 0, "TXT UDP response lost");
            using var client = new TcpClient(AddressFamily.InterNetwork);
            await client.ConnectAsync(IPAddress.Loopback, relayPort, deadline.Token);
            for (var i = 0; i < 2; i++)
            {
                var q = DnsWire.Query("example.test", 16);
                await WriteFrame(client.GetStream(), q, deadline.Token);
                var r = await ReadFrame(client.GetStream(), deadline.Token); DnsWire.ValidateResponse(q, r);
                Require(r[^1] == (byte)'k', "TXT TCP response lost");
            }
            await serve;
            Require(relay.IsRunning, "Relay stopped unexpectedly");
            await relay.DisposeAsync(); Require(!relay.IsRunning, "Relay still running after disposal");
        });
        await Test("Relay returns SERVFAIL when active upstream is absent", async () =>
        {
            using var transport = new DnsTransport();
            await using var relay = new DnsRelay(transport, () => null);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var port = await StartRelayFixtureAsync(relay, false, deadline.Token);
            using var client = new UdpClient(AddressFamily.InterNetwork); client.Connect(IPAddress.Loopback, port);
            var query = DnsWire.Query("example.test"); await client.SendAsync(query, deadline.Token);
            var result = (await client.ReceiveAsync(deadline.Token)).Buffer; DnsWire.ValidateResponse(query, result);
            Require((result[3] & 15) == 2, "Missing upstream must cause SERVFAIL");
        });
        await Test("Relay lifetime cancellation releases loopback listener ports", async () =>
        {
            using var transport = new DnsTransport(); using var lifetime = new CancellationTokenSource();
            await using var relay = new DnsRelay(transport, () => null);
            var port = await StartRelayFixtureAsync(relay, false, lifetime.Token);
            lifetime.Cancel(); Require(!relay.IsRunning, "Cancelled relay still reports running");
            using var replacementTcp = new TcpListener(IPAddress.Loopback, port); replacementTcp.Start();
            using var replacementUdp = new UdpClient(new IPEndPoint(IPAddress.Loopback, port));
        });
        await Test("Relay forwards over IPv6 loopback UDP and TCP", async () =>
        {
            if (!Socket.OSSupportsIPv6) throw new PlatformNotSupportedException("IPv6 is required for this test");
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = Task.Run(async () => { for (var i = 0; i < 2; i++) await ServeOne(upstream, TxtAnswer, deadline.Token); });
            await using var relay = new DnsRelay(transport, () => LocalProfile(Port(upstream)));
            var port = await StartRelayFixtureAsync(relay, true, deadline.Token);
            using var udp = new UdpClient(AddressFamily.InterNetworkV6); udp.Connect(IPAddress.IPv6Loopback, port);
            var query = DnsWire.Query("example.test", 16); await udp.SendAsync(query, deadline.Token);
            DnsWire.ValidateResponse(query, (await udp.ReceiveAsync(deadline.Token)).Buffer);
            using var tcp = new TcpClient(AddressFamily.InterNetworkV6); await tcp.ConnectAsync(IPAddress.IPv6Loopback, port, deadline.Token);
            await WriteFrame(tcp.GetStream(), query, deadline.Token);
            var reply = await ReadFrame(tcp.GetStream(), deadline.Token); DnsWire.ValidateResponse(query, reply);
            Require(reply[^1] == (byte)'k', "IPv6 TCP answer lost"); await serve;
        });
        await Test("Relay converts oversized UDP response to TC without breaking question", async () =>
        {
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var serve = ServeOne(upstream, LargeTxtAnswer, deadline.Token);
            await using var relay = new DnsRelay(transport, () => LocalProfile(Port(upstream)));
            var port = await StartRelayFixtureAsync(relay, false, deadline.Token);
            using var client = new UdpClient(AddressFamily.InterNetwork); client.Connect(IPAddress.Loopback, port);
            var query = DnsWire.Query("example.test", 16); await client.SendAsync(query, deadline.Token);
            var response = (await client.ReceiveAsync(deadline.Token)).Buffer; DnsWire.ValidateResponse(query, response);
            Require(response.Length <= 512 && (response[2] & 2) != 0, "UDP size/TC limit missing");
            await serve;
        });
        await Test("DoH pins bootstrap IP and rejects untrusted HTTPS certificate", async () =>
        {
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var accepted = false;
            var serve = InvalidTlsServer(server, () => accepted = true, deadline.Token);
            using var transport = new DnsTransport();
            var profile = LocalProfile(53); profile.Mode = DnsMode.Doh;
            profile.DohUrl = "https://doh.netpilot.invalid:" + ((IPEndPoint)server.LocalEndpoint).Port + "/dns-query";
            await ExpectRejectedAsync(() => transport.ExchangeAsync(profile, DnsWire.Query("example.test"), deadline.Token));
            await serve;
            Require(accepted, "Explicit bootstrap IP was not contacted");
        });
        await Test("DoH attempts secondary after primary TLS certificate failure", async () =>
        {
            using var primary = new TcpListener(IPAddress.Loopback, 0); primary.Start();
            using var secondary = new TcpListener(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)primary.LocalEndpoint).Port); secondary.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(6)); using var transport = new DnsTransport();
            var firstAccepted = false; var secondAccepted = false;
            var first = InvalidTlsServer(primary, () => firstAccepted = true, deadline.Token);
            var second = InvalidTlsServer(secondary, () => secondAccepted = true, deadline.Token);
            try
            {
                var profile = LocalProfile(53); profile.Mode = DnsMode.Doh; profile.Servers = ["127.0.0.1", "127.0.0.2"];
                profile.DohUrl = "https://doh.netpilot.invalid:" + ((IPEndPoint)primary.LocalEndpoint).Port + "/dns-query";
                await ExpectRejectedAsync(() => transport.ExchangeAsync(profile, DnsWire.Query("example.test"), deadline.Token));
                Require(firstAccepted && secondAccepted, "TLS failure on primary prevented secondary bootstrap attempt");
                await Task.WhenAll(first, second);
            }
            finally
            {
                deadline.Cancel();
                try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
            }
        });
        await Test("DoH primary TLS timeout leaves deadline for secondary", async () =>
        {
            using var primary = new TcpListener(IPAddress.Loopback, 0); primary.Start();
            using var secondary = new TcpListener(IPAddress.Parse("127.0.0.2"), ((IPEndPoint)primary.LocalEndpoint).Port); secondary.Start();
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(7)); using var transport = new DnsTransport();
            var firstAccepted = false; var secondAccepted = false;
            var first = Task.Run(async () =>
            {
                using var client = await primary.AcceptTcpClientAsync(deadline.Token); firstAccepted = true;
                await Task.Delay(Timeout.InfiniteTimeSpan, deadline.Token); // TCP accepts but never completes TLS.
            });
            var second = InvalidTlsServer(secondary, () => secondAccepted = true, deadline.Token);
            try
            {
                var profile = LocalProfile(53); profile.Mode = DnsMode.Doh; profile.Servers = ["127.0.0.1", "127.0.0.2"];
                profile.DohUrl = "https://doh.netpilot.invalid:" + ((IPEndPoint)primary.LocalEndpoint).Port + "/dns-query";
                await ExpectRejectedAsync(() => transport.ExchangeAsync(profile, DnsWire.Query("example.test"), deadline.Token));
                Require(firstAccepted && secondAccepted && !deadline.IsCancellationRequested, "Primary TLS wait consumed the secondary's deadline");
                await second;
            }
            finally
            {
                deadline.Cancel();
                try { await Task.WhenAll(first, second); } catch (OperationCanceledException) { }
            }
        });
        await Test("HTTPS probe resolves through profile and preserves standard TLS rejection", async () =>
        {
            using var server = new TcpListener(IPAddress.Loopback, 0); server.Start();
            using var upstream = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            using var transport = new DnsTransport(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var accepted = false;
            var tlsServe = InvalidTlsServer(server, () => accepted = true, deadline.Token);
            var dnsServe = Task.Run(async () =>
            {
                for (var i = 0; i < 2; i++) await ServeOne(upstream, q => (q[^3] == 28) ? Header(q, 0) : Answer(q, IPAddress.Loopback), deadline.Token);
            });
            var result = await new ProfileProbe(transport).CheckAsync(LocalProfile(Port(upstream)), "https://probe.netpilot.invalid:" + ((IPEndPoint)server.LocalEndpoint).Port + "/", TimeSpan.FromSeconds(5), deadline.Token);
            await Task.WhenAll(tlsServe, dnsServe);
            Require(accepted && !result.Success && result.Stage == "https" && result.DnsMs > 0 && result.HttpsMs > 0, "Probe must resolve explicitly, reject TLS, and separate timings");
        });
    }

    private static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void ExpectRejected(Action action)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidDataException or ArgumentException or IOException) { return; }
        throw new Exception("Malformed/forged message accepted");
    }
    private static async Task ExpectRejectedAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or HttpRequestException or AuthenticationException or InvalidDataException) { return; }
        throw new Exception("Untrusted transport response accepted");
    }
    private static int Port(UdpClient client) => ((IPEndPoint)client.Client.LocalEndPoint!).Port;
    private static (TcpListener Tcp, UdpClient Udp) ReserveDnsFixture()
    {
        SocketException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // TCP bind(0) says nothing about UDP occupancy. Hold both protocol reservations
            // before starting either responder; retry only a conflicting candidate port.
            var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
            var tcp = new TcpListener(IPAddress.Loopback, Port(udp));
            try { tcp.Start(); return (tcp, udp); }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                last = ex; tcp.Stop(); udp.Dispose();
            }
            catch { tcp.Stop(); udp.Dispose(); throw; }
        }
        throw new IOException("Не удалось одновременно зарезервировать TCP/UDP порт для DNS фикстуры после 8 кандидатов.", last);
    }
    private static async Task<int> StartRelayFixtureAsync(DnsRelay relay, bool ipv6, CancellationToken token)
    {
        SocketException? last = null;
        for (var attempt = 0; attempt < 8; attempt++)
        {
            // This test needs any unused high port, not the system DNS port. A TCP-only
            // bind(0) cannot establish that UDP is free. Verify both, then let the
            // relay own them; another process can still race the release/rebind gap.
            var reservation = ReserveDnsFixture();
            int port = Port(reservation.Udp);
            reservation.Tcp.Stop(); reservation.Udp.Dispose();
            try { await relay.StartAsync(port, ipv6, token); return port; }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                last = ex;
                Console.WriteLine($"INFO test-port bind rejected: port={port}, error={ex.NativeErrorCode}, candidate={attempt + 1}/8");
            }
        }
        throw new IOException("Не удалось запустить тестовый relay на свободном TCP/UDP порту после 8 кандидатов.", last);
    }
    private static ChannelProfile LocalProfile(int port) => new() { Mode = DnsMode.Udp, Servers = ["127.0.0.1"], DnsPort = port };
    private static async Task ServeOne(UdpClient client, Func<byte[], byte[]> response, CancellationToken token)
    {
        var request = await client.ReceiveAsync(token);
        await client.SendAsync(response(request.Buffer), request.RemoteEndPoint, token);
    }
    private static byte[] Header(byte[] query, int answers)
    {
        var response = (byte[])query.Clone(); response[2] = 0x81; response[3] = 0x80;
        BinaryPrimitives.WriteUInt16BigEndian(response.AsSpan(6), (ushort)answers);
        return response;
    }
    private static byte[] Answer(byte[] query, IPAddress address)
    {
        using var packet = new MemoryStream(); packet.Write(Header(query, 1)); WriteRecord(packet, [0xc0, 12], address); return packet.ToArray();
    }
    private static void WriteRecord(Stream packet, byte[] name, IPAddress address)
    {
        packet.Write(name); Write16(packet, address.AddressFamily == AddressFamily.InterNetwork ? 1 : 28); Write16(packet, 1); Write32(packet, 60);
        var bytes = address.GetAddressBytes(); Write16(packet, bytes.Length); packet.Write(bytes);
    }
    private static byte[] TxtAnswer(byte[] query)
    {
        using var packet = new MemoryStream(); packet.Write(Header(query, 1));
        packet.Write([0xc0, 12]); Write16(packet, 16); Write16(packet, 1); Write32(packet, 60); Write16(packet, 3); packet.Write([2, (byte)'o', (byte)'k']);
        return packet.ToArray();
    }
    private static byte[] LargeTxtAnswer(byte[] query)
    {
        using var packet = new MemoryStream(); packet.Write(Header(query, 3));
        for (var i = 0; i < 3; i++)
        {
            packet.Write([0xc0, 12]); Write16(packet, 16); Write16(packet, 1); Write32(packet, 60); Write16(packet, 251);
            packet.WriteByte(250); packet.Write(new byte[250]);
        }
        return packet.ToArray();
    }
    private static byte[] Name(string value)
    {
        using var bytes = new MemoryStream(); foreach (var label in value.Split('.')) { bytes.WriteByte((byte)label.Length); bytes.Write(System.Text.Encoding.ASCII.GetBytes(label)); }
        bytes.WriteByte(0); return bytes.ToArray();
    }
    private static void Write16(Stream stream, int value) { Span<byte> b = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)value); stream.Write(b); }
    private static void Write32(Stream stream, int value) { Span<byte> b = stackalloc byte[4]; BinaryPrimitives.WriteInt32BigEndian(b, value); stream.Write(b); }
    private static async Task WriteFrame(Stream stream, byte[] packet, CancellationToken token)
    {
        var prefix = new byte[2]; BinaryPrimitives.WriteUInt16BigEndian(prefix, (ushort)packet.Length);
        await stream.WriteAsync(prefix, token); await stream.WriteAsync(packet, token);
    }
    private static async Task<byte[]> ReadFrame(Stream stream, CancellationToken token)
    {
        var prefix = new byte[2]; await stream.ReadExactlyAsync(prefix, token);
        var bytes = new byte[BinaryPrimitives.ReadUInt16BigEndian(prefix)]; await stream.ReadExactlyAsync(bytes, token); return bytes;
    }
    private static async Task InvalidTlsServer(TcpListener listener, Action accepted, CancellationToken token)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=untrusted.netpilot.invalid", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        using var client = await listener.AcceptTcpClientAsync(token); accepted();
        using var tls = new SslStream(client.GetStream(), false);
        try
        {
            await tls.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, token);
            var buffer = new byte[1024]; await tls.ReadAtLeastAsync(buffer, 1, false, token);
        }
        catch (Exception ex) when (ex is IOException or AuthenticationException) { }
    }
}
