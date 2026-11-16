using System.ComponentModel;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace NetPilot.Tests;

public static class ApplicationConnectionResetTests
{
    private const string UserSid = "S-1-5-21-101-202-303-1001", OtherSid = "S-1-5-21-101-202-303-1002";
    private static readonly ApplicationReconnectOwner Owner = new(UserSid, 4);
    private const ApplicationReconnectTargets Both = ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex;

    // Every test uses in-memory process/socket snapshots or caller validation. No native reset/flush is invoked.
    public static void Run(Action<string, bool, string> report)
    {
        void Check(string name, Action test)
        {
            try { test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        Check("application reset rejects invalid targets before reading or mutating Windows", () =>
        {
            foreach (var targets in new[] { ApplicationReconnectTargets.None, (ApplicationReconnectTargets)4, (ApplicationReconnectTargets)(-1) })
            {
                var fake = Ready();
                Reject<ArgumentException>(() => ApplicationConnectionReset.Execute(targets, Owner, fake));
                Require(fake.Calls.Count == 0, "Invalid flags reached the platform.");
            }
        });
        Check("application reset refuses unconfirmed users and service sessions before flushing", () =>
        {
            foreach (var owner in new[] { new ApplicationReconnectOwner("", 4), new("not-a-sid", 4), new(UserSid, 0), new(UserSid, -1),
                new("S-1-5-18", 4), new("S-1-5-19", 4), new("S-1-5-20", 4), new("S-1-5-80-1-2-3-4-5", 4) })
            {
                var fake = Ready();
                Reject<UnauthorizedAccessException>(() => ApplicationConnectionReset.Execute(Both, owner, fake));
                Require(fake.Calls.Count == 0, "An invalid owner reached the platform.");
            }
        });
        Check("application reset checks elevation and confirms DNS flush before socket work", () =>
        {
            var denied = Ready(); denied.Administrator = false;
            Reject<UnauthorizedAccessException>(() => ApplicationConnectionReset.Execute(Both, Owner, denied));
            Require(denied.Calls.SequenceEqual(new[] { "admin" }) && denied.Writes.Count == 0, "Non-admin attempted a flush or reset.");
            var flushFailed = Ready(); flushFailed.FlushOk = false;
            Reject<IOException>(() => ApplicationConnectionReset.Execute(Both, Owner, flushFailed));
            Require(flushFailed.Calls.SequenceEqual(new[] { "admin", "flush" }) && flushFailed.Writes.Count == 0, "A failed flush was followed by process/socket work.");
            var working = Ready(); ApplicationConnectionReset.Execute(Both, Owner, working);
            Require(working.Calls.Take(3).SequenceEqual(new[] { "admin", "flush", "pids" }), "Flush was not performed before reading and resetting.");
        });
        Check("application reset targets exact current image names and the caller user and session", () =>
        {
            var fake = Ready(); fake.Processes.Clear(); fake.Ipv4.Clear(); fake.Ipv6.Clear();
            var processes = new[] { Process(1), Process(2, "CoDeX.ExE"), Process(3) with { Sid = OtherSid },
                Process(4) with { SessionId = 9 }, Process(5, "firefox.exe"), Process(6, "chrome.exe.old"),
                Process(7, "RtlineDnsPilot.exe"), Process(fake.CurrentProcessId), Process(9) with { ImagePath = "chrome.exe" },
                Process(11, "code.exe"), Process(12, "codex-helper.exe") };
            foreach (var process in processes) { fake.Processes[process.ProcessId] = process; fake.Ipv4.Add(Connection(process.ProcessId)); }
            // A mismatching identity returned for an enumerated PID is also untrusted.
            fake.Processes[10] = Process(100); fake.Ipv4.Add(Connection(10));
            fake.Ipv6.Add(Connection(2, ipv6: true)); fake.Ipv6.Add(Connection(3, ipv6: true));
            fake.Ipv4.Add(Connection(1)); // A repeated snapshot row must not be deleted twice.
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result == new ApplicationReconnectResult(2, 2, 0, 0, 1), "Unexpected target/count result: " + result);
            Require(fake.Writes.Select(x => x.ProcessId).OrderBy(x => x).SequenceEqual(new[] { 1, 2 }), "A foreign user, session, application or current Pilot process was touched.");
        });
        Check("application reset respects the separate Chrome and Codex choices", () =>
        {
            foreach (var choice in new[] { ApplicationReconnectTargets.Chrome, ApplicationReconnectTargets.Codex })
            {
                var fake = Ready(); fake.Processes[2] = Process(2, "codex.exe"); fake.Ipv4.Add(Connection(2));
                fake.Ipv6.Add(Connection(1, ipv6: true)); fake.Ipv6.Add(Connection(2, ipv6: true));
                var result = ApplicationConnectionReset.Execute(choice, Owner, fake);
                int expectedPid = choice == ApplicationReconnectTargets.Chrome ? 1 : 2;
                Require(result == new ApplicationReconnectResult(1, 1, 0, 0, 1) && fake.Writes.Single().ProcessId == expectedPid,
                    "The unselected application was reset or included in counts.");
            }
        });
        Check("application reset skips loopback, listeners, closing sockets and non-HTTPS endpoints", () =>
        {
            var fake = Ready(); fake.Ipv4.Clear();
            var good = new[] { Connection(1) with { LocalPort = 50001 }, Connection(1) with { LocalPort = 50002, State = ApplicationReconnectTcpState.SynSent },
                Connection(1) with { LocalPort = 50003, State = ApplicationReconnectTcpState.CloseWait } };
            fake.Ipv4.AddRange(good);
            foreach (var state in new[] { ApplicationReconnectTcpState.Closed, ApplicationReconnectTcpState.Listen, ApplicationReconnectTcpState.SynReceived,
                ApplicationReconnectTcpState.FinWait1, ApplicationReconnectTcpState.FinWait2, ApplicationReconnectTcpState.Closing,
                ApplicationReconnectTcpState.LastAck, ApplicationReconnectTcpState.TimeWait, ApplicationReconnectTcpState.DeleteTcb })
                fake.Ipv4.Add(Connection(1) with { LocalPort = 50100 + (int)state, State = state });
            fake.Ipv4.AddRange(new[] { Connection(1) with { RemotePort = 80 }, Connection(1) with { LocalPort = 443 },
                Connection(1) with { LocalAddress = IPAddress.Parse("127.2.3.4") }, Connection(1) with { RemoteAddress = IPAddress.Loopback },
                Connection(1) with { RemoteAddress = IPAddress.Any }, Connection(1) with { LocalAddress = IPAddress.Any },
                Connection(1) with { LocalPort = 0 }, Connection(1) with { RemoteAddress = IPAddress.IPv6Loopback },
                // An inbound accepted connection from peer source port 443 is excluded by its local listener.
                Connection(1) with { LocalPort = 8080 }, Connection(1) with { LocalPort = 8080, LocalAddress = IPAddress.Any, State = ApplicationReconnectTcpState.Listen } });
            fake.Ipv6.Add(Connection(1, ipv6: true));
            fake.Ipv6.Add(Connection(1, ipv6: true) with { LocalPort = 52002, RemoteAddress = IPAddress.IPv6Loopback });
            fake.Ipv6.Add(Connection(1, ipv6: true) with { LocalPort = 52003, RemoteAddress = IPAddress.Parse("::ffff:127.0.0.2") });
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result == new ApplicationReconnectResult(1, 3, 0, 0, 1), "Ineligible sockets were included: " + result);
            Require(fake.Writes.SequenceEqual(good), "The requested full endpoints were altered or an IPv6/ineligible socket was reset.");
        });
        Check("application reset revalidates missing, replaced and reused process identities before deletion", () =>
        {
            var original = Process(1);
            foreach (var replacement in new ApplicationReconnectProcess?[] { null, original with { StartUtcTicks = original.StartUtcTicks + 1 },
                original with { ProcessId = 99 }, original with { Sid = OtherSid }, original with { SessionId = 5 },
                original with { ImagePath = @"C:\Different\chrome.exe" }, original with { ImagePath = @"C:\Apps\firefox.exe" } })
            {
                var fake = Ready(); fake.OnReadProcess = (pid, read) => read == 1 ? original : replacement;
                var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
                Require(result == new ApplicationReconnectResult(1, 0, 0, 1, 0) && fake.Writes.Count == 0, "A stale PID/identity reached deletion.");
            }
            var missing = Ready(); missing.OnReadProcess = (_, _) => null;
            Require(ApplicationConnectionReset.Execute(Both, Owner, missing).MatchedProcesses == 0 && missing.Writes.Count == 0,
                "A process without a confirmed identity was targeted.");
        });
        Check("application reset requires a fresh complete TCP tuple and PID before deletion", () =>
        {
            var original = Connection(1);
            foreach (var replacement in new ApplicationReconnectTcpConnection?[] { null, original with { ProcessId = 2 }, original with { LocalPort = original.LocalPort + 1 },
                original with { RemotePort = 8443 }, original with { LocalAddress = IPAddress.Parse("192.0.2.21") },
                original with { RemoteAddress = IPAddress.Parse("198.51.100.21") }, original with { State = ApplicationReconnectTcpState.TimeWait } })
            {
                var fake = Ready();
                fake.OnReadTable = (family, read) => family == AddressFamily.InterNetworkV6 ? Array.Empty<ApplicationReconnectTcpConnection>()
                    : read == 1 ? new[] { original } : replacement is null ? Array.Empty<ApplicationReconnectTcpConnection>() : new[] { replacement };
                var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
                Require(result == new ApplicationReconnectResult(1, 0, 0, 1, 0) && fake.Writes.Count == 0, "A stale/foreign TCP tuple reached deletion.");
            }
        });
        Check("application reset accepts a still-live tuple transitioning to close-wait", () =>
        {
            // Connection() creates fresh IPAddress instances: tuple identity must use address values, not references.
            var fake = Ready(); var closing = Connection(1) with { State = ApplicationReconnectTcpState.CloseWait };
            fake.OnReadTable = (family, read) => family == AddressFamily.InterNetworkV6 ? Array.Empty<ApplicationReconnectTcpConnection>()
                : new[] { read == 1 ? Connection(1) : closing };
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result.Disconnected == 1 && fake.Writes.Single() == closing, "A current eligible TCP state was discarded or the stale row was used.");
        });
        Check("application reset accounts separately for success, write failure and a concurrent close", () =>
        {
            var fake = Ready(); fake.Ipv4.Add(Connection(1) with { LocalPort = 55002 }); fake.Ipv4.Add(Connection(1) with { LocalPort = 55003 });
            fake.OnDisconnect = (_, row) => row.LocalPort == 55002 ? ApplicationReconnectWriteOutcome.Failed
                : row.LocalPort == 55003 ? ApplicationReconnectWriteOutcome.AlreadyClosed : ApplicationReconnectWriteOutcome.Disconnected;
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result == new ApplicationReconnectResult(1, 1, 1, 1, 0) && fake.Writes.Count == 3, "A write failure or natural close was reported as a successful reset.");
        });
        Check("application reset counts per-connection access errors and continues other eligible sockets", () =>
        {
            var fake = Ready(); fake.Ipv4.Add(Connection(1) with { LocalPort = 55002 });
            fake.OnDisconnect = (_, row) => row.LocalPort == Connection(1).LocalPort ? throw new Win32Exception(5) : ApplicationReconnectWriteOutcome.Disconnected;
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result == new ApplicationReconnectResult(1, 1, 1, 0, 0), "A per-socket permission error lost the result or blocked other sockets.");
        });
        Check("application reset stops before writes when an initial table cannot be read", () =>
        {
            var fake = Ready(); fake.OnReadTable = (family, _) => family == AddressFamily.InterNetwork ? fake.Ipv4 : throw new Win32Exception(5);
            Reject<Win32Exception>(() => ApplicationConnectionReset.Execute(Both, Owner, fake));
            Require(fake.Writes.Count == 0, "A socket was deleted before all initial tables were available.");
        });
        Check("application reset reports a candidate table-read error as failure without guessing", () =>
        {
            var fake = Ready(); fake.OnReadTable = (family, read) => family == AddressFamily.InterNetworkV6 ? fake.Ipv6
                : read == 1 ? fake.Ipv4 : throw new IOException("fixture read failed");
            var result = ApplicationConnectionReset.Execute(Both, Owner, fake);
            Require(result == new ApplicationReconnectResult(1, 0, 1, 0, 0) && fake.Writes.Count == 0, "An unverifiable candidate was reset or called already closed.");
        });
        Check("application reset authenticates the caller PID, start, package image, SID and session", () =>
        {
            const string image = @"C:\Package\RtlineDnsPilot.exe";
            var caller = Process(70) with { ImagePath = image };
            var owner = NativeApplicationReconnectPlatform.ValidateCallerIdentity(caller, caller.ProcessId, caller.StartUtcTicks, image.ToUpperInvariant());
            Require(owner == Owner, "Caller SID/session did not come from its confirmed token identity.");
            foreach (var replacement in new ApplicationReconnectProcess?[] { null, caller with { ProcessId = 71 }, caller with { StartUtcTicks = caller.StartUtcTicks + 1 },
                caller with { ImagePath = @"C:\Other\RtlineDnsPilot.exe" }, caller with { SessionId = 0 }, caller with { Sid = "S-1-5-18" } })
                Reject<UnauthorizedAccessException>(() => NativeApplicationReconnectPlatform.ValidateCallerIdentity(replacement, caller.ProcessId, caller.StartUtcTicks, image));
            Reject<UnauthorizedAccessException>(() => NativeApplicationReconnectPlatform.ValidateCallerIdentity(caller with { ImagePath = @"C:\Package\other.exe" },
                caller.ProcessId, caller.StartUtcTicks, @"C:\Package\other.exe"));
        });
        Check("application reset marshals the IPv4 endpoints and network-byte-order ports exactly", () =>
        {
            var connection = Connection(1) with { LocalPort = 0xc123, LocalAddress = IPAddress.Parse("192.0.2.19"), RemoteAddress = IPAddress.Parse("198.51.100.7") };
            var native = NativeApplicationReconnectPlatform.CreateDeleteRow(connection);
            Require(Marshal.SizeOf<NativeApplicationReconnectPlatform.NativeDeleteRow>() == 20 && native.State == 12, "MIB_TCPROW layout/state is incorrect.");
            Require(native.LocalPort == 0x23c1 && native.RemotePort == 0xbb01, "TCP ports were not converted to network byte order.");
            Require(BitConverter.GetBytes(native.LocalAddress).SequenceEqual(connection.LocalAddress.GetAddressBytes())
                && BitConverter.GetBytes(native.RemoteAddress).SequenceEqual(connection.RemoteAddress.GetAddressBytes()), "The IPv4 endpoints were changed.");
            Require(NativeApplicationReconnectPlatform.NetworkToHostPort(0xbeefbb01) == 443, "Network port decoding included the unused upper WORD.");
            Require(NativeApplicationReconnectPlatform.NetworkToHostPort(native.LocalPort) == connection.LocalPort, "The local port failed a round trip.");
        });
        Check("application reset parses documented IPv4 and IPv6 OWNER_PID row layouts without native effects", () =>
        {
            WithBuffer(4 + 24, (buffer, bytes) =>
            {
                Marshal.WriteInt32(buffer, 1); IntPtr row = IntPtr.Add(buffer, 4);
                Marshal.WriteInt32(row, 0, 5); CopyAddress(row, 4, "192.0.2.19"); WritePort(row, 8, 0xc123);
                CopyAddress(row, 12, "198.51.100.7"); WritePort(row, 16, 443); Marshal.WriteInt32(row, 20, 1234);
                var result = NativeApplicationReconnectPlatform.DecodeTable(buffer, bytes, AddressFamily.InterNetwork).Single();
                Require(result == new ApplicationReconnectTcpConnection(1234, IPAddress.Parse("192.0.2.19"), 0xc123, IPAddress.Parse("198.51.100.7"), 443,
                    ApplicationReconnectTcpState.Established), "The IPv4 OWNER_PID layout was decoded incorrectly.");
            });
            WithBuffer(4 + 56, (buffer, bytes) =>
            {
                Marshal.WriteInt32(buffer, 1); IntPtr row = IntPtr.Add(buffer, 4);
                CopyAddress(row, 0, "2001:db8::19"); WritePort(row, 20, 0xc123); CopyAddress(row, 24, "2001:db8::7");
                WritePort(row, 44, 443); Marshal.WriteInt32(row, 48, 3); Marshal.WriteInt32(row, 52, 1234);
                var result = NativeApplicationReconnectPlatform.DecodeTable(buffer, bytes, AddressFamily.InterNetworkV6).Single();
                Require(result == new ApplicationReconnectTcpConnection(1234, IPAddress.Parse("2001:db8::19"), 0xc123, IPAddress.Parse("2001:db8::7"), 443,
                    ApplicationReconnectTcpState.SynSent), "The IPv6 OWNER_PID layout was decoded incorrectly.");
            });
        });
        Check("application reset rejects truncated and oversized native TCP tables", () =>
        {
            WithBuffer(4 + 24, (buffer, bytes) =>
            {
                Marshal.WriteInt32(buffer, 2);
                Reject<IOException>(() => NativeApplicationReconnectPlatform.DecodeTable(buffer, bytes, AddressFamily.InterNetwork));
                Marshal.WriteInt32(buffer, -1);
                Reject<IOException>(() => NativeApplicationReconnectPlatform.DecodeTable(buffer, bytes, AddressFamily.InterNetwork));
                Reject<IOException>(() => NativeApplicationReconnectPlatform.DecodeTable(buffer, 3, AddressFamily.InterNetwork));
                Reject<IOException>(() => NativeApplicationReconnectPlatform.DecodeTable(buffer, 16 * 1024 * 1024 + 1, AddressFamily.InterNetwork));
            });
        });
        Check("application reset native read-only smoke verifies process identity and both TCP table layouts", () =>
        {
            var native = new NativeApplicationReconnectPlatform();
            using var current = System.Diagnostics.Process.GetCurrentProcess();
            using var token = System.Security.Principal.WindowsIdentity.GetCurrent();
            var identity = native.ReadProcess(Environment.ProcessId);
            Require(identity is not null && identity.ProcessId == Environment.ProcessId && identity.StartUtcTicks == current.StartTime.ToUniversalTime().Ticks
                && identity.SessionId == current.SessionId && identity.Sid == token.User?.Value && Path.IsPathFullyQualified(identity.ImagePath)
                && Path.GetFileName(identity.ImagePath).Equals(Path.GetFileName(Environment.ProcessPath), StringComparison.OrdinalIgnoreCase),
                "The native current-process identity did not match the runtime identity.");
            foreach (var family in new[] { AddressFamily.InterNetwork, AddressFamily.InterNetworkV6 })
            {
                var rows = native.ReadTcpTable(family);
                Require(rows.All(x => x.ProcessId >= 0 && x.LocalPort is >= 0 and <= 65535 && x.RemotePort is >= 0 and <= 65535
                    && x.LocalAddress.AddressFamily == family && x.RemoteAddress.AddressFamily == family), "A native TCP row had an invalid shape.");
            }
            // Do not report process paths, SID, addresses or ports. Neither Flush nor Disconnect is called.
        });
    }

    private static ApplicationReconnectProcess Process(int pid, string name = "chrome.exe") => new(pid, 638000000000000000 + pid, UserSid, Owner.SessionId, @"C:\Apps\" + name);
    private static ApplicationReconnectTcpConnection Connection(int pid, bool ipv6 = false) => new(pid,
        IPAddress.Parse(ipv6 ? "2001:db8::19" : "192.0.2.19"), 55001, IPAddress.Parse(ipv6 ? "2001:db8::7" : "198.51.100.7"), 443, ApplicationReconnectTcpState.Established);
    private static FakePlatform Ready()
    {
        var fake = new FakePlatform(); fake.Processes[1] = Process(1); fake.Ipv4.Add(Connection(1)); return fake;
    }
    private sealed class FakePlatform : IApplicationReconnectPlatform
    {
        public bool Administrator = true, FlushOk = true;
        public int CurrentProcessId => 999;
        public Dictionary<int, ApplicationReconnectProcess> Processes { get; } = new();
        public List<ApplicationReconnectTcpConnection> Ipv4 { get; } = new();
        public List<ApplicationReconnectTcpConnection> Ipv6 { get; } = new();
        public List<ApplicationReconnectTcpConnection> Writes { get; } = new();
        public List<string> Calls { get; } = new();
        public Func<int, int, ApplicationReconnectProcess?>? OnReadProcess;
        public Func<AddressFamily, int, IReadOnlyList<ApplicationReconnectTcpConnection>>? OnReadTable;
        public Func<ApplicationReconnectProcess, ApplicationReconnectTcpConnection, ApplicationReconnectWriteOutcome>? OnDisconnect;
        private readonly Dictionary<int, int> _processReads = new();
        private readonly Dictionary<AddressFamily, int> _tableReads = new();
        public bool IsAdministrator { get { Calls.Add("admin"); return Administrator; } }
        public bool FlushDnsResolverCache() { Calls.Add("flush"); return FlushOk; }
        public IReadOnlyList<int> ReadProcessIds() { Calls.Add("pids"); return Processes.Keys.ToArray(); }
        public ApplicationReconnectProcess? ReadProcess(int processId)
        {
            Calls.Add("process"); _processReads.TryGetValue(processId, out int reads); _processReads[processId] = ++reads;
            return OnReadProcess is null ? Processes.GetValueOrDefault(processId) : OnReadProcess(processId, reads);
        }
        public IReadOnlyList<ApplicationReconnectTcpConnection> ReadTcpTable(AddressFamily family)
        {
            Calls.Add(family == AddressFamily.InterNetwork ? "ipv4" : "ipv6");
            _tableReads.TryGetValue(family, out int reads); _tableReads[family] = ++reads;
            return OnReadTable is null ? (family == AddressFamily.InterNetwork ? Ipv4 : Ipv6).ToArray() : OnReadTable(family, reads);
        }
        public ApplicationReconnectWriteOutcome Disconnect(ApplicationReconnectProcess expectedProcess, ApplicationReconnectTcpConnection expectedConnection)
        {
            Calls.Add("disconnect"); Writes.Add(expectedConnection);
            return OnDisconnect?.Invoke(expectedProcess, expectedConnection) ?? ApplicationReconnectWriteOutcome.Disconnected;
        }
    }
    private static void WithBuffer(int bytes, Action<IntPtr, int> test)
    {
        IntPtr buffer = Marshal.AllocHGlobal(bytes);
        try { Marshal.Copy(new byte[bytes], 0, buffer, bytes); test(buffer, bytes); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static void CopyAddress(IntPtr row, int offset, string address)
    {
        var data = IPAddress.Parse(address).GetAddressBytes(); Marshal.Copy(data, 0, IntPtr.Add(row, offset), data.Length);
    }
    private static void WritePort(IntPtr row, int offset, int port) => Marshal.WriteInt32(row, offset, unchecked((int)NativeApplicationReconnectPlatform.HostToNetworkPort(port)));
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
    private static void Reject<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Expected rejection: " + typeof(T).Name);
    }
}
