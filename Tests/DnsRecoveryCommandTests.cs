using System.Diagnostics;
using System.Text.Json;

namespace NetPilot.Tests;

public static class DnsRecoveryCommandTests
{
    private const string AdapterId = "f1b42f8a-ed20-41c4-926c-16f15f3debd0";

    public static async Task RunAsync(Action<string, bool, string> report)
    {
        async Task Check(string name, Func<Task> test)
        {
            try { await test(); report(name, true, ""); }
            catch (Exception ex) { report(name, false, ex.Message); }
        }
        static DnsFamilySnapshot Family(DnsFamily family, params string[] addresses) => new()
        { Family = family, Enabled = true, Source = addresses.Length == 0 ? DnsSource.Dhcp : DnsSource.Static, Servers = addresses, EffectiveServers = addresses };

        await Check("fallback restores the complete IPv4 list in one family-specific operation", async () =>
        {
            using var result = await Execute(Family(DnsFamily.IPv4, "127.0.0.1"), Family(DnsFamily.IPv4, "9.9.9.9", "1.1.1.1", "8.8.8.8"));
            var root = result.RootElement;
            Require(root.GetProperty("Ok").GetBoolean() && root.GetProperty("Writes").GetInt32() == 1, "Restoration did not make exactly one DNS operation.");
            Require(root.GetProperty("Family").GetString() == "IPv4" && root.GetProperty("Index").GetInt32() == 42, "Wrong interface or IP family.");
            Require(root.GetProperty("Addresses").EnumerateArray().Select(x => x.GetString()).SequenceEqual(new[] { "9.9.9.9", "1.1.1.1", "8.8.8.8" }), "Ordered static addresses were not passed intact.");
            Require(!root.GetProperty("Reset").GetBoolean() && !root.GetProperty("Validate").GetBoolean(), "Static DNS became automatic or queried a remote server during restoration.");
        });
        await Check("fallback automatic IPv6 resets only the selected family without pinning an old lease", async () =>
        {
            var desired = Family(DnsFamily.IPv6); desired.EffectiveServers = ["2001:db8::53"];
            using var result = await Execute(Family(DnsFamily.IPv6, "::1"), desired);
            var root = result.RootElement;
            Require(root.GetProperty("Ok").GetBoolean() && root.GetProperty("Writes").GetInt32() == 1
                && root.GetProperty("Family").GetString() == "IPv6" && root.GetProperty("Reset").GetBoolean()
                && root.GetProperty("Addresses").GetArrayLength() == 0, "Automatic IPv6 wrote a static lease or targeted another family: " + root.GetRawText());
        });
        await Check("fallback refuses an interface index recycled after the GUID check", async () =>
        {
            using var result = await Execute(Family(DnsFamily.IPv4, "127.0.0.1"), Family(DnsFamily.IPv4, "9.9.9.9"), actualIndex: 73);
            Require(!result.RootElement.GetProperty("Ok").GetBoolean() && result.RootElement.GetProperty("Writes").GetInt32() == 0, "A recycled index received a DNS write.");
        });
        await Check("fallback refuses a different adapter GUID before writing DNS", async () =>
        {
            using var result = await Execute(Family(DnsFamily.IPv4, "127.0.0.1"), Family(DnsFamily.IPv4, "9.9.9.9"), actualGuid: "d315d24e-0338-4771-a4f8-02f074d8fdfd");
            Require(!result.RootElement.GetProperty("Ok").GetBoolean() && result.RootElement.GetProperty("Writes").GetInt32() == 0, "A different adapter received a DNS write.");
        });
        await Check("fallback preserves a third-party DNS change made while its process starts", async () =>
        {
            using var result = await Execute(Family(DnsFamily.IPv6, "::1"), Family(DnsFamily.IPv6, "2001:4860:4860::8888"), actualNameServer: "2620:fe::fe");
            Require(!result.RootElement.GetProperty("Ok").GetBoolean() && result.RootElement.GetProperty("Writes").GetInt32() == 0, "Fallback overwrote DNS changed after the outer readback.");
        });
    }

    // Real PowerShell execution, with only the four OS-facing DNS operations replaced.
    // No system DNS setter or service operation is executed by this fixture.
    private static async Task<JsonDocument> Execute(DnsFamilySnapshot expected, DnsFamilySnapshot desired,
        int actualIndex = 42, string actualGuid = AdapterId, string? actualNameServer = null)
    {
        string raw = actualNameServer ?? string.Join(',', expected.Servers);
        string script = $$"""
            $script:writes = 0; $script:addresses = @(); $script:reset = $false; $script:validate = $false; $script:family = ''; $script:index = 0
            function Get-NetAdapter { param([switch]$IncludeHidden) [pscustomobject]@{InterfaceGuid='{{actualGuid}}';ifIndex={{actualIndex}}} }
            function Get-ItemProperty { param($LiteralPath) if ($LiteralPath -notlike '*Interfaces*') { throw 'Unexpected registry read' }; [pscustomobject]@{NameServer='{{raw}}'} }
            function Get-DnsClientServerAddress { [CmdletBinding()] param($InterfaceIndex,$AddressFamily) $script:family=[string]$AddressFamily; $script:index=[int]$InterfaceIndex; [pscustomobject]@{InterfaceIndex=$InterfaceIndex;AddressFamily=$AddressFamily} }
            function Set-DnsClientServerAddress {
                [CmdletBinding(SupportsShouldProcess=$true)] param($InputObject,[string[]]$ServerAddresses,[switch]$ResetServerAddresses,[switch]$Validate)
                if ($InputObject.InterfaceIndex -ne 42 -or $InputObject.AddressFamily -notin @('IPv4','IPv6')) { throw 'Wrong setter target' }
                $script:writes++; $script:addresses=@($ServerAddresses | Where-Object { $null -ne $_ }); $script:reset=[bool]$ResetServerAddresses; $script:validate=[bool]$Validate
            }
            $ok = $false; $detail = ''
            try {
            {{WinDnsBackend.BuildRecoveryScript(AdapterId, 42, expected, desired)}}
            $ok = $true
            } catch { $detail = $_.Exception.Message }
            [pscustomobject]@{Ok=$ok;Writes=$script:writes;Addresses=@($script:addresses);Reset=$script:reset;Validate=$script:validate;Family=$script:family;Index=$script:index;Detail=$detail} | ConvertTo-Json -Compress
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"))
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-Command", script }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start the isolated PowerShell fixture.");
        var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        try { await process.WaitForExitAsync(deadline.Token); }
        catch { try { process.Kill(true); } catch (InvalidOperationException) { } throw; }
        if (process.ExitCode != 0) throw new IOException(await error);
        return JsonDocument.Parse(await output);
    }
    private static void Require(bool condition, string detail) { if (!condition) throw new InvalidOperationException(detail); }
}
