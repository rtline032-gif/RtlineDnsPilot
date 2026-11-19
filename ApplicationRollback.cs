using System.Security.Cryptography;
using System.Text;

namespace NetPilot;

internal sealed partial class ApplicationUpdateLauncher
{
    private const string RollbackRequestFile = "rollback-request.json";
    private const string RollbackReadyFile = "rollback-worker-started.json";
    private const string RollbackSelectionFile = "rollback-selected.json";

    public string GetRollbackVersion(string dataDirectory) =>
        ReadCompletedUpdate(dataDirectory).PreviousVersion;

    public string PrepareRollback(string dataDirectory, string previousVersion)
    {
        var update = ReadCompletedUpdate(dataDirectory);
        if (update.PreviousVersion != previousVersion)
            throw new InvalidOperationException("Версия для отката изменилась. Откройте команду отката снова.");
        if (ValidMarker(JobFile(update, RollbackSelectionFile), update.Nonce))
            throw new InvalidOperationException("Это обновление уже было отменено.");
        string path = JobFile(update, RollbackRequestFile);
        WriteRollbackFile(path, new RollbackRequest
        {
            UpdateId = update.Id,
            UpdateNonce = update.Nonce,
            DataDirectory = update.DataDirectory,
            ParentProcessId = _host.ProcessId,
            ParentStartUtcTicks = _host.ProcessStartUtcTicks,
            Nonce = Convert.ToHexString(RandomNumberGenerator.GetBytes(24))
        });
        return path;
    }

    public void StartRollbackWorker(string path)
    {
        var (_, update) = ReadRollbackRequest(path);
        _host.Start(Path.Combine(update.Update!.DirectoryPath, "RtlineDnsPilot.exe"),
            ["--complete-application-rollback", Path.GetFullPath(path)]);
    }

    public async Task WaitForRollbackWorkerReadyAsync(string path, CancellationToken cancellationToken = default)
    {
        var (request, update) = ReadRollbackRequest(path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            while (!ValidMarker(JobFile(update, RollbackReadyFile), request.Nonce))
                await Task.Delay(100, deadline.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Помощник отката не подтвердил запуск. Текущее окно оставлено открытым.");
        }
    }

    public async Task<int> CompleteRollbackAsync(string path, IProgress<ApplicationUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        UpdateRequest? update = null;
        bool parentExited = false, serviceChanged = false;
        FileStream? completionLock = null;
        try
        {
            var request = ReadRollbackRequest(path);
            update = request.Update;
            if (!SameDirectory(_host.ApplicationDirectory, update.Update!.DirectoryPath))
                throw new InvalidDataException("Помощник отката должен запускаться из проверенного текущего пакета.");
            string lockPath = Path.Combine(_root, ".completion.lock");
            EnsureInside(lockPath);
            if (File.Exists(lockPath)) EnsureOrdinaryFile(lockPath);
            completionLock = new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            if (ValidMarker(JobFile(update, RollbackSelectionFile), update.Nonce))
                throw new InvalidOperationException("Это обновление уже было отменено.");
            WriteRollbackFile(JobFile(update, RollbackReadyFile), new StartupMarker { Nonce = request.Request.Nonce });
            progress?.Report(new("Ожидание закрытия программы…"));
            using (var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                deadline.CancelAfter(TimeSpan.FromSeconds(35));
                await _host.WaitForProcessExitAsync(request.Request.ParentProcessId, request.Request.ParentStartUtcTicks, deadline.Token);
            }
            parentExited = true;

            if (ReadRollbackRequest(path).Request.Nonce != request.Request.Nonce)
                throw new InvalidDataException("Задание отката изменилось во время ожидания.");
            BackupUserData(update, request.Request.Nonce);
            if (_host.ServiceInstalled)
            {
                progress?.Report(new("Откат службы. Подтвердите разрешение Windows для восстановления исходного DNS и замены службы."));
                try { serviceChanged = await _host.RollbackServiceAsync(update.PreviousVersion, cancellationToken); }
                catch (ServiceRollbackConfirmationException) { serviceChanged = true; throw; }
            }

            VerifyPrevious(update);
            progress?.Report(new("Запуск версии " + update.PreviousVersion + "…"));
            using var readyDeadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            readyDeadline.CancelAfter(TimeSpan.FromSeconds(40));
            if (!await _host.LaunchPreviousAndWaitForReadyAsync(Path.Combine(update.PreviousDirectory, "RtlineDnsPilot.exe"),
                    update.PreviousVersion, update.DataDirectory, () =>
                    {
                        // Older shortcuts still reach this package through the forward pointer.
                        WriteRollbackFile(JobFile(update, RollbackSelectionFile), new StartupMarker { Nonce = update.Nonce });
                    }, readyDeadline.Token))
                throw new IOException("Предыдущее окно не подтвердило запуск.");
            return 0;
        }
        catch (Exception ex)
        {
            string warning = "Откат не завершён. " + Bound(ex.Message);
            if (update is not null && parentExited)
            {
                if (serviceChanged)
                {
                    try
                    {
                        progress?.Report(new("Возврат службы текущей версии. Подтвердите разрешение Windows."));
                        ValidateUpdate(update.Update!);
                        await _host.InstallServiceAsync(update.Update!.DirectoryPath, CancellationToken.None);
                        warning += " Служба текущей версии восстановлена, управление DNS выключено.";
                    }
                    catch (Exception undo) { warning += " Возврат службы не подтверждён: " + Bound(undo.Message); }
                }
                try
                {
                    ValidateUpdate(update.Update!);
                    _host.Start(Path.Combine(update.Update!.DirectoryPath, "RtlineDnsPilot.exe"),
                        ["--skip-update-route", "--data-dir", update.DataDirectory, "--update-warning", Bound(warning)]);
                }
                catch (Exception restart) { warning += " Открыть текущую версию не удалось: " + Bound(restart.Message); }
            }
            progress?.Report(new(warning));
            return 1;
        }
        finally { completionLock?.Dispose(); }
    }

    public bool TryLaunchRollback(string[] args, string dataDirectory, out string? warning)
    {
        warning = null;
        if (args.Any(arg => NonGuiModes.Contains(arg, StringComparer.Ordinal)) || !File.Exists(PointerPath)) return false;
        try
        {
            var pointer = ReadPointer();
            if (ParseVersion(pointer.Update!.Release.Version) != ParseVersion(_host.ApplicationVersion)) return false;
            string selection = Path.Combine(_root, "jobs", pointer.Id, RollbackSelectionFile);
            if (!File.Exists(selection)) return false;
            var request = ReadRequest(Path.Combine(_root, "jobs", pointer.Id, "request.json"));
            if (!SameDirectory(dataDirectory, request.DataDirectory)) return false;
            var update = ReadCompletedUpdate(dataDirectory);
            if (update.Id != pointer.Id || !ValidMarker(selection, update.Nonce))
                throw new InvalidDataException("Повреждена запись отката.");
            _host.Start(Path.Combine(update.PreviousDirectory, "RtlineDnsPilot.exe"),
                ["--skip-update-route", "--data-dir", update.DataDirectory]);
            return true;
        }
        catch (Exception ex)
        {
            warning = "Запуск сохранённой предыдущей версии не удался. Открыта текущая версия. " + Bound(ex.Message);
            return false;
        }
    }

    private UpdateRequest ReadCompletedUpdate(string dataDirectory)
    {
        const string unavailable = "Нет сохранённого завершённого обновления для этой программы. Откат появится после обновления через кнопку «Обновить программу».";
        if (!File.Exists(PointerPath)) throw new InvalidOperationException(unavailable);
        var pointer = ReadPointer();
        if (pointer.Update?.Release is null || pointer.Update.FileHashes is null)
            throw new InvalidDataException("Повреждена запись выбранного обновления.");
        if (ParseVersion(pointer.Update.Release.Version) != ParseVersion(_host.ApplicationVersion))
            throw new InvalidOperationException(unavailable);
        var update = ReadRequest(Path.Combine(_root, "jobs", pointer.Id, "request.json"));
        if (!SameDirectory(AbsoluteDirectory(Path.GetFullPath(dataDirectory)), update.DataDirectory))
            throw new InvalidOperationException("Последнее обновление использовало другую папку настроек.");
        if (update.Update?.FileHashes is null || pointer.Update.Release != update.Update.Release
            || !SameDirectory(pointer.Update.DirectoryPath, update.Update.DirectoryPath)
            || update.Update.FileHashes.Count != pointer.Update.FileHashes.Count
            || !pointer.Update.FileHashes.All(pair =>
                update.Update.FileHashes.TryGetValue(pair.Key, out var hash) && string.Equals(pair.Value, hash, StringComparison.OrdinalIgnoreCase))
            || !ValidMarker(JobFile(update, "worker-started.json"), update.Nonce)
            || !ValidMarker(JobFile(update, "startup-ready.json"), update.Nonce))
            throw new InvalidDataException("Последнее обновление не имеет проверенного подтверждения запуска.");
        ValidateUpdate(update.Update);
        VerifyPrevious(update);
        return update;
    }

    private (RollbackRequest Request, UpdateRequest Update) ReadRollbackRequest(string path)
    {
        path = Path.GetFullPath(path);
        var request = JsonData.Decode<RollbackRequest>(Encoding.UTF8.GetString(ReadBounded(path)));
        if (request.ParentProcessId < 1 || request.ParentStartUtcTicks <= 0 || request.ParentStartUtcTicks > DateTime.MaxValue.Ticks
            || request.Nonce is not { Length: 48 } || !request.Nonce.All(Uri.IsHexDigit))
            throw new InvalidDataException("Повреждено задание отката.");
        var update = ReadCompletedUpdate(AbsoluteDirectory(request.DataDirectory));
        if (request.UpdateId != update.Id || request.UpdateNonce != update.Nonce
            || !string.Equals(path, JobFile(update, RollbackRequestFile), StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Задание отката не соответствует последнему обновлению.");
        return (request, update);
    }

    private void WriteRollbackFile<T>(string path, T value)
    {
        EnsureInside(path);
        if (File.Exists(path)) EnsureOrdinaryFile(path);
        if (File.Exists(path + ".bak")) EnsureOrdinaryFile(path + ".bak");
        AtomicStore.Write(path, value);
    }

    private void BackupUserData(UpdateRequest update, string nonce)
    {
        string data = update.DataDirectory;
        string snapshot = JobFile(update, "rollback-data-" + nonce);
        EnsureInside(snapshot);
        long size = 0;
        CopyFiles(data, snapshot, ["ui-config.json", "ui-config.pending.json", "bundled-channels.json", "service-config.last-seen.json"]);
        CopyFiles(Path.Combine(data, "observer"), Path.Combine(snapshot, "observer"), ["config.json", "statistics.json", "events.json", "chart-history.json"]);

        void CopyFiles(string source, string destination, string[] names)
        {
            foreach (string name in names.SelectMany(name => new[] { name, name + ".bak" }))
            {
                string from = Path.Combine(source, name);
                if (!File.Exists(from)) continue;
                EnsureOrdinaryFile(from);
                size += new FileInfo(from).Length;
                if (size > PackageLimit) throw new IOException("Резервная копия настроек и истории превышает допустимый размер.");
                string to = Path.Combine(destination, name);
                EnsureInside(to);
                Directory.CreateDirectory(destination);
                File.Copy(from, to, overwrite: false);
            }
        }
    }

    private sealed class RollbackRequest
    {
        public string UpdateId { get; set; } = "";
        public string UpdateNonce { get; set; } = "";
        public string DataDirectory { get; set; } = "";
        public int ParentProcessId { get; set; }
        public long ParentStartUtcTicks { get; set; }
        public string Nonce { get; set; } = "";
    }
}
