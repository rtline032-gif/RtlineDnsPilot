namespace NetPilot.Tests;

public static class ProfileConfigurationTests
{
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, Action test)
        {
            try { test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        static void Require(bool condition, string message)
        {
            if (!condition) throw new Exception(message);
        }
        static string Reject(Action action)
        {
            try { action(); }
            catch (ArgumentException ex) { return ex.Message; }
            throw new Exception("Invalid profile configuration was accepted.");
        }
        static ChannelProfile Udp(string name, params string[] servers) => new()
        {
            Name = name, Mode = DnsMode.Udp, Servers = servers
        };
        static AppConfig Config(params ChannelProfile[] profiles) => new() { Profiles = [.. profiles] };

        Check("fresh IP-only profile validates as ordinary DNS", () =>
        {
            var profile = new ChannelProfile { Servers = ["1.1.1.1"] };
            profile.Validate();
            Require(profile.Mode == DnsMode.Udp, "IP-only entry still defaults to mandatory DoH.");
        });
        Check("built-in profiles retain explicit HTTPS DNS", () =>
        {
            var config = AppConfig.Defaults();
            config.Validate();
            Require(config.Profiles.All(p => p.Mode == DnsMode.Doh), "A built-in provider was changed to plaintext DNS.");
        });
        Check("explicit DoH with missing URL is rejected without runtime downgrade", () =>
        {
            var profile = new ChannelProfile { Name = "HTTPS", Mode = DnsMode.Doh, Servers = ["1.1.1.1"], DohUrl = "" };
            Reject(profile.Validate);
            Require(profile.Mode == DnsMode.Doh, "Validation silently downgraded explicit DoH.");
        });
        Check("duplicate profile names are trimmed and case insensitive", () =>
        {
            var message = Reject(Config(Udp("Existing provider", "1.1.1.1"), Udp("  eXISTING PROVIDER  ", "9.9.9.9")).Validate);
            Require(message.Contains("Existing provider", StringComparison.Ordinal), "Duplicate error omitted the existing channel name.");
        });
        Check("ordinary DNS duplicate ignores IP order name and enabled state", () =>
        {
            var first = Udp("Existing UDP", "1.1.1.1", "9.9.9.9");
            var second = Udp("Another name", "9.9.9.9", "1.1.1.1");
            second.Enabled = false;
            second.DohUrl = "https://unused.example/dns-query";
            var message = Reject(Config(first, second).Validate);
            Require(message.Contains("Existing UDP", StringComparison.Ordinal), "Duplicate error omitted the existing channel name.");
        });
        Check("equivalent IPv6 spellings identify the same channel", () =>
        {
            var first = Udp("IPv6 original", "2606:4700:4700::1111");
            var second = Udp("IPv6 copy", "2606:4700:4700:0000:0000:0000:0000:1111");
            Reject(Config(first, second).Validate);
        });
        Check("same IPs on distinct UDP ports remain distinct", () =>
        {
            var first = Udp("Default port", "1.1.1.1");
            var second = Udp("Alternate port", "1.1.1.1");
            second.DnsPort = 5353;
            Config(first, second).Validate();
        });
        Check("profiles sharing only some IPs remain distinct", () =>
            Config(Udp("Pair one", "1.1.1.1", "1.0.0.1"), Udp("Pair two", "1.1.1.1", "9.9.9.9")).Validate());
        Check("same bootstrap IPs across DNS protocols remain distinct", () =>
        {
            var doh = Udp("HTTPS", "1.1.1.1");
            doh.Mode = DnsMode.Doh;
            doh.DohUrl = "https://dns.example/dns-query";
            Config(Udp("UDP", "1.1.1.1"), doh).Validate();
        });
        Check("DoH duplicates normalize host and default HTTPS port", () =>
        {
            var first = new ChannelProfile { Name = "Existing HTTPS", Mode = DnsMode.Doh, Servers = ["1.1.1.1", "9.9.9.9"], DohUrl = "https://DNS.EXAMPLE:443/dns-query" };
            var second = new ChannelProfile { Name = "HTTPS copy", Mode = DnsMode.Doh, Servers = ["9.9.9.9", "1.1.1.1"], DohUrl = "https://dns.example/dns-query", DnsPort = 5353 };
            var message = Reject(Config(first, second).Validate);
            Require(message.Contains("Existing HTTPS", StringComparison.Ordinal), "DoH duplicate error omitted the existing channel name.");
        });
        Check("DoH paths retain case when checking duplicates", () =>
        {
            var first = new ChannelProfile { Name = "Upper path", Mode = DnsMode.Doh, Servers = ["1.1.1.1"], DohUrl = "https://dns.example/DNS-query" };
            var second = new ChannelProfile { Name = "Lower path", Mode = DnsMode.Doh, Servers = ["1.1.1.1"], DohUrl = "https://dns.example/dns-query" };
            Config(first, second).Validate();
        });
        Check("identical IP entries inside one profile are reported", () =>
            Reject(Udp("Repeated IPv4", "1.1.1.1", "1.1.1.1").Validate));
        Check("equivalent IPv6 entries inside one profile are reported", () =>
            Reject(Udp("Repeated IPv6", "2606:4700:4700::1111", "2606:4700:4700:0:0:0:0:1111").Validate));
        Check("profile ID duplicates use canonical GUID identity", () =>
        {
            var first = Udp("Original ID", "1.1.1.1");
            var second = Udp("Copy ID", "9.9.9.9");
            second.Id = Guid.Parse(first.Id).ToString("B").ToUpperInvariant();
            Reject(Config(first, second).Validate);
        });
        Check("malformed HTTPS URL is rejected rather than repaired", () =>
        {
            var profile = new ChannelProfile { Name = "Malformed", Mode = DnsMode.Doh, Servers = ["1.1.1.1"], DohUrl = "https://dns.example/dns%zzquery" };
            Reject(profile.Validate);
        });
        Check("null profile from imported JSON is a validation error", () =>
            Reject(new AppConfig { Profiles = [null!] }.Validate));

        Check("editor builds IP-only input without mandatory DoH", () =>
        {
            var source = new ChannelProfile();
            var profile = ProfileEditor.Build(source, "  Ordinary DNS  ", true, "1.1.1.1; 9.9.9.9", "  ", 5353, " note ");
            Require(profile.Mode == DnsMode.Udp && profile.DohUrl == "" && profile.DnsPort == 5353, "Empty optional URL did not select ordinary DNS.");
            Require(profile.Name == "Ordinary DNS" && profile.Note == "note" && profile.Servers.SequenceEqual(["1.1.1.1", "9.9.9.9"]), "Editor input was not parsed and trimmed correctly.");
        });
        Check("editor derives HTTPS DNS from a validated optional URL", () =>
        {
            var profile = ProfileEditor.Build(new ChannelProfile(), "HTTPS", true, "1.1.1.1", " https://dns.example/dns-query ", 53, "");
            Require(profile.Mode == DnsMode.Doh && profile.DohUrl == "https://dns.example/dns-query", "Valid DoH input was not selected and trimmed.");
        });
        Check("editor rejects invalid supplied DoH rather than downgrading", () =>
        {
            foreach (var url in new[] { "http://dns.example/dns-query", "https://1.1.1.1/dns-query", "https://user:secret@dns.example/dns-query", "https://dns.example/dns-query?dns=abc", "https://dns.example/dns-query#fragment", "https://dns.example/dns%zzquery" })
                Reject(() => ProfileEditor.Build(new ChannelProfile(), "Invalid HTTPS", true, "1.1.1.1", url, 53, ""));
        });
        Check("editing ordinary DNS clears unused historical DoH URL", () =>
        {
            var source = Udp("Legacy ordinary DNS", "1.1.1.1");
            source.DohUrl = "https://unused.example/dns-query";
            var input = ProfileEditor.GetDohUrlForEditing(source);
            Require(input == "", "Unused historical URL was presented as active DoH input.");
            var profile = ProfileEditor.Build(source, source.Name, source.Enabled, "1.1.1.1", input, 53, source.Note);
            Require(profile.Mode == DnsMode.Udp && profile.DohUrl == "", "Editing silently activated the historical DoH URL.");
        });
        Check("editing HTTPS DNS retains its active DoH URL", () =>
        {
            var source = new ChannelProfile { Name = "Active HTTPS", Mode = DnsMode.Doh, Servers = ["1.1.1.1"], DohUrl = "https://dns.example/dns-query" };
            Require(ProfileEditor.GetDohUrlForEditing(source) == "https://dns.example/dns-query", "Editor dropped the active DoH endpoint.");
        });
        Check("editor reports repeated IP input without silently removing it", () =>
            Reject(() => ProfileEditor.Build(new ChannelProfile(), "Repeated", true, "1.1.1.1\n1.1.1.1", "", 53, "")));
        Check("editor duplicate name error identifies the existing channel", () =>
        {
            var existing = Udp("Existing editor channel", "1.1.1.1");
            var message = Reject(() => ProfileEditor.Build(new ChannelProfile(), " EXISTING EDITOR CHANNEL ", true, "9.9.9.9", "", 53, "", [existing]));
            Require(message.Contains("Existing editor channel", StringComparison.Ordinal), "Editor omitted the existing channel name.");
        });
        Check("editor checks duplicate configuration before accepting save", () =>
        {
            var existing = Udp("Existing configuration", "1.1.1.1", "9.9.9.9");
            var message = Reject(() => ProfileEditor.Build(new ChannelProfile(), "Copy", true, "9.9.9.9,1.1.1.1", "", 53, "", [existing]));
            Require(message.Contains("Existing configuration", StringComparison.Ordinal), "Editor duplicate configuration error omitted the existing channel.");
        });
        Check("editor excludes its own canonical GUID during duplicate checks", () =>
        {
            var existing = Udp("Current channel", "1.1.1.1");
            var source = JsonData.Clone(existing);
            source.Id = Guid.Parse(source.Id).ToString("B").ToUpperInvariant();
            var profile = ProfileEditor.Build(source, source.Name, true, "1.1.1.1", "", 53, "", [existing]);
            Require(Guid.Parse(profile.Id) == Guid.Parse(existing.Id), "Editing changed the channel identity.");
        });
        Check("failed editor save leaves source and existing profiles unchanged", () =>
        {
            var source = Udp("Source", "9.9.9.9");
            var existing = Udp("Existing", "1.1.1.1");
            var sourceBefore = JsonData.Encode(source);
            var existingBefore = JsonData.Encode(existing);
            Reject(() => ProfileEditor.Build(source, "Edited", false, "1.1.1.1", "", 53, "changed", [existing, source]));
            Require(JsonData.Encode(source) == sourceBefore && JsonData.Encode(existing) == existingBefore, "Rejected save mutated existing data.");
        });
        Check("successful editor save returns a separate profile", () =>
        {
            var source = Udp("Original", "9.9.9.9");
            var before = JsonData.Encode(source);
            var profile = ProfileEditor.Build(source, "Edited", false, "1.1.1.1", "", 53, "changed", [source]);
            Require(!ReferenceEquals(source, profile) && profile.Name == "Edited" && JsonData.Encode(source) == before, "Editor modified the input object before the caller accepted it.");
        });
    }
}
