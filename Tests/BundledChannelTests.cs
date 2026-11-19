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
            Require(added == 29 && all.Profiles.Count == 32, "Source-checked channel set is incomplete or duplicated.");
            all.Validate();
            Require(all.Profiles.Any(p => p.Mode == DnsMode.Udp && p.Servers.Contains("159.194.200.33")), "The user's GeoHide DNS address was not added.");
            Require(all.Profiles.Single(p => p.Name == "Comss.one (DoH)").Servers.SequenceEqual(["141.133.184.24", "141.133.183.77"]), "Comss DoH bootstrap does not match the linked official profile.");
            Require(all.Profiles.Single(p => p.Name == "Comss.one (DoH, резерв)").Servers.SequenceEqual(["109.73.193.146"]), "The separately published Comss router configuration was lost.");
            Require(all.Profiles.Single(p => p.Name == "Bezmezhau").Servers.SequenceEqual(["143.20.64.55", "31.58.216.201"])
                && all.Profiles.Single(p => p.Name == "Bezmezhau (UDP/TCP)").Servers.SequenceEqual(["143.20.64.55", "31.58.216.201"]),
                "Bezmezhau still uses the previous primary or lost its documented secondary.");
            Require(all.Profiles.Single(p => p.Name == "dns.malw.link (DoH)").Servers.SequenceEqual(["179.254.166.171"]), "An unverified secondary server was incorrectly added to DoH.");
            Require(all.Profiles.Count(p => p.Name.StartsWith("MagicDNS", StringComparison.Ordinal)) == 3, "Working MagicDNS variants were not shipped with the catalog.");
            Require(all.Profiles.Single(p => p.Name == "Dagami (DoH, 2026-11)").Servers.SequenceEqual(["48.102.213.179"]), "Current published Dagami bootstrap is missing.");
            Require(!all.Profiles.Single(p => p.Name == "Dagami (DoH)").Enabled && !all.Profiles.Single(p => p.Name == "Astracat (Commonnet DoH)").Enabled,
                "Historical or failed optional candidates were silently enabled in a fresh catalog.");
            return Task.CompletedTask;
        });
        await Check("confirmed provider notices disable new Xbox defaults and explain static Zapret limitations", () =>
        {
            var catalog = BundledChannels.All();
            Require(!AppConfig.Defaults().Profiles.Single(p => p.Name == "Xbox DNS").Enabled,
                "A fresh initial Xbox profile ignored the provider's temporary outage notice.");
            foreach (var profile in catalog.Where(p => p.Name.StartsWith("Xbox DNS", StringComparison.Ordinal)))
            {
                Require(!profile.Enabled && profile.Note.Contains("17.11.2026") && profile.Note.Contains("https://xbox-dns.ru/")
                    && profile.Note.Contains("временной неработоспособности"),
                    "A new Xbox profile was enabled or lost the dated provider notice.");
            }
            var zapret = catalog.Where(p => p.Name.StartsWith("Zapret DNS", StringComparison.Ordinal)).ToList();
            Require(zapret.Count == 4 && zapret.All(p => !p.Enabled && p.Note.Contains("17.11.2026")
                && p.Note.Contains("https://dns.zapret.moe/") && p.Note.Contains("без постоянного IP")),
                "Historical Zapret variants were dropped, enabled or presented as current fixed-IP defaults.");
            foreach (var profile in zapret)
            {
                Require(profile.Servers.SequenceEqual([profile.Name.Contains("2026-11") ? "38.180.241.191" : "83.217.211.149"]),
                    "A documented provider notice silently replaced a retained Zapret address.");
                Require(profile.Mode == DnsMode.Doh
                    ? profile.Note.Contains("статический bootstrap") && profile.Note.Contains("не доказывает отключение именно этого IP")
                    : profile.Note.Contains("53") && profile.Note.Contains("853") && profile.Note.Contains("03.11.2026"),
                    "The notice did not distinguish unsupported static DoH from the provider's closed DNS ports.");
            }
            return Task.CompletedTask;
        });
        await Check("catalog provider notices preserve saved Xbox and Zapret identities flags notes and selection", () =>
        {
            var config = new AppConfig { Profiles = BundledChannels.All().Where(p =>
                p.Name.StartsWith("Xbox DNS", StringComparison.Ordinal) || p.Name.StartsWith("Zapret DNS", StringComparison.Ordinal)).ToList() };
            foreach (var profile in config.Profiles)
            {
                profile.Id = Guid.NewGuid().ToString("B").ToUpperInvariant();
                profile.Enabled = true;
                profile.Note = "Saved user note: " + profile.Name;
            }
            config.ManualProfileId = config.Profiles[0].Id;
            config.ManualUntil = DateTimeOffset.UtcNow.AddHours(1);
            config.PinnedProfileId = config.Profiles[1].Id;
            var before = JsonData.Encode(config);
            var expanded = BundledChannels.AddMissing(config, out var added, BundledChannels.PreviousRevision);
            var retained = JsonData.Clone(expanded); retained.Profiles = retained.Profiles.Take(config.Profiles.Count).ToList();
            Require(added == 26 && expanded.Profiles.Count == 32 && JsonData.Encode(retained) == before && JsonData.Encode(config) == before,
                "Metadata corrections changed saved provider identities, enabled flags, notes or manual/preferred selection.");
            return Task.CompletedTask;
        });
        await Check("the catalog keeps older provider variants and reports pending additions at capacity", () =>
        {
            var config = new AppConfig { Profiles = BundledChannels.All().Take(26).ToList() };
            foreach (var profile in config.Profiles)
            {
                if (profile.Name.StartsWith("Bezmezhau", StringComparison.Ordinal)) profile.Servers = ["181.215.40.49", "31.58.216.201"];
                if (profile.Name == "Comss.one (DoH)") { profile.Servers = ["195.133.25.16"]; profile.Enabled = false; }
                if (profile.Name == "Comss.one (DoH, резерв)") profile.Servers = ["83.220.169.155", "212.109.195.93"];
                profile.Note = "Saved user note: " + profile.Name;
            }
            config.Profiles.Add(new ChannelProfile { Name = "Custom DNS", Servers = ["8.8.8.8"], Enabled = false });
            config.ManualProfileId = config.Profiles.Single(p => p.Name == "Bezmezhau").Id;
            config.ManualUntil = DateTimeOffset.UtcNow.AddMinutes(15);
            config.PinnedProfileId = config.Profiles.Single(p => p.Name == "Comss.one (DoH, резерв)").Id;
            var before = JsonData.Encode(config);
            var expanded = BundledChannels.AddMissing(config, out var added, "2026-11-16");
            var retained = JsonData.Clone(expanded); retained.Profiles = retained.Profiles.Take(config.Profiles.Count).ToList();
            Require(added == 5 && expanded.Profiles.Count == 32 && JsonData.Encode(retained) == before && JsonData.Encode(config) == before,
                "A provider refresh overwrote old channels, flags, notes, custom data or manual/preferred selection.");
            Require(expanded.Profiles.Skip(config.Profiles.Count).All(p => !config.Profiles.Any(old => Guid.Parse(old.Id) == Guid.Parse(p.Id)))
                && BundledChannels.PendingCount(expanded, "2026-11-16") == 5,
                "Replacement configurations reused an occupied identity or missing additions were hidden.");
            var repeated = BundledChannels.AddMissing(expanded, out added, "2026-11-16");
            Require(added == 0 && JsonData.Encode(repeated) == JsonData.Encode(expanded), "Retrying a full list modified the saved profiles.");
            return Task.CompletedTask;
        });
        await Check("a new catalog revision fills all missing profiles while preserving existing channel settings", () =>
        {
            var config = new AppConfig { Profiles = BundledChannels.All().Take(26).ToList() };
            var deleted = config.Profiles.Last(); config.Profiles.Remove(deleted);
            config.Profiles[0].Enabled = false; config.Profiles[0].Note = "User note";
            config.PinnedProfileId = config.ManualProfileId = config.Profiles[1].Id;
            config.ManualUntil = DateTimeOffset.UtcNow.AddHours(1);
            var before = JsonData.Encode(config);
            var all = BundledChannels.AddMissing(config, out var added, BundledChannels.PreviousRevision);
            Require(added == 7 && all.Profiles.Count == 32 && all.Profiles.Any(p => AppConfig.SameEffectiveConfiguration(p, deleted)),
                "An older missing channel or one of the six test configurations was not added.");
            var settings = JsonData.Clone(all); settings.Profiles = settings.Profiles.Take(config.Profiles.Count).ToList();
            Require(JsonData.Encode(settings) == before && JsonData.Encode(config) == before,
                "A catalog update altered existing identities, flags, notes or manual selection.");
            Require(BundledChannels.PendingCount(all, BundledChannels.PreviousRevision) == 0, "The complete shipped catalog was not acknowledged.");
            return Task.CompletedTask;
        });
        await Check("an accepted current catalog revision never restores a later deletion", () =>
        {
            var all = BundledChannels.AddMissing(AppConfig.Defaults(), out _);
            all.Profiles.RemoveAll(p => p.Name == "MagicDNS (DoH, 2026-11)");
            var before = JsonData.Encode(all);
            var repeated = BundledChannels.AddMissing(all, out var added, BundledChannels.Revision);
            Require(added == 0 && JsonData.Encode(repeated) == before, "An acknowledged revision undid an explicit deletion.");
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
        await Check("a catalog ID collision preserves the custom profile and adds a separate provider identity", () =>
        {
            var config = AppConfig.Defaults();
            var source = BundledChannels.All().Single(p => p.Name == "MagicDNS (DoH, 2026-11)");
            var custom = new ChannelProfile { Id = Guid.Parse(source.Id).ToString("B").ToUpperInvariant(), Name = source.Name,
                Servers = ["9.9.9.9"], Enabled = false, Note = "Keep custom" };
            config.Profiles.Add(custom);
            var all = BundledChannels.AddMissing(config, out _);
            Require(all.Profiles.Single(p => p.Id == custom.Id).Note == "Keep custom" && !all.Profiles.Single(p => p.Id == custom.Id).Enabled,
                "An existing custom identity was overwritten.");
            var bundled = all.Profiles.Single(p => AppConfig.SameEffectiveConfiguration(p, source));
            Require(Guid.Parse(bundled.Id) != Guid.Parse(custom.Id) && bundled.Name != custom.Name && bundled.Name.Length <= 100,
                "A colliding provider identity/name was skipped or remained ambiguous.");
            return Task.CompletedTask;
        });
        await Check("a full database retains all existing channels and reports unadded catalog configurations", () =>
        {
            var config = new AppConfig { Profiles = Enumerable.Range(1, 32).Select(n => new ChannelProfile
                { Name = "Custom " + n, Servers = ["203.0.113." + n] }).ToList() };
            var before = JsonData.Encode(config);
            var all = BundledChannels.AddMissing(config, out var added, BundledChannels.PreviousRevision);
            Require(added == 0 && all.Profiles.Count == 32 && JsonData.Encode(all) == before && JsonData.Encode(config) == before,
                "The capacity limit displaced or modified an existing channel.");
            Require(BundledChannels.PendingCount(all, BundledChannels.PreviousRevision) == 32, "Missing catalog profiles were silently lost at capacity.");
            return Task.CompletedTask;
        });
        await Check("a nearly full database appends only available slots and leaves the remaining delta pending", () =>
        {
            var config = new AppConfig { Profiles = Enumerable.Range(1, 31).Select(n => new ChannelProfile
                { Name = "Custom " + n, Servers = ["203.0.113." + n] }).ToList() };
            var before = JsonData.Encode(config.Profiles);
            var all = BundledChannels.AddMissing(config, out var added, BundledChannels.PreviousRevision);
            Require(added == 1 && all.Profiles.Count == 32 && JsonData.Encode(all.Profiles.Take(31).ToList()) == before,
                "A partial capacity merge dropped an existing configuration.");
            Require(BundledChannels.PendingCount(all, BundledChannels.PreviousRevision) == 31, "A partial merge hid the unadded configurations.");
            return Task.CompletedTask;
        });
        await Check("catalog getters do not share mutable profile arrays", () =>
        {
            var first = BundledChannels.All(); first[0].Servers[0] = "9.9.9.9"; first[0].Note = "User edit";
            var fresh = BundledChannels.All();
            Require(fresh[0].Servers[0] == "111.88.96.54" && fresh[0].Note != "User edit", "Editing a catalog result corrupted future imports.");
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
                Require(backend.Status.Config.Profiles.Count == 32, "The migration was not loaded into the window.");
                var edited = JsonData.Clone(backend.Status.Config);
                deleted = edited.Profiles.Last().Id; edited.Profiles.RemoveAt(edited.Profiles.Count - 1);
                foreach (var p in edited.Profiles) p.Enabled = false;
                await backend.ConfigureAsync(edited);
            }
            await using var reopened = new UiBackend(directory, service, includeBundledChannels: true);
            Require(reopened.Status.Config.Profiles.Count == 31 && reopened.Status.Config.Profiles.All(p => p.Id != deleted), "The migration silently undid an explicit deletion.");
        });
        await Check("catalog migration reserves room for saved service channels before filling the window", async () =>
        {
            var directory = Path.Combine(AppContext.BaseDirectory, "test-data", "catalog-merge-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var local = AppConfig.Defaults();
            int catalogCount = BundledChannels.All().Count;
            int localOnlyCount = Math.Max(1, 32 - catalogCount);
            for (int i = 1; i <= localOnlyCount; i++)
                local.Profiles.Add(new ChannelProfile { Name = "Saved local DNS " + i, Servers = ["198.51.100." + i] });
            AtomicStore.Write(Path.Combine(directory, "ui-config.json"), local);
            var remote = AppConfig.Defaults();
            var custom = new ChannelProfile { Name = "User service-only DNS", Servers = ["9.9.9.9"], Note = "Saved service settings" };
            remote.Profiles.Add(custom);
            var service = new UiPersistenceTests.FakeService { State = new AppStatus { Config = remote } };
            await using var backend = new UiBackend(directory, service, includeBundledChannels: true);
            await backend.RefreshAsync();
            var profiles = backend.Status.Config.Profiles;
            Require(profiles.Count == 32 && profiles.Any(p => p.Id == custom.Id && p.Note == custom.Note)
                && local.Profiles.Skip(3).All(saved => profiles.Any(p => p.Id == saved.Id)),
                "Optional catalog entries took space required by saved local and service channels.");
            int pending = catalogCount + localOnlyCount + 1 - 32;
            Require(!File.Exists(Path.Combine(directory, "bundled-channels.json"))
                && backend.ConfigurationWarning?.Contains("осталось " + pending) == true,
                "An incomplete synchronized catalog was marked complete or its pending entry was hidden.");
            Require(service.ConfigurationCalls == 0, "Catalog migration reconfigured the live service.");
        });
    }
}
