using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NetPilot;

internal interface IApplicationUpdateHost
{
    int ProcessId { get; }
    long ProcessStartUtcTicks { get; }
    string ApplicationDirectory { get; }
    bool ServiceInstalled { get; }
    Task WaitForProcessExitAsync(int processId, long startUtcTicks, CancellationToken token);
    Task InstallServiceAsync(string packageDirectory, CancellationToken token);
    void Start(string executable, IReadOnlyList<string> arguments);
    Task<bool> LaunchAndWaitForReadyAsync(string executable, string dataDirectory, string ackPath, CancellationToken token);
}

internal sealed class ApplicationUpdateLauncher
{
    private const int MetadataLimit = 256 * 1024;
    private const long PackageLimit = 128L * 1024 * 1024;
    private static readonly string[] RequiredFiles = ["RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json"];
    private static readonly HashSet<string> AllowedFiles = new([
        "RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json",
        "profiles.json", "CHANNELS.txt", "README.txt", "LICENSE", "PRIVACY.md", "CODE_SIGNING.md", "SHA256SUMS.txt"
    ], StringComparer.OrdinalIgnoreCase);
    private static readonly string[] NonGuiModes = ["--service", "--install-service", "--uninstall-service", "--restore-dns", "--automatic-dns", "--probe",
        "--ui-smoke", "--journal-smoke", "--settings-smoke", "--channels-smoke", "--complete-application-update", "--skip-update-route"];
    private readonly string _root;
    private readonly IApplicationUpdateHost _host;
    private readonly Action<string, string> _validatePackage;
    private string PointerPath => Path.Combine(_root, "current.json");
    public static string DefaultRoot => Path.Combine(AppPaths.LocalData, "application-updates");
    internal ApplicationUpdateLauncher(string? root = null, IApplicationUpdateHost? host = null,
        Action<string, string>? validatePackage = null)
    {
        _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root ?? DefaultRoot));
        _host = host ?? new WindowsApplicationUpdateHost(_root);
        _validatePackage = validatePackage ?? ((directory, version) => ServiceInstaller.ReadPackage(directory, version));
    }
    public Task<string> PrepareAsync(StagedApplicationUpdate update, string currentDirectory, string dataDirectory,
        bool updateService, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); EnsureRoot();
        ValidateUpdate(update);
        if (ParseVersion(update.Release.Version) <= ParseVersion(AppVersion.Current)) throw new InvalidDataException("Для обновления нужна более новая версия программы.");
        ValidateNotDowngrade(update);
        string source = AbsoluteDirectory(currentDirectory); EnsureNoReparse(source);
        _validatePackage(source, AppVersion.Current);
        var jobId = Guid.NewGuid().ToString("N"); var jobDirectory = Path.Combine(_root, "jobs", jobId);
        var previous = Path.Combine(jobDirectory, "previous", "RtlineDnsPilot"); EnsureInside(previous);
        Directory.CreateDirectory(previous); EnsureNoReparse(previous);
        long bytes = 0;
        foreach (string name in AllowedFiles)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(source, name); if (!File.Exists(path)) continue;
            EnsureOrdinaryFile(path); bytes += new FileInfo(path).Length;
            if (bytes > PackageLimit) throw new InvalidDataException("Предыдущий пакет превышает размер резервной копии обновления.");
            File.Copy(path, Path.Combine(previous, name), overwrite: false);
        }
        var hashes = HashPackage(previous); VerifyFiles(previous, hashes); _validatePackage(previous, AppVersion.Current);
        var request = new UpdateRequest
        {
            Id = jobId, CreatedUtc = DateTimeOffset.UtcNow, ParentProcessId = _host.ProcessId, ParentStartUtcTicks = _host.ProcessStartUtcTicks,
            Update = update, PreviousDirectory = previous, PreviousVersion = AppVersion.Current, PreviousHashes = hashes,
            DataDirectory = AbsoluteDirectory(Path.GetFullPath(dataDirectory)), UpdateService = updateService, Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
        };
        string pathToRequest = Path.Combine(jobDirectory, "request.json"); AtomicStore.Write(pathToRequest, request);
        return Task.FromResult(pathToRequest);
    }
    public void StartCompletionWorker(string requestPath)
    {
        var request = ReadRequest(requestPath); VerifyPrevious(request); ValidateUpdate(request.Update!);
        _host.Start(Path.Combine(request.PreviousDirectory, "RtlineDnsPilot.exe"), ["--complete-application-update", Path.GetFullPath(requestPath)]);
    }
    public async Task WaitForWorkerReadyAsync(string requestPath, CancellationToken cancellationToken = default)
    {
        var request = ReadRequest(requestPath); string marker = JobFile(request, "worker-started.json");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!ValidMarker(marker, request.Nonce)) await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Помощник обновления не подтвердил запуск. Текущее окно оставлено открытым."); }
    }
    public async Task<int> CompleteAsync(string requestPath, IProgress<ApplicationUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UpdateRequest? request = null; bool parentExited = false, committed = false; byte[]? previousPointer = null;
        FileStream? completionLock = null;
        try
        {
            request = ReadRequest(requestPath); VerifyPrevious(request);
            if (!SameDirectory(_host.ApplicationDirectory, request.PreviousDirectory) || ParseVersion(request.PreviousVersion) != ParseVersion(AppVersion.Current))
                throw new InvalidDataException("Помощник должен запускаться из сохранённой предыдущей версии.");
            string lockPath = Path.Combine(_root, ".completion.lock"); EnsureInside(lockPath); if (File.Exists(lockPath)) EnsureOrdinaryFile(lockPath);
            try { completionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { progress?.Report(new("Другое обновление уже завершается; текущее окно можно оставить открытым.")); return 1; }
            AtomicStore.Write(JobFile(request, "worker-started.json"), new StartupMarker { Nonce = request.Nonce });
            progress?.Report(new("Ожидание закрытия предыдущего окна…"));
            using (var parentDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                parentDeadline.CancelAfter(TimeSpan.FromSeconds(35));
                await _host.WaitForProcessExitAsync(request.ParentProcessId, request.ParentStartUtcTicks, parentDeadline.Token);
            }
            parentExited = true;
            ValidateUpdate(request.Update!); VerifyPrevious(request); ValidateNotDowngrade(request.Update!);
            if (request.UpdateService && _host.ServiceInstalled)
            {
                progress?.Report(new("Обновление службы. Подтвердите разрешение Windows; исходные DNS будут восстановлены."));
                await _host.InstallServiceAsync(request.Update!.DirectoryPath, cancellationToken);
            }
            ValidateUpdate(request.Update!);
            previousPointer = File.Exists(PointerPath) ? ReadBounded(PointerPath) : null;
            if (File.Exists(PointerPath + ".bak")) EnsureOrdinaryFile(PointerPath + ".bak");
            var pointer = new LaunchPointer { Id = request.Id, Update = request.Update };
            AtomicStore.Write(PointerPath, pointer); committed = true;
            progress?.Report(new("Запуск RtlineDnsPilot " + request.Update!.Release.Version + "…"));
            string ack = JobFile(request, "startup-ready.json"); if (File.Exists(ack)) { EnsureOrdinaryFile(ack); File.Delete(ack); }
            using (var launchDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                launchDeadline.CancelAfter(TimeSpan.FromSeconds(40));
                if (!await _host.LaunchAndWaitForReadyAsync(Path.Combine(request.Update!.DirectoryPath, "RtlineDnsPilot.exe"), request.DataDirectory, ack, launchDeadline.Token))
                    throw new IOException("Новая версия не подтвердила открытие окна.");
            }
            progress?.Report(new("Обновление до " + request.Update!.Release.Version + " завершено.")); return 0;
        }
        catch (Exception ex)
        {
            string warning = "Обновление не завершено. " + Bound(ex.Message);
            if (request is not null && committed)
            {
                try { RestorePointer(request.Id, previousPointer); }
                catch (Exception restoreError) { warning += " Запись запуска требует проверки: " + Bound(restoreError.Message); }
            }
            progress?.Report(new(warning));
            if (request is not null && parentExited)
            {
                try
                {
                    VerifyPrevious(request);
                    _host.Start(Path.Combine(request.PreviousDirectory, "RtlineDnsPilot.exe"),
                        ["--skip-update-route", "--data-dir", request.DataDirectory, "--update-warning", Bound(warning + " Предыдущее окно восстановлено. После обновления службы управление DNS остаётся выключенным.")]);
                }
                catch (Exception restartError) { progress?.Report(new("Не удалось открыть предыдущую версию: " + Bound(restartError.Message))); }
            }
            return 1;
        }
        finally { completionLock?.Dispose(); }
    }
    public bool TryLaunchCurrent(string[] originalArgs, out string? warning)
    {
        warning = null;
        if (originalArgs.Any(arg => NonGuiModes.Contains(arg, StringComparer.Ordinal))) return false;
        if (!File.Exists(PointerPath)) return false;
        try
        {
            var pointer = ReadPointer(); var update = pointer.Update!;
            if (ParseVersion(update.Release.Version) <= ParseVersion(AppVersion.Current)) return false;
            ValidateUpdate(update);
            if (SameDirectory(update.DirectoryPath, _host.ApplicationDirectory)) return false;
            var forwarded = originalArgs.ToArray();
            int dataOption = Array.IndexOf(forwarded, "--data-dir");
            if (dataOption >= 0 && dataOption + 1 < forwarded.Length) forwarded[dataOption + 1] = Path.GetFullPath(forwarded[dataOption + 1]);
            _host.Start(Path.Combine(update.DirectoryPath, "RtlineDnsPilot.exe"), forwarded); return true;
        }
        catch (Exception ex)
        { warning = "Сохранённое обновление не запущено; доступна эта версия программы. " + Bound(ex.Message); return false; }
    }
    public void AcknowledgeStartup(string ackPath)
    {
        ackPath = Path.GetFullPath(ackPath); EnsureInside(ackPath);
        if (Path.GetFileName(ackPath) != "startup-ready.json") throw new InvalidDataException("Недопустимый файл подтверждения запуска.");
        string pathToRequest = Path.Combine(Path.GetDirectoryName(ackPath)!, "request.json"); var request = ReadRequest(pathToRequest);
        if (!string.Equals(ackPath, JobFile(request, "startup-ready.json"), StringComparison.OrdinalIgnoreCase)
            || !SameDirectory(_host.ApplicationDirectory, request.Update!.DirectoryPath))
            throw new InvalidDataException("Запуск может подтвердить только выбранная новая версия.");
        ValidateUpdate(request.Update);
        AtomicStore.Write(ackPath, new StartupMarker { Nonce = request.Nonce });
    }

    private UpdateRequest ReadRequest(string path)
    {
        path = Path.GetFullPath(path); EnsureInside(path); string parent = Path.GetDirectoryName(path)!;
        if (Path.GetFileName(path) != "request.json" || Path.GetFileName(Path.GetDirectoryName(parent)!) != "jobs") throw new InvalidDataException("Недопустимая запись обновления.");
        var request = JsonData.Decode<UpdateRequest>(Encoding.UTF8.GetString(ReadBounded(path)));
        if (request.Protocol != 1 || !Guid.TryParseExact(request.Id, "N", out _) || Path.GetFileName(parent) != request.Id
            || request.CreatedUtc == default || request.ParentProcessId < 1 || request.ParentStartUtcTicks <= 0
            || request.ParentStartUtcTicks > DateTime.MaxValue.Ticks || request.Update is null || request.PreviousHashes is null
            || string.IsNullOrEmpty(request.Nonce) || request.Nonce.Length != 48 || !request.Nonce.All(Uri.IsHexDigit)) throw new InvalidDataException("Повреждены параметры обновления.");
        if (!string.Equals(path, JobFile(request, "request.json"), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Запись обновления находится в посторонней папке заданий.");
        if (!SameDirectory(request.PreviousDirectory, Path.Combine(parent, "previous", "RtlineDnsPilot"))) throw new InvalidDataException("Недопустимая резервная копия программы.");
        _ = AbsoluteDirectory(request.DataDirectory);
        if (ParseVersion(request.Update.Release.Version) <= ParseVersion(request.PreviousVersion)) throw new InvalidDataException("Откат версии не является обновлением.");
        return request;
    }
    private LaunchPointer ReadPointer()
    {
        var pointer = JsonData.Decode<LaunchPointer>(Encoding.UTF8.GetString(ReadBounded(PointerPath)));
        if (pointer.Protocol != 1 || !Guid.TryParseExact(pointer.Id, "N", out _) || pointer.Update is null) throw new InvalidDataException("Повреждена запись выбранной версии.");
        return pointer;
    }
    private void ValidateNotDowngrade(StagedApplicationUpdate update)
    {
        if (!File.Exists(PointerPath)) return;
        LaunchPointer current;
        try { current = ReadPointer(); ValidateUpdate(current.Update!); }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException or ArgumentException) { return; }
        if (ParseVersion(current.Update!.Release.Version) > ParseVersion(update.Release.Version))
            throw new InvalidDataException("Более новая версия уже выбрана для запуска. Понижение версии не выполнено.");
    }
    private void ValidateUpdate(StagedApplicationUpdate update)
    {
        if (update is null || update.Release is null || update.FileHashes is null) throw new InvalidDataException("Нет проверенного пакета обновления.");
        var release = update.Release; _ = ParseVersion(release.Version);
        if ((release.Tag != "v" + release.Version && release.Tag != release.Version) || release.Size < 1 || release.Size > 32L * 1024 * 1024 || string.IsNullOrEmpty(release.Sha256) || release.Sha256.Length != 64 || !release.Sha256.All(Uri.IsHexDigit))
            throw new InvalidDataException("Некорректные сведения о выпуске.");
        const string repo = "https://github.com/rtline032-gif/RtlineDnsPilot/releases/";
        if (release.PageUrl != repo + "tag/" + release.Tag || (release.DownloadUrl != repo + "download/" + release.Tag + "/RtlineDnsPilot-" + release.Version + "-win-x64.zip"
            && release.DownloadUrl != repo + "download/" + release.Tag + "/RtlineDnsPilot-" + release.Version + "-public-win-x64.zip"))
            throw new InvalidDataException("Обновление принадлежит другому источнику.");
        string directory = AbsoluteDirectory(update.DirectoryPath); EnsureInside(directory);
        string envelope = Path.GetDirectoryName(directory)!;
        if (Path.GetFileName(directory) != "RtlineDnsPilot" || !Path.GetFileName(envelope).StartsWith("staging-", StringComparison.Ordinal)
            || !Guid.TryParseExact(Path.GetFileName(envelope)[8..], "N", out _) || !SameDirectory(Path.GetDirectoryName(envelope)!, _root))
            throw new InvalidDataException("Пакет находится вне папки загрузок обновления.");
        VerifyFiles(directory, update.FileHashes); _validatePackage(directory, release.Version);
    }
    private void VerifyPrevious(UpdateRequest request)
    {
        EnsureInside(request.PreviousDirectory); VerifyFiles(request.PreviousDirectory, request.PreviousHashes!);
        _validatePackage(request.PreviousDirectory, request.PreviousVersion);
    }
    private void VerifyFiles(string directory, IReadOnlyDictionary<string, string> hashes)
    {
        EnsureNoReparse(directory);
        if (hashes.Count < RequiredFiles.Length || hashes.Count > AllowedFiles.Count || RequiredFiles.Any(file => !hashes.ContainsKey(file)))
            throw new InvalidDataException("Пакет содержит неполный набор файлов.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase); long bytes = 0;
        foreach (var entry in hashes)
        {
            if (!AllowedFiles.Contains(entry.Key) || !seen.Add(entry.Key) || entry.Value is null || entry.Value.Length != 64 || !entry.Value.All(Uri.IsHexDigit))
                throw new InvalidDataException("Недопустимый файл или контрольная сумма пакета.");
            string file = Path.Combine(directory, entry.Key); EnsureOrdinaryFile(file); bytes += new FileInfo(file).Length;
            if (bytes > PackageLimit) throw new InvalidDataException("Пакет обновления превышает предел размера.");
            if (!string.Equals(Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))), entry.Value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Изменён файл пакета обновления: " + entry.Key);
        }
        if (Directory.EnumerateFileSystemEntries(directory).Any(path => !hashes.Keys.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
            throw new InvalidDataException("В папке обновления появился посторонний файл.");
    }
    private static Dictionary<string, string> HashPackage(string directory) => Directory.EnumerateFiles(directory).ToDictionary(path => Path.GetFileName(path),
        path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))), StringComparer.OrdinalIgnoreCase);
    private string JobFile(UpdateRequest request, string name) => Path.Combine(_root, "jobs", request.Id, name);
    private bool ValidMarker(string path, string nonce)
    {
        if (!File.Exists(path)) return false;
        try { return JsonData.Decode<StartupMarker>(Encoding.UTF8.GetString(ReadBounded(path, 1024))).Nonce == nonce; }
        catch (Exception ex) when (ex is IOException or JsonException or InvalidDataException) { return false; }
    }
    private void RestorePointer(string id, byte[]? previous)
    {
        if (!File.Exists(PointerPath) || ReadPointer().Id != id) throw new InvalidDataException("Запись запуска изменена другим действием и сохранена.");
        if (previous is null) { EnsureOrdinaryFile(PointerPath); File.Delete(PointerPath); return; }
        string temp = PointerPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { file.Write(previous); file.Flush(true); }
            File.Replace(temp, PointerPath, PointerPath + ".bak", true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }
    private byte[] ReadBounded(string path, int limit = MetadataLimit)
    {
        EnsureInside(path); EnsureOrdinaryFile(path); if (new FileInfo(path).Length > limit) throw new InvalidDataException("Запись обновления превышает допустимый размер.");
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (file.Length > limit) throw new InvalidDataException("Запись обновления превышает допустимый размер.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        if (file.ReadByte() != -1) throw new InvalidDataException("Запись обновления изменилась во время чтения."); return bytes;
    }
    private void EnsureRoot() { EnsureNoReparse(_root, allowMissing: true); Directory.CreateDirectory(_root); EnsureNoReparse(_root); }
    private void EnsureInside(string path)
    {
        string full = Path.GetFullPath(path);
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Путь находится вне папки обновлений.");
        EnsureNoReparse(full, allowMissing: true);
    }
    private static string AbsoluteDirectory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > 4096 || !Path.IsPathFullyQualified(path)) throw new InvalidDataException("Нужен полный путь папки приложения.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
    }
    private static void EnsureOrdinaryFile(string path)
    {
        EnsureNoReparse(path); var attributes = File.GetAttributes(path);
        if ((attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) != 0) throw new InvalidDataException("Нужен обычный файл обновления.");
    }
    private static void EnsureNoReparse(string path, bool allowMissing = false)
    {
        string? current = Path.GetFullPath(path);
        while (current is not null)
        {
            if (File.Exists(current) || Directory.Exists(current))
            {
                if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException("Ссылки и перенаправления в пути обновления запрещены.");
            }
            else if (!allowMissing) throw new IOException("Не найдена папка или файл обновления: " + Path.GetFileName(current));
            current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current));
        }
    }
    private static bool SameDirectory(string a, string b) => string.Equals(AbsoluteDirectory(a), AbsoluteDirectory(b), StringComparison.OrdinalIgnoreCase);
    private static Version ParseVersion(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 32 || value.Any(c => !char.IsAsciiDigit(c) && c != '.') || !Version.TryParse(value, out var parsed))
            throw new InvalidDataException("Некорректная версия программы.");
        return new(parsed.Major, parsed.Minor, Math.Max(0, parsed.Build), Math.Max(0, parsed.Revision));
    }
    private static string Bound(string text) => text.Length <= 600 ? text : text[..600];

    private sealed class UpdateRequest
    {
        [JsonRequired] public int Protocol { get; set; } = 1;
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
        [JsonRequired] public int ParentProcessId { get; set; }
        [JsonRequired] public long ParentStartUtcTicks { get; set; }
        [JsonRequired] public StagedApplicationUpdate? Update { get; set; }
        [JsonRequired] public string PreviousDirectory { get; set; } = "";
        [JsonRequired] public string PreviousVersion { get; set; } = "";
        [JsonRequired] public Dictionary<string, string>? PreviousHashes { get; set; }
        [JsonRequired] public string DataDirectory { get; set; } = "";
        [JsonRequired] public bool UpdateService { get; set; }
        [JsonRequired] public string Nonce { get; set; } = "";
    }
    private sealed class LaunchPointer
    {
        [JsonRequired] public int Protocol { get; set; } = 1;
        [JsonRequired] public string Id { get; set; } = "";
        [JsonRequired] public StagedApplicationUpdate? Update { get; set; }
    }
    private sealed class StartupMarker { [JsonRequired] public string Nonce { get; set; } = ""; }

    private sealed class WindowsApplicationUpdateHost(string root) : IApplicationUpdateHost
    {
        public int ProcessId => Environment.ProcessId;
        public long ProcessStartUtcTicks { get { using var process = Process.GetCurrentProcess(); return process.StartTime.ToUniversalTime().Ticks; } }
        public string ApplicationDirectory => AppContext.BaseDirectory;
        public bool ServiceInstalled => ServiceClient.IsInstalled;
        public async Task WaitForProcessExitAsync(int processId, long startUtcTicks, CancellationToken token)
        {
            try
            {
                using var process = Process.GetProcessById(processId);
                if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != startUtcTicks) return;
                await process.WaitForExitAsync(token);
            }
            catch (ArgumentException) { }
            catch (InvalidOperationException) { } // Process exited between opening its PID and reading its start time.
        }
        public Task InstallServiceAsync(string packageDirectory, CancellationToken token) => ServiceClient.InstallPackageAsync(packageDirectory, token);
        public void Start(string executable, IReadOnlyList<string> arguments)
        {
            using var process = Process.Start(StartInfo(executable, arguments)) ?? throw new IOException("Не удалось запустить окно программы.");
        }
        public async Task<bool> LaunchAndWaitForReadyAsync(string executable, string dataDirectory, string ackPath, CancellationToken token)
        {
            var owner = new ApplicationUpdateLauncher(root);
            var request = owner.ReadRequest(Path.Combine(Path.GetDirectoryName(ackPath)!, "request.json"));
            bool acknowledged = false;
            using var process = Process.Start(StartInfo(executable, ["--data-dir", dataDirectory, "--update-startup-ack", ackPath]))
                ?? throw new IOException("Не удалось открыть новую версию программы.");
            long started = process.StartTime.ToUniversalTime().Ticks;
            try
            {
                while (!token.IsCancellationRequested)
                {
                    if (owner.ValidMarker(ackPath, request.Nonce)) { acknowledged = true; return true; }
                    if (process.HasExited) return false;
                    await Task.Delay(100, token);
                }
                return false;
            }
            catch (OperationCanceledException) { return false; }
            finally
            {
                if (!acknowledged && !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started)
                { try { process.Kill(entireProcessTree: true); await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or TimeoutException) { } }
            }
        }
        private static ProcessStartInfo StartInfo(string executable, IReadOnlyList<string> arguments)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(executable)!, CreateNoWindow = true };
            foreach (string argument in arguments) start.ArgumentList.Add(argument); return start;
        }
    }
}
