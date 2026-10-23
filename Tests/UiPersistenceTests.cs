namespace NetPilot.Tests;

public static class UiPersistenceTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<string, Task> test)
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "ui-sync-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            try { await test(directory); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.ToString()); }
        }
        static void Require(bool condition, string message) { if (!condition) throw new Exception(message); }
        static AppConfig Defaults() { var config = AppConfig.Defaults(); foreach (var profile in config.Profiles) profile.Enabled = false; return config; }
        static ChannelProfile Custom() => new() { Name = "Comss.one DNS", Enabled = false, Mode = DnsMode.Doh,
            Servers = ["83.220.169.155", "212.109.195.93"], DohUrl = "https://dns.comss.one/dns-query" };

        await Check("service attachment preserves a legacy local fourth channel", async dir =>
        {
            var local = Defaults(); local.Profiles.Add(Custom());
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            var service = new FakeService { State = new AppStatus { Config = Defaults() } };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            Require(backend.Status.Config.Profiles.Count == 4 && backend.Status.Config.Profiles.Any(p => p.Name == "Comss.one DNS"), "Local channel disappeared on first service connection.");
            Require(backend.PendingServiceConfiguration && File.Exists(Path.Combine(dir, "service-config.last-seen.json")), "Difference was not visible and backed up.");
            Require(backend.Status.Config.Profiles.Take(3).Select(p => p.Id).SequenceEqual(service.State.Config.Profiles.Select(p => p.Id)), "Service statistics would lose their channel identities.");
            await backend.RefreshAsync();
            Require(backend.Status.Config.Profiles.Count == 4, "Repeated status overwrote the saved channel.");
            Require(AtomicStore.Read<AppConfig>(Path.Combine(dir, "ui-config.json"))!.Profiles.Count == 4, "Four-channel database was not persisted.");
            Require(service.ConfigurationCalls == 0, "A status refresh reconfigured the service.");
        });

        await Check("a failed configure reply cannot erase a newly saved channel after reopening", async dir =>
        {
            var config = Defaults(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new FakeService { State = new AppStatus { Config = config }, FailConfigure = true };
            await using (var backend = new UiBackend(dir, service))
            {
                await backend.RefreshAsync();
                var edited = JsonData.Clone(backend.Status.Config); edited.Profiles.Add(Custom());
                try { await backend.ConfigureAsync(edited); } catch (IOException) { }
            }
            await using var reopened = new UiBackend(dir, service);
            await reopened.RefreshAsync();
            Require(reopened.Status.Config.Profiles.Count == 4, "Unacknowledged channel was lost after reopening.");
            Require(service.ConfigurationCalls == 1, "Failed mutation was automatically replayed.");
        });

        await Check("a lost configure acknowledgement is confirmed by status without replaying the mutation", async dir =>
        {
            var config = Defaults(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new FakeService { State = new AppStatus { Config = config }, FailConfigure = true, CommitBeforeFailure = true };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            var edited = JsonData.Clone(backend.Status.Config); edited.Profiles.Add(Custom());
            try { await backend.ConfigureAsync(edited); } catch (IOException) { }
            await backend.RefreshAsync();
            Require(backend.Status.Config.Profiles.Count == 4 && AtomicStore.Read<AppConfig>(Path.Combine(dir, "ui-config.json"))!.Profiles.Count == 4, "Committed configuration was not recovered from status.");
            Require(!backend.PendingServiceConfiguration && !File.Exists(Path.Combine(dir, "ui-config.pending.json")), "Read-only confirmation did not clear the pending marker.");
            Require(service.ConfigurationCalls == 1, "Mutation was replayed instead of checked.");
        });

        await Check("an explicit pending deletion survives a restart against the old service list", async dir =>
        {
            var config = Defaults(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new FakeService { State = new AppStatus { Config = config }, FailConfigure = true };
            string deleted = config.Profiles[2].Id;
            await using (var backend = new UiBackend(dir, service))
            {
                await backend.RefreshAsync();
                var edited = JsonData.Clone(backend.Status.Config); edited.Profiles.RemoveAt(2);
                try { await backend.ConfigureAsync(edited); } catch (IOException) { }
            }
            await using var reopened = new UiBackend(dir, service);
            await reopened.RefreshAsync();
            Require(reopened.Status.Config.Profiles.Count == 2 && !reopened.Status.Config.Profiles.Any(p => p.Id == deleted), "A pending explicit deletion was silently undone.");
        });

        await Check("an applied channel edit is persisted and survives a new backend instance", async dir =>
        {
            var config = Defaults(); AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new FakeService { State = new AppStatus { Config = config } };
            await using (var backend = new UiBackend(dir, service))
            {
                await backend.RefreshAsync();
                var edited = JsonData.Clone(backend.Status.Config); edited.Profiles.Add(Custom());
                await backend.ConfigureAsync(edited);
                Require(!backend.PendingServiceConfiguration, "An acknowledged edit remained pending.");
            }
            await using var reopened = new UiBackend(dir, service);
            await reopened.RefreshAsync();
            Require(reopened.Status.Config.Profiles.Count == 4 && service.ConfigurationCalls == 1, "Committed edit disappeared or was replayed.");
        });

        await Check("pending endpoint changes cannot inherit old availability or active-channel evidence", async dir =>
        {
            var config = Defaults(); config.Profiles[0].Enabled = true;
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var id = config.Profiles[0].Id;
            var service = new FakeService { FailConfigure = true, State = new AppStatus { Config = config, ActiveProfileId = id, SystemDnsManaged = true,
                Statistics = [new ProfileStats { ProfileId = id, Samples = [new ProbeResult { ProfileId = id, Success = true, HttpsMs = 50 }] }] } };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            var edited = JsonData.Clone(backend.Status.Config); edited.Profiles[0].Servers = ["83.220.169.155"]; edited.Profiles[0].DohUrl = "https://dns.comss.one/dns-query";
            try { await backend.ConfigureAsync(edited); } catch (IOException) { }
            await backend.RefreshAsync();
            Require(backend.PendingServiceConfiguration && backend.Status.Statistics.Count == 0 && backend.Status.ActiveProfileId is null, "Old endpoint evidence was attached to an unapplied endpoint.");
            Require(backend.Status.SystemDnsManaged, "The native service state was incorrectly hidden by the draft.");
        });

        await Check("corrupt pending state does not hide a valid four-channel local database", async dir =>
        {
            var local = Defaults(); local.Profiles.Add(Custom());
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            File.WriteAllText(Path.Combine(dir, "ui-config.pending.json"), "{");
            await using var backend = new UiBackend(dir, new FakeService());
            Require(backend.Status.Config.Profiles.Count == 4 && backend.ConfigurationWarning!.Contains("Основная локальная база сохранена"), "A corrupt pending file replaced valid profiles with defaults.");
            Require(File.ReadAllText(Path.Combine(dir, "ui-config.pending.json")) == "{", "The unreadable source was overwritten before user action.");
        });

        await Check("conflicting local and service names retain both database snapshots", async dir =>
        {
            var local = Defaults(); var serviceConfig = JsonData.Clone(local);
            var custom = Custom(); custom.Name = "Независимый канал"; local.Profiles.Add(custom);
            var conflict = JsonData.Clone(custom); conflict.Id = Guid.NewGuid().ToString("N"); conflict.Servers = ["194.87.196.13"]; serviceConfig.Profiles.Add(conflict);
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            var service = new FakeService { State = new AppStatus { Config = serviceConfig } };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            Require(backend.PendingServiceConfiguration && backend.Status.Config.Profiles.Last().Servers[0] == "83.220.169.155", "A name conflict overwrote the local channel.");
            Require(AtomicStore.Read<AppConfig>(Path.Combine(dir, "service-config.last-seen.json"))!.Profiles.Last().Servers[0] == "194.87.196.13", "The conflicting service variant was not preserved.");
            Require(service.ConfigurationCalls == 0 && backend.ConfigurationWarning!.Contains("конфликтуют"), "A conflict was silently applied.");
        });

        await Check("enable updates an older service without dropping the staged fourth channel", async dir =>
        {
            var local = Defaults(); local.Profiles.Add(Custom()); local.Profiles.Last().Enabled = true;
            local.AdapterId = Guid.NewGuid().ToString("D");
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            var service = new FakeService { State = new AppStatus { Config = Defaults() } };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            await backend.EnableManagementAsync(backend.Status.Config);
            Require(service.InstallCalls == 1 && service.ConfigurationCalls == 1, "The old service was not upgraded exactly once.");
            Require(service.State.Config.Profiles.Count == 4 && backend.Status.Config.Profiles.Count == 4 && service.Commands.Last() == "enable", "Enable sent the initial service defaults instead of the user's database.");
        });

        await Check("disable does not apply pending profile edits", async dir =>
        {
            var local = Defaults(); local.Profiles.Add(Custom());
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            var service = new FakeService { State = new AppStatus { Config = Defaults() } };
            await using var backend = new UiBackend(dir, service);
            await backend.RefreshAsync();
            await backend.RestoreAsync();
            Require(service.ConfigurationCalls == 0 && service.Commands.Last() == "disable" && backend.Status.Config.Profiles.Count == 4, "Recovery unexpectedly applied or discarded a pending channel edit.");
        });

        await Check("explicit service update preserves local channels without configuring or enabling DNS", async dir =>
        {
            var local = Defaults(); local.Profiles.Add(Custom());
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), local);
            var service = new FakeService { State = new AppStatus { Config = Defaults() } };
            await using var backend = new UiBackend(dir, service);
            Require(backend.ServiceUpdateAvailable, "Update is unavailable before the old service can return status.");
            await backend.UpdateServiceAsync();
            Require(service.InstallCalls == 1 && service.ConfigurationCalls == 0 && !service.Commands.Contains("enable"), "Updating implicitly reconfigured or enabled DNS.");
            Require(backend.Status.Config.Profiles.Count == 4 && backend.PendingServiceConfiguration && !backend.ServiceNeedsUpdate, "Service update hid local channels or failed to report the new version.");
        });

        await Check("an idempotent explicit service update also ends in observation mode", async dir =>
        {
            var config = Defaults(); config.ManagementRequested = true;
            AtomicStore.Write(Path.Combine(dir, "ui-config.json"), config);
            var service = new FakeService { State = new AppStatus { Config = config, ServiceVersion = AppVersion.Current, SystemDnsManaged = true } };
            await using var backend = new UiBackend(dir, service);
            await backend.UpdateServiceAsync();
            Require(!backend.Status.SystemDnsManaged && !backend.Status.Config.ManagementRequested && service.Commands.Contains("disable"), "An update retry continued DNS management against the explicit observation-mode promise.");
        });
    }

    internal sealed class FakeService : IUiServiceConnection
    {
        public bool IsInstalled { get; set; } = true;
        public AppStatus State { get; set; } = new();
        public bool FailConfigure { get; set; }
        public bool CommitBeforeFailure { get; set; }
        public int ConfigurationCalls { get; private set; }
        public int InstallCalls { get; private set; }
        public List<string> Commands { get; } = [];
        public Task InstallAsync() { InstallCalls++; State.ServiceVersion = AppVersion.Current; return Task.CompletedTask; }
        public Task RecoverAsync() => Task.CompletedTask;
        public Task<RpcReply> SendAsync(RpcRequest request, CancellationToken token)
        {
            Commands.Add(request.Command);
            if (request.Command == "configure")
            {
                ConfigurationCalls++;
                if (!FailConfigure || CommitBeforeFailure) State.Config = JsonData.Clone(request.Config!);
                if (FailConfigure) throw new IOException("Simulated incomplete pipe reply.");
            }
            if (request.Command == "disable") { State.SystemDnsManaged = false; State.Config.ManagementRequested = false; State.DnsRecoveryPending = false; }
            return Task.FromResult(new RpcReply { Ok = true, Status = JsonData.Clone(State) });
        }
    }
}
