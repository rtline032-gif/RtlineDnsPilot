using System.Reflection;

namespace NetPilot.Tests;

public static class ChannelTableOrderTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        void Check(string name, Action check)
        {
            try { check(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.GetBaseException().Message); }
        }
        var now = DateTimeOffset.UtcNow;
        Check("channel table ranks fresh working channels by reliability then latency with stable ties", () =>
        {
            var config = Config(11); config.AutoSelection = AutoSelectionMode.Fast;
            var profiles = config.Profiles; profiles[4].Enabled = false;
            var stats = new List<ProfileStats>
            {
                Stats(config, 0, now, 10, true, false),
                Stats(config, 1, now, 80, false, true),
                Stats(config, 3, now, 120, true),
                Stats(config, 4, now, 1, true),
                Stats(config, 5, now, 40, true),
                Stats(config, 6, now, 40, true),
                Stats(config, 7, now.AddSeconds(-MonitoringTargets.FreshnessSeconds(config)-1), 1, true),
                Stats(config, 8, now.AddSeconds(1), 1, true),
                Stats(config, 9, now, 1, true),
                Stats(config, 10, now, 20, false, true)
            };
            stats.Single(s => s.ProfileId == profiles[9].Id).Last!.MonitoringKey = "another-context";
            var before = JsonData.Encode(new AppStatus { Config = config, Statistics = stats });
            var rows = ChannelTableOrder.Build(config, stats, now);
            Require(rows.Select(r => r.Profile.Id).SequenceEqual(new[] { 5, 6, 3, 10, 1, 0, 2, 4, 7, 8, 9 }.Select(i => profiles[i].Id)),
                "Working, tied or nonworking rows lost their required order.");
            Require(rows.All(r => ReferenceEquals(r.Profile, profiles.Single(p => p.Id == r.Profile.Id))),
                "The table changed profile identities.");
            Require(JsonData.Encode(new AppStatus { Config = config, Statistics = stats }) == before,
                "Building a table view changed configuration, sample order, counters or history.");
        });
        Check("channel table uses successful-check share only from the current monitoring context", () =>
        {
            var config = Config(2);
            var a = Stats(config, 0, now, 10, false, true);
            var b = Stats(config, 1, now, 100, true);
            for (int i = 0; i < 8; i++)
            {
                a.Samples.Insert(0, new ProbeResult { Success = true, At = now, MonitoringKey = "another-context" });
                b.Samples.Insert(0, new ProbeResult { Success = false, At = now, MonitoringKey = "another-context" });
            }
            var rows = ChannelTableOrder.Build(config, [a, b], now);
            Require(rows[0].Profile.Id == config.Profiles[1].Id && rows[0].Statistics!.SuccessPercent == 100
                && rows[1].Statistics!.SuccessPercent == 50,
                "Evidence from another target set influenced the displayed ranking.");
            config.MonitorGemini = true;
            Require(ChannelTableOrder.Build(config, [a, b], now).All(row => row.Statistics!.Last is null),
                "Changing monitored targets retained working evidence from the previous context.");
        });
        Check("channel table freshness includes the bounded full round and rejects future or expired evidence", () =>
        {
            var config = Config(3); config.IntervalSeconds = 10; config.TimeoutSeconds = 30;
            config.MonitorGemini = config.MonitorTailscale = true;
            var freshness = MonitoringTargets.FreshnessSeconds(config);
            Require(freshness > 60, "The fixture did not exercise full-round freshness.");
            var stats = new[] { Stats(config, 0, now.AddSeconds(-freshness-1), 1, true),
                Stats(config, 1, now.AddSeconds(-freshness), 2, true), Stats(config, 2, now.AddTicks(1), 1, true) };
            Require(ChannelTableOrder.Build(config, stats, now).Select(r => r.Profile.Id)
                .SequenceEqual(new[] { config.Profiles[1].Id, config.Profiles[0].Id, config.Profiles[2].Id }),
                "Freshness boundaries or the stable order of nonworking rows are wrong.");
        });
        try { await CheckUiAsync(); report("channel rows preserve selection and show the last check even after it ages", true, ""); }
        catch (Exception ex) { report("channel rows preserve selection and show the last check even after it ages", false, ex.GetBaseException().Message); }
    }

    private static AppConfig Config(int count)
    {
        var config = AppConfig.Defaults();
        config.Profiles = Enumerable.Range(0, count).Select(i => new ChannelProfile
            { Name = "Channel " + i, Servers = ["1.1.1.1"], Enabled = true }).ToList();
        return config;
    }

    private static ProfileStats Stats(AppConfig config, int index, DateTimeOffset at, double latency, params bool[] successes)
    {
        var stats = new ProfileStats { ProfileId = config.Profiles[index].Id };
        foreach (var success in successes)
            stats.Add(new ProbeResult { ProfileId = stats.ProfileId, MonitoringKey = MonitoringTargets.Key(config), At = at,
                Success = success, HttpsMs = latency, Stage = success ? "http" : "dns", HttpStatus = success ? 200 : null });
        return stats;
    }

    private static Task CheckUiAsync()
    {
        var complete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var root = Path.Combine(AppContext.BaseDirectory, "test-data", "channel-table-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config = AppConfig.Defaults(); config.MonitoringPaused = true;
                config.Profiles[0].Enabled = true;
                AtomicStore.Write(Path.Combine(root, "ui-config.json"), config);
                var now = DateTimeOffset.UtcNow;
                var service = new UiPersistenceTests.FakeService { State = new AppStatus { Config = config, ServiceVersion = AppVersion.Current,
                    ActiveProfileId = config.Profiles[0].Id,
                    Statistics = [Stats(config, 0, now, 10, true, false), Stats(config, 1, now, 80, true), Stats(config, 2, now, 20, false, true)] } };
                var backend = new UiBackend(root, service);
                using var form = new MainForm(backend, new DnsShareClient(), new ApplicationUpdateClient(new NoNetwork()),
                    new ApplicationUpdateLauncher(Path.Combine(root, "updates")));
                form.ShowInTaskbar = false; form.Opacity = 0;
                form.Shown += async (_, _) =>
                {
                    try
                    {
                        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
                        var busy = typeof(MainForm).GetField("_busy", flags)!;
                        async Task Idle()
                        {
                            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            while ((bool)busy.GetValue(form)!) await Task.Delay(10, deadline.Token);
                        }
                        await Idle();
                        ((System.Windows.Forms.Timer)typeof(MainForm).GetField("_timer", flags)!.GetValue(form)!).Stop();
                        var grid = (DataGridView)typeof(MainForm).GetField("_grid", flags)!.GetValue(form)!;
                        var ids = config.Profiles.Select(p => p.Id).ToArray();
                        DataGridViewRow Row(string id) => grid.Rows.Cast<DataGridViewRow>().Single(r => Equals(r.Tag, id));
                        string[] Order() => grid.Rows.Cast<DataGridViewRow>().Select(r => (string)r.Tag!).ToArray();
                        void Update() => typeof(MainForm).GetMethod("UpdateView", flags)!.Invoke(form, null);
                        Require(Order().SequenceEqual(new[] { ids[1], ids[2], ids[0] }), "The UI did not render the table ranking.");
                        Require(Row(ids[0]).DefaultCellStyle.BackColor == Color.FromArgb(220, 235, 253)
                            && Row(ids[0]).Cells["result"].Style.ForeColor == Color.FromArgb(169, 40, 31),
                            "A failed active channel lost its blue style or failure indicator.");
                        grid.CurrentCell = Row(ids[2]).Cells["enabled"];
                        Require(form.PrepareChannelMenu(Row(ids[2]).Index), "The sorted channel menu was unavailable.");
                        grid.CurrentCell = Row(ids[2]).Cells["enabled"];
                        var configuration = JsonData.Encode(backend.Status.Config);
                        service.State.Statistics.Single(s => s.ProfileId == ids[1]).Add(new ProbeResult
                            { ProfileId = ids[1], MonitoringKey = MonitoringTargets.Key(config), At = now, Success = false });
                        await backend.RefreshAsync();
                        var history = JsonData.Encode(backend.Status.Statistics);
                        Update(); Update();
                        Require(Order().SequenceEqual(new[] { ids[2], ids[0], ids[1] }), "New evidence did not reorder only the view.");
                        Require(grid.SelectedRows.Count == 1 && Equals(grid.SelectedRows[0].Tag, ids[2])
                            && Equals(grid.CurrentCell!.OwningRow!.Tag, ids[2]) && grid.CurrentCell.OwningColumn!.Name == "enabled",
                            "A refresh moved the selection or keyboard focus onto another channel.");
                        Require((string?)typeof(MainForm).GetField("_contextProfileId", flags)!.GetValue(form) == ids[2]
                            && ((ChannelProfile?)typeof(MainForm).GetMethod("SelectedProfile", flags)!.Invoke(form, null))?.Id == ids[2],
                            "Selection or an already-open context menu was retargeted by sorting.");
                        Require(JsonData.Encode(backend.Status.Config) == configuration && JsonData.Encode(backend.Status.Statistics) == history
                            && service.ConfigurationCalls == 0 && grid.Rows.Cast<DataGridViewRow>().All(r => (bool)r.Cells["enabled"].Value!),
                            "Rendering changed checkboxes, configuration or history.");
                        var key = new KeyEventArgs(Keys.Space);
                        typeof(Control).GetMethod("OnKeyDown", flags)!.Invoke(grid, [key]);
                        await Idle();
                        Require(key.Handled && service.ConfigurationCalls == 1 && !service.State.Config.Profiles[2].Enabled
                            && service.State.Config.Profiles.Take(2).All(p => p.Enabled), "Space toggled a different channel after sorting.");
                        var row = Row(ids[2]);
                        typeof(DataGridView).GetMethod("OnCellClick", flags)!.Invoke(grid,
                            [new DataGridViewCellEventArgs(grid.Columns["enabled"]!.Index, row.Index)]);
                        await Idle();
                        Require(service.ConfigurationCalls == 2 && service.State.Config.Profiles.All(p => p.Enabled)
                            && Order()[0] == ids[2], "Clicking a sorted checkbox lost its profile binding.");
                        var saved = AtomicStore.Read<AppConfig>(Path.Combine(root, "ui-config.json"))!;
                        Require(saved.Profiles.Select(p => p.Id).SequenceEqual(ids)
                            && JsonData.Encode(backend.Status.Statistics) == history,
                            "A sorted action persisted the display order or changed historical evidence.");

                        var previous = now.AddSeconds(-MonitoringTargets.FreshnessSeconds(config) - 5);
                        service.State.Statistics = [Stats(config, 1, previous, 80, false), Stats(config, 2, previous, 20, true)];
                        await backend.RefreshAsync();
                        Update();
                        var previousTime = previous.ToLocalTime().ToString("HH:mm:ss");
                        foreach (var column in new[] { "result", "service_chatgpt" })
                        {
                            Require(Equals(Row(ids[1]).Cells[column].Value, previousTime + " · Был недоступен · dns"),
                                "An older failure lost its time or outcome in " + column + ".");
                            Require(Equals(Row(ids[2]).Cells[column].Value, previousTime + " · Был доступен · HTTP 200"),
                                "An older success lost its time or outcome in " + column + ".");
                            Require(Equals(Row(ids[0]).Cells[column].Value, "Нет измерений"),
                                "A channel with no saved check displayed an invented result.");
                        }
                        Require(Row(ids[1]).DefaultCellStyle.BackColor == Color.FromArgb(247, 249, 251)
                            && Row(ids[2]).DefaultCellStyle.BackColor == Color.FromArgb(247, 249, 251),
                            "Old measurements were colored as current availability.");
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
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }

    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            => throw new InvalidOperationException("External HTTP is forbidden in the channel table test.");
    }
}
