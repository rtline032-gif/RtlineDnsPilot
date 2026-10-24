using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace NetPilot.Tests;

public static class ApplicationUpdateClientTests
{
    private const string Root = "https://github.com/rtline032-gif/RtlineDnsPilot";
    private const string Api = "https://api.github.com/repos/rtline032-gif/RtlineDnsPilot/releases?per_page=20";
    private static readonly string[] Files = ["RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json", "profiles.json", "README.txt", "CHANNELS.txt", "LICENSE", "PRIVACY.md", "CODE_SIGNING.md"];

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        async Task WithRoot(Func<string, Task> test)
        {
            string directory = Path.Combine(Path.GetTempPath(), "RtlineDnsPilot-update-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); }
            finally
            {
                string full = Path.GetFullPath(directory);
                string temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("RtlineDnsPilot-update-test-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the generated update test directory.");
                Directory.Delete(full, true);
            }
        }

        await Check("update selection compares numeric versions and ignores drafts and older releases", async () =>
        {
            var handler = new Handler(_ => Json(new[] { Release("0.1.9"), Release("0.1.11"), Release("0.1.12", draft: true), Release("0.1.10") }));
            using var client = new ApplicationUpdateClient(handler);
            var release = await client.CheckAsync(new Version(0, 1, 10));
            Require(release is { Version: "0.1.11", Tag: "v0.1.11", IsPrerelease: true }, "Newest published numeric release was not selected.");
            Require(handler.Requests.SequenceEqual(new[] { Api }), "Update check contacted a destination other than the single official repository API.");
            Require(handler.Methods.All(m => m == HttpMethod.Get) && !handler.SawAuthorization, "Update check sent an authenticated request or mutation.");
        });
        await Check("up-to-date and newer installed versions never offer a downgrade", async () =>
        {
            using var client = new ApplicationUpdateClient(new Handler(_ => Json(new[] { Release("0.1.8"), Release("0.1.10") })));
            Require(await client.CheckAsync(new Version(0, 1, 10)) is null, "Equal version was offered as an update.");
            Require(await client.CheckAsync(new Version(0, 1, 11)) is null, "A downgrade was offered.");
        });
        await Check("update selection rejects a missing GitHub asset digest", async () =>
        {
            using var client = new ApplicationUpdateClient(new Handler(_ => Json(new[] { Release("0.1.11", digest: null) })));
            await Reject(() => client.CheckAsync(new Version(0, 1, 10)));
        });
        await Check("update selection rejects foreign asset URLs and duplicate Windows assets", async () =>
        {
            using var foreign = new ApplicationUpdateClient(new Handler(_ => Json(new[] { Release("0.1.11", url: "https://example.invalid/program.zip") })));
            await Reject(() => foreign.CheckAsync(new Version(0, 1, 10)));
            using var duplicate = new ApplicationUpdateClient(new Handler(_ => Json(new[] { Release("0.1.11", duplicate: true) })));
            await Reject(() => duplicate.CheckAsync(new Version(0, 1, 10)));
        });
        await Check("metadata redirects and oversized metadata are rejected", async () =>
        {
            var redirect = new Handler(_ => Redirect("https://example.invalid/releases"));
            using (var client = new ApplicationUpdateClient(redirect)) await Reject(() => client.CheckAsync(new Version(0, 1, 10)));
            Require(redirect.Requests.Count == 1, "A metadata redirect escaped the official API.");
            using var oversized = new ApplicationUpdateClient(new Handler(_ => Bytes(new byte[1024 * 1024 + 1])));
            await Reject(() => oversized.CheckAsync(new Version(0, 1, 10)));
        });
        await Check("GitHub rate limiting and malformed metadata preserve the running version", async () =>
        {
            using (var limited = new ApplicationUpdateClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests))))
                await Reject(() => limited.CheckAsync(new Version(0, 1, 10)));
            using var malformed = new ApplicationUpdateClient(new Handler(_ => Bytes(Encoding.UTF8.GetBytes("{broken"))));
            await Reject(() => malformed.CheckAsync(new Version(0, 1, 10)));
        });
        await Check("pre-cancelled update checking sends no network request", async () =>
        {
            var handler = new Handler(_ => throw new Exception("Cancelled check sent HTTP."));
            using var client = new ApplicationUpdateClient(handler);
            using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
            await Reject(() => client.CheckAsync(new Version(0, 1, 10), cancellation.Token));
            Require(handler.Requests.Count == 0, "Cancelled check contacted GitHub.");
        });

        await Check("verified update stages all release files without replacing user data", () => WithRoot(async directory =>
        {
            var archive = Archive(); var release = Offer(archive);
            string userConfig = Path.Combine(directory, "ui-config.json"); File.WriteAllText(userConfig, "preserve channels and history");
            int validations = 0;
            using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (path, version) =>
            {
                Require(version == "0.1.11" && File.Exists(Path.Combine(path, "RtlineDnsPilot.dll")), "Preflight did not receive the staged package and expected new version.");
                validations++;
            });
            var staged = await client.DownloadAsync(release, directory);
            Require(validations == 1 && staged.Release == release && staged.FileHashes.Count == 11, "Successful update did not pass package validation or retain all file hashes.");
            Require(Path.GetFullPath(staged.DirectoryPath).StartsWith(Path.GetFullPath(directory) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase), "Staged files escaped the update root.");
            Require(File.ReadAllText(userConfig) == "preserve channels and history", "User settings were replaced while staging.");
            foreach (var pair in staged.FileHashes)
                Require(Hash(File.ReadAllBytes(Path.Combine(staged.DirectoryPath, pair.Key))) == pair.Value, "Returned file receipt does not match staged bytes.");
        }));
        await Check("each update download has its own fresh stage", () => WithRoot(async directory =>
        {
            var archive = Archive(); using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => { });
            var first = await client.DownloadAsync(Offer(archive), directory); var second = await client.DownloadAsync(Offer(archive), directory);
            Require(first.DirectoryPath != second.DirectoryPath && File.Exists(Path.Combine(first.DirectoryPath, "RtlineDnsPilot.exe")), "A later stage overwrote the previous downloaded version.");
        }));
        await Check("archive digest mismatch never reaches extraction or package validation", () => WithRoot(async directory =>
        {
            var archive = Archive(); int validations = 0;
            using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => validations++);
            await Reject(() => client.DownloadAsync(Offer(archive) with { Sha256 = new string('a', 64) }, directory));
            Require(validations == 0 && !Directory.EnumerateFiles(directory, "RtlineDnsPilot.exe", SearchOption.AllDirectories).Any(), "Unverified archive reached launchable staging.");
        }));
        await Check("internal checksum mismatch never reaches executable preflight", () => WithRoot(async directory =>
        {
            var archive = Archive(tamper: true); int validations = 0;
            using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => validations++);
            await Reject(() => client.DownloadAsync(Offer(archive), directory));
            Require(validations == 0, "A package with a mismatched inner manifest reached preflight.");
        }));
        foreach (string path in new[] { "../escaped.txt", "RtlineDnsPilot/../../escaped.txt", "RtlineDnsPilot/README.txt:stream", "RtlineDnsPilot/sub/README.txt", "RtlineDnsPilot\\README.txt", "/absolute.txt", "RtlineDnsPilot/ui-config.json" })
        {
            string rejectedPath = path;
            await Check("ZIP rejects unsafe or non-release entry " + rejectedPath, () => WithRoot(async directory =>
            {
                var archive = Archive(extraName: rejectedPath);
                using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => throw new Exception("Unsafe ZIP reached preflight."));
                await Reject(() => client.DownloadAsync(Offer(archive), directory));
                Require(!File.Exists(Path.Combine(directory, "escaped.txt")), "ZIP traversal wrote outside the package folder.");
            }));
        }
        await Check("ZIP case collisions and symlink entries are rejected", () => WithRoot(async directory =>
        {
            foreach (var archive in new[] { Archive(extraName: "RtlineDnsPilot/readme.txt"), Archive(symlink: true) })
            {
                using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => throw new Exception("Unsafe ZIP reached preflight."));
                await Reject(() => client.DownloadAsync(Offer(archive), directory));
            }
        }));
        await Check("uncompressed archive size is bounded even for a small ZIP", () => WithRoot(async directory =>
        {
            var archive = Archive(large: true);
            using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => throw new Exception("Inflated archive reached preflight."));
            await Reject(() => client.DownloadAsync(Offer(archive), directory));
        }));
        await Check("claimed download size and actual response bytes must agree", () => WithRoot(async directory =>
        {
            var archive = Archive(); using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)), (_, _) => throw new Exception("Wrong-size archive reached preflight."));
            await Reject(() => client.DownloadAsync(Offer(archive) with { Size = archive.Length + 1 }, directory));
            await Reject(() => client.DownloadAsync(Offer(archive) with { Size = 32L * 1024 * 1024 + 1 }, directory));
        }));
        await Check("GitHub asset redirects accept only the bounded HTTPS release CDN", () => WithRoot(async directory =>
        {
            var archive = Archive(); var release = Offer(archive);
            var handler = new Handler(request => request.RequestUri!.Host == "github.com" ? Redirect("https://release-assets.githubusercontent.com/release-test/archive.zip?token=public-download") : Bytes(archive));
            using var client = new ApplicationUpdateClient(handler, (_, _) => { });
            var staged = await client.DownloadAsync(release, directory);
            Require(staged.FileHashes.Count == 11 && handler.Requests.Count == 2 && handler.Methods.All(m => m == HttpMethod.Get), "Release CDN redirect did not preserve a single GET download flow.");
        }));
        await Check("unsafe download redirects and redirect loops never reach external sites", () => WithRoot(async directory =>
        {
            foreach (string url in new[] { "http://release-assets.githubusercontent.com/archive.zip", "https://example.invalid/archive.zip", "https://user:secret@release-assets.githubusercontent.com/archive.zip", "https://release-assets.githubusercontent.com:444/archive.zip" })
            {
                var archive = Archive(); var handler = new Handler(_ => Redirect(url));
                using var client = new ApplicationUpdateClient(handler, (_, _) => throw new Exception("Unsafe redirect reached preflight."));
                await Reject(() => client.DownloadAsync(Offer(archive), directory));
                Require(handler.Requests.Count == 1, "A denied redirect was sent.");
            }
            var loop = new Handler(_ => Redirect("https://release-assets.githubusercontent.com/loop.zip"));
            using var looping = new ApplicationUpdateClient(loop, (_, _) => throw new Exception("Redirect loop reached preflight."));
            await Reject(() => looping.DownloadAsync(Offer(Archive()), directory));
            Require(loop.Requests.Count <= 4, "Redirect loop was unbounded.");
        }));
        await Check("default download preflight rejects a checksum-valid non-executable package", () => WithRoot(async directory =>
        {
            var archive = Archive(); using var client = new ApplicationUpdateClient(new Handler(_ => Bytes(archive)));
            await Reject(() => client.DownloadAsync(Offer(archive), directory));
        }));
    }

    private static object Release(string version, bool draft = false, string? digest = "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", string? url = null, bool duplicate = false)
    {
        string tag = "v" + version; string name = "RtlineDnsPilot-" + version + "-win-x64.zip";
        var asset = new { name, browser_download_url = url ?? Root + "/releases/download/" + tag + "/" + name, size = 1234, digest, state = "uploaded" };
        return new { tag_name = tag, html_url = Root + "/releases/tag/" + tag, draft, prerelease = true, published_at = draft ? null : "2026-10-24T08:00:00Z", assets = duplicate ? new[] { asset, asset } : new[] { asset } };
    }
    private static ApplicationRelease Offer(byte[] archive) => new("0.1.11", "v0.1.11", Root + "/releases/tag/v0.1.11", Root + "/releases/download/v0.1.11/RtlineDnsPilot-0.1.11-win-x64.zip", archive.Length, Hash(archive), true);
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static HttpResponseMessage Json(object value) => Bytes(JsonSerializer.SerializeToUtf8Bytes(value));
    private static HttpResponseMessage Bytes(byte[] value) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(value) };
    private static HttpResponseMessage Redirect(string uri) { var response = new HttpResponseMessage(HttpStatusCode.Found); response.Headers.Location = new Uri(uri); return response; }
    private static async Task Reject(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception ex) when (ex is InvalidDataException or IOException or HttpRequestException or OperationCanceledException or TimeoutException or ArgumentException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe or failed update was accepted.");
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private static byte[] Archive(bool tamper = false, string? extraName = null, bool symlink = false, bool large = false)
    {
        var contents = Files.ToDictionary(name => name, name => Encoding.UTF8.GetBytes("fixture bytes for " + name), StringComparer.Ordinal);
        string sums = string.Join('\n', contents.Select(pair => Hash(pair.Value) + "  " + pair.Key)) + "\n";
        if (tamper) contents["RtlineDnsPilot.dll"] = Encoding.UTF8.GetBytes("different DLL bytes");
        if (large) contents["RtlineDnsPilot.dll"] = new byte[64 * 1024 * 1024 + 1];
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            foreach (var pair in contents)
            {
                var entry = zip.CreateEntry("RtlineDnsPilot/" + pair.Key, CompressionLevel.Optimal);
                if (symlink && pair.Key == "README.txt") entry.ExternalAttributes = unchecked((int)(0xA1FFu << 16));
                using var stream = entry.Open(); stream.Write(pair.Value);
            }
            using (var stream = zip.CreateEntry("RtlineDnsPilot/SHA256SUMS.txt").Open()) stream.Write(Encoding.UTF8.GetBytes(sums));
            if (extraName is not null) using (var stream = zip.CreateEntry(extraName).Open()) stream.Write(Encoding.UTF8.GetBytes("unexpected"));
        }
        return bytes.ToArray();
    }
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];
        public List<HttpMethod> Methods { get; } = [];
        public bool SawAuthorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request.RequestUri!.AbsoluteUri); Methods.Add(request.Method); SawAuthorization |= request.Headers.Authorization is not null;
            return Task.FromResult(response(request));
        }
    }
}
