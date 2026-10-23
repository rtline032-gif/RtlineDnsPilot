using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;

namespace NetPilot.Tests;

public static class OneClickDnsShareTests
{
    private const string TestEndpoint = "https://script.google.com/macros/s/example_deployment_for_tests/exec";
    private static DnsShareCandidate Profile(string ip = "8.8.8.8") => new()
    { Mode = DnsMode.Udp, Servers = [ip], DnsPort = 53, WorkedServices = [MonitoringTargets.ChatGpt] };
    private static HttpResponseMessage Reply(int accepted = 1, int duplicates = 0, int protocol = 2) => new(HttpStatusCode.OK)
    { Content = new StringContent(JsonData.Encode(new { Protocol = protocol, Ok = true, Accepted = accepted, Duplicates = duplicates }), Encoding.UTF8, "application/json") };

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> action)
        { try { await action(); report(name, true, ""); } catch (Exception ex) { report(name, false, ex.ToString()); } }
        static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        static async Task<DnsShareException> Error(Func<Task> action)
        { try { await action(); } catch (DnsShareException ex) { return ex; } throw new InvalidOperationException("An uncertain request was reported as confirmed."); }

        await Check("one-click sharing has no endpoint invite or settings parameter", () =>
        {
            var methods = typeof(DnsShareClient).GetMethods(BindingFlags.Instance | BindingFlags.Public).Where(m => m.Name == nameof(DnsShareClient.SendToOwnerAsync)).ToArray();
            Require(methods.Length == 1 && methods[0].GetParameters().Select(p => p.ParameterType)
                .SequenceEqual(new[] { typeof(IReadOnlyCollection<DnsShareCandidate>), typeof(CancellationToken) }), "The product sharing API accepts a recipient override.");
            Require(typeof(DnsShareClient).GetConstructors().All(c => c.GetParameters().Length == 0), "A public constructor accepts another recipient.");
            Require(typeof(MainForm).Assembly.GetType("NetPilot.DnsShareDialog") is null, "The retired receiver configuration dialog is still distributable.");
            return Task.CompletedTask;
        });
        await Check("one-click empty offer makes no HTTP request even without destination setup", async () =>
        {
            var handler = new Handler((_, _) => throw new InvalidOperationException("Empty offer reached HTTP."));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: "");
            var result = await client.SendToOwnerAsync([]);
            Require(result is { Accepted: 0, Duplicates: 0, Unconfirmed: 0 } && handler.Calls == 0, "An empty list used the network.");
        });
        await Check("one-click missing owner deployment fails before HTTP without asking user for a URL", async () =>
        {
            var handler = new Handler((_, _) => throw new InvalidOperationException("Missing owner receiver reached HTTP."));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: "");
            var error = await Error(() => client.SendToOwnerAsync([Profile()]));
            Require(!error.MayHaveBeenSent && handler.Calls == 0 && !error.Message.Contains("Введите") && !error.Message.Contains("приглаш"), "Missing deployment was misrepresented or asked the sender for credentials.");
        });
        await Check("one-click production destination does not read legacy receiver settings", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "one-click-legacy-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var path = Path.Combine(directory, DnsShareSettingsStore.FileName);
            var legacy = "{\"Version\":1,\"Protected\":\"unreadable-legacy-blob\",\"Endpoint\":\"https://example.invalid/steal\"}";
            File.WriteAllText(path, legacy);
            var handler = new Handler((request, _) =>
            {
                Require(request.RequestUri!.AbsoluteUri == DnsShareDestination.Endpoint, "A configurable destination overrode the owner catalogue.");
                return Task.FromResult(Reply());
            });
            using var client = new DnsShareClient(handler);
            if (string.IsNullOrWhiteSpace(DnsShareDestination.Endpoint))
            {
                var error = await Error(() => client.SendToOwnerAsync([Profile()]));
                Require(!error.MayHaveBeenSent && handler.Calls == 0, "An unconfigured product tried a legacy receiver.");
            }
            else
            {
                var result = await client.SendToOwnerAsync([Profile()]);
                Require(result.Accepted == 1 && handler.Calls == 1, "The fixed deployment was not used.");
            }
            Require(File.ReadAllText(path) == legacy, "An obsolete local receiver file was rewritten or removed.");
        });
        await Check("one-click protocol2 POST contains only public DNS fields and no secret", async () =>
        {
            var handler = new Handler(async (request, token) =>
            {
                Require(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == TestEndpoint, "The upload did not use the fixed receiver.");
                Require(!request.Headers.Contains("Authorization") && !request.Headers.Contains("Cookie") && !request.Headers.Contains("Referer"), "Account credentials were forwarded.");
                var bytes = await request.Content!.ReadAsByteArrayAsync(token);
                Require(bytes.Length <= DnsShareClient.MaxRequestBytes, "The payload exceeded the existing bound.");
                using var document = JsonDocument.Parse(bytes); var envelope = document.RootElement;
                Require(envelope.GetProperty("Protocol").GetInt32() == 2 && envelope.EnumerateObject().Select(p => p.Name).Order()
                    .SequenceEqual(new[] { "Profiles", "Protocol" }), "A URL, invite or observation entered the envelope.");
                var profile = envelope.GetProperty("Profiles")[0];
                Require(profile.EnumerateObject().Select(p => p.Name).Order().SequenceEqual(new[] { "DnsPort", "DohUrl", "Mode", "Servers", "WorkedServices" }), "A private profile field was serialized.");
                Require(profile.GetProperty("Servers")[0].GetString() == "8.8.8.8" && profile.GetProperty("WorkedServices")[0].GetString() == "chatgpt", "The approved public evidence was altered.");
                return Reply();
            });
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(result is { Accepted: 1, Duplicates: 0, Unconfirmed: 0 } && handler.Posts == 1, "The single confirmed upload failed.");
        });
        await Check("one-click repeated confirmed submission does not POST the same DNS again", async () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply()));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            await client.SendToOwnerAsync([Profile()]);
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(handler.Posts == 1 && result is { Accepted: 0, Duplicates: 1, Unconfirmed: 0 }, "A second click reposted a confirmed candidate.");
        });
        await Check("one-click simultaneous calls serialize without duplicating a DNS POST", async () =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var handler = new Handler(async (_, token) => { started.SetResult(); await release.Task.WaitAsync(token); return Reply(); });
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var first = client.SendToOwnerAsync([Profile()]); await started.Task;
            var second = client.SendToOwnerAsync([Profile()]);
            Require(handler.Posts == 1 && !second.IsCompleted, "A duplicate in-flight call started another POST.");
            release.SetResult(); var results = await Task.WhenAll(first, second);
            Require(handler.Posts == 1 && results.Sum(r => r.Accepted) == 1 && results.Sum(r => r.Duplicates) == 1, "Concurrent clicks lost the one-request contract.");
        });
        await Check("one-click timeout is uncertain and the next click cannot repost that DNS", async () =>
        {
            var handler = new Handler(async (_, token) => { await Task.Delay(Timeout.Infinite, token); return Reply(); });
            using var client = new DnsShareClient(handler, TimeSpan.FromMilliseconds(60), TestEndpoint);
            var error = await Error(() => client.SendToOwnerAsync([Profile()]));
            var second = await client.SendToOwnerAsync([Profile()]);
            Require(error.MayHaveBeenSent && handler.Posts == 1 && second is { Accepted: 0, Duplicates: 0, Unconfirmed: 1 }, "An uncertain request was replayed or reported as accepted.");
        });
        await Check("one-click new DNS can be sent while an earlier uncertain DNS stays suppressed", async () =>
        {
            var handler = new Handler((request, _) => request.Content!.ReadAsStringAsync().ContinueWith(t =>
            {
                if (t.Result.Contains("8.8.8.8")) throw new HttpRequestException("lost reply");
                return Reply();
            }, TaskScheduler.Default));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            await Error(() => client.SendToOwnerAsync([Profile()]));
            var result = await client.SendToOwnerAsync([Profile(), Profile("9.9.9.9")]);
            Require(handler.Posts == 2 && result is { Accepted: 1, Duplicates: 0, Unconfirmed: 1 }, "Uncertainty either blocked a new DNS or reposted the old DNS.");
        });
        await Check("one-click confirmed duplicates are counted separately from additions", async () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply(1, 1)));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var result = await client.SendToOwnerAsync([Profile(), Profile("9.9.9.9")]);
            Require(result is { Accepted: 1, Duplicates: 1, Unconfirmed: 0 } && handler.Posts == 1, "A known catalogue entry was presented as a new addition.");
        });
        await Check("one-click rejects protocol1 confirmation and suppresses an ambiguous repost", async () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply(protocol: 1)));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var error = await Error(() => client.SendToOwnerAsync([Profile()]));
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(error.MayHaveBeenSent && result.Unconfirmed == 1 && handler.Posts == 1, "An old incompatible receiver was trusted or reposted to.");
        });
        await Check("one-click Google response redirect is GET and never repeats the protocol2 POST", async () =>
        {
            var handler = new Handler((request, _) =>
            {
                if (request.Method == HttpMethod.Post)
                {
                    var response = new HttpResponseMessage(HttpStatusCode.Found);
                    response.Headers.Location = new Uri("https://script.googleusercontent.com/macros/echo?user_content_key=test&lib=test");
                    return Task.FromResult(response);
                }
                Require(request.Method == HttpMethod.Get && request.Content is null && request.RequestUri!.Host == "script.googleusercontent.com", "A response redirect received the upload body.");
                return Task.FromResult(Reply());
            });
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(result.Accepted == 1 && handler.Calls == 2 && handler.Posts == 1, "Protocol2 response forwarding changed POST semantics.");
        });
        await Check("one-click unsafe redirects remain bounded uncertain results with no repost", async () =>
        {
            var handler = new Handler((_, _) =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.TemporaryRedirect);
                response.Headers.Location = new Uri("https://example.invalid/collect");
                return Task.FromResult(response);
            });
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var error = await Error(() => client.SendToOwnerAsync([Profile()]));
            Require(error.MayHaveBeenSent && handler.Calls == 1 && handler.Posts == 1, "An unsafe redirect was followed.");
        });
        await Check("one-click explicit receiver refusal permits a later deliberate retry", async () =>
        {
            var denied = true;
            var handler = new Handler((_, _) => Task.FromResult(denied ? new HttpResponseMessage(HttpStatusCode.OK)
            { Content = new StringContent("{\"Protocol\":2,\"Ok\":false,\"Accepted\":0,\"Duplicates\":0,\"Error\":\"busy\"}") } : Reply()));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            var error = await Error(() => client.SendToOwnerAsync([Profile()])); denied = false;
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(!error.MayHaveBeenSent && result.Accepted == 1 && handler.Posts == 2, "A confirmed non-write was wrongly treated as a possibly completed upload.");
        });
        await Check("one-click pre-cancelled upload makes no HTTP request and does not suppress a later click", async () =>
        {
            var handler = new Handler((_, _) => Task.FromResult(Reply()));
            using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            try { await client.SendToOwnerAsync([Profile()], cancellation.Token); throw new InvalidOperationException("A pre-cancelled call completed."); }
            catch (OperationCanceledException) { }
            Require(handler.Calls == 0, "Cancellation before upload reached HTTP.");
            var result = await client.SendToOwnerAsync([Profile()]);
            Require(result.Accepted == 1 && handler.Posts == 1, "An unsent DNS was wrongly suppressed.");
        });
        await Check("one-click baseline private and duplicate candidates never reach HTTP", async () =>
        {
            foreach (var profiles in new[]
            {
                new[] { Profile("127.0.0.1") }, new[] { Profile("10.0.0.1") }, new[] { Profile(), Profile() },
                new[] { new DnsShareCandidate { Mode = DnsMode.Udp, Servers = ["111.88.96.54", "111.88.96.55"], WorkedServices = [MonitoringTargets.ChatGpt] } }
            })
            {
                var handler = new Handler((_, _) => Task.FromResult(Reply()));
                using var client = new DnsShareClient(handler, ownerEndpointForTests: TestEndpoint);
                try { await client.SendToOwnerAsync(profiles); throw new InvalidOperationException("An ineligible DNS was submitted."); }
                catch (ArgumentException) { Require(handler.Calls == 0, "An ineligible DNS reached HTTP."); }
            }
        });
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public int Posts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Calls++; if (request.Method == HttpMethod.Post) Posts++; return send(request, token); }
    }
}
