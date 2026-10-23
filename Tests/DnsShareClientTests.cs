using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NetPilot.Tests;

public static class DnsShareClientTests
{
    private const string Endpoint = "https://script.google.com/macros/s/example_deployment_for_tests/exec";
    private static readonly string Invite = new('a', 64);
    private static DnsShareCandidate Profile(string ip = "8.8.8.8") => new() { Mode = DnsMode.Udp, Servers = [ip], DnsPort = 53, WorkedServices = [MonitoringTargets.ChatGpt] };
    private static HttpResponseMessage Json(string text = "{\"Protocol\":1,\"Ok\":true,\"Accepted\":1,\"Duplicates\":0}") => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/json") };
    private static HttpResponseMessage Redirect(string location) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(location); return response; }
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        { try { await test(); report(name, true, ""); } catch (Exception ex) { report(name, false, ex.Message); } }
        static void Require(bool value, string reason) { if (!value) throw new InvalidOperationException(reason); }
        static void Reject(Action action) { try { action(); } catch (ArgumentException) { return; } throw new InvalidOperationException("Invalid public receiver parameters were accepted."); }
        static async Task<DnsShareException> Error(Func<Task> action)
        { try { await action(); } catch (DnsShareException ex) { return ex; } throw new InvalidOperationException("Unconfirmed upload was reported as a success."); }

        await Check("share receiver accepts only an exact HTTPS Apps Script exec endpoint", () =>
        {
            Require(DnsShareClient.ValidateSettings(Endpoint, Invite).Endpoint == Endpoint, "Valid receiver was rejected.");
            foreach (var value in new[] { "", "http://script.google.com/macros/s/abc/exec", Endpoint + "?token=x", Endpoint + "#part", Endpoint.Replace("script.google.com", "script.google.com:443"),
                Endpoint.Replace("script.google.com", "script.google.com:8443"), Endpoint.Replace("script.google.com", "script.google.com.evil.invalid"),
                Endpoint.Replace("script.google.com", "account@script.google.com"), Endpoint.Replace("/exec", "/dev"), "https://accounts.google.com/", "https://script.google.com/macros/s/a%2fb/exec" })
                Reject(() => DnsShareClient.ValidateSettings(value, Invite));
            return Task.CompletedTask;
        });
        await Check("share invite is a per-recipient 64-character lower-case hex code", () =>
        {
            foreach (var value in new[] { "", new string('a', 63), new string('A', 64), new string('x', 64), Invite + "extra" })
                Reject(() => DnsShareClient.ValidateSettings(Endpoint, value));
            return Task.CompletedTask;
        });
        await Check("default sharing transport disables redirects, cookies and default credentials", () =>
        {
            using var handler = (HttpClientHandler)typeof(DnsShareClient).GetMethod("CreateHandler", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, null)!;
            Require(!handler.AllowAutoRedirect && !handler.UseCookies && !handler.UseDefaultCredentials && handler.Credentials is null, "Transport could automatically redirect credentials or reuse cookies.");
            return Task.CompletedTask;
        });
        await Check("share upload sends only the bounded public profile schema once", async () =>
        {
            var handler = new FakeHandler(async (request, token) =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == Endpoint, "Initial request is not a single receiver POST.");
                Require(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") && !request.Headers.Contains("Referer"), "Credential header leaked.");
                var payload = await request.Content!.ReadAsByteArrayAsync(token);
                Require(payload.Length <= DnsShareClient.MaxRequestBytes, "Request exceeded its byte limit.");
                using var json = JsonDocument.Parse(payload);
                Require(json.RootElement.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "InviteCode", "Profiles", "Protocol" }), "Envelope includes undeclared fields.");
                var profile = json.RootElement.GetProperty("Profiles")[0];
                Require(profile.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "DnsPort", "DohUrl", "Mode", "Servers", "WorkedServices" }), "Local names/IDs/observations were uploaded.");
                Require(json.RootElement.GetProperty("InviteCode").GetString() == Invite && profile.GetProperty("Mode").GetString() == "Udp", "Wire credentials or enum schema changed.");
                return Json();
            });
            using var client = new DnsShareClient(handler);
            var result = await client.SendAsync(Endpoint, Invite, [Profile()]);
            Require(result is { Accepted: 1, Duplicates: 0 } && handler.Calls == 1, "Confirmed reply or one-attempt contract failed.");
        });
        await Check("confirmed share reply distinguishes accepted and duplicate profiles", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"Protocol\":1,\"Ok\":true,\"Accepted\":1,\"Duplicates\":1}")));
            using var client = new DnsShareClient(handler);
            var result = await client.SendAsync(Endpoint, Invite, [Profile(), Profile("9.9.9.9")]);
            Require(result is { Accepted: 1, Duplicates: 1 }, "Accepted and duplicate counts were conflated.");
        });
        await Check("share response rejects missing, negative, excessive or contradictory counts", async () =>
        {
            foreach (var text in new[] { "{\"Protocol\":1,\"Ok\":true,\"Accepted\":2,\"Duplicates\":0}", "{\"Protocol\":1,\"Ok\":true,\"Accepted\":1,\"Duplicates\":1}",
                "{\"Protocol\":1,\"Ok\":true,\"Accepted\":0,\"Duplicates\":0}",
                "{\"Protocol\":1,\"Ok\":true,\"Accepted\":-1,\"Duplicates\":0}", "{\"Protocol\":1,\"Ok\":true,\"Accepted\":0}",
                "{\"Protocol\":2,\"Ok\":true,\"Accepted\":1,\"Duplicates\":0}", "{\"Protocol\":1,\"Ok\":true,\"Accepted\":\"1\",\"Duplicates\":0}",
                "{\"Protocol\":1,\"Ok\":false,\"Accepted\":1,\"Duplicates\":0}", "{\"Protocol\":1,\"Ok\":true,\"Ok\":false,\"Accepted\":0,\"Duplicates\":0}" })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(Json(text)));
                using var client = new DnsShareClient(handler);
                var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
                Require(error.MayHaveBeenSent && handler.Calls == 1, "Bad confirmation was trusted or replayed.");
            }
        });
        await Check("receiver denial is explicit without echoing its untrusted error text", async () =>
        {
            foreach (var code in new[] { "denied", "rate_limit", "capacity", "busy", "not_configured", "invalid_request", "secret-" + Invite })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(Json("{\"Protocol\":1,\"Ok\":false,\"Accepted\":0,\"Duplicates\":0,\"Error\":\"" + code + "\"}")));
                using var client = new DnsShareClient(handler);
                var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
                Require(!error.MayHaveBeenSent && !error.Message.Contains(Invite) && !error.Message.Contains("secret-") && error.Message.Length < 600, "Server error text or secret was exposed.");
            }
        });
        await Check("Apps Script response redirect uses GET without forwarding the invite body or headers", async () =>
        {
            var handler = new FakeHandler((request, _) =>
            {
                if (request.Method == HttpMethod.Post) return Task.FromResult(Redirect("https://script.googleusercontent.com/macros/echo?user_content_key=test&lib=test"));
                Require(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri!.Host == "script.googleusercontent.com", "Upload body was replayed to a redirect.");
                Require(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") && !request.Headers.Contains("Referer") && !request.RequestUri!.AbsoluteUri.Contains(Invite), "Redirect forwarded credentials.");
                return Task.FromResult(Json());
            });
            using var client = new DnsShareClient(handler);
            await client.SendAsync(Endpoint, Invite, [Profile()]);
            Require(handler.Calls == 2 && handler.Posts == 1, "Response redirect replayed the upload.");
        });
        await Check("sharing permits at most two response GET redirects", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Redirect("https://script.googleusercontent.com/macros/echo?user_content_key=again")));
            using var client = new DnsShareClient(handler);
            var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
            Require(error.MayHaveBeenSent && handler.Calls == 3 && handler.Posts == 1, "Redirect chain exceeded its bound or repeated POST.");
        });
        await Check("share response redirects reject foreign hosts, login, plaintext, ports and other paths", async () =>
        {
            foreach (var target in new[] { "https://evil.invalid/macros/echo", "https://accounts.google.com/ServiceLogin", "http://script.googleusercontent.com/macros/echo", "https://script.googleusercontent.com:443/macros/echo",
                "https://script.googleusercontent.com/macros/other", "https://user@script.googleusercontent.com/macros/echo", "https://script.googleusercontent.com/macros/echo?invite=" + Invite })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(Redirect(target)));
                using var client = new DnsShareClient(handler);
                var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
                Require(error.MayHaveBeenSent && handler.Calls == 1 && handler.Posts == 1 && !error.Message.Contains(target), "Unsafe redirect was followed or leaked.");
            }
        });
        await Check("share timeout is ambiguous and never automatically resends", async () =>
        {
            var handler = new FakeHandler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Json(); });
            using var client = new DnsShareClient(handler, TimeSpan.FromMilliseconds(60));
            var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
            Require(error.MayHaveBeenSent && handler.Calls == 1, "Timed-out POST was replayed or claimed not sent.");
        });
        await Check("network errors redact private exception details and never replay the POST", async () =>
        {
            var handler = new FakeHandler((_, _) => throw new HttpRequestException("untrusted " + Invite + " " + Endpoint));
            using var client = new DnsShareClient(handler);
            var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
            Require(error.MayHaveBeenSent && handler.Calls == 1 && !error.ToString().Contains(Invite) && !error.ToString().Contains(Endpoint), "Transport details leaked through exception text.");
        });
        await Check("pre-cancelled share does not start an HTTP request", async () =>
        {
            var handler = new FakeHandler((_, _) => Task.FromResult(Json()));
            using var client = new DnsShareClient(handler);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()], cancellation.Token));
            Require(!error.MayHaveBeenSent && handler.Calls == 0, "Cancelled operation started an upload.");
        });
        await Check("share response byte limits apply to declared and streaming bodies", async () =>
        {
            foreach (var streaming in new[] { false, true })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                { Content = streaming ? new StreamContent(new NonSeekableStream(new byte[DnsShareClient.MaxResponseBytes + 1])) : new ByteArrayContent(new byte[DnsShareClient.MaxResponseBytes + 1]) }));
                using var client = new DnsShareClient(handler);
                var error = await Error(() => client.SendAsync(Endpoint, Invite, [Profile()]));
                Require(error.MayHaveBeenSent && handler.Calls == 1, "Oversized response was accepted or replayed.");
            }
        });
        await Check("share sender rejects empty, excessive, private, duplicate and base profiles before HTTP", async () =>
        {
            var baseline = AppConfig.Defaults().Profiles[0];
            foreach (var values in new IReadOnlyCollection<DnsShareCandidate>[] { [], Enumerable.Repeat(Profile(), 33).ToArray(), [Profile("192.168.1.1")], [Profile(), Profile()],
                [new DnsShareCandidate { Mode = baseline.Mode, Servers = baseline.Servers, DnsPort = baseline.DnsPort, DohUrl = baseline.DohUrl, WorkedServices = [MonitoringTargets.ChatGpt] }] })
            {
                var handler = new FakeHandler((_, _) => Task.FromResult(Json()));
                using var client = new DnsShareClient(handler);
                try { await client.SendAsync(Endpoint, Invite, values); throw new InvalidOperationException("Invalid candidate uploaded."); }
                catch (ArgumentException) { Require(handler.Calls == 0, "Invalid profile reached HTTP."); }
            }
        });
        await Check("share settings default empty and round-trip only through current-user DPAPI", () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "work", "dns-share-tests", Guid.NewGuid().ToString("N"));
            var empty = DnsShareSettingsStore.Load(directory);
            Require(empty.Endpoint.Length == 0 && empty.InviteCode.Length == 0, "A distributable receiver or secret was embedded by default.");
            DnsShareSettingsStore.Save(directory, new DnsShareSettings { Endpoint = Endpoint, InviteCode = Invite });
            var bytes = File.ReadAllBytes(Path.Combine(directory, DnsShareSettingsStore.FileName));
            Require(!Encoding.UTF8.GetString(bytes).Contains(Invite) && !Encoding.UTF8.GetString(bytes).Contains(Endpoint), "Saved receiver credentials are plaintext.");
            var loaded = DnsShareSettingsStore.Load(directory);
            Require(loaded.Endpoint == Endpoint && loaded.InviteCode == Invite, "Current-user protected settings did not round trip.");
            return Task.CompletedTask;
        });
        await Check("corrupt protected share settings are preserved and reported without secret details", () =>
        {
            var directory = Path.Combine(Environment.CurrentDirectory, "work", "dns-share-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DnsShareSettingsStore.FileName);
            var damaged = "{\"Version\":1,\"Protected\":\"not-a-valid-blob\"}";
            File.WriteAllText(path, damaged);
            try { DnsShareSettingsStore.Load(directory); throw new InvalidOperationException("Corrupt DPAPI file was accepted."); }
            catch (InvalidDataException ex) { Require(File.ReadAllText(path) == damaged && !ex.Message.Contains(directory), "Damaged file was rewritten or local path leaked."); }
            return Task.CompletedTask;
        });
    }
    private sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; if (request.Method == HttpMethod.Post) Posts++; return send(request, cancellationToken); }
    }
    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}
