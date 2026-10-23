using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace NetPilot;

public enum DnsFamily { IPv4 = 4, IPv6 = 6 }
public enum DnsSource { Dhcp = 1, Static = 2 }

public sealed class DnsFamilySnapshot
{
    [JsonRequired] public DnsFamily Family { get; set; }
    [JsonRequired] public bool Enabled { get; set; }
    [JsonRequired] public DnsSource Source { get; set; }
    // NameServer (static override) only. DHCP observations must never be reapplied as static DNS.
    [JsonRequired] public string[] Servers { get; set; } = [];
    [JsonRequired] public string[] EffectiveServers { get; set; } = [];
}

public sealed class AdapterDnsSnapshot
{
    [JsonRequired] public string AdapterId { get; set; } = "";
    public string AdapterName { get; set; } = "";
    [JsonRequired] public bool Up { get; set; }
    [JsonRequired] public DnsFamilySnapshot IPv4 { get; set; } = new();
    [JsonRequired] public DnsFamilySnapshot IPv6 { get; set; } = new();
}

public interface IDnsBackend
{
    Task<AdapterDnsSnapshot> ReadAsync(string adapterId);
    // The backend must compare expected before each command, and verify the resulting state.
    Task ApplyAsync(string adapterId, DnsFamilySnapshot expected, DnsFamilySnapshot desired);
}

public sealed class DnsSessionManager
{
    private readonly string journalPath;
    private readonly IDnsBackend backend;
    private readonly Action<string>? protectFile;
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool requiresJournal;
    private volatile bool managed;

    public DnsSessionManager(string dataDirectory, IDnsBackend? backend = null, Action<string>? protectFile = null)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) throw new ArgumentException("Не задан каталог журнала DNS.", nameof(dataDirectory));
        journalPath = Path.Combine(Path.GetFullPath(dataDirectory), "dns-session.json");
        this.backend = backend ?? new WinDnsBackend();
        this.protectFile = protectFile;
    }
    public bool Managed => managed;
    public bool HasJournal => File.Exists(journalPath) || File.Exists(journalPath + ".bak");

    // The service calls this only after its relay is listening and an upstream is healthy.
    public Task EnableAsync(string adapterId) => ChangeAsync(adapterId, DnsOperation.Relay);

    // Explicit user action: discard manual DNS in favour of DHCP/RA DNS, without
    // changing IP assignment, routing or protocol bindings. This is not Restore.
    public Task ResetAutomaticAsync(string adapterId) => ChangeAsync(adapterId, DnsOperation.Automatic);

    private async Task ChangeAsync(string adapterId, DnsOperation operation)
    {
        await gate.WaitAsync();
        try
        {
            if (managed || requiresJournal || HasJournal)
                throw new InvalidOperationException("Сначала восстановите исходный DNS: активен или не завершён предыдущий журнал.");
            if (!Guid.TryParse(adapterId, out var id)) throw new ArgumentException("Некорректный GUID адаптера.", nameof(adapterId));
            var original = await ReadValidatedAsync(id.ToString("D"));
            if (operation == DnsOperation.Relay && !original.IPv4.Enabled) throw new InvalidOperationException("Для локального посредника требуется включённый IPv4. Привязки протоколов не изменены.");
            if (!original.IPv4.Enabled && !original.IPv6.Enabled) throw new InvalidOperationException("На адаптере нет включённого IPv4 или IPv6. Привязки протоколов не изменены.");
            foreach (var family in new[] { original.IPv4, original.IPv6 }.Where(x => x.Enabled))
            {
                if (family.Servers.Concat(family.EffectiveServers).Any(x => IPAddress.IsLoopback(IPAddress.Parse(x))))
                    throw new InvalidOperationException("На адаптере уже указан DNS loopback, но нет действующего журнала владения. Автоматическое изменение заблокировано.");
                if (!DnsState.Verified(original, family))
                    throw new InvalidOperationException("Настроенные и действующие DNS различаются; безопасный исходный снимок не подтверждён.");
            }
            var journal = new DnsJournal
            {
                Version = 1, SessionId = Guid.NewGuid().ToString("D"), CreatedUtc = DateTimeOffset.UtcNow,
                Original = JsonData.Clone(original), Operation = operation
            };
            if (operation == DnsOperation.Automatic && new[] { original.IPv4, original.IPv6 }.Where(x => x.Enabled).All(x => x.Source == DnsSource.Dhcp)) return;
            try
            {
                Save(journal); // Durable original precedes every possible mutation.
                requiresJournal = true;
                foreach (var family in new[] { DnsFamily.IPv4, DnsFamily.IPv6 })
                {
                    var saved = DnsState.Family(original, family);
                    if (!saved.Enabled) continue;
                    var before = await ReadValidatedAsync(original.AdapterId);
                    if (!DnsState.SameConfiguration(DnsState.Family(before, family), saved))
                        throw new InvalidOperationException("DNS изменён другим приложением после снимка. Включение остановлено.");
                    var desired = Target(operation, family);
                    if (DnsState.SameConfiguration(DnsState.Family(before, family), desired)) continue;
                    SetTouched(journal, family, true);
                    Save(journal); // Write-ahead, including a command that throws after changing DNS.
                    await backend.ApplyAsync(original.AdapterId, DnsState.Family(before, family), desired);
                    var after = await ReadValidatedAsync(original.AdapterId);
                    if (!DnsState.Verified(after, desired)) throw new IOException("Windows не подтвердила применение DNS " + family + ".");
                }
                if (operation == DnsOperation.Relay) managed = true;
                else
                {
                    // Commit the explicit DHCP goal. A complete reset must not be
                    // reverted to former manual DNS by the next service startup.
                    journal.IPv4Touched = false; journal.IPv6Touched = false; journal.Finished = true;
                    Save(journal); DeleteCompletedJournal();
                }
            }
            catch (Exception activationError)
            {
                managed = false;
                if (HasJournal || requiresJournal)
                {
                    try { await RestoreCoreAsync(); }
                    catch (Exception restorationError)
                    {
                        throw new AggregateException((operation == DnsOperation.Relay ? "Включение DNS" : "Сброс на автоматический DNS") + " не выполнен; откат не завершён. Журнал сохранён, новые изменения заблокированы.", activationError, restorationError);
                    }
                    throw new InvalidOperationException((operation == DnsOperation.Relay ? "Включение DNS" : "Сброс на автоматический DNS") + " не выполнен; откат изменений NetPilot завершён.", activationError);
                }
                throw;
            }
        }
        finally { gate.Release(); }
    }

    public async Task RestoreAsync()
    {
        await gate.WaitAsync();
        try { await RestoreCoreAsync(); }
        finally { gate.Release(); }
    }

    private async Task RestoreCoreAsync()
    {
        if (!HasJournal)
        {
            if (managed || requiresJournal) throw new InvalidDataException("Журнал активного DNS отсутствует. Исходная настройка неизвестна; изменения заблокированы.");
            return;
        }
        requiresJournal = true;
        // Never recover a stale backup over a missing or malformed primary journal.
        if (!File.Exists(journalPath)) throw new InvalidDataException("Основной журнал DNS отсутствует; резервная копия сохранена для ручной проверки.");
        if (new FileInfo(journalPath).Length > 128 * 1024) throw new InvalidDataException("Недопустимый размер журнала DNS; изменения заблокированы.");
        DnsJournal journal;
        try
        {
            journal = AtomicStore.Read<DnsJournal>(journalPath) ?? throw new InvalidDataException("Пустой журнал DNS.");
            ValidateJournal(journal);
        }
        catch (Exception e) when (e is not UnauthorizedAccessException)
        { throw new InvalidDataException("Журнал DNS повреждён или имеет неподдерживаемый формат. Он сохранён, сеть не изменена.", e); }
        managed = false;
        if (journal.Finished) { DeleteCompletedJournal(); return; }
        var original = journal.Original!;
        var current = await ReadValidatedAsync(original.AdapterId);

        // Preflight every still-owned family before making any restoration write.
        foreach (var family in new[] { DnsFamily.IPv6, DnsFamily.IPv4 }.Where(x => Touched(journal, x)))
            RequireRestorable(DnsState.Family(current, family), DnsState.Family(original, family), journal.Operation);

        foreach (var family in new[] { DnsFamily.IPv6, DnsFamily.IPv4 }.Where(x => Touched(journal, x)))
        {
            current = await ReadValidatedAsync(original.AdapterId);
            var before = DnsState.Family(current, family);
            var saved = DnsState.Family(original, family);
            RequireRestorable(before, saved, journal.Operation);
            if (!DnsState.SameConfiguration(before, saved))
            {
                await backend.ApplyAsync(original.AdapterId, before, JsonData.Clone(saved));
                current = await ReadValidatedAsync(original.AdapterId);
            }
            if (!DnsState.Verified(current, saved)) throw new IOException("Не подтверждено восстановление действующего DNS " + family + ". Журнал сохранён.");
            // Once restored, this family no longer belongs to this transaction.
            SetTouched(journal, family, false);
            Save(journal);
        }
        journal.Finished = true;
        Save(journal);
        DeleteCompletedJournal();
    }

    private async Task<AdapterDnsSnapshot> ReadValidatedAsync(string id)
    {
        var snapshot = await backend.ReadAsync(id);
        DnsState.Validate(snapshot);
        if (Guid.Parse(snapshot.AdapterId) != Guid.Parse(id)) throw new IOException("Получен снимок другого адаптера; DNS не изменён.");
        return snapshot;
    }
    private static DnsFamilySnapshot Target(DnsOperation operation, DnsFamily family) => operation == DnsOperation.Relay
        ? DnsState.Loopback(family) : new() { Family = family, Enabled = true, Source = DnsSource.Dhcp, Servers = [], EffectiveServers = [] };
    private static void RequireRestorable(DnsFamilySnapshot current, DnsFamilySnapshot original, DnsOperation operation)
    {
        if (!DnsState.SameConfiguration(current, original) && !DnsState.SameConfiguration(current, Target(operation, original.Family)))
            throw new InvalidOperationException("Конфликт DNS " + original.Family + ": текущее состояние не совпадает с исходным или применённым NetPilot. Сторонняя настройка и журнал сохранены.");
    }
    private void Save(DnsJournal journal) => AtomicStore.Write(journalPath, journal, protectFile);
    private static bool Touched(DnsJournal journal, DnsFamily family) => family == DnsFamily.IPv4 ? journal.IPv4Touched : journal.IPv6Touched;
    private static void SetTouched(DnsJournal journal, DnsFamily family, bool value)
    { if (family == DnsFamily.IPv4) journal.IPv4Touched = value; else journal.IPv6Touched = value; }
    private static void ValidateJournal(DnsJournal journal)
    {
        if (journal.Version != 1 || !Enum.IsDefined(journal.Operation) || !Guid.TryParse(journal.SessionId, out _) || journal.CreatedUtc == default || journal.Original == null)
            throw new InvalidDataException("Не хватает обязательных полей журнала.");
        DnsState.Validate(journal.Original);
        if ((journal.IPv4Touched && !journal.Original.IPv4.Enabled) || (journal.IPv6Touched && !journal.Original.IPv6.Enabled)
            || (journal.Finished && (journal.IPv4Touched || journal.IPv6Touched))) throw new InvalidDataException("Неверные флаги семей в журнале DNS.");
    }
    private void DeleteCompletedJournal()
    {
        // A finished primary remains until its stale backup has been removed.
        File.Delete(journalPath + ".bak");
        File.Delete(journalPath);
        requiresJournal = false;
        managed = false;
    }

    private enum DnsOperation { Relay, Automatic }
    private sealed class DnsJournal
    {
        [JsonRequired] public int Version { get; set; }
        [JsonRequired] public string SessionId { get; set; } = "";
        [JsonRequired] public DateTimeOffset CreatedUtc { get; set; }
        [JsonRequired] public AdapterDnsSnapshot? Original { get; set; }
        [JsonRequired] public bool IPv4Touched { get; set; }
        [JsonRequired] public bool IPv6Touched { get; set; }
        [JsonRequired] public bool Finished { get; set; }
        // Missing only in legacy v1 relay journals.
        public DnsOperation Operation { get; set; } = DnsOperation.Relay;
    }
}

internal static class DnsState
{
    public static DnsFamilySnapshot Family(AdapterDnsSnapshot snapshot, DnsFamily family) => family == DnsFamily.IPv4 ? snapshot.IPv4 : snapshot.IPv6;
    public static DnsFamilySnapshot Loopback(DnsFamily family)
    {
        string address = family == DnsFamily.IPv4 ? "127.0.0.1" : "::1";
        return new() { Family = family, Enabled = true, Source = DnsSource.Static, Servers = [address], EffectiveServers = [address] };
    }
    public static bool SameConfiguration(DnsFamilySnapshot a, DnsFamilySnapshot b) => a.Family == b.Family && a.Enabled == b.Enabled && a.Source == b.Source && EqualAddresses(a.Servers, b.Servers);
    public static bool Verified(AdapterDnsSnapshot snapshot, DnsFamilySnapshot desired)
    {
        var actual = Family(snapshot, desired.Family);
        // DHCP owns its lease and observed addresses; restoring DHCP must not pin an obsolete lease.
        return SameConfiguration(actual, desired) && (desired.Source == DnsSource.Dhcp || !snapshot.Up || EqualAddresses(actual.EffectiveServers, desired.Servers));
    }
    private static bool EqualAddresses(string[] a, string[] b) => a.Length == b.Length && a.Zip(b).All(x => IPAddress.Parse(x.First).Equals(IPAddress.Parse(x.Second)));
    public static void Validate(AdapterDnsSnapshot snapshot)
    {
        if (snapshot == null || !Guid.TryParse(snapshot.AdapterId, out _) || snapshot.IPv4 == null || snapshot.IPv6 == null
            || snapshot.IPv4.Family != DnsFamily.IPv4 || snapshot.IPv6.Family != DnsFamily.IPv6) throw new InvalidDataException("Некорректный снимок адаптера DNS.");
        Validate(snapshot.IPv4); Validate(snapshot.IPv6);
    }
    public static void Validate(DnsFamilySnapshot family)
    {
        if (family == null || !Enum.IsDefined(family.Family) || !Enum.IsDefined(family.Source) || family.Servers == null || family.EffectiveServers == null
            || family.Servers.Length > 64 || family.EffectiveServers.Length > 64 || (family.Source == DnsSource.Dhcp && family.Servers.Length != 0)
            || (family.Source == DnsSource.Static && family.Servers.Length == 0)) throw new InvalidDataException("Некорректный источник или список DNS.");
        var expected = family.Family == DnsFamily.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        foreach (var address in family.Servers.Concat(family.EffectiveServers))
            if (!IPAddress.TryParse(address, out var ip) || ip.AddressFamily != expected) throw new InvalidDataException("Некорректный DNS в снимке: " + address);
        if (family.Servers.Select(x => IPAddress.Parse(x).ToString()).Distinct(StringComparer.OrdinalIgnoreCase).Count() != family.Servers.Length)
            throw new InvalidDataException("Повторяющиеся статические DNS нельзя гарантированно восстановить netsh.");
    }
}

public static class AdapterCatalog
{
    public static List<AdapterInfo> Read()
    {
        var result = new List<AdapterInfo>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            try
            {
                if (!WindowsAdapters.IsSafePhysical(nic)) continue;
                var properties = nic.GetIPProperties();
                result.Add(new AdapterInfo
                {
                    Id = Guid.Parse(nic.Id).ToString("D"), Name = nic.Name, Up = nic.OperationalStatus == OperationalStatus.Up,
                    HasGateway = properties.GatewayAddresses.Any(x => !x.Address.Equals(IPAddress.Any) && !x.Address.Equals(IPAddress.IPv6Any)),
                    Dns = properties.DnsAddresses.Select(x => x.ToString()).ToArray()
                });
            }
            catch (NetworkInformationException) { }
            catch (Win32Exception) { }
        }
        return result.OrderByDescending(x => x.Up).ThenByDescending(x => x.HasGateway).ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}

public sealed class WinDnsBackend : IDnsBackend
{
    public Task<AdapterDnsSnapshot> ReadAsync(string adapterId)
    {
        var nic = WindowsAdapters.Resolve(adapterId);
        var properties = nic.GetIPProperties();
        var effective = properties.DnsAddresses.ToArray();
        var snapshot = new AdapterDnsSnapshot
        {
            AdapterId = Guid.Parse(nic.Id).ToString("D"), AdapterName = nic.Name, Up = nic.OperationalStatus == OperationalStatus.Up,
            IPv4 = ReadFamily(nic, DnsFamily.IPv4, effective), IPv6 = ReadFamily(nic, DnsFamily.IPv6, effective)
        };
        DnsState.Validate(snapshot);
        return Task.FromResult(snapshot);
    }

    public async Task ApplyAsync(string adapterId, DnsFamilySnapshot expected, DnsFamilySnapshot desired)
    {
        using var identity = WindowsIdentity.GetCurrent();
        if (!new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator))
            throw new UnauthorizedAccessException("Для изменения DNS нужна повышенная служба Windows. Текущий процесс не имеет прав администратора.");
        DnsState.Validate(expected); DnsState.Validate(desired);
        if (!expected.Enabled || !desired.Enabled || expected.Family != desired.Family) throw new InvalidOperationException("Изменение привязки протокола запрещено.");
        var previous = JsonData.Clone(expected);
        int count = desired.Source == DnsSource.Dhcp ? 1 : desired.Servers.Length;
        for (int i = 0; i < count; i++)
        {
            var before = await ReadAsync(adapterId);
            if (!DnsState.SameConfiguration(DnsState.Family(before, desired.Family), previous))
                throw new InvalidOperationException("Конфликт: DNS изменился перед командой netsh. Сторонняя настройка сохранена.");
            // Resolve by GUID for EVERY command; interface indexes are not persistent identities.
            var nic = WindowsAdapters.Resolve(adapterId);
            int index = WindowsAdapters.Index(nic, desired.Family);
            await RunNetshAsync(BuildArguments(desired, index)[i]);
            previous = JsonData.Clone(desired);
            if (desired.Source == DnsSource.Static)
            {
                previous.Servers = desired.Servers.Take(i + 1).ToArray();
                previous.EffectiveServers = previous.Servers.ToArray();
            }
            await VerifyAsync(adapterId, previous);
        }
    }

    internal static IReadOnlyList<string[]> BuildArguments(DnsFamilySnapshot desired, int currentIndex)
    {
        DnsState.Validate(desired);
        if (!desired.Enabled || currentIndex < 1) throw new ArgumentException("Для команды DNS нужен включённый протокол и текущий индекс адаптера.");
        string family = desired.Family == DnsFamily.IPv4 ? "ipv4" : "ipv6";
        string name = "name=" + currentIndex.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (desired.Source == DnsSource.Dhcp) return [["interface", family, "set", "dnsservers", name, "source=dhcp"]];
        var plan = new List<string[]> { new[] { "interface", family, "set", "dnsservers", name, "source=static", "address=" + desired.Servers[0], "validate=no" } };
        for (int i = 1; i < desired.Servers.Length; i++)
            plan.Add(["interface", family, "add", "dnsservers", name, "address=" + desired.Servers[i], "index=" + (i + 1).ToString(System.Globalization.CultureInfo.InvariantCulture), "validate=no"]);
        return plan;
    }

    private static DnsFamilySnapshot ReadFamily(NetworkInterface nic, DnsFamily family, IPAddress[] effective)
    {
        bool enabled = nic.Supports(family == DnsFamily.IPv4 ? NetworkInterfaceComponent.IPv4 : NetworkInterfaceComponent.IPv6);
        var addressFamily = family == DnsFamily.IPv4 ? AddressFamily.InterNetwork : AddressFamily.InterNetworkV6;
        // Do not infer DNS mode from IPv4Properties.IsDhcpEnabled: it describes address assignment.
        string service = family == DnsFamily.IPv4 ? "Tcpip" : "Tcpip6";
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
        using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + service + @"\Parameters\Interfaces\" + Guid.Parse(nic.Id).ToString("B"), false);
        if (enabled && key == null) throw new IOException("Не удалось прочитать источник DNS " + family + " для исходного GUID.");
        var value = key?.GetValue("NameServer", null, RegistryValueOptions.DoNotExpandEnvironmentNames);
        string text = value switch { null => "", string v => v, string[] v => string.Join(' ', v), _ => throw new InvalidDataException("Неизвестный тип реестрового значения NameServer.") };
        var configured = text.Split([' ', '\t', '\r', '\n', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(x =>
        {
            if (!IPAddress.TryParse(x, out var ip) || ip.AddressFamily != addressFamily) throw new InvalidDataException("Не удалось однозначно прочитать NameServer: " + x);
            return ip.ToString();
        }).ToArray();
        return new DnsFamilySnapshot
        {
            Family = family, Enabled = enabled, Source = configured.Length == 0 ? DnsSource.Dhcp : DnsSource.Static,
            Servers = configured, EffectiveServers = effective.Where(x => x.AddressFamily == addressFamily).Select(x => x.ToString()).ToArray()
        };
    }

    private async Task VerifyAsync(string adapterId, DnsFamilySnapshot desired)
    {
        var timer = Stopwatch.StartNew();
        do
        {
            var observed = await ReadAsync(adapterId);
            if (DnsState.Verified(observed, desired)) return;
            await Task.Delay(150);
        } while (timer.Elapsed < TimeSpan.FromSeconds(5));
        throw new IOException("netsh завершился, но источник и порядок DNS " + desired.Family + " не подтверждены. Журнал требуется для восстановления.");
    }

    private static async Task RunNetshAsync(IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "netsh.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        if (!process.Start()) throw new IOException("Не удалось запустить системный netsh.exe.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        // Waiting for pipe readers together with exit would defeat timeout: a hung process
        // can leave both pipes open after WaitForExitAsync has been cancelled.
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(output, error).WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } catch (Win32Exception) { }
            throw new TimeoutException("Истёк 12-секундный тайм-аут netsh. Команда могла частично изменить DNS; требуется проверка журнала.");
        }
        if (process.ExitCode != 0)
        {
            string detail = (await error + " " + await output).Trim();
            if (detail.Length > 800) detail = detail[..800];
            throw new IOException("netsh вернул код " + process.ExitCode + ": " + detail);
        }
    }
}

internal static class WindowsAdapters
{
    private static readonly string[] Excluded = ["virtual", "vpn", "tailscale", "wireguard", "tunnel", "tap-", "tap ", "tun ", "hyper-v", "vmware", "virtualbox", "wan miniport", "loopback", "npcap", "docker", "zerotier", "hamachi", "bluetooth"];
    public static NetworkInterface Resolve(string adapterId)
    {
        if (!Guid.TryParse(adapterId, out var id)) throw new ArgumentException("Некорректный GUID адаптера.", nameof(adapterId));
        var nic = NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(x => Guid.TryParse(x.Id, out var candidate) && candidate == id);
        if (nic == null) throw new IOException("Адаптер с исходным GUID отсутствует. Журнал сохранён до его возвращения.");
        if (!IsSafePhysical(nic)) throw new InvalidOperationException("Выбранный GUID не подтверждён как физический Ethernet/Wi-Fi адаптер. Изменение заблокировано.");
        return nic;
    }
    public static int Index(NetworkInterface nic, DnsFamily family) => family == DnsFamily.IPv4
        ? nic.GetIPProperties().GetIPv4Properties()?.Index ?? throw new IOException("Нет текущего IPv4 индекса исходного GUID.")
        : nic.GetIPProperties().GetIPv6Properties()?.Index ?? throw new IOException("Нет текущего IPv6 индекса исходного GUID.");
    public static bool IsSafePhysical(NetworkInterface nic)
    {
        if (!OperatingSystem.IsWindows() || !Guid.TryParse(nic.Id, out _) || nic.NetworkInterfaceType is not (NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211)) return false;
        string label = nic.Name + " " + nic.Description;
        if (Excluded.Any(x => label.Contains(x, StringComparison.OrdinalIgnoreCase))) return false;
        int index = nic.Supports(NetworkInterfaceComponent.IPv4) ? Index(nic, DnsFamily.IPv4) : Index(nic, DnsFamily.IPv6);
        var row = new NativeIfRow
        {
            InterfaceIndex = checked((uint)index), Alias = "", Description = "",
            PhysicalAddress = new byte[32], PermanentPhysicalAddress = new byte[32]
        };
        uint error = GetIfEntry2(ref row);
        if (error != 0) throw new Win32Exception(checked((int)error));
        const byte Hardware = 1, Filter = 2, Connector = 4, Endpoint = 128;
        return (row.StatusFlags & (Hardware | Connector)) == (Hardware | Connector) && (row.StatusFlags & (Filter | Endpoint)) == 0 && row.InterfaceGuid == Guid.Parse(nic.Id);
    }

    // Complete native layout avoids relying on an undocumented offset into MIB_IF_ROW2.
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeIfRow
    {
        public ulong InterfaceLuid;
        public uint InterfaceIndex;
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Alias;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 257)] public string Description;
        public uint PhysicalAddressLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PhysicalAddress;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] PermanentPhysicalAddress;
        public uint Mtu, Type, TunnelType, MediaType, PhysicalMediumType, AccessType, DirectionType;
        public byte StatusFlags;
        public uint OperStatus, AdminStatus, MediaConnectState;
        public Guid NetworkGuid;
        public uint ConnectionType;
        public ulong TransmitLinkSpeed, ReceiveLinkSpeed;
        public ulong InOctets, InUcastPkts, InNUcastPkts, InDiscards, InErrors, InUnknownProtos, InUcastOctets, InMulticastOctets, InBroadcastOctets;
        public ulong OutOctets, OutUcastPkts, OutNUcastPkts, OutDiscards, OutErrors, OutUcastOctets, OutMulticastOctets, OutBroadcastOctets, OutQLen;
    }
    [DllImport("iphlpapi.dll", ExactSpelling = true)]
    private static extern uint GetIfEntry2(ref NativeIfRow row);
}
