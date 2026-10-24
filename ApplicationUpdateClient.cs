using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace NetPilot;

internal sealed record ApplicationRelease(string Version, string Tag, string PageUrl, string DownloadUrl, long Size, string Sha256, bool IsPrerelease);
internal sealed record StagedApplicationUpdate(ApplicationRelease Release, string DirectoryPath, Dictionary<string, string> FileHashes);
internal sealed record ApplicationUpdateProgress(string Message, long Completed = 0, long? Total = null);

internal sealed class ApplicationUpdateClient : IDisposable
{
    private const string Repository = "https://github.com/rtline032-gif/RtlineDnsPilot";
    private const string ReleasesApi = "https://api.github.com/repos/rtline032-gif/RtlineDnsPilot/releases?per_page=20";
    private const int MetadataLimit = 1024 * 1024;
    private const long ArchiveLimit = 32L * 1024 * 1024;
    private const long InflatedLimit = 64L * 1024 * 1024;
    private const int RedirectLimit = 3;
    private readonly HttpClient client;
    private readonly Action<string, string> validatePackage;

    internal static readonly IReadOnlySet<string> AllowedPackageFiles = new HashSet<string>(StringComparer.Ordinal)
    {
        "RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json",
        "profiles.json", "README.txt", "CHANNELS.txt", "LICENSE", "PRIVACY.md", "CODE_SIGNING.md", "SHA256SUMS.txt"
    };

    public ApplicationUpdateClient() : this(new HttpClientHandler
    { AllowAutoRedirect = false, UseCookies = false, UseDefaultCredentials = false }) { }

    internal ApplicationUpdateClient(HttpMessageHandler handler, Action<string, string>? validatePackage = null)
    {
        client = new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("RtlineDnsPilot/" + AppVersion.Current);
        this.validatePackage = validatePackage ?? ((directory, version) => ServiceInstaller.ReadPackage(directory, version));
    }

    public async Task<ApplicationRelease?> CheckAsync(Version current, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(current);
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            using var response = await SendAsync(new Uri(ReleasesApi), metadata: true, deadline.Token);
            if (IsRedirect(response.StatusCode)) throw new InvalidDataException("GitHub перенаправил список обновлений на другой адрес; проверка остановлена.");
            EnsureSuccess(response, "список выпусков");
            var bytes = await ReadBoundedAsync(response.Content, MetadataLimit, deadline.Token);
            try
            {
                using var json = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 24 });
                return SelectRelease(json.RootElement, current);
            }
            catch (JsonException ex) { throw new InvalidDataException("GitHub вернул повреждённый список обновлений.", ex); }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("GitHub не ответил на проверку обновлений за 15 секунд. Текущая программа сохранена."); }
    }

    public async Task<StagedApplicationUpdate> DownloadAsync(ApplicationRelease release, string updatesRoot,
        IProgress<ApplicationUpdateProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOffer(release);
        if (string.IsNullOrWhiteSpace(updatesRoot)) throw new ArgumentException("Не задан каталог обновления.", nameof(updatesRoot));
        string root = Path.GetFullPath(updatesRoot);
        EnsureNoReparseAncestors(root);
        Directory.CreateDirectory(root);
        EnsureNoReparseAncestors(root);
        string stage = Path.Combine(root, "staging-" + Guid.NewGuid().ToString("N"));
        string directory = Path.Combine(stage, "RtlineDnsPilot");
        string archivePath = Path.Combine(stage, "release.zip");
        Directory.CreateDirectory(stage);
        var created = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromMinutes(3));
        try
        {
            progress?.Report(new("Загрузка RtlineDnsPilot " + release.Version, 0, release.Size));
            await DownloadArchiveAsync(release, archivePath, progress, deadline.Token);
            progress?.Report(new("Проверка архива и контрольных сумм"));
            var hashes = await ExtractAsync(archivePath, directory, created, deadline.Token);
            deadline.Token.ThrowIfCancellationRequested();
            progress?.Report(new("Проверка исполняемых файлов версии " + release.Version));
            validatePackage(directory, release.Version);
            deadline.Token.ThrowIfCancellationRequested();
            File.Delete(archivePath);
            progress?.Report(new("Обновление загружено и проверено"));
            return new(release, directory, hashes);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Cleanup(stage, directory, archivePath, created);
            throw new TimeoutException("Загрузка обновления не завершилась за 3 минуты. Текущая программа сохранена.");
        }
        catch
        {
            Cleanup(stage, directory, archivePath, created);
            throw;
        }
    }

    private static ApplicationRelease? SelectRelease(JsonElement releases, Version current)
    {
        if (releases.ValueKind != JsonValueKind.Array || releases.GetArrayLength() > 20)
            throw new InvalidDataException("Недопустимый список выпусков GitHub.");
        var candidates = new List<(Version Version, string Text, string Tag, JsonElement Release)>();
        foreach (var release in releases.EnumerateArray())
        {
            if (release.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Недопустимая запись выпуска.");
            if (ReadBoolean(release, "draft")) continue;
            if (!release.TryGetProperty("published_at", out var published) || published.ValueKind != JsonValueKind.String
                || !DateTimeOffset.TryParse(published.GetString(), out _)) continue;
            string tag = ReadString(release, "tag_name", 64);
            if (!TryReleaseVersion(tag, out var version, out var text)) continue;
            if (Normalize(version!) <= Normalize(current)) continue;
            candidates.Add((version!, text!, tag, release));
        }
        foreach (var candidate in candidates.OrderByDescending(c => Normalize(c.Version)))
        {
            var release = candidate.Release;
            if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array || assets.GetArrayLength() > 100)
                throw new InvalidDataException("Не удалось проверить файлы выпуска " + candidate.Text + ".");
            string plainName = "RtlineDnsPilot-" + candidate.Text + "-win-x64.zip";
            string publicName = "RtlineDnsPilot-" + candidate.Text + "-public-win-x64.zip";
            var matching = assets.EnumerateArray().Where(a => a.ValueKind == JsonValueKind.Object && a.TryGetProperty("name", out var name)
                && name.ValueKind == JsonValueKind.String && (name.GetString() == plainName || name.GetString() == publicName)).ToArray();
            if (matching.Length == 0) continue;
            if (matching.Length != 1) throw new InvalidDataException("В выпуске несколько архивов Windows x64; автоматический выбор заблокирован.");
            var asset = matching[0];
            if (ReadString(asset, "state", 32) != "uploaded") throw new InvalidDataException("Архив выпуска ещё не загружен полностью.");
            string digest = ReadString(asset, "digest", 80);
            if (!digest.StartsWith("sha256:", StringComparison.Ordinal) || !IsSha256(digest[7..]))
                throw new InvalidDataException("GitHub не предоставил SHA-256 архива обновления.");
            if (!asset.TryGetProperty("size", out var size) || !size.TryGetInt64(out long length)) throw new InvalidDataException("Не задан размер обновления.");
            var result = new ApplicationRelease(candidate.Text, candidate.Tag, ReadString(release, "html_url", 512),
                ReadString(asset, "browser_download_url", 2048), length, digest[7..].ToLowerInvariant(), ReadBoolean(release, "prerelease"));
            ValidateOffer(result);
            return result;
        }
        return null;
    }

    private async Task DownloadArchiveAsync(ApplicationRelease release, string destination, IProgress<ApplicationUpdateProgress>? progress, CancellationToken token)
    {
        Uri uri = new(release.DownloadUrl);
        for (int hop = 0; hop <= RedirectLimit; hop++)
        {
            using var response = await SendAsync(uri, metadata: false, token);
            if (IsRedirect(response.StatusCode))
            {
                if (hop == RedirectLimit || response.Headers.Location is null) throw new InvalidDataException("Слишком много перенаправлений при загрузке обновления.");
                var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
                if (!IsDownloadRedirect(next, release.DownloadUrl)) throw new InvalidDataException("Загрузка перенаправлена за пределы разрешённых серверов GitHub.");
                uri = next; continue;
            }
            EnsureSuccess(response, "архив обновления");
            if (response.StatusCode != HttpStatusCode.OK) throw new InvalidDataException("GitHub вернул неполный архив обновления.");
            if (response.Content.Headers.ContentLength is long declared && declared != release.Size)
                throw new InvalidDataException("Размер ответа не совпадает с опубликованным архивом.");
            using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using var source = await response.Content.ReadAsStreamAsync(token);
            await using var target = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true);
            var buffer = new byte[65536]; long length = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, token); if (count == 0) break;
                length = checked(length + count);
                if (length > release.Size || length > ArchiveLimit) throw new InvalidDataException("Архив обновления превышает разрешённый размер.");
                await target.WriteAsync(buffer.AsMemory(0, count), token); digest.AppendData(buffer, 0, count);
                progress?.Report(new("Загрузка RtlineDnsPilot " + release.Version, length, release.Size));
            }
            await target.FlushAsync(token);
            if (length != release.Size || !Convert.ToHexStringLower(digest.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма или размер скачанного архива не совпали с GitHub. Обновление не применено.");
            return;
        }
    }

    private static async Task<Dictionary<string, string>> ExtractAsync(string archivePath, string directory, List<string> created, CancellationToken token)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        if (archive.Entries.Count is < 11 or > 12) throw new InvalidDataException("Неожиданное число файлов в архиве обновления.");
        var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
        long inflated = 0; bool rootSeen = false;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName == "RtlineDnsPilot/" && entry.Length == 0)
            {
                if (rootSeen || IsLink(entry)) throw new InvalidDataException("Недопустимый каталог архива.");
                rootSeen = true; continue;
            }
            const string prefix = "RtlineDnsPilot/";
            if (!entry.FullName.StartsWith(prefix, StringComparison.Ordinal)) throw new InvalidDataException("Архив содержит путь вне папки RtlineDnsPilot.");
            string name = entry.FullName[prefix.Length..];
            if (!AllowedPackageFiles.Contains(name) || name.IndexOfAny(['/', '\\', ':']) >= 0 || IsLink(entry) || !entries.TryAdd(name, entry))
                throw new InvalidDataException("Архив содержит недопустимый, повторный или ссылочный файл: " + name);
            if (entry.Length < 0 || entry.Length > ArchiveLimit || (inflated = checked(inflated + entry.Length)) > InflatedLimit)
                throw new InvalidDataException("Распакованный архив обновления слишком велик.");
        }
        if (entries.Count != AllowedPackageFiles.Count || AllowedPackageFiles.Any(name => !entries.ContainsKey(name)))
            throw new InvalidDataException("В архиве отсутствуют обязательные файлы выпуска.");
        Directory.CreateDirectory(directory); EnsureNoReparseAncestors(directory);
        var hashes = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        long written = 0;
        foreach (var pair in entries)
        {
            token.ThrowIfCancellationRequested();
            string path = Path.Combine(directory, pair.Key);
            EnsureNoReparseAncestors(directory);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                created.Add(path);
                await using var source = pair.Value.Open();
                var buffer = new byte[65536]; long fileWritten = 0;
                while (true)
                {
                    int count = await source.ReadAsync(buffer, token); if (count == 0) break;
                    fileWritten = checked(fileWritten + count); written = checked(written + count);
                    if (fileWritten > pair.Value.Length || fileWritten > ArchiveLimit || written > InflatedLimit)
                        throw new InvalidDataException("Распаковка превысила разрешённый размер.");
                    await target.WriteAsync(buffer.AsMemory(0, count), token); hash.AppendData(buffer, 0, count);
                }
                if (fileWritten != pair.Value.Length) throw new InvalidDataException("Архив содержит неполный файл.");
            }
            hashes.Add(pair.Key, Convert.ToHexStringLower(hash.GetHashAndReset()));
        }
        ValidateManifest(File.ReadAllText(Path.Combine(directory, "SHA256SUMS.txt")), hashes);
        return hashes;
    }

    private static void ValidateManifest(string text, Dictionary<string, string> hashes)
    {
        if (text.Length > 8192) throw new InvalidDataException("Список контрольных сумм слишком велик.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string raw in text.Split('\n'))
        {
            string line = raw.TrimEnd('\r'); if (line.Length == 0) continue;
            if (line.Length < 67 || line[64..66] != "  ") throw new InvalidDataException("Повреждён список контрольных сумм.");
            string hash = line[..64], name = line[66..];
            if (!IsSha256(hash) || name == "SHA256SUMS.txt" || !AllowedPackageFiles.Contains(name) || !seen.Add(name)
                || !hashes.TryGetValue(name, out var actual) || !actual.Equals(hash, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Не совпала внутренняя контрольная сумма файла " + name + ".");
        }
        if (seen.Count != AllowedPackageFiles.Count - 1) throw new InvalidDataException("Внутренние контрольные суммы покрывают не весь выпуск.");
    }

    private async Task<HttpResponseMessage> SendAsync(Uri uri, bool metadata, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(metadata ? "application/vnd.github+json" : "application/octet-stream"));
        if (metadata) request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        return await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token);
    }
    private static async Task<byte[]> ReadBoundedAsync(HttpContent content, int limit, CancellationToken token)
    {
        if (content.Headers.ContentLength > limit) throw new InvalidDataException("Ответ GitHub превышает разрешённый размер.");
        await using var source = await content.ReadAsStreamAsync(token); using var output = new MemoryStream();
        var buffer = new byte[16384]; int count;
        while ((count = await source.ReadAsync(buffer, token)) != 0)
        {
            if (output.Length + count > limit) throw new InvalidDataException("Ответ GitHub превышает разрешённый размер.");
            output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static void ValidateOffer(ApplicationRelease release)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!TryReleaseVersion(release.Tag, out _, out var version) || version != release.Version || !IsSha256(release.Sha256)
            || release.Size < 1 || release.Size > ArchiveLimit)
            throw new InvalidDataException("Недопустимая версия, размер или SHA-256 обновления.");
        if (!Uri.TryCreate(release.PageUrl, UriKind.Absolute, out var page) || !SafeHttps(page)
            || page.AbsoluteUri != Repository + "/releases/tag/" + release.Tag)
            throw new InvalidDataException("Выпуск не принадлежит официальному репозиторию RtlineDnsPilot.");
        string prefix = Repository + "/releases/download/" + release.Tag + "/RtlineDnsPilot-" + release.Version;
        if (!Uri.TryCreate(release.DownloadUrl, UriKind.Absolute, out var download) || !SafeHttps(download)
            || (download.AbsoluteUri != prefix + "-win-x64.zip" && download.AbsoluteUri != prefix + "-public-win-x64.zip"))
            throw new InvalidDataException("Архив обновления не принадлежит официальному выпуску Windows x64.");
    }
    private static bool IsDownloadRedirect(Uri uri, string original) => SafeHttps(uri) && uri.AbsoluteUri.Length <= 16384
        && (uri.AbsoluteUri == original || uri.Host is "release-assets.githubusercontent.com" or "objects.githubusercontent.com" or "github-releases.githubusercontent.com");
    private static bool SafeHttps(Uri uri) => uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Fragment.Length == 0;
    private static bool IsSha256(string? value) => value is { Length: 64 } && value.All(char.IsAsciiHexDigit);
    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;
    private static void EnsureSuccess(HttpResponseMessage response, string what)
    {
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new HttpRequestException("GitHub ограничил запросы обновлений. Повторите позже; текущая версия сохранена.", null, response.StatusCode);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException("GitHub не вернул " + what + " (HTTP " + (int)response.StatusCode + ").", null, response.StatusCode);
    }
    private static bool ReadBoolean(JsonElement value, string name) => value.TryGetProperty(name, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False
        ? field.GetBoolean() : throw new InvalidDataException("В списке обновлений отсутствует признак " + name + ".");
    private static string ReadString(JsonElement value, string name, int limit) => value.TryGetProperty(name, out var field) && field.ValueKind == JsonValueKind.String
        && field.GetString() is string text && text.Length is > 0 && text.Length <= limit ? text : throw new InvalidDataException("В списке обновлений отсутствует поле " + name + ".");
    private static bool TryReleaseVersion(string tag, out Version? version, out string? text)
    {
        text = tag.StartsWith('v') ? tag[1..] : tag;
        var parts = text.Split('.'); version = null;
        return parts.Length is 3 or 4 && parts.All(p => p.Length > 0 && p.All(char.IsAsciiDigit) && (p.Length == 1 || p[0] != '0')) && Version.TryParse(text, out version);
    }
    private static Version Normalize(Version value) => new(value.Major, value.Minor, Math.Max(0, value.Build), Math.Max(0, value.Revision));
    private static bool IsLink(ZipArchiveEntry entry)
    {
        int kind = (entry.ExternalAttributes >> 16) & 0xF000;
        return kind is not (0 or 0x8000 or 0x4000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0;
    }
    private static void EnsureNoReparseAncestors(string path)
    {
        for (var directory = new DirectoryInfo(path); directory is not null; directory = directory.Parent)
            if (directory.Exists && (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Каталог обновления содержит ссылку или перенаправление файловой системы.");
    }
    private static void Cleanup(string stage, string directory, string archivePath, List<string> created)
    {
        // Remove only files this operation created; never recursively delete a user tree.
        foreach (string path in created.Append(archivePath))
            try { if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        foreach (string path in new[] { directory, stage })
            try { if (Directory.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0) Directory.Delete(path, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
    public void Dispose() => client.Dispose();
}
