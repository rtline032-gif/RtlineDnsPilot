namespace NetPilot.Tests;

public static class ProfileImportTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, Action action)
        {
            try { action(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        static void Require(bool value, string message) { if (!value) throw new Exception(message); }
        static ChannelProfile Profile(string name, string address) => new() { Name = name, Servers = [address] };
        static void Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException) { return; }
            throw new Exception("Invalid import was accepted.");
        }

        Check("import adds three channels without replacing the existing list or selection", () =>
        {
            var config = AppConfig.Defaults();
            config.PinnedProfileId = config.ManualProfileId = config.Profiles[1].Id;
            config.ManualUntil = DateTimeOffset.UtcNow.AddHours(1);
            config.AutoSelection = AutoSelectionMode.Fast;
            config.MonitorGemini = config.MonitoringPaused = true;
            config.AdapterId = Guid.NewGuid().ToString("D");
            config.IntervalSeconds = 90;
            config.Profiles[0].Enabled = false;
            var before = JsonData.Encode(config);
            var added = new[] { Profile("New one", "1.1.1.1"), Profile("New two", "9.9.9.9"), Profile("New three", "8.8.8.8") };
            var result = ProfileImport.Merge(config, added);
            Require(result.Added == 3 && result.Duplicates == 0 && result.Config.Profiles.Count == 6, "Import replaced existing channels.");
            Require(JsonData.Encode(result.Config.Profiles.Take(3).ToList()) == JsonData.Encode(config.Profiles), "Existing channel data or identities changed.");
            var settings = JsonData.Clone(result.Config); settings.Profiles = config.Profiles;
            Require(JsonData.Encode(settings) == before && JsonData.Encode(config) == before, "Import reset selection/settings or mutated its input.");
            added[0].Servers[0] = "4.4.4.4";
            Require(result.Config.Profiles[3].Servers[0] == "1.1.1.1", "Imported input objects are shared with the saved configuration.");
        });
        Check("import skips equivalent configurations within the file and against existing disabled channels", () =>
        {
            var first = Profile("Existing", "2606:4700:4700::1111"); first.Enabled = false;
            var config = new AppConfig { Profiles = [first] };
            var duplicate = Profile("Different label", "2606:4700:4700:0:0:0:0:1111");
            var fresh = Profile("New", "9.9.9.9");
            var result = ProfileImport.Merge(config, [duplicate, fresh, JsonData.Clone(fresh)]);
            Require(result.Added == 1 && result.Duplicates == 2 && !result.Config.Profiles[0].Enabled, "Duplicates were added or existing disabled state changed.");
            var repeated = ProfileImport.Merge(result.Config, [duplicate, fresh]);
            Require(repeated.Added == 0 && repeated.Duplicates == 2 && JsonData.Encode(repeated.Config) == JsonData.Encode(result.Config), "Repeated import changed the database.");
        });
        Check("import deduplicates normalized DoH without confusing distinct DNS transports", () =>
        {
            var config = new AppConfig { Profiles = [new() { Name = "Existing DoH", Mode = DnsMode.Doh, Servers = ["1.1.1.1", "1.0.0.1"], DohUrl = "https://DNS.EXAMPLE:443/dns-query" }] };
            var copy = JsonData.Clone(config.Profiles[0]); copy.Id = Guid.NewGuid().ToString("N"); copy.Servers = ["1.0.0.1", "1.1.1.1"]; copy.DohUrl = "https://dns.example/dns-query";
            var udp = JsonData.Clone(copy); udp.Mode = DnsMode.Udp; udp.Name = "UDP";
            var result = ProfileImport.Merge(config, [copy, udp]);
            Require(result.Duplicates == 1 && result.Added == 1 && result.Config.Profiles.Last().Mode == DnsMode.Udp, "DoH normalization or transport distinction was lost.");
        });
        Check("import retains both configurations on canonical ID and long name collisions", () =>
        {
            var original = Profile(new string('A', 100), "1.1.1.1");
            var incoming = Profile("  " + new string('a', 98), "9.9.9.9"); incoming.Id = Guid.Parse(original.Id).ToString("B").ToUpperInvariant();
            incoming.Name = original.Name.ToLowerInvariant();
            var config = new AppConfig { Profiles = [original], ManualProfileId = original.Id };
            var result = ProfileImport.Merge(config, [incoming]);
            Require(result.Added == 1 && result.Renamed == 1 && result.Config.Profiles[1].Name.Length <= 100, "The colliding imported channel was lost or its name exceeded the limit.");
            Require(Guid.Parse(result.Config.Profiles[1].Id) != Guid.Parse(original.Id) && result.Config.ManualProfileId == original.Id && result.Config.Profiles[0].Servers[0] == "1.1.1.1", "ID collision replaced or unpinned the existing channel.");
            var repeated = ProfileImport.Merge(result.Config, [incoming]);
            Require(repeated.Added == 0 && repeated.Duplicates == 1, "Reimport duplicated a renamed channel.");
        });
        Check("invalid later import entry leaves both input lists intact", () =>
        {
            var config = AppConfig.Defaults();
            var incoming = new[] { Profile("Valid", "1.1.1.1"), Profile("Invalid", "127.0.0.1") };
            var before = JsonData.Encode(config); var sourceBefore = JsonData.Encode(incoming);
            Reject(() => ProfileImport.Merge(config, incoming));
            Require(before == JsonData.Encode(config) && sourceBefore == JsonData.Encode(incoming), "A rejected import partially mutated the database.");
        });
        Check("import limit rejects the whole addition while duplicates still fit a full database", () =>
        {
            var config = new AppConfig { Profiles = Enumerable.Range(1, 32).Select(n => Profile("Channel " + n, "203.0.113." + n)).ToList() };
            var before = JsonData.Encode(config);
            Reject(() => ProfileImport.Merge(config, [Profile("Overflow", "9.9.9.9")]));
            Require(JsonData.Encode(config) == before, "Overflow caused a partial import.");
            var duplicate = JsonData.Clone(config.Profiles[0]); duplicate.Id = Guid.NewGuid().ToString("N");
            var result = ProfileImport.Merge(config, [duplicate]);
            Require(result.Added == 0 && result.Duplicates == 1 && result.Config.Profiles.Count == 32, "Duplicate-only import incorrectly exceeded capacity.");
        });
        Check("JSON profile reader supports array and full exports with duplicate entries for merge", () =>
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "test-data", "import-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var profile = Profile("Imported", "9.9.9.9");
            var path = Path.Combine(dir, "profiles.json");
            foreach (var json in new[] { JsonData.Encode(new[] { profile, profile }), JsonData.Encode(new AppConfig { Profiles = [profile, profile] }) })
            {
                File.WriteAllText(path, json);
                var result = ProfileImport.Merge(AppConfig.Defaults(), UiBackend.ReadProfiles(path));
                Require(result.Added == 1 && result.Duplicates == 1 && result.Config.Profiles.Count == 4, "JSON import did not merge/deduplicate an exported file.");
            }
        });
    }
}
