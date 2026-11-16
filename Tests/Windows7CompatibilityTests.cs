using System.Buffers.Binary;
using System.Net;
using System.IO.Pipes;
using System.Reflection;
using System.Security.Authentication;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace NetPilot.Tests;

public static class Windows7CompatibilityTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.GetBaseException().Message); }
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
#if LEGACY_WINDOWS
        const string platform = "win7-x64";
        const string otherPlatform = "win-x64";
        const int runtimeMajor = 6;
#else
        const string platform = "win-x64";
        const string otherPlatform = "win7-x64";
        const int runtimeMajor = 10;
#endif
        await Check("update selection accepts only this Windows runtime flavor", async () =>
        {
            using var client = new ApplicationUpdateClient(new ManifestHandler(platform));
            var release = await client.CheckAsync(new Version(0, 1, 12));
            Require(release?.DownloadUrl.EndsWith("-" + platform + ".zip", StringComparison.Ordinal) == true,
                "The compatible package was not selected.");
        });
        await Check("update selection never offers the other Windows runtime flavor", async () =>
        {
            using var client = new ApplicationUpdateClient(new ManifestHandler(otherPlatform));
            Require(await client.CheckAsync(new Version(0, 1, 12)) is null, "An incompatible runtime was offered.");
        });
        await Check("installer accepts this runtime family and rejects the other family before mutation", () =>
        {
            var validate = typeof(ServiceInstaller).GetMethod("ValidateRuntimeConfig", BindingFlags.Static | BindingFlags.NonPublic)!;
            byte[] Config(int major) => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                runtimeOptions = new { tfm = "net" + major + ".0", frameworks = new[]
                {
                    new { name = "Microsoft.NETCore.App", version = major + ".0.36" },
                    new { name = "Microsoft.WindowsDesktop.App", version = major + ".0.36" }
                } }
            }));
            validate.Invoke(null, new object[] { Config(runtimeMajor) });
            try { validate.Invoke(null, new object[] { Config(runtimeMajor == 6 ? 10 : 6) }); }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidDataException) { return Task.CompletedTask; }
            throw new Exception("A package from the other runtime family was accepted.");
        });
        await Check("fragmented RPC frames preserve the complete UTF8 body", async () =>
        {
            var message = Encoding.UTF8.GetBytes("{\"Ok\":true,\"Error\":\"проверка\"}");
            var bytes = new byte[message.Length + 4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, message.Length); message.CopyTo(bytes, 4);
            using var stream = new FragmentedStream(bytes);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            Require(await PipeMessages.ReadAsync(stream, 1024, deadline.Token) == Encoding.UTF8.GetString(message), "A partial read lost RPC bytes.");
        });
        await Check("truncated RPC header or body is never accepted", async () =>
        {
            foreach (var bytes in new[] { new byte[] { 3, 0 }, new byte[] { 5, 0, 0, 0, 123, 125 } })
            {
                using var stream = new FragmentedStream(bytes);
                try { await PipeMessages.ReadAsync(stream, 1024, CancellationToken.None); }
                catch (EndOfStreamException) { continue; }
                throw new Exception("A truncated control frame was accepted.");
            }
        });
        await Check("Windows 7 HTTPS policy enables TLS12 with the standard certificate validator", () =>
        {
            using var normal = RuntimeCompatibility.ConfigureHttpsHandler(new HttpClientHandler(), windows7: false);
            using var legacy = RuntimeCompatibility.ConfigureHttpsHandler(new HttpClientHandler(), windows7: true);
            using var pinned = new SocketsHttpHandler(); RuntimeCompatibility.ConfigurePinnedHttpsHandler(pinned, windows7: true);
            Require(normal.SslProtocols == SslProtocols.None && legacy.SslProtocols == SslProtocols.Tls12
                && pinned.SslOptions.EnabledSslProtocols == SslProtocols.Tls12, "TLS defaults or the legacy TLS12 policy changed.");
            Require(legacy.ServerCertificateCustomValidationCallback is null && pinned.SslOptions.RemoteCertificateValidationCallback is null,
                "A custom callback bypassed the platform certificate validator.");
            return Task.CompletedTask;
        });
        await Check("Windows 7 transport detection does not downgrade newer Windows", () =>
        {
            Require(RuntimeCompatibility.RequiresLegacyTls(new Version(6, 1, 7601))
                && !RuntimeCompatibility.RequiresLegacyTls(new Version(6, 2, 9200))
                && !RuntimeCompatibility.RequiresLegacyTls(new Version(10, 0, 19045)), "The legacy transport was enabled on the wrong operating system.");
            return Task.CompletedTask;
        });
        await Check("the control pipe refuses an already occupied name", async () =>
        {
            string directory = Path.Combine(Path.GetTempPath(), "RtlineDnsPilot-pipe-flavor-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try
            {
                await using var engine = new MonitorEngine(AppConfig.Defaults(), directory, serviceMode: false);
                string pipeName = "NetPilot.Test." + Guid.NewGuid().ToString("N");
                string sid = WindowsIdentity.GetCurrent().User!.Value;
                using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                var first = new ServiceRpc(engine, sid, pipeName).RunAsync(lifetime.Token);
                try
                {
                    try { await new ServiceRpc(engine, sid, pipeName).RunAsync(lifetime.Token); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }
                    throw new Exception("A second service claimed the occupied control-pipe name.");
                }
                finally { lifetime.Cancel(); try { await first; } catch (OperationCanceledException) { } }
            }
            finally { Directory.Delete(directory, true); }
        });
    }

    private sealed class ManifestHandler(string platform) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            const string root = "https://github.com/rtline032-gif/RtlineDnsPilot";
            var name = "RtlineDnsPilot-99.0.0-" + platform + ".zip";
            var manifest = new[] { new { tag_name = "v99.0.0", html_url = root + "/releases/tag/v99.0.0", draft = false,
                prerelease = true, published_at = "2026-11-10T00:00:00Z", assets = new[] { new { name,
                    browser_download_url = root + "/releases/download/v99.0.0/" + name, size = 1024,
                    digest = "sha256:" + new string('a', 64), state = "uploaded" } } } };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(manifest)) });
        }
    }

    private sealed class FragmentedStream(byte[] bytes) : MemoryStream(bytes, false)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, 1));
        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, 1)]);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
            => base.ReadAsync(buffer[..Math.Min(buffer.Length, 1)], token);
    }
}
