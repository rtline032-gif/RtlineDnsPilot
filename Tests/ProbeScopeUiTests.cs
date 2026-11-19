using System.Reflection;

namespace NetPilot.Tests;

public static class ProbeScopeUiTests
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;
    private const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic;

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }

        await Check("public HTTPS caption is selected by built-in identity without renaming custom targets", () =>
        {
            var caption = typeof(MainForm).GetMethod("ProbeCaption", Static)!;
            var builtIn = new MonitoringTarget(MonitoringTargets.ChatGpt, "Stored legacy name", MonitoringTargets.DefaultChatGptUrl);
            var custom = new MonitoringTarget("11111111222233334444555555555555", "ChatGPT / Codex", "https://example.org/");
            Require((string)caption.Invoke(null, [builtIn])! == "HTTPS ChatGPT", "The built-in caption still implies authenticated Codex health.");
            Require((string)caption.Invoke(null, [custom])! == custom.Name, "A custom target was renamed by text matching.");
            Require(builtIn.Name == "Stored legacy name" && custom.Name == "ChatGPT / Codex"
                && MonitoringTargets.Name(MonitoringTargets.ChatGpt) == "ChatGPT / Codex",
                "A presentation change rewrote the persisted/internal target name.");
            return Task.CompletedTask;
        });

        await Check("monitoring UI describes public HTTPS consistently while preserving targets and saved context", InspectUiAsync);

        await Check("automatic DNS confirmation explains disabled service and exact restoration alternative without executing it", () =>
        {
            var adapter = new AdapterInfo { Id = "11111111-2222-3333-4444-555555555555", Name = "Synthetic adapter" };
            var text = (string)typeof(MainForm).GetMethod("AutomaticDnsConfirmation", Static)!.Invoke(null, [adapter])!;
            Require(text.Contains(adapter.Id) && text.Contains(adapter.Name), "The confirmation lost the concrete selected adapter.");
            Require(text.Contains("служба RtlineDnsPilot DNS установлена") && text.Contains("остановлена")
                && text.Contains("тип запуска станет «Отключена»"), "The installed service's stopped/Disabled outcome is not disclosed.");
            Require(text.Contains("возвратом исходных DNS") && text.Contains("«Отключить управление»")
                && text.Contains("не восстанавливает прежние ручные DNS"), "DHCP was confused with restoring the original DNS.");
            Require(text.Contains("IPv4 и включённого IPv6") && text.Contains("IP-адрес, шлюз, VPN и прокси не меняются")
                && text.Contains("Для обычных проверок права администратора не нужны"), "The bounded network scope or privilege explanation disappeared.");
            return Task.CompletedTask;
        });
    }

    private static Task InspectUiAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "probe-scope-ui-" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(directory);
                var config = AppConfig.Defaults(); config.MonitoringPaused = true;
                config.Profiles = [config.Profiles[0]]; config.Profiles[0].Enabled = true;
                config.MonitorGemini = true;
                config.AdditionalTargets = [new MonitoringEndpoint
                {
                    Id = "11111111222233334444555555555555", Name = "Мой Codex", Url = "https://example.org/", Enabled = true
                }];
                config.Validate();
                var key = MonitoringTargets.Key(config);
                var targets = JsonData.Encode(MonitoringTargets.Selected(config));
                var path = Path.Combine(directory, "ui-config.json"); AtomicStore.Write(path, config);
                var original = File.ReadAllText(path);
                var service = new Connection(config); var backend = new UiBackend(directory, service);
                backend.RefreshAsync().GetAwaiter().GetResult();
                var network = new NoNetwork(); var sharing = new NoNetwork();
                using var form = new MainForm(backend, new DnsShareClient(sharing), new ApplicationUpdateClient(network),
                    new ApplicationUpdateLauncher(Path.Combine(directory, "updates")));
                form.ShowInTaskbar = false;
                form.StartPosition = FormStartPosition.Manual; form.Location = new Point(-20000, -20000);
                var preview = Path.Combine(directory, "public-https-channels.png");
                form.RenderSmoke(preview);
                var grid = Field<DataGridView>(form, "_grid");
                var check = Field<CheckBox>(form, "_monitorChatGpt");
                var tips = Field<ToolTip>(form, "_tips");
                var customId = config.AdditionalTargets[0].Id;
                Require(check.Text == "HTTPS ChatGPT" && grid.Columns["service_chatgpt"]!.HeaderText == check.Text,
                    "The checkbox and channel column disagree about public HTTPS scope.");
                var help = tips.GetToolTip(check) ?? "";
                Require(help.Contains("Анонимная публичная HTTPS") && help.Contains("разрешением имени через канал")
                    && help.Contains("Codex") && help.Contains("WebSocket не проверяются")
                    && grid.Columns["service_chatgpt"]!.ToolTipText == help,
                    "The tooltip still claims application/session health or omits its scope.");
                Require(grid.Columns["service_" + customId]!.HeaderText == "Мой Codex"
                    && grid.Columns["service_" + customId]!.ToolTipText == string.Empty
                    && grid.Columns["service_gemini"]!.HeaderText == "Gemini", "Another target was relabeled as the built-in check.");
                Require(grid.Rows[0].Cells["service_chatgpt"].ToolTipText.StartsWith("HTTPS ChatGPT\r\n", StringComparison.Ordinal)
                    && grid.Rows[0].Cells["service_" + customId].ToolTipText.StartsWith("Мой Codex\r\n", StringComparison.Ordinal),
                    "Result tooltips retained the misleading caption or renamed the custom target.");
                var countries = Field<Label>(form, "_countries");
                Require(countries.Text.Contains("HTTPS ChatGPT:") && countries.Text.Contains("Мой Codex:")
                    && !countries.Text.Contains("ChatGPT / Codex:") && (tips.GetToolTip(countries) ?? "").Contains("HTTPS ChatGPT"),
                    "The country summary disagrees with the target's displayed scope.");
                Require(Field<Label>(form, "_targetsHint").Text.Contains("HTTPS ChatGPT, Gemini, Мой Codex"),
                    "The applied-target hint kept the old caption.");
                var tabs = Field<TabControl>(form, "_tabs"); tabs.SelectedIndex = 1;
                Require(Find(form).OfType<Label>().Any(l => l.Text == "HTTPS ChatGPT")
                    && !Find(form).OfType<Label>().Any(l => l.Text == "HTTPS ChatGPT / Codex"),
                    "The editable built-in URL still promises a Codex check.");
                form.RenderSmoke(Path.Combine(directory, "public-https-targets.png"));
                var dhcp = tips.GetToolTip(Field<Button>(form, "_automaticDns")) ?? "";
                Require(dhcp.Contains("служба будет остановлена и отключена") && dhcp.Contains("«Отключить управление»")
                    && (tips.GetToolTip(Field<Button>(form, "_disable")) ?? "").Contains("вернуть исходные DNS"),
                    "The two DNS actions have no distinct, explained outcomes.");
                Require(MonitoringTargets.Key(backend.Status.Config) == key && JsonData.Encode(MonitoringTargets.Selected(backend.Status.Config)) == targets
                    && File.ReadAllText(path) == original, "Rendering changed target identity, URL, names, monitoring key or saved settings.");
                Require(service.Commands.SequenceEqual(new[] { "status" }) && network.Requests == 0 && sharing.Requests == 0,
                    "Inspecting UI sent controls, probes or external HTTP.");
                Console.WriteLine("INFO public HTTPS UI previews: " + preview + " ; " + Path.Combine(directory, "public-https-targets.png"));
                complete.TrySetResult();
            }
            catch (Exception ex) { complete.TrySetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(12));
    }

    private static T Field<T>(MainForm form, string name) => (T)typeof(MainForm).GetField(name, Instance)!.GetValue(form)!;
    private static IEnumerable<Control> Find(Control parent)
    { foreach (Control child in parent.Controls) { yield return child; foreach (var nested in Find(child)) yield return nested; } }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }

    private sealed class NoNetwork : HttpMessageHandler
    {
        public int Requests { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        { Requests++; throw new InvalidOperationException("External HTTP is forbidden in public HTTPS UI tests."); }
    }

    private sealed class Connection : IUiServiceConnection
    {
        private readonly AppConfig _config;
        public Connection(AppConfig config) => _config = JsonData.Clone(config);
        public bool IsInstalled => true;
        public List<string> Commands { get; } = [];
        public Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token)
        {
            Commands.Add(request.Command);
            if (request.Command != "status") throw new InvalidOperationException("Unexpected control: " + request.Command);
            var now = DateTimeOffset.UtcNow;
            var services = MonitoringTargets.Selected(_config).Select(t => new ServiceProbeResult
            {
                ServiceId = t.Id, At = now, Success = true, DnsMs = 10, HttpsMs = 40, HttpStatus = 200, Stage = "http",
                Detail = "Synthetic public HTTPS result", CountryCode = t.Id == MonitoringTargets.ChatGpt ? "DE" : null,
                CountrySource = t.Id == MonitoringTargets.ChatGpt ? ConnectionCountrySource.SiteTrace : ConnectionCountrySource.None
            }).ToArray();
            var stats = new ProfileStats { ProfileId = _config.Profiles[0].Id };
            stats.Add(MonitoringTargets.Combine(stats.ProfileId, _config, services));
            return Task.FromResult(new RpcReply { Ok = true, Status = new AppStatus
            {
                Config = JsonData.Clone(_config), ServiceConnected = true, ServiceVersion = AppVersion.Current,
                Monitoring = false, ActiveProfileId = stats.ProfileId, Statistics = [stats],
                Adapters = [new AdapterInfo { Id = "11111111-2222-3333-4444-555555555555", Name = "Synthetic adapter", Up = true, HasGateway = true }]
            }});
        }
        public Task InstallAsync() => throw new InvalidOperationException("No service installation in UI scope tests.");
        public Task RecoverAsync() => throw new InvalidOperationException("No DNS recovery in UI scope tests.");
    }
}
