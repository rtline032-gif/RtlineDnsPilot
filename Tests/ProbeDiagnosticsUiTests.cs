using System.IO.Pipes;
using System.Reflection;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace NetPilot.Tests;

public static class ProbeDiagnosticsUiTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<string, Task> test)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "probe-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }

        await Check("channel diagnostic snapshots use the latest current context without probes or attached mutable data", async dir =>
        {
            var config = Config(); var samples = Statistics(config);
            var otherContext = JsonData.Clone(samples[0].Last!); otherContext.MonitoringKey = "other-targets";
            otherContext.Detail = "Unrelated target result"; samples[0].Samples.Add(otherContext);
            AtomicStore.Write(Path.Combine(dir, "statistics.json"), samples);
            await using var engine = new MonitorEngine(config, dir, false, (_, _, _, _) => throw new Exception("A read triggered a network probe."));
            var before = EngineState(engine);
            var snapshot = engine.GetProbeDiagnostics(config.Profiles[0].Id);
            Require(snapshot.ProfileId == config.Profiles[0].Id && snapshot.Services.Count == 2 && snapshot.Success == false
                && snapshot.Services.All(s => s.Diagnostics is not null), "The requested current-context sample was not selected.");
            snapshot.Services[0].Diagnostics!.Dns[0].Addresses[0] = "198.51.100.99";
            snapshot.Services.Clear(); snapshot.Targets.Clear(); snapshot.ProfileName = "changed by caller";
            var second = engine.GetProbeDiagnostics(config.Profiles[0].Id);
            Require(second.Services[0].Diagnostics!.Dns[0].Addresses[0] == "192.0.2.11" && second.Targets.Count == 2
                && second.ProfileName == config.Profiles[0].Name && EngineState(engine) == before,
                "A diagnostic reader changed engine measurements or configuration.");
            Require(!File.Exists(Path.Combine(dir, "dns-session.json")), "Reading diagnostics created a DNS transaction.");
        });

        await Check("stale missing legacy and future diagnostics state their actual limits without invented phases", async dir =>
        {
            var config = Config(); var now = DateTimeOffset.UtcNow; var statistics = Statistics(config);
            statistics[0].Last!.At = now.AddSeconds(-MonitoringTargets.FreshnessSeconds(config) - 1);
            var stale = ChannelDiagnostics.Create(config, statistics, config.Profiles[0].Id, now, "Synthetic");
            Require(stale.Detail.Contains("устарело") && stale.At == statistics[0].Last!.At, "Stale evidence was presented as current.");
            statistics[0].Last!.At = now.AddSeconds(1);
            Require(ChannelDiagnostics.Create(config, statistics, config.Profiles[0].Id, now, "Synthetic").Detail.Contains("будущем"),
                "Future evidence did not disclose its unknown freshness.");
            var legacy = new ProfileStats { ProfileId = config.Profiles[0].Id, Samples = [new ProbeResult
                { ProfileId = config.Profiles[0].Id, MonitoringKey = MonitoringTargets.Key(config), Stage = "tls", Detail = "legacy TLS timeout", Success = false }] };
            var legacyConfig = JsonData.Clone(config); legacyConfig.MonitorGemini = false;
            legacy.Last!.MonitoringKey = MonitoringTargets.Key(legacyConfig);
            var older = ChannelDiagnostics.Create(legacyConfig, [legacy], legacyConfig.Profiles[0].Id, now, "Synthetic");
            Require(older.Services.Count == 1 && older.Services[0].Diagnostics is null && ChannelDiagnostics.Format(older).Contains("не сохранены"),
                "Legacy Detail text was converted into invented phase measurements.");
            var missing = ChannelDiagnostics.Create(config, [legacy], config.Profiles[0].Id, now, "Synthetic");
            Require(missing.At is null && missing.Success is null && missing.Services.Count == 0 && missing.Detail.Contains("Нет сохранённого измерения"),
                "A different endpoint context supplied a fabricated current result.");
            await Task.CompletedTask;
        });

        await Check("diagnostic report preserves each DNS answer and separate TCP TLS HTTP result for every IP", dir =>
        {
            var config = Config(); var snapshot = ChannelDiagnostics.Create(config, Statistics(config), config.Profiles[0].Id, DateTimeOffset.UtcNow, "Synthetic");
            var text = ChannelDiagnostics.Format(snapshot);
            Require(text.Contains("DNS") && text.Contains("AAAA") && text.Contains("192.0.2.11") && text.Contains("2001:db8::11")
                && text.Contains("TCP") && text.Contains("TLS") && text.Contains("HTTP")
                && text.Contains("Synthetic TLS timeout") && text.Contains("Synthetic IPv6 route failure")
                && text.Contains("Gemini") && text.Contains("chatgpt.com"), "Distinct targets, DNS answers, IP families or phase evidence disappeared.");
            Require(!text.Contains("авторизован", StringComparison.OrdinalIgnoreCase), "Anonymous HTTPS was presented as account access.");
            return Task.CompletedTask;
        });

        await Check("a diagnostic snapshot bounds corrupt persisted phase arrays and text before RPC serialization", dir =>
        {
            var config = Config(); var statistics = Statistics(config); var diagnostic = statistics[0].Last!.Services[0].Diagnostics!;
            diagnostic.Dns = Enumerable.Range(0, 20).Select(_ => new DnsQueryDiagnostic
                { QueryType = "A", State = "success", Detail = new string('x', 20000), Addresses = Enumerable.Repeat("192.0.2.11", 1000).ToList() }).ToList();
            diagnostic.Attempts = Enumerable.Range(0, 500).Select(_ => new HttpsAttemptDiagnostic
                { Address = "192.0.2.11", Family = "IPv4", Tcp = new() { State = "error", Detail = new string('x', 20000) } }).ToList();
            var snapshot = ChannelDiagnostics.Create(config, statistics, config.Profiles[0].Id, DateTimeOffset.UtcNow, "Synthetic");
            var copied = snapshot.Services[0].Diagnostics!;
            Require(copied.Dns.Count <= 2 && copied.Dns.All(d => d.Addresses.Count <= 64) && copied.Attempts.Count <= 64,
                "Unbounded saved diagnostic arrays entered the reply.");
            Require(Encoding.UTF8.GetByteCount(JsonData.Encode(new RpcReply { Ok = true, ProbeDiagnostics = snapshot })) < 4 * 1024 * 1024
                && ChannelDiagnostics.Format(snapshot).Length <= ChannelDiagnostics.MaximumReportCharacters + 100,
                "One channel exceeded the RPC or dialog text limit.");
            Require(diagnostic.Dns.Count == 20 && diagnostic.Attempts.Count == 500, "Bounding a read rewrote stored evidence.");
            return Task.CompletedTask;
        });

        await Check("probe-diagnostics RPC reads one channel without status and rejects invalid identities without mutations", async dir =>
        {
            var config = Config(); AtomicStore.Write(Path.Combine(dir, "statistics.json"), Statistics(config));
            await using var engine = new MonitorEngine(config, dir, false, (_, _, _, _) => throw new Exception("RPC triggered a probe."));
            var before = EngineState(engine);
            var pipeName = "RtlineDnsPilot.ProbeDiagnostics.Test." + Guid.NewGuid().ToString("N");
            using var lifetime = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var server = new ServiceRpc(engine, WindowsIdentity.GetCurrent().User!.Value, pipeName).RunAsync(lifetime.Token);
            try
            {
                async Task<(RpcReply Reply, string Json)> Send(string? id)
                {
                    using var client = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                    await client.ConnectAsync(2000, lifetime.Token);
                    await PipeMessages.WriteAsync(client, JsonData.Encode(new RpcRequest { Command = "probe-diagnostics", ProfileId = id }), lifetime.Token);
                    var json = await PipeMessages.ReadAsync(client, 4 * 1024 * 1024, lifetime.Token);
                    return (JsonData.Decode<RpcReply>(json), json);
                }
                var answer = await Send(config.Profiles[0].Id);
                using var document = JsonDocument.Parse(answer.Json);
                Require(answer.Reply.Ok && answer.Reply.ProbeDiagnostics?.ProfileId == config.Profiles[0].Id
                    && answer.Reply.Status is null && !document.RootElement.TryGetProperty("Status", out _)
                    && !answer.Json.Contains(config.Profiles[1].Id), "Diagnostics included unrelated channels or a full status.");
                foreach (var invalid in new[] { null, "invalid-guid", Guid.NewGuid().ToString("N") })
                {
                    var rejected = await Send(invalid);
                    Require(!rejected.Reply.Ok && rejected.Reply.ProbeDiagnostics is null && rejected.Reply.Status is null
                        && !string.IsNullOrWhiteSpace(rejected.Reply.Error), "An invalid diagnostic identity was accepted or attached full status.");
                }
                Require(EngineState(engine) == before, "A read-only RPC changed the engine state.");
            }
            finally { lifetime.Cancel(); try { await server; } catch (OperationCanceledException) { } }
        });

        await Check("backend reads detached service diagnostics on explicit request without control commands", async dir =>
        {
            var config = Config(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new Connection(config); await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync(); var before = File.ReadAllText(Path.Combine(dir, "ui-config.json"));
            var snapshot = await backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id);
            snapshot.Services[0].Diagnostics!.Attempts.Clear();
            Require(service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "probe-diagnostics" })
                && service.Commands.Last().ProfileId == config.Profiles[0].Id && service.Snapshot.Services[0].Diagnostics!.Attempts.Count == 2,
                "Reading diagnostics sent controls, retargeted the profile, or returned attached state.");
            Require(File.ReadAllText(Path.Combine(dir, "ui-config.json")) == before && backend.ServiceConnected,
                "An optional read changed saved configuration or connection state.");
        });

        await Check("older services get update guidance and unknown service state cannot use local diagnostics", async dir =>
        {
            foreach (var offline in new[] { false, true })
            {
                var testDir = Path.Combine(dir, offline ? "unknown" : "old"); Directory.CreateDirectory(testDir);
                var config = Config(); AtomicStore.Write(Path.Combine(testDir, "ui-config.json"), config);
                var service = new Connection(config) { Version = "0.1.17", Offline = offline };
                await using var backend = new UiBackend(testDir, service); await backend.RefreshAsync();
                var error = await Rejection(() => backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id));
                Require(error.Contains(offline ? "Проверить связь" : "Обновить службу")
                    && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status" }),
                    "An old/unknown service received an unsupported read or was replaced with local evidence.");
            }
        });

        await Check("pending selected profile or target changes cannot reuse prior service diagnostics", async dir =>
        {
            foreach (var changeTargets in new[] { false, true })
            {
                var testDir = Path.Combine(dir, changeTargets ? "targets" : "profile"); Directory.CreateDirectory(testDir);
                var config = Config(); AtomicStore.Write(Path.Combine(testDir, "ui-config.json"), config);
                var service = new Connection(config) { RejectConfigure = true };
                await using var backend = new UiBackend(testDir, service); await backend.RefreshAsync();
                var draft = JsonData.Clone(config);
                if (changeTargets) draft.MonitorTailscale = true; else draft.Profiles[0].Servers = ["192.0.2.200"];
                await Rejection(() => backend.ConfigureAsync(draft));
                var error = await Rejection(() => backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id));
                Require(error.Contains("Применить настройки") && service.Commands.All(r => r.Command != "probe-diagnostics"),
                    "Unapplied network or target settings reused previous service evidence.");
            }
        });

        await Check("pending names intervals and an unrelated profile edit retain a matching selected diagnostic context", async dir =>
        {
            var config = Config(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new Connection(config) { RejectConfigure = true };
            await using var backend = new UiBackend(dir, service); await backend.RefreshAsync();
            var draft = JsonData.Clone(config); draft.Profiles[0].Name += " renamed"; draft.IntervalSeconds = 30;
            draft.Profiles[1].Servers = ["192.0.2.200"];
            await Rejection(() => backend.ConfigureAsync(draft));
            var snapshot = await backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id);
            Require(snapshot.ProfileId == config.Profiles[0].Id && service.Commands.Count(r => r.Command == "probe-diagnostics") == 1,
                "An unrelated or metadata-only draft unnecessarily blocked matching diagnostic evidence.");
        });

        await Check("backend rejects a diagnostic reply from a different channel or measurement context", async dir =>
        {
            foreach (var mismatch in new[] { "profile", "monitoring", "context", "targets" })
            {
                var testDir = Path.Combine(dir, mismatch); Directory.CreateDirectory(testDir);
                var config = Config(); AtomicStore.Write(Path.Combine(testDir, "ui-config.json"), config);
                var service = new Connection(config) { Mismatch = mismatch };
                await using var backend = new UiBackend(testDir, service); await backend.RefreshAsync();
                var error = await Rejection(() => backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id));
                Require(error.Contains("Обновите состояние") && backend.ServiceConnected && backend.Status.Config.Profiles[0].Id == config.Profiles[0].Id,
                    "Mismatched evidence was accepted or changed the current service state.");
            }
        });

        await Check("canceled and timed out diagnostic reads release the backend gate without changing service state", async dir =>
        {
            var config = Config(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new Connection(config) { DelayDiagnostics = true };
            await using var backend = new UiBackend(dir, service); await backend.RefreshAsync();
            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
            bool canceled = false;
            try { await backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id, cancel.Token); }
            catch (OperationCanceledException) { canceled = true; }
            await backend.RefreshAsync(); bool timedOut = false;
            try { await backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id); }
            catch (TimeoutException ex) { timedOut = ex.Message.Contains("3 секунды"); }
            await backend.RefreshAsync();
            Require(canceled && timedOut && backend.ServiceConnected
                && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "probe-diagnostics", "status", "probe-diagnostics", "status" }),
                "Cancellation/deadline blocked later reads, altered connection state or sent another control.");
        });

        await Check("paused local observer reads its saved channel diagnostics without service or network requests", async dir =>
        {
            var config = Config(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var observer = Path.Combine(dir, "observer"); Directory.CreateDirectory(observer);
            AtomicStore.Write(Path.Combine(observer, "statistics.json"), Statistics(config));
            var service = new Connection(config) { Installed = false };
            await using var backend = new UiBackend(dir, service); await backend.RefreshAsync();
            var snapshot = await backend.ReadProbeDiagnosticsAsync(config.Profiles[0].Id);
            Require(snapshot.Services.Count == 2 && snapshot.Source.Contains("Локальное") && service.Commands.Count == 0
                && !backend.Status.Monitoring && !File.Exists(Path.Combine(observer, "dns-session.json")),
                "A local read used the service, started background probes or created a DNS transaction.");
        });

        await Check("diagnostic channel button and context menu read once on demand into a read-only dialog", CheckUiAsync);
    }

    private static AppConfig Config()
    {
        var config = AppConfig.Defaults(); config.MonitoringPaused = true; config.MonitorGemini = true;
        foreach (var profile in config.Profiles) profile.Enabled = false;
        return config;
    }

    private static List<ProfileStats> Statistics(AppConfig config) => config.Profiles.Select(p => new ProfileStats
    {
        ProfileId = p.Id, TotalChecks = 1, Samples = [new ProbeResult
        {
            ProfileId = p.Id, MonitoringKey = MonitoringTargets.Key(config), At = DateTimeOffset.UtcNow.AddSeconds(-1), Success = false,
            Services = MonitoringTargets.Selected(config).Select(t => new ServiceProbeResult
            {
                ServiceId = t.Id, At = DateTimeOffset.UtcNow.AddSeconds(-1), Stage = "tls", Detail = "Synthetic overall failure", Success = false,
                Diagnostics = new ProbeDiagnostics
                {
                    Dns = [new() { QueryType = "A", State = "success", ElapsedMs = 5, ResponseCode = 0, Addresses = ["192.0.2.11"] },
                        new() { QueryType = "AAAA", State = "success", ElapsedMs = 7, ResponseCode = 0, Addresses = ["2001:db8::11"] }],
                    Attempts = [new() { Address = "192.0.2.11", Family = "IPv4", Tcp = new() { State = "success", ElapsedMs = 20 },
                        Tls = new() { State = "timeout", ElapsedMs = 100, Detail = "Synthetic TLS timeout" }, Http = new() { State = "not-started" } },
                        new() { Address = "2001:db8::11", Family = "IPv6", Tcp = new() { State = "error", ElapsedMs = 1, Detail = "Synthetic IPv6 route failure" },
                            Tls = new() { State = "not-started" }, Http = new() { State = "not-started" } }]
                }
            }).ToList()
        }]
    }).ToList();

    private static async Task<string> Rejection(Func<Task> action)
    {
        try { await action(); }
        catch (InvalidOperationException ex) { return ex.Message; }
        throw new InvalidOperationException("Expected a clear diagnostic rejection.");
    }

    private static Task CheckUiAsync(string directory)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var config = Config(); AtomicStore.Write(Path.Combine(directory, "ui-config.json"), config);
                var service = new Connection(config); var backend = new UiBackend(directory, service);
                using var form = new MainForm(backend, new DnsShareClient(), new ApplicationUpdateClient(new NoNetwork()),
                    new ApplicationUpdateLauncher(Path.Combine(directory, "updates")));
                form.ShowInTaskbar = false; form.Opacity = 0;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        var busy = typeof(MainForm).GetField("_busy", flags)!;
                        while ((bool)busy.GetValue(form)!) await Task.Delay(10, deadline.Token);
                        var timer = (System.Windows.Forms.Timer)typeof(MainForm).GetField("_timer", flags)!.GetValue(form)!;
                        timer.Stop();
                        typeof(System.Windows.Forms.Timer).GetMethod("OnTick", flags)!.Invoke(timer, [EventArgs.Empty]);
                        while ((bool)typeof(MainForm).GetField("_refreshing", flags)!.GetValue(form)!) await Task.Delay(10, deadline.Token);
                        Require(service.Commands.All(r => r.Command == "status"), "A timer read diagnostics before a user action.");
                        var grid = (DataGridView)typeof(MainForm).GetField("_grid", flags)!.GetValue(form)!;
                        var button = (Button)typeof(MainForm).GetField("_diagnostics", flags)!.GetValue(form)!;
                        var row = grid.Rows.Cast<DataGridViewRow>().Single(r => Equals(r.Tag, config.Profiles[0].Id));
                        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["name"];
                        Require(button.Enabled && button.Text == "Диагностика канала", "The selected channel has no available diagnostic action.");
                        var original = File.ReadAllText(Path.Combine(directory, "ui-config.json"));

                        void OpenAndInspect(Action open, string expectedId)
                        {
                            Exception? failure = null; bool observed = false; var until = DateTime.UtcNow.AddSeconds(4);
                            using var observer = new System.Windows.Forms.Timer { Interval = 15 };
                            observer.Tick += (_, _) =>
                            {
                                var dialog = Application.OpenForms.OfType<ProbeDiagnosticsDialog>().FirstOrDefault();
                                if (dialog is null) return;
                                try
                                {
                                    var text = Find(dialog).OfType<TextBox>().Single(t => t.Name == "ProbeDiagnosticsReport");
                                    var copy = Find(dialog).OfType<Button>().Single(b => b.Name == "CopyProbeDiagnostics");
                                    if (!copy.Enabled && DateTime.UtcNow < until) return;
                                    Require(copy.Enabled && text.ReadOnly && text.ScrollBars == ScrollBars.Both && !text.WordWrap,
                                        "The diagnostic view is editable, incomplete or cannot scroll its bounded report.");
                                    Require(service.Commands.Last().Command == "probe-diagnostics" && service.Commands.Last().ProfileId == expectedId
                                        && text.Text.Contains(expectedId) && text.Text.Contains("192.0.2.11") && text.Text.Contains("Synthetic TLS timeout"),
                                        "The opened dialog showed another channel or lost separate phase evidence.");
                                    Require(Find(dialog).OfType<Button>().Count() == 2, "The read-only report gained an unrelated control action.");
                                    using var bitmap = new Bitmap(dialog.Width, dialog.Height);
                                    dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
                                    var image = Path.Combine(directory, "diagnostics-preview-" + Guid.NewGuid().ToString("N") + ".png");
                                    bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
                                    Console.WriteLine("INFO channel diagnostics preview: " + image);
                                    observed = true;
                                }
                                catch (Exception ex) { failure = ex; }
                                observer.Stop(); dialog.Close();
                            };
                            observer.Start(); open(); observer.Stop();
                            if (failure is not null) throw failure;
                            Require(observed, "The diagnostic action did not display its report.");
                        }

                        OpenAndInspect(() => button.PerformClick(), config.Profiles[0].Id);
                        var second = grid.Rows.Cast<DataGridViewRow>().Single(r => Equals(r.Tag, config.Profiles[1].Id));
                        Require(form.PrepareChannelMenu(second.Index), "The context menu could not capture the selected profile.");
                        grid.ClearSelection(); row.Selected = true; grid.CurrentCell = row.Cells["name"];
                        var context = (ToolStripMenuItem)typeof(MainForm).GetField("_contextDiagnostics", flags)!.GetValue(form)!;
                        OpenAndInspect(() => context.PerformClick(), config.Profiles[1].Id);
                        Require(service.Commands.Count(r => r.Command == "probe-diagnostics") == 2
                            && service.Commands.All(r => r.Command is "status" or "probe-diagnostics")
                            && File.ReadAllText(Path.Combine(directory, "ui-config.json")) == original,
                            "Opening reports repeated probes, sent controls or changed settings.");
                        complete.TrySetResult();
                    }
                    catch (Exception ex) { complete.TrySetException(ex); }
                    finally { form.Dispose(); Application.ExitThread(); }
                };
                Application.Run(form);
            }
            catch (Exception ex) { complete.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(18));
    }

    private static IEnumerable<Control> Find(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Find(child)) yield return nested; } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private static string EngineState(MonitorEngine engine)
    {
        var status = engine.GetStatus();
        return JsonData.Encode(new { status.Config, status.Statistics, status.Events, status.Monitoring, status.ActiveProfileId, status.Error });
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("External HTTP is forbidden in diagnostic UI tests.");
    }

    private sealed class Connection : IUiServiceConnection
    {
        private readonly AppConfig _config;
        private readonly List<ProfileStats> _statistics;
        public Connection(AppConfig config)
        {
            _config = JsonData.Clone(config); _statistics = Statistics(config);
            Snapshot = ChannelDiagnostics.Create(_config, _statistics, _config.Profiles[0].Id, DateTimeOffset.UtcNow, "Synthetic service");
        }
        public bool Installed { get; set; } = true;
        public bool IsInstalled => Installed;
        public bool Offline { get; set; }
        public bool RejectConfigure { get; set; }
        public bool DelayDiagnostics { get; set; }
        public string Version { get; set; } = "0.1.18";
        public string? Mismatch { get; set; }
        public List<RpcRequest> Commands { get; } = [];
        public ChannelDiagnosticsSnapshot Snapshot { get; private set; }
        public async Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token)
        {
            Commands.Add(JsonData.Clone(request));
            if (Offline) throw new TimeoutException("Simulated disconnected service.");
            if (request.Command == "probe-diagnostics")
            {
                if (DelayDiagnostics) await Task.Delay(Timeout.Infinite, token);
                Snapshot = ChannelDiagnostics.Create(_config, _statistics, request.ProfileId!, DateTimeOffset.UtcNow, "Synthetic service");
                if (Mismatch == "profile") Snapshot.ProfileId = _config.Profiles[1].Id;
                if (Mismatch == "monitoring") Snapshot.MonitoringKey = "wrong-monitoring-key";
                if (Mismatch == "context") Snapshot.ContextKey = "wrong-profile-context";
                if (Mismatch == "targets") Snapshot.Targets.Clear();
                return new RpcReply { Ok = true, ProbeDiagnostics = Snapshot };
            }
            if (request.Command == "configure" && RejectConfigure) throw new InvalidOperationException("Simulated rejected settings.");
            if (request.Command != "status") throw new InvalidOperationException("Unexpected control: " + request.Command);
            return new RpcReply { Ok = true, Status = new AppStatus { Config = JsonData.Clone(_config), ServiceConnected = true,
                ServiceVersion = Version, Statistics = JsonData.Clone(_statistics) } };
        }
        public Task InstallAsync() => throw new InvalidOperationException("No service installation in diagnostic tests.");
        public Task RecoverAsync() => throw new InvalidOperationException("No DNS recovery in diagnostic tests.");
    }
}
