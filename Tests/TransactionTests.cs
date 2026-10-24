using System.Text.Json;

namespace NetPilot.Tests;

public static class TransactionTests
{
    private const string AdapterId = "30c21c5b-bab5-42d5-916e-f8a254f28561";

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<string, Task> test)
        {
            var dir = Path.Combine(Path.GetTempPath(), "NetPilot-fake-DNS-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try { await test(dir); report(name, true, ""); }
            catch (Exception e) { report(name, false, e.Message); }
            finally
            {
                string full = Path.GetFullPath(dir);
                string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("NetPilot-fake-DNS-", StringComparison.Ordinal))
                    throw new InvalidOperationException("Refusing recursive cleanup outside the task's test directory.");
                try { Directory.Delete(full, true); } catch (IOException) { }
            }
        }

        await Check("static DNS preserves both families and exact address order", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            Require(session.Managed && session.HasJournal, "Activation was not durably marked managed.");
            Require(backend.State.IPv4.Servers.SequenceEqual(new[] { "127.0.0.1" }) && backend.State.IPv6.Servers.SequenceEqual(new[] { "::1" }), "Activation did not exclusively choose the relay loopback DNS.");
            await session.RestoreAsync();
            Same(original, backend.State);
            Require(!session.Managed && !session.HasJournal, "Successful restoration retained an active transaction.");
        });

        await Check("DHCP snapshot preserves observation and restores automatic source after lease change", async dir =>
        {
            var state = StaticSnapshot();
            state.IPv4 = Family(DnsFamily.IPv4, DnsSource.Dhcp, [], ["192.168.1.1", "192.168.1.2"]);
            var backend = new FakeBackend(state) { NewDhcpServers = ["192.168.7.1", "192.168.7.2"] };
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            using (var json = JsonDocument.Parse(File.ReadAllText(JournalPath(dir))))
                Require(json.RootElement.GetProperty("Original").GetProperty("IPv4").GetProperty("EffectiveServers").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "192.168.1.1", "192.168.1.2" }), "Historical DHCP DNS order was lost.");
            await session.RestoreAsync();
            Require(backend.State.IPv4.Source == DnsSource.Dhcp && backend.State.IPv4.Servers.Length == 0, "The historical DHCP lease was incorrectly pinned as static DNS.");
            Require(backend.State.IPv4.EffectiveServers.SequenceEqual(new[] { "192.168.7.1", "192.168.7.2" }), "Fresh DHCP DNS was overwritten.");
        });

        await Check("partial IPv6 activation failure restores IPv4 and IPv6", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { FailAfterIpv6Loopback = true };
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await Reject(() => session.EnableAsync(AdapterId));
            Same(original, backend.State);
            Require(!session.Managed && !session.HasJournal, "Rollback did not finish after a partially successful IPv6 command.");
        });

        await Check("write-ahead touched flags exist before every possible activation write", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            backend.BeforeApply = desired =>
            {
                using var json = JsonDocument.Parse(File.ReadAllText(JournalPath(dir)));
                Require(json.RootElement.GetProperty(desired.Family == DnsFamily.IPv4 ? "IPv4Touched" : "IPv6Touched").GetBoolean(), "The command can mutate before its family is in the durable journal.");
                Require(json.RootElement.GetProperty("Original").GetProperty("AdapterId").GetString() == AdapterId, "The durable original adapter GUID is missing.");
            };
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            await session.RestoreAsync();
        });

        await Check("third-party DNS replacement is preserved and blocks further activation", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            backend.State.IPv4 = Family(DnsFamily.IPv4, DnsSource.Static, ["208.67.222.222"]);
            var changed = JsonData.Clone(backend.State);
            var count = backend.Writes;
            await Reject(session.RestoreAsync);
            Same(changed, backend.State);
            Require(session.HasJournal && backend.Writes == count, "Conflict performed writes or discarded recovery evidence.");
            await Reject(() => session.EnableAsync(AdapterId));
            Require(backend.Writes == count, "Activation ignored the unresolved journal.");
        });

        await Check("a new manager recovers the persisted transaction", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var original = JsonData.Clone(backend.State);
            await new DnsSessionManager(dir, backend).EnableAsync(AdapterId);
            var recovered = new DnsSessionManager(dir, backend);
            Require(recovered.HasJournal && !recovered.Managed, "Startup incorrectly assumed a live managed session.");
            await recovered.RestoreAsync();
            Same(original, backend.State);
            Require(!recovered.HasJournal, "Recovered transaction was not completed.");
        });

        await Check("corrupt journal fails closed without reading or writing the adapter", async dir =>
        {
            File.WriteAllText(JournalPath(dir), "{ definitely not a snapshot");
            var backend = new FakeBackend(StaticSnapshot());
            var session = new DnsSessionManager(dir, backend);
            Require(session.HasJournal, "Corruption was treated as no journal.");
            await Reject(session.RestoreAsync);
            await Reject(() => session.EnableAsync(AdapterId));
            Require(File.Exists(JournalPath(dir)) && backend.Writes == 0 && backend.Reads == 0, "Corruption caused a network access or loss of evidence.");
        });

        await Check("a backup without its primary journal blocks all network writes", async dir =>
        {
            File.WriteAllText(JournalPath(dir) + ".bak", "{}");
            var backend = new FakeBackend(StaticSnapshot());
            var session = new DnsSessionManager(dir, backend);
            Require(session.HasJournal, "A surviving backup was ignored.");
            await Reject(session.RestoreAsync);
            await Reject(() => session.EnableAsync(AdapterId));
            Require(backend.Writes == 0 && backend.Reads == 0, "A missing primary used a potentially stale backup to mutate DNS.");
        });

        await Check("journal loss during management is an explicit unrecoverable state", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            File.Delete(JournalPath(dir)); File.Delete(JournalPath(dir) + ".bak");
            var count = backend.Writes;
            await Reject(session.RestoreAsync);
            await Reject(() => session.EnableAsync(AdapterId));
            Require(backend.Writes == count, "Lost recovery information was replaced with an invented snapshot.");
        });

        await Check("missing adapter preserves journal until the original GUID returns", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            backend.Missing = true;
            await Reject(session.RestoreAsync);
            Require(session.HasJournal, "Missing adapter discarded the original snapshot.");
            backend.Missing = false;
            await session.RestoreAsync();
            Same(original, backend.State);
        });

        await Check("disabled IPv6 is neither changed nor enabled", async dir =>
        {
            var state = StaticSnapshot(); state.IPv6.Enabled = false;
            var backend = new FakeBackend(state);
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId); await session.RestoreAsync();
            Same(original, backend.State);
            Require(backend.AppliedFamilies.All(x => x == DnsFamily.IPv4), "An IPv6 command was sent to a disabled binding.");
        });

        await Check("successful command without an actual DNS change triggers rollback", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { IgnoreIpv6Loopback = true };
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await Reject(() => session.EnableAsync(AdapterId));
            Same(original, backend.State);
            Require(!session.Managed && !session.HasJournal, "An unverified activation was accepted.");
        });

        await Check("a completed family is not overwritten when recovery resumes", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { FailBeforeIpv4Restore = true };
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            await Reject(session.RestoreAsync);
            Require(session.HasJournal && backend.State.IPv6.Servers.SequenceEqual(original.IPv6.Servers), "The successful IPv6 restoration was not retained.");
            backend.State.IPv6 = Family(DnsFamily.IPv6, DnsSource.Static, ["2001:4860:4860::8844"]);
            await new DnsSessionManager(dir, backend).RestoreAsync();
            Require(backend.State.IPv4.Servers.SequenceEqual(original.IPv4.Servers), "IPv4 recovery failed.");
            Require(backend.State.IPv6.Servers.SequenceEqual(new[] { "2001:4860:4860::8844" }), "Recovery overwrote a completed family's later third-party change.");
        });

        await Check("netsh static plan restores address order without modifying IP or routes", dir =>
        {
            var plan = WinDnsBackend.BuildArguments(Family(DnsFamily.IPv4, DnsSource.Static, ["9.9.9.9", "1.1.1.1", "8.8.8.8"]), 42);
            string[] expected =
            [
                "interface ipv4 set dnsservers name=42 source=static address=9.9.9.9 validate=no",
                "interface ipv4 add dnsservers name=42 address=1.1.1.1 index=2 validate=no",
                "interface ipv4 add dnsservers name=42 address=8.8.8.8 index=3 validate=no"
            ];
            Require(plan.Select(x => string.Join(' ', x)).SequenceEqual(expected), "Static netsh plan has a wrong source, address order, target, or non-DNS operation.");
            return Task.CompletedTask;
        });

        await Check("netsh DHCP plan restores only automatic source with the current index", dir =>
        {
            var saved = Family(DnsFamily.IPv6, DnsSource.Dhcp, [], ["2001:db8::53", "2001:db8::54"]);
            var plan = WinDnsBackend.BuildArguments(saved, 73);
            Require(plan.Count == 1 && string.Join(' ', plan[0]) == "interface ipv6 set dnsservers name=73 source=dhcp", "DHCP plan reapplies stale observed addresses or changes a non-DNS property.");
            return Task.CompletedTask;
        });

        await Check("syntactically valid but incomplete journal is never accepted", async dir =>
        {
            File.WriteAllText(JournalPath(dir), "{}");
            var backend = new FakeBackend(StaticSnapshot());
            var session = new DnsSessionManager(dir, backend);
            await Reject(session.RestoreAsync);
            Require(session.HasJournal && backend.Reads == 0 && backend.Writes == 0, "Default values fabricated an original DNS snapshot.");
        });

        await Check("unowned loopback without a journal never becomes an invented original", async dir =>
        {
            var state = StaticSnapshot(); state.IPv4 = Family(DnsFamily.IPv4, DnsSource.Static, ["127.0.0.1"]);
            var backend = new FakeBackend(state);
            var session = new DnsSessionManager(dir, backend);
            await Reject(() => session.EnableAsync(AdapterId));
            Require(!session.Managed && !session.HasJournal && backend.Writes == 0, "A missing restoration journal was silently replaced with loopback as the original.");
        });

        await Check("partial static restoration resumes its own durable attempt after reopening", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { FailDuringIpv4Restore = true };
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            await Reject(session.RestoreAsync);
            Require(session.HasJournal && backend.State.IPv4.Servers.SequenceEqual(new[] { "9.9.9.9" }), "Injected partial restoration was lost or falsely reported complete.");
            await new DnsSessionManager(dir, backend).RestoreAsync();
            Require(backend.State.IPv4.Servers.SequenceEqual(new[] { "9.9.9.9", "1.1.1.1", "8.8.8.8" }) && !session.HasJournal,
                "The application's own partially restored list could never be completed.");
        });

        await Check("a timed-out restoration that actually committed is verified and completed", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { TimeoutAfterIpv6Restore = true };
            var original = JsonData.Clone(backend.State);
            var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId);
            await manager.RestoreAsync();
            Same(original, backend.State);
            Require(!manager.HasJournal && !manager.Managed, "A completed command was left permanently pending after its timeout.");
        });

        await Check("failure restoring IPv6 does not prevent restoring the owned IPv4 family", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { FailBeforeIpv6Restore = true };
            var original = JsonData.Clone(backend.State);
            var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId);
            await Reject(manager.RestoreAsync);
            Require(backend.State.IPv4.Servers.SequenceEqual(original.IPv4.Servers), "One failed family stranded IPv4 on 127.0.0.1.");
            Require(manager.HasJournal && backend.State.IPv6.Servers.SequenceEqual(new[] { "::1" }), "The unconfirmed IPv6 restoration was falsely completed.");
        });

        await Check("restoring automatic DNS cannot confirm effective loopback servers", async dir =>
        {
            var state = StaticSnapshot();
            state.IPv4 = Family(DnsFamily.IPv4, DnsSource.Dhcp, [], ["192.168.1.1"]);
            var backend = new FakeBackend(state) { NewDhcpServers = ["127.0.0.1"] };
            var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId);
            await Reject(manager.RestoreAsync);
            Require(manager.HasJournal && backend.State.IPv4.EffectiveServers.SequenceEqual(new[] { "127.0.0.1" }), "DHCP source alone was mistaken for a working restoration.");
        });

        await Check("a failed primary restore uses the alternative backend for the same owned family", async dir =>
        {
            var backend = new RecoveryBackend(StaticSnapshot()) { FailBeforeIpv6Restore = true };
            var original = JsonData.Clone(backend.State);
            var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId);
            await manager.RestoreAsync();
            Same(original, backend.State);
            Require(backend.RecoveryCalls == 1 && !manager.HasJournal, "A primary failure did not reach verified alternative restoration.");
        });

        await Check("the alternative restore never overwrites a third-party replacement after primary failure", async dir =>
        {
            var backend = new RecoveryBackend(StaticSnapshot());
            var manager = new DnsSessionManager(dir, backend);
            await manager.EnableAsync(AdapterId);
            backend.BeforeApply = desired =>
            {
                if (desired.Family != DnsFamily.IPv6 || desired.Servers.SequenceEqual(new[] { "::1" })) return;
                backend.State.IPv6 = Family(DnsFamily.IPv6, DnsSource.Static, ["2001:4860:4860::8844"]);
                throw new IOException("Injected simultaneous third-party replacement.");
            };
            await Reject(manager.RestoreAsync);
            Require(backend.RecoveryCalls == 0 && manager.HasJournal && backend.State.IPv6.Servers.SequenceEqual(new[] { "2001:4860:4860::8844" }),
                "Fallback ignored a replacement outside the journal's known states.");
        });

        await Check("an unconfirmed running writer blocks rollback and survives reopening until its process identity changes", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot()) { WriterStillRunningAfterIpv6Loopback = true };
            var original = JsonData.Clone(backend.State);
            var manager = new DnsSessionManager(dir, backend);
            await Reject(() => manager.EnableAsync(AdapterId));
            Require(manager.HasJournal && backend.Writes == 2 && backend.State.IPv4.Servers.SequenceEqual(new[] { "127.0.0.1" }), "Rollback raced an unconfirmed running writer.");
            var writes = backend.Writes;
            await Reject(() => new DnsSessionManager(dir, backend).RestoreAsync());
            Require(backend.Writes == writes, "Reopening lost the live-writer guard.");
            var pending = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(JournalPath(dir)))!;
            using var currentProcess = System.Diagnostics.Process.GetCurrentProcess();
            // Same PID with another start time means the old writer has gone; do not
            // block restoration merely because its PID has since been reused.
            pending["PendingWriterStartUtcTicks"] = currentProcess.StartTime.ToUniversalTime().Ticks - TimeSpan.TicksPerSecond;
            File.WriteAllText(JournalPath(dir), pending.ToJsonString());
            await new DnsSessionManager(dir, backend).RestoreAsync();
            Same(original, backend.State);
            Require(!manager.HasJournal, "A reused PID permanently blocked restoration.");
        });

        await Check("original-looking static registry without effective confirmation retains recovery journal", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            var original = JsonData.Clone(backend.State);
            var session = new DnsSessionManager(dir, backend);
            await session.EnableAsync(AdapterId);
            backend.State.IPv6 = JsonData.Clone(original.IPv6);
            backend.State.IPv6.EffectiveServers = ["::1"];
            var v6Writes = backend.AppliedFamilies.Count(x => x == DnsFamily.IPv6);
            await Reject(session.RestoreAsync);
            Require(session.HasJournal && backend.AppliedFamilies.Count(x => x == DnsFamily.IPv6) == v6Writes
                && backend.State.IPv6.EffectiveServers.SequenceEqual(new[] { "::1" }), "Configuration-only equality concealed an unverified effective DNS state.");
        });

        await Check("missing journal touched flag is corruption and cannot silently abandon IPv4 loopback", async dir =>
        {
            var backend = new FakeBackend(StaticSnapshot());
            await new DnsSessionManager(dir, backend).EnableAsync(AdapterId);
            var malformed = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(JournalPath(dir)))!.AsObject();
            malformed.Remove("IPv4Touched");
            File.WriteAllText(JournalPath(dir), malformed.ToJsonString());
            var reads = backend.Reads; var writes = backend.Writes;
            var recovered = new DnsSessionManager(dir, backend);
            await Reject(recovered.RestoreAsync);
            Require(recovered.HasJournal && backend.Writes == writes && backend.Reads == reads, "An omitted write-ahead flag was treated as false, losing recovery ownership.");
        });
    }

    private static string JournalPath(string dir) => Path.Combine(dir, "dns-session.json");
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Reject(Func<Task> action)
    {
        try { await action(); }
        catch (Exception e) when (e is InvalidOperationException or InvalidDataException or IOException or AggregateException or UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("The unsafe or failed operation was accepted.");
    }
    private static void Same(AdapterDnsSnapshot expected, AdapterDnsSnapshot actual)
    {
        Require(JsonData.Encode(expected) == JsonData.Encode(actual), "DNS source, configured/effective address order, enabled families, or adapter identity changed.");
    }
    private static DnsFamilySnapshot Family(DnsFamily family, DnsSource source, string[] servers, string[]? effective = null) => new()
    { Family = family, Enabled = true, Source = source, Servers = servers, EffectiveServers = effective ?? servers.ToArray() };
    private static AdapterDnsSnapshot StaticSnapshot() => new()
    {
        AdapterId = AdapterId, AdapterName = "Physical test adapter", Up = true,
        IPv4 = Family(DnsFamily.IPv4, DnsSource.Static, ["9.9.9.9", "1.1.1.1", "8.8.8.8"]),
        IPv6 = Family(DnsFamily.IPv6, DnsSource.Static, ["2620:fe::fe", "2001:4860:4860::8888"])
    };

    private class FakeBackend(AdapterDnsSnapshot state) : IDnsBackend
    {
        public AdapterDnsSnapshot State { get; set; } = JsonData.Clone(state);
        public bool FailAfterIpv6Loopback { get; set; }
        public bool IgnoreIpv6Loopback { get; set; }
        public bool FailBeforeIpv4Restore { get; set; }
        public bool FailDuringIpv4Restore { get; set; }
        public bool TimeoutAfterIpv6Restore { get; set; }
        public bool FailBeforeIpv6Restore { get; set; }
        public bool WriterStillRunningAfterIpv6Loopback { get; set; }
        public bool Missing { get; set; }
        public int Reads { get; private set; }
        public int Writes { get; private set; }
        public string[]? NewDhcpServers { get; set; }
        public List<DnsFamily> AppliedFamilies { get; } = [];
        public Action<DnsFamilySnapshot>? BeforeApply { get; set; }
        public Task<AdapterDnsSnapshot> ReadAsync(string adapterId)
        {
            Reads++;
            if (Missing || !Guid.TryParse(adapterId, out var id) || id != Guid.Parse(AdapterId)) throw new IOException("Original adapter cannot be resolved.");
            return Task.FromResult(JsonData.Clone(State));
        }
        public Task ApplyAsync(string adapterId, DnsFamilySnapshot expected, DnsFamilySnapshot desired)
        {
            if (adapterId != AdapterId || Missing) throw new IOException("Wrong or missing adapter.");
            var current = desired.Family == DnsFamily.IPv4 ? State.IPv4 : State.IPv6;
            Require(current.Enabled == expected.Enabled && current.Source == expected.Source && current.Servers.SequenceEqual(expected.Servers), "The manager supplied a stale expected DNS configuration.");
            BeforeApply?.Invoke(desired);
            bool loopback = desired.Source == DnsSource.Static && desired.Servers is ["127.0.0.1"] or ["::1"];
            if (FailBeforeIpv4Restore && desired.Family == DnsFamily.IPv4 && !loopback)
            { FailBeforeIpv4Restore = false; throw new IOException("Injected failure before IPv4 restore."); }
            if (FailBeforeIpv6Restore && desired.Family == DnsFamily.IPv6 && !loopback)
            { FailBeforeIpv6Restore = false; throw new IOException("Injected failure before IPv6 restore."); }
            Writes++; AppliedFamilies.Add(desired.Family);
            if (IgnoreIpv6Loopback && desired.Family == DnsFamily.IPv6 && loopback) return Task.CompletedTask;
            var next = JsonData.Clone(desired);
            next.EffectiveServers = desired.Source == DnsSource.Dhcp ? NewDhcpServers ?? desired.EffectiveServers.ToArray() : desired.Servers.ToArray();
            if (desired.Family == DnsFamily.IPv4) State.IPv4 = next; else State.IPv6 = next;
            if (FailDuringIpv4Restore && desired.Family == DnsFamily.IPv4 && !loopback)
            {
                FailDuringIpv4Restore = false;
                State.IPv4.Servers = [desired.Servers[0]];
                State.IPv4.EffectiveServers = [desired.Servers[0]];
                throw new IOException("Injected failure between static DNS restoration commands.");
            }
            if (FailAfterIpv6Loopback && desired.Family == DnsFamily.IPv6 && loopback)
            { FailAfterIpv6Loopback = false; throw new IOException("Injected failure after IPv6 changed."); }
            if (TimeoutAfterIpv6Restore && desired.Family == DnsFamily.IPv6 && !loopback)
            { TimeoutAfterIpv6Restore = false; throw new TimeoutException("Injected timeout after the DNS change committed."); }
            if (WriterStillRunningAfterIpv6Loopback && desired.Family == DnsFamily.IPv6 && loopback)
            {
                WriterStillRunningAfterIpv6Loopback = false;
                using var process = System.Diagnostics.Process.GetCurrentProcess();
                throw new DnsCommandStillRunningException("Injected unconfirmed running writer.")
                { ProcessId = process.Id, StartUtcTicks = process.StartTime.ToUniversalTime().Ticks };
            }
            return Task.CompletedTask;
        }
    }

    private sealed class RecoveryBackend(AdapterDnsSnapshot state) : FakeBackend(state), IDnsRecoveryBackend
    {
        public int RecoveryCalls { get; private set; }
        public Task ApplyRecoveryAsync(string adapterId, DnsFamilySnapshot expected, DnsFamilySnapshot desired)
        {
            RecoveryCalls++;
            return ApplyAsync(adapterId, expected, desired);
        }
    }
}
