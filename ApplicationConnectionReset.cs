using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace NetPilot;

[Flags]
internal enum ApplicationReconnectTargets { None = 0, Chrome = 1, Codex = 2 }
internal sealed record ApplicationReconnectOwner(string Sid, int SessionId);
internal sealed record ApplicationReconnectResult(int MatchedProcesses, int Disconnected, int Failed, int AlreadyClosed, int SkippedIpv6);
internal sealed record ApplicationReconnectProcess(int ProcessId, long StartUtcTicks, string Sid, int SessionId, string ImagePath);
internal enum ApplicationReconnectTcpState : uint
{
    Closed = 1, Listen = 2, SynSent = 3, SynReceived = 4, Established = 5,
    FinWait1 = 6, FinWait2 = 7, CloseWait = 8, Closing = 9, LastAck = 10, TimeWait = 11, DeleteTcb = 12
}
internal sealed record ApplicationReconnectTcpConnection(int ProcessId, IPAddress LocalAddress, int LocalPort,
    IPAddress RemoteAddress, int RemotePort, ApplicationReconnectTcpState State);
internal enum ApplicationReconnectWriteOutcome { Disconnected, AlreadyClosed, Failed }

// No process termination, adapter control, service control, UAC or shell operation is exposed here.
internal interface IApplicationReconnectPlatform
{
    bool IsAdministrator { get; }
    int CurrentProcessId { get; }
    bool FlushDnsResolverCache();
    IReadOnlyList<int> ReadProcessIds();
    ApplicationReconnectProcess? ReadProcess(int processId);
    IReadOnlyList<ApplicationReconnectTcpConnection> ReadTcpTable(AddressFamily family);
    ApplicationReconnectWriteOutcome Disconnect(ApplicationReconnectProcess expectedProcess, ApplicationReconnectTcpConnection expectedConnection);
}

internal static class ApplicationConnectionReset
{
    public static ApplicationReconnectResult Execute(ApplicationReconnectTargets targets, ApplicationReconnectOwner owner,
        IApplicationReconnectPlatform platform)
    {
        ArgumentNullException.ThrowIfNull(platform);
        if (targets == ApplicationReconnectTargets.None || (targets & ~(ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex)) != 0)
            throw new ArgumentException("Выберите Chrome и/или Codex.", nameof(targets));
        ValidateOwner(owner);
        if (!platform.IsAdministrator) throw new UnauthorizedAccessException("Для сброса соединений требуются права администратора Windows.");
        if (!platform.FlushDnsResolverCache()) throw new IOException("Windows не подтвердила очистку DNS-кэша. Сброс соединений не выполнялся.");

        var processes = new Dictionary<int, ApplicationReconnectProcess>();
        foreach (int pid in platform.ReadProcessIds().Distinct())
        {
            if (pid <= 0 || pid == platform.CurrentProcessId) continue;
            var process = platform.ReadProcess(pid);
            if (process is not null && process.ProcessId == pid && process.StartUtcTicks > 0 && Matches(process, targets, owner))
                processes.Add(pid, process);
        }

        // Obtain both initial tables before deleting anything: a table-read failure is not a completed reset.
        var ipv4 = platform.ReadTcpTable(AddressFamily.InterNetwork);
        var ipv6 = platform.ReadTcpTable(AddressFamily.InterNetworkV6);
        int skippedIpv6 = ipv6.Where(x => x.LocalAddress.AddressFamily == AddressFamily.InterNetworkV6
            && processes.ContainsKey(x.ProcessId) && IsOutgoingHttpsConnection(x, ipv6)).DistinctBy(Key).Count();
        int disconnected = 0, failed = 0, alreadyClosed = 0;
        foreach (var planned in ipv4.Where(x => x.LocalAddress.AddressFamily == AddressFamily.InterNetwork
            && processes.ContainsKey(x.ProcessId) && IsOutgoingHttpsConnection(x, ipv4)).DistinctBy(Key))
        {
            var expected = processes[planned.ProcessId];
            try
            {
                var currentProcess = platform.ReadProcess(expected.ProcessId);
                if (!SameProcess(expected, currentProcess)) { alreadyClosed++; continue; }
                var currentTable = platform.ReadTcpTable(AddressFamily.InterNetwork);
                var current = currentTable.FirstOrDefault(x => SameTuple(planned, x) && IsOutgoingHttpsConnection(x, currentTable));
                if (current is null) { alreadyClosed++; continue; }
                // The native writer repeats these checks while retaining a handle to this exact process.
                switch (platform.Disconnect(expected, current))
                {
                    case ApplicationReconnectWriteOutcome.Disconnected: disconnected++; break;
                    case ApplicationReconnectWriteOutcome.AlreadyClosed: alreadyClosed++; break;
                    default: failed++; break;
                }
            }
            catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException)
            {
                failed++;
            }
        }
        return new(processes.Count, disconnected, failed, alreadyClosed, skippedIpv6);
    }

    internal static void ValidateOwner(ApplicationReconnectOwner owner)
    {
        ArgumentNullException.ThrowIfNull(owner);
        if (owner.SessionId <= 0 || string.IsNullOrWhiteSpace(owner.Sid))
            throw new UnauthorizedAccessException("Не подтверждён пользователь интерактивного приложения.");
        SecurityIdentifier sid;
        try { sid = new SecurityIdentifier(owner.Sid); }
        catch (ArgumentException ex) { throw new UnauthorizedAccessException("Некорректный SID пользователя приложения.", ex); }
        string value = sid.Value;
        if (!string.Equals(value, owner.Sid, StringComparison.OrdinalIgnoreCase)
            || value is "S-1-5-18" or "S-1-5-19" or "S-1-5-20" || value.StartsWith("S-1-5-80-", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Сброс разрешён только для пользователя интерактивного приложения.");
    }

    internal static bool Matches(ApplicationReconnectProcess process, ApplicationReconnectTargets targets, ApplicationReconnectOwner owner) =>
        process.SessionId == owner.SessionId && string.Equals(process.Sid, owner.Sid, StringComparison.OrdinalIgnoreCase)
        && IsSelectedImage(process.ImagePath, targets);

    internal static bool IsSelectedImage(string image, ApplicationReconnectTargets targets)
    {
        if (string.IsNullOrWhiteSpace(image) || !Path.IsPathFullyQualified(image)) return false;
        string name = Path.GetFileName(image);
        return (targets.HasFlag(ApplicationReconnectTargets.Chrome) && name.Equals("chrome.exe", StringComparison.OrdinalIgnoreCase))
            || (targets.HasFlag(ApplicationReconnectTargets.Codex) && name.Equals("codex.exe", StringComparison.OrdinalIgnoreCase));
    }

    internal static bool SameProcess(ApplicationReconnectProcess expected, ApplicationReconnectProcess? actual) => actual is not null
        && expected.ProcessId == actual.ProcessId && expected.StartUtcTicks == actual.StartUtcTicks
        && expected.SessionId == actual.SessionId && string.Equals(expected.Sid, actual.Sid, StringComparison.OrdinalIgnoreCase)
        && string.Equals(expected.ImagePath, actual.ImagePath, StringComparison.OrdinalIgnoreCase);

    internal static bool IsHttpsConnection(ApplicationReconnectTcpConnection connection) =>
        connection.ProcessId > 0 && connection.RemotePort == 443 && connection.LocalPort is > 0 and <= 65535
        // A source bound to the HTTPS server port is deliberately excluded, including ambiguous inbound connections.
        && connection.LocalPort != 443
        && connection.LocalAddress.AddressFamily == connection.RemoteAddress.AddressFamily
        && connection.LocalAddress.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6
        && !IsLoopbackOrUnspecified(connection.LocalAddress) && !IsLoopbackOrUnspecified(connection.RemoteAddress)
        && connection.State is ApplicationReconnectTcpState.Established or ApplicationReconnectTcpState.SynSent or ApplicationReconnectTcpState.CloseWait;

    private static bool IsLoopbackOrUnspecified(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return IPAddress.IsLoopback(address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any);
    }

    internal static bool IsOutgoingHttpsConnection(ApplicationReconnectTcpConnection connection, IReadOnlyList<ApplicationReconnectTcpConnection> table) =>
        IsHttpsConnection(connection) && !table.Any(x => x.ProcessId == connection.ProcessId && x.State == ApplicationReconnectTcpState.Listen
            && x.LocalPort == connection.LocalPort && x.LocalAddress.AddressFamily == connection.LocalAddress.AddressFamily
            && (x.LocalAddress.Equals(connection.LocalAddress) || x.LocalAddress.Equals(IPAddress.Any) || x.LocalAddress.Equals(IPAddress.IPv6Any)));

    private static (int, IPAddress, int, IPAddress, int) Key(ApplicationReconnectTcpConnection connection) =>
        (connection.ProcessId, connection.LocalAddress, connection.LocalPort, connection.RemoteAddress, connection.RemotePort);
    internal static bool SameTuple(ApplicationReconnectTcpConnection expected, ApplicationReconnectTcpConnection actual) => Key(expected).Equals(Key(actual));
}

internal sealed class NativeApplicationReconnectPlatform : IApplicationReconnectPlatform
{
    private const uint QueryLimitedInformation = 0x1000, Synchronize = 0x00100000, TokenQuery = 0x0008;
    private const uint ErrorInsufficientBuffer = 122, ErrorNotFound = 1168, WaitTimeout = 258;
    private const uint OwnerPidAll = 5;
    private const int MaximumTableBytes = 16 * 1024 * 1024, MaximumTableAttempts = 4;
    public bool IsAdministrator
    {
        get { using var identity = WindowsIdentity.GetCurrent(); return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator); }
    }
    public int CurrentProcessId => Environment.ProcessId;
    public bool FlushDnsResolverCache()
    {
        try { return DnsFlushResolverCache(); }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        { throw new PlatformNotSupportedException("Эта Windows не предоставляет очистку DNS-кэша. Сброс соединений не выполнялся.", ex); }
    }

    public IReadOnlyList<int> ReadProcessIds()
    {
        var processes = Process.GetProcesses();
        try { return processes.Select(x => x.Id).Where(x => x > 0).ToArray(); }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public ApplicationReconnectProcess? ReadProcess(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            using var process = OpenReadableProcess(processId);
            return ReadIdentity(process, processId);
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Inaccessible and exited processes are ineligible; their identity is never guessed from ProcessName.
            return null;
        }
    }

    // The elevated helper accepts only a live GUI in the same canonical package as the helper.
    public static ApplicationReconnectOwner ReadCaller(int pid, long startUtcTicks, string expectedExecutable)
    {
        if (pid <= 0 || startUtcTicks <= 0) throw new ArgumentException("Не заданы PID и время запуска приложения.");
        if (string.IsNullOrWhiteSpace(expectedExecutable) || !Path.IsPathFullyQualified(expectedExecutable)
            || !Path.GetFileName(expectedExecutable).Equals("RtlineDnsPilot.exe", StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Недопустимый путь приложения, запросившего сброс.");
        string currentExecutable = Environment.ProcessPath ?? throw new UnauthorizedAccessException("Не определён исполняемый файл помощника.");
        string expected = CanonicalFilePath(expectedExecutable);
        if (!Path.GetFileName(expected).Equals("RtlineDnsPilot.exe", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected, CanonicalFilePath(currentExecutable), StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Приложение и помощник должны принадлежать одному пакету RtlineDnsPilot.");
        using var process = OpenReadableProcess(pid);
        var caller = ReadIdentity(process, pid);
        return ValidateCallerIdentity(caller, pid, startUtcTicks, expected);
    }

    internal static ApplicationReconnectOwner ValidateCallerIdentity(ApplicationReconnectProcess? caller, int pid, long startUtcTicks, string expectedCanonicalExecutable)
    {
        if (string.IsNullOrWhiteSpace(expectedCanonicalExecutable) || !Path.IsPathFullyQualified(expectedCanonicalExecutable)
            || !Path.GetFileName(expectedCanonicalExecutable).Equals("RtlineDnsPilot.exe", StringComparison.OrdinalIgnoreCase)
            || caller is null || caller.ProcessId != pid || caller.StartUtcTicks != startUtcTicks || startUtcTicks <= 0
            || !string.Equals(caller.ImagePath, expectedCanonicalExecutable, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Запросившее сброс приложение завершено или его идентификатор изменился.");
        var owner = new ApplicationReconnectOwner(caller.Sid, caller.SessionId);
        ApplicationConnectionReset.ValidateOwner(owner);
        return owner;
    }

    public IReadOnlyList<ApplicationReconnectTcpConnection> ReadTcpTable(AddressFamily family)
    {
        uint nativeFamily = family switch { AddressFamily.InterNetwork => 2, AddressFamily.InterNetworkV6 => 23,
            _ => throw new ArgumentException("Поддерживаются только IPv4 и IPv6.", nameof(family)) };
        uint requested = 0;
        uint error = GetExtendedTcpTable(IntPtr.Zero, ref requested, false, nativeFamily, OwnerPidAll, 0);
        if (error != ErrorInsufficientBuffer) throw new Win32Exception(checked((int)error), "Не удалось получить размер таблицы TCP.");
        for (int attempt = 0; attempt < MaximumTableAttempts; attempt++)
        {
            if (requested < sizeof(uint) || requested > MaximumTableBytes) throw new IOException("Размер таблицы TCP превышает допустимую границу.");
            int allocated = checked((int)requested);
            IntPtr buffer = Marshal.AllocHGlobal(allocated);
            try
            {
                uint used = requested;
                error = GetExtendedTcpTable(buffer, ref used, false, nativeFamily, OwnerPidAll, 0);
                if (error == ErrorInsufficientBuffer) { requested = used; continue; }
                if (error != 0) throw new Win32Exception(checked((int)error), "Не удалось прочитать таблицу TCP.");
                if (used > allocated) throw new IOException("Windows вернула некорректный размер таблицы TCP.");
                return DecodeTable(buffer, checked((int)used), family);
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        throw new IOException("Таблица TCP изменялась слишком быстро; сброс остановлен.");
    }

    internal static IReadOnlyList<ApplicationReconnectTcpConnection> DecodeTable(IntPtr buffer, int bytes, AddressFamily family)
    {
        int stride = family switch { AddressFamily.InterNetwork => 24, AddressFamily.InterNetworkV6 => 56,
            _ => throw new ArgumentException("Поддерживаются только IPv4 и IPv6.", nameof(family)) };
        if (buffer == IntPtr.Zero || bytes < sizeof(uint) || bytes > MaximumTableBytes) throw new IOException("Некорректная таблица TCP.");
        uint count = unchecked((uint)Marshal.ReadInt32(buffer));
        if (count > (bytes - sizeof(uint)) / stride) throw new IOException("Таблица TCP обрезана.");
        var connections = new List<ApplicationReconnectTcpConnection>(checked((int)count));
        for (int index = 0; index < count; index++)
        {
            IntPtr row = IntPtr.Add(buffer, checked(sizeof(uint) + index * stride));
            uint Read(int offset) => unchecked((uint)Marshal.ReadInt32(row, offset));
            IPAddress Address(int offset, int length, uint scope = 0)
            {
                var data = new byte[length]; Marshal.Copy(IntPtr.Add(row, offset), data, 0, data.Length);
                return length == 4 ? new IPAddress(data) : new IPAddress(data, NetworkToHost32(scope));
            }
            uint pid = Read(family == AddressFamily.InterNetwork ? 20 : 52);
            if (pid > int.MaxValue) continue;
            if (family == AddressFamily.InterNetwork)
                connections.Add(new((int)pid, Address(4, 4), NetworkToHostPort(Read(8)), Address(12, 4), NetworkToHostPort(Read(16)), (ApplicationReconnectTcpState)Read(0)));
            else
                connections.Add(new((int)pid, Address(0, 16, Read(16)), NetworkToHostPort(Read(20)), Address(24, 16, Read(40)), NetworkToHostPort(Read(44)), (ApplicationReconnectTcpState)Read(48)));
        }
        return connections;
    }

    public ApplicationReconnectWriteOutcome Disconnect(ApplicationReconnectProcess expectedProcess, ApplicationReconnectTcpConnection expectedConnection)
    {
        if (expectedProcess.ProcessId == CurrentProcessId || expectedProcess.ProcessId != expectedConnection.ProcessId
            || !ApplicationConnectionReset.IsSelectedImage(expectedProcess.ImagePath, ApplicationReconnectTargets.Chrome | ApplicationReconnectTargets.Codex)
            || !ApplicationConnectionReset.IsHttpsConnection(expectedConnection) || expectedConnection.LocalAddress.AddressFamily != AddressFamily.InterNetwork)
            return ApplicationReconnectWriteOutcome.Failed;
        try
        {
            ApplicationConnectionReset.ValidateOwner(new(expectedProcess.Sid, expectedProcess.SessionId));
            SafeProcessHandle process;
            try { process = OpenReadableProcess(expectedProcess.ProcessId); }
            catch (Win32Exception ex) when (ex.NativeErrorCode is 87 or 1168) { return ApplicationReconnectWriteOutcome.AlreadyClosed; }
            using var processLifetime = process;
            if (!ApplicationConnectionReset.SameProcess(expectedProcess, ReadIdentity(process, expectedProcess.ProcessId))) return ApplicationReconnectWriteOutcome.AlreadyClosed;
            var currentTable = ReadTcpTable(AddressFamily.InterNetwork);
            var current = currentTable.FirstOrDefault(x => ApplicationConnectionReset.SameTuple(expectedConnection, x)
                && ApplicationConnectionReset.IsOutgoingHttpsConnection(x, currentTable));
            if (current is null) return ApplicationReconnectWriteOutcome.AlreadyClosed;
            if (!ApplicationConnectionReset.SameProcess(expectedProcess, ReadIdentity(process, expectedProcess.ProcessId))) return ApplicationReconnectWriteOutcome.AlreadyClosed;
            var row = CreateDeleteRow(current);
            // SetTcpEntry has no PID or atomic compare-and-delete operation. Keep the process handle open,
            // validate its liveness/identity and re-read tuple+PID immediately before this documented IPv4 call.
            uint error = SetTcpEntry(ref row);
            if (error == 0) return ApplicationReconnectWriteOutcome.Disconnected;
            if (error == ErrorNotFound) return ApplicationReconnectWriteOutcome.AlreadyClosed;
            // Do not misreport access/parameter errors as a successful reset. A simultaneous natural close is separate.
            return ReadTcpTable(AddressFamily.InterNetwork).Any(x => ApplicationConnectionReset.SameTuple(current, x)
                && ApplicationConnectionReset.IsHttpsConnection(x)) ? ApplicationReconnectWriteOutcome.Failed : ApplicationReconnectWriteOutcome.AlreadyClosed;
        }
        catch (Exception ex) when (ex is Win32Exception or IOException or UnauthorizedAccessException or ArgumentException)
        { return ApplicationReconnectWriteOutcome.Failed; }
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct NativeDeleteRow
    {
        internal uint State, LocalAddress, LocalPort, RemoteAddress, RemotePort;
    }
    internal static NativeDeleteRow CreateDeleteRow(ApplicationReconnectTcpConnection connection)
    {
        if (connection.LocalAddress.AddressFamily != AddressFamily.InterNetwork || connection.RemoteAddress.AddressFamily != AddressFamily.InterNetwork
            || connection.LocalPort is <= 0 or > 65535 || connection.RemotePort is <= 0 or > 65535)
            throw new ArgumentException("Недопустимая строка IPv4 TCP.", nameof(connection));
        return new() { State = (uint)ApplicationReconnectTcpState.DeleteTcb,
            LocalAddress = BitConverter.ToUInt32(connection.LocalAddress.GetAddressBytes(), 0), LocalPort = HostToNetworkPort(connection.LocalPort),
            RemoteAddress = BitConverter.ToUInt32(connection.RemoteAddress.GetAddressBytes(), 0), RemotePort = HostToNetworkPort(connection.RemotePort) };
    }
    internal static int NetworkToHostPort(uint value) => (int)(((value & 0xff) << 8) | ((value >> 8) & 0xff));
    internal static uint HostToNetworkPort(int value) => (uint)(((value & 0xff) << 8) | ((value >> 8) & 0xff));
    private static uint NetworkToHost32(uint value) => ((value & 0x000000ff) << 24) | ((value & 0x0000ff00) << 8)
        | ((value & 0x00ff0000) >> 8) | ((value & 0xff000000) >> 24);

    private static SafeProcessHandle OpenReadableProcess(int pid)
    {
        var process = OpenProcess(QueryLimitedInformation | Synchronize, false, pid);
        if (!process.IsInvalid) return process;
        int error = Marshal.GetLastWin32Error(); process.Dispose(); throw new Win32Exception(error, "Не удалось проверить процесс приложения.");
    }
    private static bool IsLive(SafeProcessHandle process)
    {
        uint state = WaitForSingleObject(process, 0);
        if (state == uint.MaxValue) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось проверить состояние процесса.");
        return state == WaitTimeout;
    }
    private static ApplicationReconnectProcess? ReadIdentity(SafeProcessHandle process, int pid)
    {
        if (!IsLive(process)) return null;
        if (!GetProcessTimes(process, out long created, out _, out _, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
        var image = new StringBuilder(32768); uint length = (uint)image.Capacity;
        if (!QueryFullProcessImageName(process, 0, image, ref length)) throw new Win32Exception(Marshal.GetLastWin32Error());
        string canonicalImage = CanonicalFilePath(image.ToString());
        if (!OpenProcessToken(process, TokenQuery, out var token)) throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            string sid = ReadTokenUser(token);
            int session = ReadTokenSession(token);
            if (!IsLive(process)) return null;
            return new(pid, DateTime.FromFileTimeUtc(created).Ticks, sid, session, canonicalImage);
        }
    }
    private static string ReadTokenUser(SafeAccessTokenHandle token)
    {
        bool ok = GetTokenInformation(token, 1, IntPtr.Zero, 0, out uint bytes);
        if (ok || Marshal.GetLastWin32Error() != ErrorInsufficientBuffer || bytes < IntPtr.Size || bytes > 65536)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось определить SID процесса.");
        IntPtr buffer = Marshal.AllocHGlobal(checked((int)bytes));
        try
        {
            if (!GetTokenInformation(token, 1, buffer, bytes, out _)) throw new Win32Exception(Marshal.GetLastWin32Error());
            return new SecurityIdentifier(Marshal.ReadIntPtr(buffer)).Value;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static int ReadTokenSession(SafeAccessTokenHandle token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(sizeof(int));
        try
        {
            if (!GetTokenInformation(token, 12, buffer, sizeof(int), out uint bytes) || bytes != sizeof(int))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось определить сеанс процесса.");
            return Marshal.ReadInt32(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    private static string CanonicalFilePath(string path)
    {
        using var file = CreateFile(Path.GetFullPath(path), 0x80, 1 | 2 | 4, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (file.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error(), "Не удалось проверить путь исполняемого файла.");
        var output = new StringBuilder(32768);
        uint length = GetFinalPathNameByHandle(file, output, (uint)output.Capacity, 0);
        if (length == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length >= output.Capacity) throw new IOException("Слишком длинный путь исполняемого файла.");
        string result = output.ToString();
        if (result.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) result = @"\\" + result[8..];
        else if (result.StartsWith(@"\\?\", StringComparison.Ordinal)) result = result[4..];
        return Path.GetFullPath(result);
    }

    [DllImport("dnsapi.dll", EntryPoint = "DnsFlushResolverCache", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DnsFlushResolverCache();
    [DllImport("iphlpapi.dll")] private static extern uint GetExtendedTcpTable(IntPtr table, ref uint bytes,
        [MarshalAs(UnmanagedType.Bool)] bool order, uint family, uint tableClass, uint reserved);
    [DllImport("iphlpapi.dll")] private static extern uint SetTcpEntry(ref NativeDeleteRow row);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(SafeProcessHandle handle, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(SafeProcessHandle process, out long created, out long exited, out long kernel, out long user);
    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryFullProcessImageName(SafeProcessHandle process, uint flags, StringBuilder path, ref uint length);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);
    [DllImport("advapi32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetTokenInformation(SafeAccessTokenHandle token, uint informationClass, IntPtr information, uint bytes, out uint required);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string filename, uint access, uint sharing, IntPtr security, uint disposition, uint attributes, IntPtr template);
    [DllImport("kernel32.dll", EntryPoint = "GetFinalPathNameByHandleW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, uint length, uint flags);
}
