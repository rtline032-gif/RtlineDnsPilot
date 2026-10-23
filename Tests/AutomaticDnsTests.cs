using System.Text.Json;

namespace NetPilot.Tests;

public static class AutomaticDnsTests
{
    private const string AdapterId = "d8ad1c6e-f07b-437a-a29f-0c8bf4391c3c";

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<string, Task> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetPilot-automatic-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { await test(dir); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
            finally
            {
                var full = Path.GetFullPath(dir);
                var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("NetPilot-automatic-test-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing cleanup outside the generated DNS test directory.");
                try { Directory.Delete(full, true); } catch (IOException) { }
            }
        }

        await Check("automatic DNS replaces manual DNS in both families and completes its journal", async dir =>
        {
            var backend = new FakeBackend(Snapshot());
            var manager = new DnsSessionManager(dir, backend);
            await Reset(manager);
            Require(backend.State.IPv4.Source == DnsSource.Dhcp && backend.State.IPv4.Servers.Length == 0, "IPv4 is still manually configured.");
            Require(backend.State.IPv6.Source == DnsSource.Dhcp && backend.State.IPv6.Servers.Length == 0, "IPv6 is still manually configured.");
            Require(!manager.Managed && !manager.HasJournal && backend.Writes == 2, "Successful automatic DNS left managed mode or an unfinished transaction.");
        });

        await Check("automatic DNS verifies its source without pinning a DHCP lease", async dir =>
        {
            var backend = new FakeBackend(Snapshot()) { AutomaticV4 = ["192.0.2.53"], AutomaticV6 = [] };
            await Reset(new DnsSessionManager(dir, backend));
            Require(backend.State.IPv4.EffectiveServers.SequenceEqual(new[] { "192.0.2.53" }) && backend.State.IPv6.EffectiveServers.Length == 0,
                "Observed DHCP addresses were replaced or an empty lease was rejected.");
        });

        await Check("automatic DNS does not rewrite an already automatic adapter", async dir =>
        {
            var state = Snapshot(); state.IPv4 = Automatic(DnsFamily.IPv4); state.IPv6 = Automatic(DnsFamily.IPv6);
            var backend = new FakeBackend(state);
            await Reset(new DnsSessionManager(dir, backend));
            Require(backend.Writes == 0, "An already automatic source received unnecessary mutations.");
        });

        await Check("automatic DNS leaves disabled IPv6 unchanged", async dir =>
        {
            var state = Snapshot(); state.IPv6.Enabled = false;
            var original = JsonData.Clone(state.IPv6); var backend = new FakeBackend(state);
            await Reset(new DnsSessionManager(dir, backend));
            Require(JsonData.Encode(backend.State.IPv6) == JsonData.Encode(original) && backend.Writes == 1, "A disabled IPv6 binding was modified.");
        });

        await Check("automatic DNS supports enabled IPv6 without enabling IPv4", async dir =>
        {
            var state = Snapshot(); state.IPv4.Enabled = false;
            var original = JsonData.Clone(state.IPv4); var backend = new FakeBackend(state);
            await Reset(new DnsSessionManager(dir, backend));
            Require(JsonData.Encode(backend.State.IPv4) == JsonData.Encode(original) && backend.State.IPv6.Source == DnsSource.Dhcp && backend.Writes == 1,
                "Automatic DNS changed the protocol binding or skipped IPv6.");
        });

        await Check("automatic DNS saves the original and touched family before every mutation", async dir =>
        {
            var backend = new FakeBackend(Snapshot());
            backend.BeforeApply = (_, desired) =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(JournalPath(dir)));
                var journal = json.RootElement;
                Require(journal.GetProperty("Operation").GetString() == "Automatic", "The journal has no automatic-DNS target.");
                Require(journal.GetProperty("Original").GetProperty("AdapterId").GetString() == AdapterId, "The original GUID was not saved.");
                Require(journal.GetProperty(desired.Family == DnsFamily.IPv4 ? "IPv4Touched" : "IPv6Touched").GetBoolean(), "The mutation preceded its durable touched flag.");
            };
            await Reset(new DnsSessionManager(dir, backend));
        });

        await Check("failure after changing IPv6 rolls back both families in original order", async dir =>
        {
            var backend = new FakeBackend(Snapshot()) { FailAfterAutomaticV6 = true };
            var original = JsonData.Clone(backend.State); var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(JsonData.Encode(backend.State) == JsonData.Encode(original), "A partial reset did not restore both original DNS lists.");
            Require(!manager.HasJournal && !manager.Managed, "A completed rollback left ownership active.");
        });

        await Check("an unconfirmed automatic change is rejected and prior families are restored", async dir =>
        {
            var backend = new FakeBackend(Snapshot()) { IgnoreAutomaticV6 = true };
            var original = JsonData.Clone(backend.State); var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(JsonData.Encode(backend.State) == JsonData.Encode(original) && !manager.HasJournal, "An unconfirmed DHCP change was accepted or left a partial reset.");
        });

        await Check("a third-party change in an untouched family is preserved while owned changes roll back", async dir =>
        {
            var backend = new FakeBackend(Snapshot());
            backend.AfterApply = (_, desired) => { if (desired.Family == DnsFamily.IPv4 && desired.Source == DnsSource.Dhcp) backend.State.IPv6 = Manual(DnsFamily.IPv6, ["2001:db8::99"]); };
            var originalV4 = JsonData.Clone(backend.State.IPv4); var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(JsonData.Encode(backend.State.IPv4) == JsonData.Encode(originalV4) && backend.State.IPv6.Servers.SequenceEqual(new[] { "2001:db8::99" }),
                "The reset overwrote the other application's DNS or left its own IPv4 change.");
            Require(!manager.HasJournal, "A fully rolled back owned change retained an unnecessary journal.");
        });

        await Check("foreign DNS after a touched mutation blocks rollback and preserves evidence", async dir =>
        {
            var backend = new FakeBackend(Snapshot());
            backend.AfterApply = (_, desired) =>
            {
                if (desired.Family != DnsFamily.IPv6 || desired.Source != DnsSource.Dhcp) return;
                backend.State.IPv6 = Manual(DnsFamily.IPv6, ["2001:db8::99"]);
                throw new IOException("Injected concurrent replacement.");
            };
            var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(manager.HasJournal && backend.Writes == 2 && backend.State.IPv6.Servers.SequenceEqual(new[] { "2001:db8::99" }), "Conflict was overwritten or recovery evidence was discarded.");
            await Reject(() => Reset(manager));
            Require(backend.Writes == 2, "A new reset ignored unresolved ownership.");
        });

        await Check("startup recovery rolls back an interrupted automatic-DNS journal", async dir =>
        {
            var original = Snapshot();
            AtomicStore.Write(JournalPath(dir), new { Version = 1, SessionId = "1046294a-99bb-4638-84b2-4149cdd8aec3", CreatedUtc = DateTimeOffset.UtcNow,
                Original = original, IPv4Touched = true, IPv6Touched = false, Finished = false, Operation = "Automatic" });
            var backend = new FakeBackend(original); backend.State.IPv4 = Automatic(DnsFamily.IPv4);
            var manager = new DnsSessionManager(dir, backend); await manager.RestoreAsync();
            Require(JsonData.Encode(backend.State) == JsonData.Encode(original) && !manager.HasJournal, "An interrupted DHCP reset was mistaken for a relay transaction.");
        });

        await Check("unknown journal operation blocks DNS recovery without adapter access", async dir =>
        {
            AtomicStore.Write(JournalPath(dir), new { Version = 1, SessionId = "1046294a-99bb-4638-84b2-4149cdd8aec3", CreatedUtc = DateTimeOffset.UtcNow,
                Original = Snapshot(), IPv4Touched = true, IPv6Touched = false, Finished = false, Operation = "Other" });
            var backend = new FakeBackend(Snapshot()); var manager = new DnsSessionManager(dir, backend);
            await Reject(manager.RestoreAsync);
            Require(manager.HasJournal && backend.Reads == 0 && backend.Writes == 0, "An unsupported journal target was guessed.");
        });

        await Check("an active relay transaction blocks an automatic-DNS reset", async dir =>
        {
            var backend = new FakeBackend(Snapshot()); var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId); var writes = backend.Writes;
            await Reject(() => Reset(manager));
            Require(manager.Managed && manager.HasJournal && backend.Writes == writes, "The reset overwrote an active relay journal.");
            await manager.RestoreAsync();
        });

        await Check("automatic reset rejects an unowned loopback DNS", async dir =>
        {
            var state = Snapshot(); state.IPv4 = Manual(DnsFamily.IPv4, ["127.0.0.1"]);
            var backend = new FakeBackend(state); var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(!manager.HasJournal && backend.Writes == 0, "Unowned loopback was used as a fabricated safe snapshot.");
        });

        await Check("automatic reset rejects a mismatched adapter identity before journaling", async dir =>
        {
            var state = Snapshot(); state.AdapterId = "ac23a89e-27cd-4928-8caa-e27779da06dd";
            var backend = new FakeBackend(state); var manager = new DnsSessionManager(dir, backend);
            await Reject(() => Reset(manager));
            Require(!manager.HasJournal && backend.Writes == 0, "DNS reset accepted another adapter's snapshot.");
        });

        await Check("automatic DNS command plan changes DNS source only for IPv4 and IPv6", dir =>
        {
            var v4 = WinDnsBackend.BuildArguments(Automatic(DnsFamily.IPv4), 42);
            var v6 = WinDnsBackend.BuildArguments(Automatic(DnsFamily.IPv6), 73);
            Require(v4.Count == 1 && string.Join(' ', v4[0]) == "interface ipv4 set dnsservers name=42 source=dhcp", "IPv4 plan changes a non-DNS property or uses a stale lease.");
            Require(v6.Count == 1 && string.Join(' ', v6[0]) == "interface ipv6 set dnsservers name=73 source=dhcp", "IPv6 plan changes a non-DNS property or uses a stale lease.");
            return Task.CompletedTask;
        });

        await Check("atomic journal protection runs before replacing the durable primary", dir =>
        {
            var path = JournalPath(dir); AtomicStore.Write(path, new { Value = 1 });
            try
            {
                AtomicStore.Write(path, new { Value = 2 }, temporary =>
                {
                    Require(temporary != path && File.Exists(temporary), "The protector did not receive a complete temporary file.");
                    Require(File.ReadAllText(path).Contains("\"Value\":1", StringComparison.Ordinal), "The new journal became durable before protection.");
                    throw new UnauthorizedAccessException("Injected protection failure.");
                });
                throw new InvalidOperationException("Protection failure was ignored.");
            }
            catch (UnauthorizedAccessException) { }
            Require(File.ReadAllText(path).Contains("\"Value\":1", StringComparison.Ordinal) && !File.Exists(path + ".bak"), "An unprotected journal replaced the trusted original.");
            return Task.CompletedTask;
        });

        await Check("failed initial journal protection blocks DNS mutations", async dir =>
        {
            var backend = new FakeBackend(Snapshot());
            var manager = new DnsSessionManager(dir, backend, _ => throw new UnauthorizedAccessException("Injected ACL failure."));
            await Reject(() => Reset(manager));
            Require(backend.Writes == 0 && !manager.HasJournal, "DNS was changed before the protected original was durably saved.");
        });

        await Check("automatic DNS helper rejects an installed service without an owner receipt", async dir =>
        {
            await Reject(() => { ServiceInstaller.ValidateAutomaticResetOwner(null, "S-1-5-21-100-200-300-1001", true); return Task.CompletedTask; });
        });

        await Check("automatic DNS helper rejects another owner's installed or retained state", async dir =>
        {
            var receipt = new InstallReceipt { OwnerSid = "S-1-5-21-100-200-300-1001" };
            await Reject(() => { ServiceInstaller.ValidateAutomaticResetOwner(receipt, "S-1-5-21-100-200-300-1002", true); return Task.CompletedTask; });
            await Reject(() => { ServiceInstaller.ValidateAutomaticResetOwner(receipt, "S-1-5-21-100-200-300-1002", false); return Task.CompletedTask; });
        });

        await Check("automatic DNS helper accepts its owner or a fresh uninstalled machine", dir =>
        {
            const string owner = "S-1-5-21-100-200-300-1001";
            ServiceInstaller.ValidateAutomaticResetOwner(new InstallReceipt { OwnerSid = owner }, owner, true);
            ServiceInstaller.ValidateAutomaticResetOwner(null, owner, false);
            return Task.CompletedTask;
        });
    }

    private static Task Reset(DnsSessionManager manager) => manager.ResetAutomaticAsync(AdapterId);
    private static string JournalPath(string dir) => Path.Combine(dir, "dns-session.json");
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or AggregateException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("Unsafe operation was accepted.");
    }
    private static DnsFamilySnapshot Manual(DnsFamily family, string[] servers) => new()
    { Family = family, Enabled = true, Source = DnsSource.Static, Servers = servers, EffectiveServers = servers.ToArray() };
    private static DnsFamilySnapshot Automatic(DnsFamily family) => new()
    { Family = family, Enabled = true, Source = DnsSource.Dhcp, Servers = [], EffectiveServers = [] };
    private static AdapterDnsSnapshot Snapshot() => new()
    {
        AdapterId = AdapterId, AdapterName = "Physical test adapter", Up = true,
        IPv4 = Manual(DnsFamily.IPv4, ["9.9.9.9", "1.1.1.1"]), IPv6 = Manual(DnsFamily.IPv6, ["2620:fe::fe", "2001:4860:4860::8888"])
    };

    private sealed class FakeBackend(AdapterDnsSnapshot state) : IDnsBackend
    {
        public AdapterDnsSnapshot State { get; } = JsonData.Clone(state);
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public bool FailAfterAutomaticV6 { get; init; }
        public bool IgnoreAutomaticV6 { get; init; }
        public string[] AutomaticV4 { get; init; } = ["192.0.2.53"];
        public string[] AutomaticV6 { get; init; } = ["2001:db8::53"];
        public Action<DnsFamilySnapshot, DnsFamilySnapshot>? BeforeApply { get; set; }
        public Action<DnsFamilySnapshot, DnsFamilySnapshot>? AfterApply { get; set; }
        public Task<AdapterDnsSnapshot> ReadAsync(string adapterId) { Reads++; return Task.FromResult(JsonData.Clone(State)); }
        public Task ApplyAsync(string adapterId, DnsFamilySnapshot expected, DnsFamilySnapshot desired)
        {
            Require(adapterId == AdapterId, "Mutation lost the selected adapter GUID.");
            Require(DnsState.SameConfiguration(DnsState.Family(State, expected.Family), expected), "Mutation ignored a concurrent change.");
            Require(expected.Enabled && desired.Enabled && expected.Family == desired.Family, "Mutation changed a protocol binding.");
            BeforeApply?.Invoke(expected, desired); Writes++;
            if (IgnoreAutomaticV6 && desired.Family == DnsFamily.IPv6 && desired.Source == DnsSource.Dhcp) return Task.CompletedTask;
            var next = JsonData.Clone(desired);
            if (desired.Source == DnsSource.Dhcp) next.EffectiveServers = desired.Family == DnsFamily.IPv4 ? AutomaticV4.ToArray() : AutomaticV6.ToArray();
            if (desired.Family == DnsFamily.IPv4) State.IPv4 = next; else State.IPv6 = next;
            AfterApply?.Invoke(expected, desired);
            if (FailAfterAutomaticV6 && desired.Family == DnsFamily.IPv6 && desired.Source == DnsSource.Dhcp) throw new IOException("Injected failure after IPv6 source changed.");
            return Task.CompletedTask;
        }
    }
}
