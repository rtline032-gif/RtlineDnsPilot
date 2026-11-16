namespace NetPilot.Tests;

public static class ServiceUpgradeTests
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
        static string Reject<T>(Action action) where T : Exception
        {
            try { action(); }
            catch (T ex) { return ex.Message; }
            throw new Exception("Unsafe installation decision was accepted.");
        }
        const string owner = "S-1-5-21-100-200-300-1001";
        var currentVersion = Version.Parse(AppVersion.Current);
        var newerVersion = new Version(currentVersion.Major, currentVersion.Minor, currentVersion.Build + 1, 0);
        static InstallReceipt Receipt(string version) => new() { OwnerSid = owner, Version = version };
        static void WithPackageFiles(Action<string, Dictionary<string, byte[]>> test)
        {
            var directory = Path.Combine(Path.GetTempPath(), "NetPilot-upgrade-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            var files = new Dictionary<string, byte[]>
            {
                ["RtlineDnsPilot.exe"] = [1, 2, 3], ["RtlineDnsPilot.dll"] = [4, 5, 6],
                ["RtlineDnsPilot.deps.json"] = [7, 8, 9], ["RtlineDnsPilot.runtimeconfig.json"] = [10, 11, 12]
            };
            try
            {
                foreach (var pair in files) File.WriteAllBytes(Path.Combine(directory, pair.Key), pair.Value);
                test(directory, files);
            }
            finally { Directory.Delete(directory, true); }
        }
        string runtimeJson = """{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.WindowsDesktop.App","version":"10.0.0"}]}}""".Replace("10.0", RuntimeCompatibility.RuntimeMajor + ".0");
        string depsJson = """{"runtimeTarget":{"name":".NETCoreApp,Version=v10.0"},"targets":{".NETCoreApp,Version=v10.0":{"RtlineDnsPilot/0.1.8":{"runtime":{"RtlineDnsPilot.dll":{}}}}},"libraries":{"RtlineDnsPilot/0.1.8":{}}}""".Replace("0.1.8", AppVersion.Current);

        Check("new service installation receipt records corrected RPC version", () =>
            Require(new InstallReceipt().Version == AppVersion.Current, "New installations still identify themselves as the old service build."));
        Check("service update appends catalog profiles while retaining service-only channels and IDs", () =>
        {
            var config=new AppConfig {Profiles=BundledChannels.All().Take(15).ToList()};
            var custom=JsonData.Clone(BundledChannels.All()[15]);custom.Name="Service custom channel";custom.Servers=["8.8.4.4"];custom.Note="Keep service data";
            config.Profiles.Add(custom);config.PinnedProfileId=custom.Id;
            var before=JsonData.Encode(config.Profiles);
            var expanded=ServiceInstaller.AddUpgradeCatalog(config,null,true,out var added,out var pending);
            Require(added==11 && pending==0 && expanded.Profiles.Count==27 && JsonData.Encode(expanded.Profiles.Take(16).ToList())==before
                && expanded.PinnedProfileId==custom.Id,"Service catalog update replaced an existing or colliding channel.");
            Require(expanded.Profiles.Any(p=>p.Id!=custom.Id&&AppConfig.SameEffectiveConfiguration(p,BundledChannels.All()[15])),"A new catalog configuration was lost behind a service-only ID collision.");
        });
        Check("service upgrade fills missing old catalog channels once and then preserves later deletions", () =>
        {
            var config=new AppConfig {Profiles=BundledChannels.All().Take(14).ToList()};
            var deleted=BundledChannels.All()[14].Id;
            var expanded=ServiceInstaller.AddUpgradeCatalog(config,null,true,out var added,out _);
            Require(added==12 && expanded.Profiles.Count==26 && expanded.Profiles.Any(p=>p.Id==deleted),"A new service catalog did not restore a missing old configuration.");
            expanded.Profiles.RemoveAll(p=>p.Id==deleted);
            var repeated=ServiceInstaller.AddUpgradeCatalog(expanded,BundledChannels.Revision,true,out added,out var pending);
            Require(added==0&&pending==0&&JsonData.Encode(repeated)==JsonData.Encode(expanded),"Repair of the same version re-added or changed catalog entries.");
        });
        Check("different package upgrades an owned old service", () =>
            Require(ServiceInstaller.PackageNeedsUpdate(Receipt("0.1.0"), owner, false), "Existing service would keep its old binaries."));
        Check("same version with different bytes still replaces the package", () =>
            Require(ServiceInstaller.PackageNeedsUpdate(Receipt(AppVersion.Current), owner, false), "Different contents were hidden by an equal version."));
        Check("matching current package is idempotent", () =>
            Require(!ServiceInstaller.PackageNeedsUpdate(Receipt(AppVersion.Current), owner, true), "Matching package would be stopped and copied again."));
        Check("older receipt still requires restart when files already match", () =>
            Require(ServiceInstaller.PackageNeedsUpdate(Receipt("0.1.0"), owner, true), "An older service process could remain running after only the files were updated."));
        Check("four-component receipt version matches current release", () =>
            Require(!ServiceInstaller.PackageNeedsUpdate(Receipt(AppVersion.Current + ".0"), owner, true), "Equivalent version formats caused an upgrade or downgrade."));
        Check("newer receipt cannot be downgraded", () =>
            Reject<InvalidOperationException>(() => ServiceInstaller.PackageNeedsUpdate(Receipt(newerVersion.ToString()), owner, false)));
        Check("newer installed DLL cannot be hidden by an older receipt", () =>
            Reject<InvalidOperationException>(() => ServiceInstaller.PackageNeedsUpdate(Receipt("0.1.0"), owner, false, newerVersion)));
        Check("unknown receipt version does not authorize replacement", () =>
            Reject<InvalidDataException>(() => ServiceInstaller.PackageNeedsUpdate(Receipt("broken"), owner, false)));
        Check("another owner cannot reinstall even an identical package", () =>
            Reject<UnauthorizedAccessException>(() => ServiceInstaller.PackageNeedsUpdate(Receipt(AppVersion.Current), "S-1-5-21-100-200-300-1002", true)));
        Check("same source and installed directory needs no self copy", () => WithPackageFiles((directory, files) =>
        {
            Require(ServiceInstaller.PackageMatchesInstalled(files, directory), "Identical installed path was considered a different package.");
            Require(!ServiceInstaller.PackageNeedsUpdate(Receipt(AppVersion.Current), owner, ServiceInstaller.PackageMatchesInstalled(files, directory)), "Same installed path would enter replacement.");
        }));
        Check("a changed fourth file invalidates package equality", () => WithPackageFiles((directory, files) =>
        {
            File.WriteAllBytes(Path.Combine(directory, "RtlineDnsPilot.runtimeconfig.json"), [10, 11, 13]);
            Require(!ServiceInstaller.PackageMatchesInstalled(files, directory), "Changed runtime configuration was ignored.");
        }));
        Check("a missing installed file requires replacement", () => WithPackageFiles((directory, files) =>
        {
            File.Delete(Path.Combine(directory, "RtlineDnsPilot.deps.json"));
            Require(!ServiceInstaller.PackageMatchesInstalled(files, directory), "Incomplete installation was accepted as current.");
        }));
        Check("missing last source file is rejected during preflight", () => WithPackageFiles((directory, _) =>
        {
            File.Delete(Path.Combine(directory, "RtlineDnsPilot.runtimeconfig.json"));
            var message = Reject<IOException>(() => ServiceInstaller.ReadPackage(directory));
            Require(message.Contains("RtlineDnsPilot.runtimeconfig.json", StringComparison.Ordinal), "Preflight did not identify the missing source file.");
        }));
        Check("invalid runtime JSON is rejected before service mutation", () => WithPackageFiles((directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "RtlineDnsPilot.runtimeconfig.json"), "{broken");
            var message = Reject<InvalidDataException>(() => ServiceInstaller.ReadPackage(directory));
            Require(message.Contains("runtimeconfig", StringComparison.Ordinal), "Invalid runtime JSON was not identified.");
        }));
        Check("invalid dependency map is rejected before service mutation", () => WithPackageFiles((directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "RtlineDnsPilot.runtimeconfig.json"), runtimeJson);
            File.WriteAllText(Path.Combine(directory, "RtlineDnsPilot.deps.json"), "{}");
            var message = Reject<InvalidDataException>(() => ServiceInstaller.ReadPackage(directory));
            Require(message.Contains("deps", StringComparison.Ordinal), "Invalid dependency map was not identified.");
        }));
        Check("corrupt managed DLL is rejected during package preflight", () => WithPackageFiles((directory, _) =>
        {
            File.WriteAllText(Path.Combine(directory, "RtlineDnsPilot.runtimeconfig.json"), runtimeJson);
            File.WriteAllText(Path.Combine(directory, "RtlineDnsPilot.deps.json"), depsJson);
            var message = Reject<InvalidDataException>(() => ServiceInstaller.ReadPackage(directory));
            Require(message.Contains("RtlineDnsPilot.dll", StringComparison.Ordinal), "Corrupt managed DLL was not identified.");
        }));
    }
}

