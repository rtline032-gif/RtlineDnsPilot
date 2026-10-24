using System.Security.Cryptography;

namespace NetPilot.Tests;

public static class ApplicationUpdateLauncherTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Fixture, Task> test)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "test-data", "application-launcher-" + Guid.NewGuid().ToString("N"));
            var fixture = new Fixture(path);
            try { await test(fixture); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        await Check("application update snapshot keeps the old runtime and excludes user data", async f =>
        {
            var request = await f.Prepare(); f.Launcher.StartCompletionWorker(request);
            var worker = f.Host.Starts.Single();
            Require(worker.Args.SequenceEqual(new[] { "--complete-application-update", request }), "Worker did not use the completion route.");
            var snapshot = Path.GetDirectoryName(worker.Executable)!;
            Require(snapshot != f.Old && snapshot.StartsWith(f.Root, StringComparison.OrdinalIgnoreCase), "Old runtime was not snapshotted under the update root.");
            Require(File.ReadAllText(Path.Combine(snapshot, "RtlineDnsPilot.dll")) == "old-RtlineDnsPilot.dll", "Snapshot changed the previous runtime.");
            Require(!File.Exists(Path.Combine(snapshot, "ui-config.json")) && !File.Exists(Path.Combine(snapshot, "dns-session.json"))
                && !File.Exists(Path.Combine(snapshot, "statistics.json")), "User configuration or DNS state entered a runtime snapshot.");
            f.UserDataIntact();
        });
        await Check("application update commits only after parent exit and successful service update", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request);
            int exit = await f.Launcher.CompleteAsync(request);
            Require(exit == 0 && f.Host.Events.SequenceEqual(new[] { "parent-exit", "service-install", "new-window" }), "Update committed outside the required process/service order.");
            Require(f.Launcher.TryLaunchCurrent(["--data-dir", f.Data], out var warning) && warning is null, "Committed cache was not selected on a later old GUI start.");
            var target = f.Host.Starts.Last();
            Require(target.Executable == Path.Combine(f.Stage, "RtlineDnsPilot.exe") && target.Args.SequenceEqual(new[] { "--data-dir", f.Data }), "Routing changed the data directory or selected an unrelated executable.");
            f.UserDataIntact();
        });
        await Check("completion worker acknowledges readiness before the old window exits", async f =>
        {
            var releaseParent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            f.Host.PendingParentExit = releaseParent.Task;
            var request = await f.Prepare(); f.BeginWorker(request);
            var completion = f.Launcher.CompleteAsync(request);
            await f.Launcher.WaitForWorkerReadyAsync(request);
            Require(!completion.IsCompleted && !f.Host.Events.Contains("service-install") && !File.Exists(Path.Combine(f.Root, "current.json")),
                "Worker preparation changed service or launch pointer before the parent closed.");
            releaseParent.SetResult(); Require(await completion == 0, "Ready worker could not finish after the parent exited.");
        });
        await Check("cached GUI routing preserves an explicitly relative data directory", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request); Require(await f.Launcher.CompleteAsync(request) == 0, "Fixture update failed.");
            string relative = Path.GetRelativePath(Environment.CurrentDirectory, f.Data);
            Require(f.Launcher.TryLaunchCurrent(["--data-dir", relative], out _)
                && f.Host.Starts.Last().Args.SequenceEqual(new[] { "--data-dir", f.Data }), "Changing the executable working directory redirected user configuration.");
        });
        await Check("application update without an installed service never asks to install one", async f =>
        {
            f.Host.ServiceInstalled = false;
            var request = await f.Prepare(updateService: false); f.BeginWorker(request);
            Require(await f.Launcher.CompleteAsync(request) == 0 && !f.Host.Events.Contains("service-install"), "An observer update installed or modified a service.");
        });
        await Check("service update failure keeps the launch pointer and restarts the old snapshot", async f =>
        {
            f.Host.InstallError = new IOException("fixture restoration failed");
            var request = await f.Prepare(); f.BeginWorker(request);
            Require(await f.Launcher.CompleteAsync(request) != 0, "A service failure was reported as a complete application update.");
            Require(!File.Exists(Path.Combine(f.Root, "current.json")) && !f.Host.Events.Contains("new-window"), "New GUI was committed after installer failure.");
            Require(f.Host.Starts.Last().Args.Contains("--skip-update-route") && f.Host.Starts.Last().Executable.Contains("previous", StringComparison.Ordinal), "Previous verified GUI was not restarted.");
            f.UserDataIntact();
        });
        await Check("new GUI startup failure restores the previous launch pointer without downgrading service", async f =>
        {
            var first = await f.Prepare(); f.BeginWorker(first);
            Require(await f.Launcher.CompleteAsync(first) == 0, "Fixture first update failed.");
            string pointer = File.ReadAllText(Path.Combine(f.Root, "current.json"));
            var later = f.NewStage("100.0.0");
            f.Host.Ready = false; f.Host.Events.Clear(); f.Host.ApplicationDirectory = f.Old;
            var second = await f.Launcher.PrepareAsync(later, f.Old, f.Data, updateService: true); f.BeginWorker(second);
            Require(await f.Launcher.CompleteAsync(second) != 0, "Unacknowledged GUI was reported ready.");
            Require(File.ReadAllText(Path.Combine(f.Root, "current.json")) == pointer, "Previous launch pointer was not restored exactly.");
            Require(f.Host.Events.Count(x => x == "service-install") == 1 && f.Host.Starts.Last().Args.Contains("--skip-update-route"), "GUI rollback attempted service downgrade or routed back to the failed GUI.");
            f.UserDataIntact();
        });
        await Check("tampering with a staged runtime after preparation prevents service and GUI launch", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request);
            File.AppendAllText(Path.Combine(f.Stage, "RtlineDnsPilot.dll"), "changed");
            Require(await f.Launcher.CompleteAsync(request) != 0 && !f.Host.Events.Contains("service-install") && !f.Host.Events.Contains("new-window"), "Tampered runtime crossed the launch or elevation boundary.");
        });
        await Check("tampering with the old snapshot prevents starting its completion worker", async f =>
        {
            var request = await f.Prepare(); f.Launcher.StartCompletionWorker(request);
            var snapshot = Path.GetDirectoryName(f.Host.Starts[0].Executable)!;
            f.Host.Starts.Clear(); File.AppendAllText(Path.Combine(snapshot, "RtlineDnsPilot.dll"), "changed");
            await Reject(() => { f.Launcher.StartCompletionWorker(request); return Task.CompletedTask; });
            Require(f.Host.Starts.Count == 0, "Modified completion worker was executed.");
        });
        await Check("a still-running parent leaves its old window and service untouched", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request);
            f.Host.ParentError = new TimeoutException("fixture parent stayed running");
            int startsBefore = f.Host.Starts.Count;
            Require(await f.Launcher.CompleteAsync(request) != 0 && f.Host.Starts.Count == startsBefore && !f.Host.Events.Contains("service-install"), "Parent exit failure launched another GUI or changed service.");
        });
        await Check("cached update routing never redirects service or recovery helper modes", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request); Require(await f.Launcher.CompleteAsync(request) == 0, "Fixture update failed.");
            foreach (var mode in new[] { "--service", "--install-service", "--restore-dns", "--automatic-dns", "--probe", "--ui-smoke", "--skip-update-route" })
            {
                int calls = f.Host.Starts.Count;
                Require(!f.Launcher.TryLaunchCurrent([mode], out _) && f.Host.Starts.Count == calls, "A privileged/diagnostic mode followed a GUI pointer: " + mode);
            }
        });
        await Check("corrupt launch metadata leaves the original GUI available with a warning", async f =>
        {
            File.WriteAllText(Path.Combine(f.Root, "current.json"), "{}");
            Require(!f.Launcher.TryLaunchCurrent([], out var warning) && !string.IsNullOrWhiteSpace(warning) && f.Host.Starts.Count == 0,
                "Corrupt pointer launched code or concealed why the old GUI stayed open.");
        });
        await Check("a verified fresh update can repair corrupt launch metadata", async f =>
        {
            File.WriteAllText(Path.Combine(f.Root, "current.json"), "{}");
            var request = await f.Prepare(); f.BeginWorker(request);
            Require(await f.Launcher.CompleteAsync(request) == 0 && f.Launcher.TryLaunchCurrent([], out _), "A corrupt pointer permanently blocked application recovery.");
        });
        await Check("a verified same-release download can replace a corrupted approved cache", async f =>
        {
            var first = await f.Prepare(); f.BeginWorker(first); Require(await f.Launcher.CompleteAsync(first) == 0, "Fixture update failed.");
            File.AppendAllText(Path.Combine(f.Stage, "RtlineDnsPilot.dll"), "corruption");
            var repaired = f.NewStage("99.0.0"); f.Host.ApplicationDirectory = f.Old;
            var request = await f.Launcher.PrepareAsync(repaired, f.Old, f.Data, false); f.BeginWorker(request);
            Require(await f.Launcher.CompleteAsync(request) == 0 && f.Launcher.TryLaunchCurrent([], out _)
                && f.Host.Starts.Last().Executable == Path.Combine(repaired.DirectoryPath, "RtlineDnsPilot.exe"), "A corrupted cache prevented verified reinstallation of the release.");
        });
        await Check("a stale worker cannot downgrade an intact newer approved cache", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request); Require(await f.Launcher.CompleteAsync(request) == 0, "Fixture update failed.");
            f.Host.ApplicationDirectory = f.Old; var lower = f.NewStage("98.0.0"); int before = f.Host.Starts.Count;
            await Reject(() => f.Launcher.PrepareAsync(lower, f.Old, f.Data, false));
            Require(f.Host.Starts.Count == before, "Stale cached version launched over a newer approved one.");
        });
        await Check("an update candidate outside the managed root is refused", async f =>
        {
            var bad = f.Update with { DirectoryPath = f.Old };
            await Reject(() => f.Launcher.PrepareAsync(bad, f.Old, f.Data, false));
            Require(f.Host.Starts.Count == 0 && !File.Exists(Path.Combine(f.Root, "current.json")), "An unmanaged directory was accepted as an update.");
        });
        await Check("startup acknowledgement requires the selected staged application and its exact job path", async f =>
        {
            var request = await f.Prepare(); f.BeginWorker(request);
            f.Host.OnNewWindow = ack =>
            {
                var ackLauncher = new ApplicationUpdateLauncher(f.Root, f.Host, f.ValidatePackage);
                f.Host.ApplicationDirectory = f.Old;
                Reject(() => { ackLauncher.AcknowledgeStartup(ack); return Task.CompletedTask; }).GetAwaiter().GetResult();
                f.Host.ApplicationDirectory = f.Stage;
                ackLauncher.AcknowledgeStartup(ack);
                Require(File.Exists(ack), "Selected staged window could not acknowledge startup.");
                return true;
            };
            Require(await f.Launcher.CompleteAsync(request) == 0, "Correct staged startup acknowledgement was refused.");
            await Reject(() => { f.Launcher.AcknowledgeStartup(Path.Combine(f.Data, "not-an-ack.json")); return Task.CompletedTask; });
        });
    }

    private static readonly string[] Runtime = ["RtlineDnsPilot.exe", "RtlineDnsPilot.dll", "RtlineDnsPilot.deps.json", "RtlineDnsPilot.runtimeconfig.json"];
    private sealed class Fixture
    {
        public string Root { get; }
        public string Old { get; }
        public string Data { get; }
        public string Stage => Update.DirectoryPath;
        public StagedApplicationUpdate Update { get; }
        public FakeHost Host { get; }
        public ApplicationUpdateLauncher Launcher { get; }
        public Fixture(string directory)
        {
            Root = Path.Combine(directory, "updates"); Old = Path.Combine(directory, "old"); Data = Path.Combine(directory, "data");
            foreach (var path in new[] { Root, Old, Data }) Directory.CreateDirectory(path);
            foreach (string file in Runtime) File.WriteAllText(Path.Combine(Old, file), "old-" + file);
            foreach (string file in new[] { "ui-config.json", "dns-session.json", "statistics.json" })
            { File.WriteAllText(Path.Combine(Old, file), "private-old-" + file); File.WriteAllText(Path.Combine(Data, file), "keep-" + file); }
            Update = NewStage("99.0.0"); Host = new FakeHost { ApplicationDirectory = Old };
            Launcher = new ApplicationUpdateLauncher(Root, Host, ValidatePackage);
        }
        public StagedApplicationUpdate NewStage(string version)
        {
            string directory = Path.Combine(Root, "staging-" + Guid.NewGuid().ToString("N"), "RtlineDnsPilot"); Directory.CreateDirectory(directory);
            foreach (string file in Runtime) File.WriteAllText(Path.Combine(directory, file), "new-" + version + "-" + file);
            var hashes = Runtime.ToDictionary(name => name, name => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(directory, name)))), StringComparer.OrdinalIgnoreCase);
            return new StagedApplicationUpdate(new ApplicationRelease(version, "v" + version,
                "https://github.com/rtline032-gif/RtlineDnsPilot/releases/tag/v" + version,
                "https://github.com/rtline032-gif/RtlineDnsPilot/releases/download/v" + version + "/RtlineDnsPilot-" + version + "-win-x64.zip", 1024, new string('a', 64), true), directory, hashes);
        }
        public Task<string> Prepare(bool updateService = true) => Launcher.PrepareAsync(Update, Old, Data, updateService);
        public void BeginWorker(string request) { Launcher.StartCompletionWorker(request); Host.ApplicationDirectory = Path.GetDirectoryName(Host.Starts.Last().Executable)!; }
        public void ValidatePackage(string directory, string version)
        {
            Require(Version.TryParse(version, out _), "Malformed version reached package validation.");
            Require(Runtime.All(file => File.Exists(Path.Combine(directory, file))), "Incomplete package reached process launch.");
        }
        public void UserDataIntact()
        {
            foreach (string file in new[] { "ui-config.json", "dns-session.json", "statistics.json" })
                Require(File.ReadAllText(Path.Combine(Data, file)) == "keep-" + file && File.ReadAllText(Path.Combine(Old, file)) == "private-old-" + file, "Update modified user data.");
        }
    }
    private sealed class FakeHost : IApplicationUpdateHost
    {
        public int ProcessId => 1234;
        public long ProcessStartUtcTicks => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).Ticks;
        public bool ServiceInstalled { get; set; } = true;
        public string ApplicationDirectory { get; set; } = "";
        public List<string> Events { get; } = [];
        public List<(string Executable, string[] Args)> Starts { get; } = [];
        public Exception? ParentError { get; set; }
        public Task? PendingParentExit { get; set; }
        public Exception? InstallError { get; set; }
        public bool Ready { get; set; } = true;
        public Func<string, bool>? OnNewWindow { get; set; }
        public Task WaitForProcessExitAsync(int processId, long startUtcTicks, CancellationToken token)
        { Require(processId == ProcessId && startUtcTicks == ProcessStartUtcTicks, "Wrong parent identity."); Events.Add("parent-exit"); return ParentError is null ? PendingParentExit ?? Task.CompletedTask : Task.FromException(ParentError); }
        public Task InstallServiceAsync(string packageDirectory, CancellationToken token)
        { Events.Add("service-install"); return InstallError is null ? Task.CompletedTask : Task.FromException(InstallError); }
        public void Start(string executable, IReadOnlyList<string> arguments) => Starts.Add((executable, arguments.ToArray()));
        public Task<bool> LaunchAndWaitForReadyAsync(string executable, string dataDirectory, string ackPath, CancellationToken token)
        { Events.Add("new-window"); return Task.FromResult(OnNewWindow?.Invoke(ackPath) ?? Ready); }
    }
    private static void Require(bool value, string error) { if (!value) throw new InvalidOperationException(error); }
    private static async Task Reject(Func<Task> action)
    { try { await action(); } catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or InvalidOperationException) { return; } throw new Exception("Unsafe update operation was accepted."); }
}
