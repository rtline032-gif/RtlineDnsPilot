namespace NetPilot.Tests;

public static class BundledChannelTests
{
    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(name, true, ""); } catch (Exception ex) { report(name, false, ex.ToString()); }
        }
        static void Require(bool condition, string reason) { if (!condition) throw new Exception(reason); }
        await Check("published supplemental channels validate without duplicate settings", () =>
        {
            var all = BundledChannels.AddMissing(AppConfig.Defaults(), out int added);
            Require(added == 12 && all.Profiles.Count == 15, "Source-checked channel set is incomplete or duplicated.");
            all.Validate();
            Require(all.Profiles.Any(p => p.Mode == DnsMode.Udp && p.Servers.Contains("159.194.200.33")), "The user's GeoHide DNS address was not added.");
            Require(all.Profiles.Single(p => p.Name == "Comss.one (DoH)").Servers.SequenceEqual(["195.133.25.16"]), "Comss DoH bootstrap does not match the published address.");
            Require(all.Profiles.Single(p => p.Name == "dns.malw.link (DoH)").Servers.SequenceEqual(["179.254.166.171"]), "An unverified secondary server was incorrectly added to DoH.");
            return Task.CompletedTask;
        });
        await Check("adding the same catalog twice keeps user channels and identities", () =>
        {
            var all = BundledChannels.AddMissing(AppConfig.Defaults(), out _);
            all.Profiles.Last().Note = "User note";
            var before = JsonData.Encode(all);
            var repeated = BundledChannels.AddMissing(all, out int added);
            Require(added == 0 && JsonData.Encode(repeated) == before, "Repeated merge duplicated or overwrote a user profile.");
            return Task.CompletedTask;
        });
        await Check("a source catalog does not overwrite a conflicting user channel name", () =>
        {
            var config = AppConfig.Defaults();
            var custom = new ChannelProfile { Name = "GeoHide (UDP/TCP)", Servers = ["8.8.8.8"], Note = "Keep me" };
            config.Profiles.Add(custom);
            var all = BundledChannels.AddMissing(config, out _);
            Require(all.Profiles.Single(p => p.Id == custom.Id).Note == "Keep me" && all.Profiles.Any(p => p.Servers.Contains("159.194.200.33")), "A conflicting name caused lost settings.");
            return Task.CompletedTask;
        });
        await Check("one-time catalog migration never re-adds channels explicitly deleted afterwards", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "catalog-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var config = AppConfig.Defaults(); foreach (var p in config.Profiles) p.Enabled = false;
            AtomicStore.Write(Path.Combine(directory, "ui-config.json"), config);
            var service = new UiPersistenceTests.FakeService { IsInstalled = false };
            string deleted;
            await using (var backend = new UiBackend(directory, service, includeBundledChannels: true))
            {
                Require(backend.Status.Config.Profiles.Count == 15, "The migration was not loaded into the window.");
                var edited = JsonData.Clone(backend.Status.Config);
                deleted = edited.Profiles.Last().Id; edited.Profiles.RemoveAt(edited.Profiles.Count - 1);
                foreach (var p in edited.Profiles) p.Enabled = false;
                await backend.ConfigureAsync(edited);
            }
            await using var reopened = new UiBackend(directory, service, includeBundledChannels: true);
            Require(reopened.Status.Config.Profiles.Count == 14 && reopened.Status.Config.Profiles.All(p => p.Id != deleted), "The migration silently undid an explicit deletion.");
        });
        await Check("catalog migration also preserves channels that exist only in the service database", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "catalog-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var local = AppConfig.Defaults();
            AtomicStore.Write(Path.Combine(directory, "ui-config.json"), local);
            var remote = AppConfig.Defaults();
            remote.Profiles.Add(new ChannelProfile { Name = "User service-only DNS", Servers = ["9.9.9.9"] });
            var service = new UiPersistenceTests.FakeService { State = new AppStatus { Config = remote } };
            await using var backend = new UiBackend(directory, service, includeBundledChannels: true);
            await backend.RefreshAsync();
            Require(backend.Status.Config.Profiles.Count == 16 && backend.Status.Config.Profiles.Any(p => p.Name == "User service-only DNS"), "Bundled additions replaced an independent service channel.");
            Require(service.ConfigurationCalls == 0, "Catalog migration reconfigured the live service.");
        });
    }
}
