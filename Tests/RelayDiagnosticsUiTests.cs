using System.Reflection;

namespace NetPilot.Tests;

public static class RelayDiagnosticsUiTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<string, Task> test)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "relay-ui-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }

        await Check("relay diagnostic backend reads a detached snapshot once without adopting attached status or changing settings", async dir =>
        {
            var config = Config(); var service = new Connection(config);
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            var before = JsonData.Encode(backend.Status); var saved = File.ReadAllText(Path.Combine(dir, "ui-config.json"));
            service.AttachedStatus = new AppStatus { Config = Config(), ServiceVersion = "0.0.1", AllUnavailable = true };
            var originalSnapshot = service.Snapshot!;
            var snapshot = await backend.ReadRelayDiagnosticsAsync();
            Require(snapshot.SessionId == originalSnapshot.SessionId && snapshot.Received == 0 && snapshot.Requests.Count == 0,
                "The empty service observation was replaced with invented measurements.");
            snapshot.SessionId = Guid.NewGuid().ToString("N"); snapshot.Requests.Add(Request(config));
            Require(originalSnapshot.Requests.Count == 0 && snapshot.SessionId != originalSnapshot.SessionId,
                "The caller can mutate the service's attached diagnostic data.");
            Require(service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "relay-diagnostics" })
                && service.Commands.Last().Config is null && service.Commands.Last().ProfileId is null,
                "An explicit diagnostic read sent an unrelated command or control payload.");
            Require(JsonData.Encode(backend.Status) == before && File.ReadAllText(Path.Combine(dir, "ui-config.json")) == saved
                && backend.ServiceConnected, "A diagnostic reply adopted unrelated Status, rewrote configuration or disconnected the service.");
        });

        await Check("relay diagnostics on a 0.1.20 service explain the required update without sending the new command", async dir =>
        {
            var config = Config(); var service = new Connection(config) { Version = "0.1.20" };
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            var error = await Rejected<InvalidOperationException>(() => backend.ReadRelayDiagnosticsAsync());
            Require(error.Message.Contains("0.1.21") && error.Message.Contains("Обновить службу")
                && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status" }),
                "The old service received an unsupported read or no actionable update guidance.");
        });

        await Check("an unconfirmed relay connection cannot fall back to local channel observations", async dir =>
        {
            var config = Config(); var service = new Connection(config);
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            service.Offline = true; await backend.RefreshAsync();
            Require(backend.DnsStateUnknown && !backend.ServiceConnected && !backend.Status.Monitoring,
                "The fixture did not enter the unconfirmed installed-service state.");
            var before = JsonData.Encode(backend.Status);
            var error = await Rejected<IOException>(() => backend.ReadRelayDiagnosticsAsync());
            Require(error.Message.Contains("Проверить связь") && error.Message.Contains("Локальная проверка канала не заменяет")
                && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "status" })
                && JsonData.Encode(backend.Status) == before, "Missing service observations were fabricated locally or changed the backend state.");
        });

        await Check("a local-only window cannot manufacture relay service observations before a connection", async dir =>
        {
            var config = Config(); var service = new Connection(config) { Installed = false };
            await using var backend = Backend(dir, config, service);
            var error = await Rejected<IOException>(() => backend.ReadRelayDiagnosticsAsync());
            Require(error.Message.Contains("нужна связь со службой") && service.Commands.Count == 0
                && !Directory.Exists(Path.Combine(dir, "observer")), "Reading relay diagnostics created a local probe engine or contacted a missing service.");
        });

        await Check("a pending local profile draft does not relabel observed relay requests or apply the draft", async dir =>
        {
            var config = Config(); var draft = JsonData.Clone(config); draft.Profiles[0].Servers = ["192.0.2.54"];
            var service = new Connection(config); service.Snapshot = Snapshot(config);
            service.Snapshot.Received = service.Snapshot.Completed = service.Snapshot.ResponsesQueued = 1;
            service.Snapshot.Requests.Add(Request(config));
            AtomicStore.Write(Path.Combine(dir, "ui-config.pending.json"), new PendingUiConfiguration { Config = draft });
            await using var backend = Backend(dir, draft, service); await backend.RefreshAsync();
            Require(backend.PendingServiceConfiguration, "The fixture lost its unconfirmed local draft.");
            var saved = File.ReadAllText(Path.Combine(dir, "ui-config.json"));
            var snapshot = await backend.ReadRelayDiagnosticsAsync();
            Require(snapshot.Requests.Single().ProfileContext == RelayDiagnostics.Context(config.Profiles[0])
                && snapshot.Requests.Single().ProfileContext != RelayDiagnostics.Context(draft.Profiles[0])
                && File.ReadAllText(Path.Combine(dir, "ui-config.json")) == saved
                && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "relay-diagnostics" }),
                "A read applied the draft or attributed service observations to its new DNS configuration.");
        });

        await Check("missing malformed oversized and wrong-version relay snapshots are rejected without losing the backend gate", async dir =>
        {
            var config = Config(); var service = new Connection(config);
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            var before = JsonData.Encode(backend.Status);
            var invalid = new List<RelayDiagnosticsSnapshot?> { null };
            var identity = Snapshot(config); identity.SessionId = "not-a-session"; invalid.Add(identity);
            var counters = Snapshot(config); counters.Completed = 1; invalid.Add(counters);
            var oversized = Snapshot(config); oversized.Requests = Enumerable.Range(0, 65).Select(_ => Request(config)).ToList(); invalid.Add(oversized);
            var upstream = Snapshot(config); upstream.Received = upstream.Completed = upstream.ResponsesQueued = 1;
            var row = Request(config); row.UpstreamAttempts = Enumerable.Range(0, 9).Select(_ => new DnsUpstreamDiagnostic
                { Server = "192.0.2.53", Port = 5300, Transport = "UDP", Stage = "udp-validate", Outcome = "response", ResponseCode = 0 }).ToList();
            upstream.Requests = [row]; invalid.Add(upstream);
            var wrongVersion = Snapshot(config); wrongVersion.ServiceVersion = "0.1.20"; invalid.Add(wrongVersion);
            foreach (var snapshot in invalid)
            {
                service.Snapshot = snapshot;
                await Rejected<IOException>(() => backend.ReadRelayDiagnosticsAsync());
                Require(JsonData.Encode(backend.Status) == before && backend.ServiceConnected,
                    "An invalid optional read altered the last confirmed service state.");
            }
            service.Snapshot = Snapshot(config); await backend.RefreshAsync();
            var valid = await backend.ReadRelayDiagnosticsAsync();
            Require(valid.SessionId == service.Snapshot.SessionId && service.Commands.All(r => r.Command is "status" or "relay-diagnostics"),
                "A rejected reply retained the gate or caused an unrelated control/probe request.");
        });

        await Check("canceling an active relay diagnostic read preserves external cancellation and releases the backend gate", async dir =>
        {
            var config = Config(); var service = new Connection(config);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Read = async token => { started.TrySetResult(); await Task.Delay(Timeout.Infinite, token); return Snapshot(config); };
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            using var cancel = new CancellationTokenSource();
            var read = backend.ReadRelayDiagnosticsAsync(cancel.Token);
            await started.Task.WaitAsync(TimeSpan.FromSeconds(1)); cancel.Cancel();
            await Rejected<OperationCanceledException>(() => read);
            service.Read = null; await backend.RefreshAsync(); await backend.ReadRelayDiagnosticsAsync();
            Require(service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "relay-diagnostics", "status", "relay-diagnostics" })
                && backend.ServiceConnected, "Canceling an optional read retained the gate, retried the probe or disconnected the service.");
        });

        await Check("canceling a relay read waiting for the backend gate sends no second RPC and leaves its owner intact", async dir =>
        {
            var config = Config(); var service = new Connection(config);
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<RelayDiagnosticsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously);
            service.Read = token => { started.TrySetResult(); return release.Task.WaitAsync(token); };
            await using var backend = Backend(dir, config, service); await backend.RefreshAsync();
            var first = backend.ReadRelayDiagnosticsAsync(); await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            using var cancel = new CancellationTokenSource(); var waiting = backend.ReadRelayDiagnosticsAsync(cancel.Token); cancel.Cancel();
            await Rejected<OperationCanceledException>(() => waiting);
            Require(!first.IsCompleted && service.Commands.Count(r => r.Command == "relay-diagnostics") == 1,
                "A canceled waiter sent another RPC or canceled the gate's current owner.");
            release.SetResult(Snapshot(config)); await first;
            service.Read = null; await backend.RefreshAsync();
            Require(backend.ServiceConnected && service.Commands.Last().Command == "status", "The canceled waiter damaged subsequent backend access.");
        });

        await Check("relay dialog reads once and exposes only a read-only copy-ready report without touching the clipboard", dir => OnUiAsync(async owner =>
        {
            var config = Config(); var reply = new TaskCompletionSource<RelayDiagnosticsSnapshot>(TaskCreationOptions.RunContinuationsAsynchronously); int reads = 0;
            using var dialog = new RelayDiagnosticsDialog(_ => { reads++; return reply.Task; });
            var text = Find(dialog).OfType<TextBox>().Single(t => t.Name == "RelayDiagnosticsReport");
            var copy = Find(dialog).OfType<Button>().Single(b => b.Name == "CopyRelayDiagnostics");
            Require(reads == 0 && !copy.Enabled, "Constructing a hidden dialog started a read or enabled copying before a report.");
            PlaceOffscreen(dialog); dialog.Show(owner); await Until(() => reads == 1);
            Require(!copy.Enabled, "Copy became available while the read was pending.");
            reply.SetResult(Snapshot(config)); await Until(() => copy.Enabled);
            Require(text.ReadOnly && text.Multiline && text.ScrollBars == ScrollBars.Both && !text.WordWrap
                && Find(dialog).OfType<Button>().Count() == 2 && dialog.CancelButton is Button { DialogResult: DialogResult.Cancel },
                "The observation dialog gained editable data, missing scroll/close support or an unrelated action.");
            Require(text.Text.Contains("завершённые запросы не зафиксированы") && text.Text.Contains("это не доказывает")
                && text.Text.Contains("авторизованный Codex здесь не проверяются") && text.Text.Contains("Имена сайтов и пакеты не записываются"),
                "The empty snapshot claims application health or omits observation/privacy limits.");
            var original = text.Text; dialog.Refresh(); await Task.Delay(30);
            Require(reads == 1 && text.Text == original && copy.Enabled, "Repainting reread diagnostics or changed the completed report.");
            using var bitmap = new Bitmap(dialog.Width, dialog.Height); dialog.DrawToBitmap(bitmap, new Rectangle(Point.Empty, dialog.Size));
            var image = Path.Combine(dir, "relay-diagnostics-readonly.png"); bitmap.Save(image, System.Drawing.Imaging.ImageFormat.Png);
            Console.WriteLine("INFO relay diagnostics readonly preview: " + image);
            dialog.Close();
        }));

        await Check("relay dialog reports a failed or invalid read clearly without enabling Copy or automatic retries", unusedDirectory => OnUiAsync(async owner =>
        {
            foreach (var invalidSnapshot in new[] { false, true })
            {
                int reads = 0;
                using var dialog = new RelayDiagnosticsDialog(_ =>
                {
                    reads++;
                    if (!invalidSnapshot) return Task.FromException<RelayDiagnosticsSnapshot>(new IOException("Synthetic relay read failed."));
                    var value = Snapshot(Config()); value.SessionId = "invalid"; return Task.FromResult(value);
                });
                PlaceOffscreen(dialog); dialog.Show(owner);
                var text = Find(dialog).OfType<TextBox>().Single(t => t.Name == "RelayDiagnosticsReport");
                var copy = Find(dialog).OfType<Button>().Single(b => b.Name == "CopyRelayDiagnostics");
                await Until(() => text.Text.StartsWith("Диагностика не получена.", StringComparison.Ordinal));
                dialog.Refresh(); await Task.Delay(25);
                Require(reads == 1 && !copy.Enabled && text.ReadOnly
                    && text.Text.Contains(invalidSnapshot ? "Некорректный" : "Synthetic relay read failed."),
                    "The failed read was retried, treated as valid or left an unusable error state.");
                dialog.Close();
            }
        }));

        await Check("closing a pending relay dialog cancels its read without retrying or waiting for a result", unusedDirectory => OnUiAsync(async owner =>
        {
            var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var canceled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously); int reads = 0;
            using var dialog = new RelayDiagnosticsDialog(async token =>
            {
                reads++; started.TrySetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { canceled.TrySetResult(token.IsCancellationRequested); }
                return Snapshot(Config());
            });
            PlaceOffscreen(dialog); dialog.Show(owner); await started.Task.WaitAsync(TimeSpan.FromSeconds(1));
            Require(!Find(dialog).OfType<Button>().Single(b => b.Name == "CopyRelayDiagnostics").Enabled,
                "The pending report was copy-ready before its read completed.");
            dialog.Close();
            Require(await canceled.Task.WaitAsync(TimeSpan.FromSeconds(1)) && reads == 1 && dialog.IsDisposed,
                "Closing did not cancel/dispose the pending read or triggered another request.");
        }));

        await Check("the main relay diagnostic button opens one explicit read-only service report with no probe or configuration commands", dir => OnUiAsync(async owner =>
        {
            var config = Config(); var service = new Connection(config);
            var backend = Backend(dir, config, service); await backend.RefreshAsync();
            var network = new NoNetwork(); var sharing = new NoNetwork();
            using var form = new MainForm(backend, new DnsShareClient(sharing), new ApplicationUpdateClient(network),
                new ApplicationUpdateLauncher(Path.Combine(dir, "updates")));
            typeof(MainForm).GetField("_smoke", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(form, true);
            PlaceOffscreen(form); form.Show(owner);
            typeof(MainForm).GetMethod("UpdateView", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(form, null);
            var button = Find(form).OfType<Button>().Single(b => b.Name == "RelayDiagnostics");
            Require(button.Enabled && service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status" }),
                "Rendering the main window automatically read relay data or started unrelated actions.");
            var saved = File.ReadAllText(Path.Combine(dir, "ui-config.json"));
            Exception? failure = null; bool observed = false;
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var observer = new System.Windows.Forms.Timer { Interval = 15 };
            observer.Tick += (_, _) =>
            {
                var dialog = Application.OpenForms.OfType<RelayDiagnosticsDialog>().FirstOrDefault();
                if (dialog is null) return;
                bool finished = false;
                try
                {
                    var copy = Find(dialog).OfType<Button>().Single(b => b.Name == "CopyRelayDiagnostics");
                    if (!copy.Enabled && !deadline.IsCancellationRequested) return;
                    finished = true;
                    Require(copy.Enabled && Find(dialog).OfType<TextBox>().Single(t => t.Name == "RelayDiagnosticsReport").ReadOnly,
                        "The main button opened no completed read-only report.");
                    Require(service.Commands.Select(r => r.Command).SequenceEqual(new[] { "status", "relay-diagnostics" }),
                        "The main button read twice or sent probe/configuration commands.");
                    observed = true;
                }
                catch (Exception ex) { failure = ex; finished = true; }
                finally { if (finished) { observer.Stop(); dialog.Close(); } }
            };
            observer.Start(); button.PerformClick(); observer.Stop();
            if (failure is not null) throw failure;
            Require(observed && network.Requests == 0 && sharing.Requests == 0
                && File.ReadAllText(Path.Combine(dir, "ui-config.json")) == saved,
                "An explicit relay observation failed to open, used external HTTP or changed saved settings.");
            form.Dispose();
        }));
    }

    private static AppConfig Config()
    {
        var config = AppConfig.Defaults(); config.MonitoringPaused = true; config.ManagementRequested = false;
        config.Profiles = [new ChannelProfile { Id = "11111111222233334444555555555555", Name = "Synthetic channel", Mode = DnsMode.Udp, Servers = ["192.0.2.53"], DnsPort = 5300 }];
        config.AdapterId = "11111111-2222-3333-4444-555555555555"; config.Validate(); return config;
    }

    private static UiBackend Backend(string directory, AppConfig config, Connection service)
    { AtomicStore.Write(Path.Combine(directory, "ui-config.json"), config); return new UiBackend(directory, service); }

    private static RelayDiagnosticsSnapshot Snapshot(AppConfig config) => new()
    {
        ServiceVersion = AppVersion.Current, SessionId = "abcdef0123456789abcdef0123456789", CapturedAt = DateTimeOffset.UtcNow,
        ServiceMode = true, RelayRunning = false, SystemDnsManaged = false, ActiveProfileId = config.Profiles[0].Id,
        Received = 0, Completed = 0, ResponsesQueued = 0, NegativeOrError = 0, InFlight = 0, RetentionSeconds = 300, Requests = []
    };

    private static RelayRequestDiagnostic Request(AppConfig config) => new()
    {
        RequestId = 1, At = DateTimeOffset.UtcNow.AddMilliseconds(-10), CompletedAt = DateTimeOffset.UtcNow,
        TransactionId = 7, QueryType = 1, QueryClass = 1, ClientTransport = "UDP", LocalFamily = "IPv4",
        ProfileId = config.Profiles[0].Id, ProfileContext = RelayDiagnostics.Context(config.Profiles[0]),
        ReturnedCode = 0, Stage = "local-send", Outcome = "response-queued", ResponseQueued = true, ElapsedMs = 10
    };

    private static async Task<T> Rejected<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T ex) { return ex; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }

    private static async Task Until(Func<bool> ready)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        while (!ready()) await Task.Delay(10, timeout.Token);
    }

    private static Task OnUiAsync(Func<Form, Task> test)
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            Exception? failure = null;
            try
            {
                using var host = new Form { Size = new Size(1, 1), Opacity = 0 };
                PlaceOffscreen(host);
                host.Shown += async (_, _) =>
                {
                    try { await test(host); }
                    catch (Exception ex) { failure = ex; }
                    finally { host.Close(); }
                };
                Application.Run(host);
                if (failure is null) complete.TrySetResult(); else complete.TrySetException(failure);
            }
            catch (Exception ex) { complete.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static void PlaceOffscreen(Form form)
    { form.ShowInTaskbar = false; form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000); }
    private static IEnumerable<Control> Find(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Find(child)) yield return nested; } }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("External HTTP is forbidden in relay diagnostic UI tests."); }
    }

    private sealed class Connection : IUiServiceConnection
    {
        private readonly AppConfig _config;
        public Connection(AppConfig config) { _config = JsonData.Clone(config); Snapshot = RelayDiagnosticsUiTests.Snapshot(config); }
        public bool Installed { get; set; } = true;
        public bool IsInstalled => Installed;
        public bool Offline { get; set; }
        public string Version { get; set; } = AppVersion.Current;
        public RelayDiagnosticsSnapshot? Snapshot { get; set; }
        public AppStatus? AttachedStatus { get; set; }
        public Func<CancellationToken, Task<RelayDiagnosticsSnapshot>>? Read { get; set; }
        public List<RpcRequest> Commands { get; } = [];
        public async Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token)
        {
            Commands.Add(JsonData.Clone(request));
            if (Offline) throw new TimeoutException("Synthetic disconnected service.");
            if (request.Command == "relay-diagnostics")
                return new RpcReply { Ok = true, RelayDiagnostics = Read is null ? Snapshot : await Read(token), Status = AttachedStatus };
            if (request.Command != "status") throw new InvalidOperationException("Unexpected control command: " + request.Command);
            return new RpcReply { Ok = true, Status = new AppStatus
            {
                Config = JsonData.Clone(_config), ServiceVersion = Version, ServiceConnected = true, Monitoring = false,
                Adapters = [new AdapterInfo { Id = _config.AdapterId!, Name = "Synthetic adapter", Up = true, HasGateway = true }]
            }};
        }
        public Task InstallAsync() => throw new InvalidOperationException("No service installation in relay UI tests.");
        public Task RecoverAsync() => throw new InvalidOperationException("No DNS recovery in relay UI tests.");
    }
}
