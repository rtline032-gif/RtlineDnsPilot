using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Security.Cryptography;
using System.Text;

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
            Require(added==16 && pending==1 && expanded.Profiles.Count==32 && JsonData.Encode(expanded.Profiles.Take(16).ToList())==before
                && expanded.PinnedProfileId==custom.Id,"Service catalog update replaced an existing or colliding channel.");
            Require(expanded.Profiles.Any(p=>p.Id!=custom.Id&&AppConfig.SameEffectiveConfiguration(p,BundledChannels.All()[15])),"A new catalog configuration was lost behind a service-only ID collision.");
        });
        Check("service upgrade fills missing old catalog channels once and then preserves later deletions", () =>
        {
            var config=new AppConfig {Profiles=BundledChannels.All().Take(14).ToList()};
            var deleted=BundledChannels.All()[14].Id;
            var expanded=ServiceInstaller.AddUpgradeCatalog(config,null,true,out var added,out _);
            Require(added==18 && expanded.Profiles.Count==32 && expanded.Profiles.Any(p=>p.Id==deleted),"A new service catalog did not restore a missing old configuration.");
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
        RunRollbackTests(report);
    }

    private static void RunRollbackTests(Action<string,bool,string> report)
    {
        void Check(string name,Action test)
        {try {test();report(name,true,"");}catch(Exception ex) {report(name,false,ex.Message);}}
        const string owner="S-1-5-21-100-200-300-1001";
        string current=AppVersion.Current;
        string previous=new Version(Version.Parse(current).Major,Version.Parse(current).Minor,Math.Max(0,Version.Parse(current).Build-1)).ToString();
        Dictionary<string,byte[]> oldPackage=RollbackPackage(previous),newPackage=RollbackPackage(current);
        InstallReceipt Receipt(string version,string? snapshotId=null)=>new() {OwnerSid=owner,Version=version,RollbackSnapshotId=snapshotId};
        ServiceRollbackSnapshot Snapshot()=>ServiceInstaller.CreateRollbackSnapshot(Receipt(previous),oldPackage,current,newPackage);

        Check("service rollback accepts only the immediately replaced package and exact destination contents",()=>
        {
            var snapshot=Snapshot();
            ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,newPackage);
            Require(snapshot.SourceVersion==previous && snapshot.DestinationVersion==current && snapshot.Package.Count==4,"The protected snapshot lost its version pair or complete package.");
        });
        Check("service rollback rejects missing or stale snapshot identity, owner and version pair before mutation",()=>
        {
            var snapshot=Snapshot();
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(null,owner,Receipt(current),previous,newPackage));
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,Guid.NewGuid().ToString("N")),previous,newPackage));
            Reject<UnauthorizedAccessException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,"S-1-5-21-100-200-300-1002",Receipt(current,snapshot.Id),previous,newPackage));
            Reject<InvalidOperationException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),"0.0.1",newPackage));
        });
        Check("service rollback rejects corrupt package bytes and changed destination contents",()=>
        {
            var snapshot=Snapshot();snapshot.Package["RtlineDnsPilot.dll"][0]^=1;
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,newPackage));
            snapshot=Snapshot();var changed=newPackage.ToDictionary(x=>x.Key,x=>x.Value.ToArray(),StringComparer.Ordinal);
            changed["RtlineDnsPilot.exe"][^1]^=1;
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,changed));
            snapshot=Snapshot();snapshot.Package["unexpected.exe"]=[1];
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,newPackage));
        });
        Check("service rollback validates the actual PE and runtime independently of its hashes",()=>
        {
            var snapshot=Snapshot();snapshot.Package["RtlineDnsPilot.dll"][0]=0;
            snapshot.SourceHashes["RtlineDnsPilot.dll"]=Convert.ToHexString(SHA256.HashData(snapshot.Package["RtlineDnsPilot.dll"]));
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,newPackage));
            snapshot=Snapshot();snapshot.RuntimePlatform="other-runtime";
            Reject<InvalidDataException>(()=>ServiceInstaller.ValidateRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),previous,newPackage));
        });
        Check("same-version repair retains a valid preceding snapshot and binds its new destination bytes",()=>
        {
            var snapshot=Snapshot();var repaired=newPackage.ToDictionary(x=>x.Key,x=>x.Value.ToArray(),StringComparer.Ordinal);
            repaired["RtlineDnsPilot.runtimeconfig.json"]=Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(repaired["RtlineDnsPilot.runtimeconfig.json"])+" ");
            var retained=ServiceInstaller.RetainRollbackSnapshot(snapshot,owner,Receipt(current,snapshot.Id),newPackage,repaired);
            Require(retained.Id==snapshot.Id && retained.SourceVersion==previous,"Repair replaced the last real previous package.");
            ServiceInstaller.ValidateRollbackSnapshot(retained,owner,Receipt(current,retained.Id),previous,repaired);
        });
        Check("service rollback disposition refuses unrelated older and newer service versions",()=>
        {
            Require(ServiceInstaller.IsRollbackAlreadyApplied(Receipt(previous),previous),"The previous version was not recognized as an idempotent result.");
            Require(!ServiceInstaller.IsRollbackAlreadyApplied(Receipt(current),previous),"The current version was incorrectly treated as already rolled back.");
            Reject<InvalidOperationException>(()=>ServiceInstaller.IsRollbackAlreadyApplied(Receipt("99.0.0"),previous));
            Reject<InvalidOperationException>(()=>ServiceInstaller.IsRollbackAlreadyApplied(Receipt("0.0.1"),previous));
        });
        Check("service rollback restores protected bytes and receipt after a partial package write failure",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state) {FailFirstWrite=true};
            Reject<IOException>(()=>ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult());
            Require(fake.Trace.SequenceEqual(new[] {"validate","stop-restore","validate","statistics","write-previous","stop-restore","write-current","receipt-current","start","confirm-current"}),
                "Rollback did not restore the verified current service in the expected safety order.");
            Require(fake.CurrentVersion==current && !fake.ManagementRequested,"Failure did not return the current service with DNS management off.");
        });
        Check("service rollback preserves statistics before replacement and confirms the older live version",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state);
            ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult();
            Require(fake.Trace.SequenceEqual(new[] {"validate","stop-restore","validate","statistics","write-previous","receipt-previous","start","confirm-previous"})
                && fake.CurrentVersion==previous && !fake.ManagementRequested,"The rollback completed without its exact-package/status and DNS-off checks.");
        });
        Check("service rollback rejects changed current bytes before any stop or write",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state) {CurrentChanged=true};
            Reject<IOException>(()=>ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult());
            Require(fake.Trace.SequenceEqual(new[] {"validate"}),"A stale current-package plan performed a mutation.");
        });
        Check("statistics-copy failure restarts the current service without replacing runtime files",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state) {FailStatistics=true};
            Reject<IOException>(()=>ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult());
            Require(fake.Trace.SequenceEqual(new[] {"validate","stop-restore","validate","statistics","validate","start","confirm-current"})
                && fake.CurrentVersion==current && !fake.ManagementRequested,"A statistics backup error changed the protected runtime or failed to restart the current service.");
        });
        Check("failed previous-service confirmation restores and confirms the current package",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state) {FailFirstConfirmation=true};
            Reject<IOException>(()=>ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult());
            Require(fake.CurrentVersion==current && fake.Trace.Contains("confirm-previous") && fake.Trace.Last()=="confirm-current"
                && !fake.ManagementRequested,"An unconfirmed rollback left the previous package active or retained DNS intent.");
        });
        Check("incomplete current-package recovery is reported separately rather than claimed restored",()=>
        {
            var snapshot=Snapshot();var state=new ServiceRollbackState(Receipt(current,snapshot.Id),newPackage);
            var fake=new FakeRollbackPlatform(state) {FailFirstWrite=true,FailRecoveryWrite=true};
            Reject<AggregateException>(()=>ServiceInstaller.ExecuteRollbackAsync(state,snapshot,fake).GetAwaiter().GetResult());
            Require(fake.Trace.Last()=="write-current" && !fake.Trace.Contains("confirm-current"),"Incomplete current-package recovery was reported as a completed rollback.");
        });
    }

    private sealed class FakeRollbackPlatform(ServiceRollbackState initial) : IServiceRollbackPlatform
    {
        public readonly List<string> Trace=[];
        public bool FailFirstWrite,FailRecoveryWrite,FailStatistics,FailFirstConfirmation,CurrentChanged;
        public bool ManagementRequested=true;
        public string CurrentVersion=initial.Receipt.Version;
        private int _writes,_confirmations;
        public void RevalidateCurrent(ServiceRollbackState expected)
        {Trace.Add("validate");if(CurrentChanged)throw new IOException("Injected changed current package.");}
        public Task RestoreAndStopAsync() {Trace.Add("stop-restore");ManagementRequested=false;return Task.CompletedTask;}
        public void PreserveStatistics() {Trace.Add("statistics");if(FailStatistics)throw new IOException("Injected statistics copy error.");}
        public void WritePackage(IReadOnlyDictionary<string,byte[]> package)
        {
            bool isCurrent=package["RtlineDnsPilot.dll"].AsSpan().SequenceEqual(initial.Package["RtlineDnsPilot.dll"]);
            Trace.Add(isCurrent?"write-current":"write-previous");
            _writes++;
            if(FailFirstWrite && _writes==1 || FailRecoveryWrite && isCurrent)throw new IOException("Injected partial write.");
        }
        public void WriteReceipt(InstallReceipt receipt)
        {CurrentVersion=receipt.Version;Trace.Add(receipt.Version==initial.Receipt.Version?"receipt-current":"receipt-previous");}
        public Task StartAsync() {Trace.Add("start");return Task.CompletedTask;}
        public Task ConfirmAsync(Version version)
        {
            Require(ServiceClient.HasExpectedVersion(new AppStatus {ServiceVersion=CurrentVersion},version),"Confirmation received a different live service version.");
            Trace.Add(CurrentVersion==initial.Receipt.Version?"confirm-current":"confirm-previous");
            if(FailFirstConfirmation && ++_confirmations==1)throw new IOException("Injected live confirmation failure.");
            return Task.CompletedTask;
        }
    }

    private static Dictionary<string,byte[]> RollbackPackage(string version)
    {
        var metadata=new MetadataBuilder();
        metadata.AddModule(0,metadata.GetOrAddString("RtlineDnsPilot.dll"),metadata.GetOrAddGuid(Guid.NewGuid()),default,default);
        var parsed=Version.Parse(version);var assemblyVersion=new Version(parsed.Major,parsed.Minor,Math.Max(0,parsed.Build),Math.Max(0,parsed.Revision));
        metadata.AddAssembly(metadata.GetOrAddString("RtlineDnsPilot"),assemblyVersion,default,default,0,AssemblyHashAlgorithm.Sha256);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic,default,metadata.GetOrAddString("<Module>"),default,MetadataTokens.FieldDefinitionHandle(1),MetadataTokens.MethodDefinitionHandle(1));
        var code=new BlobBuilder();var encoder=new InstructionEncoder(code);encoder.OpCode(ILOpCode.Ret);
        var bodies=new BlobBuilder();int body=new MethodBodyStreamEncoder(bodies).AddMethodBody(encoder);
        var signature=new BlobBuilder();new BlobEncoder(signature).MethodSignature().Parameters(0,r=>r.Void(),p=>{});
        var main=metadata.AddMethodDefinition(MethodAttributes.Public|MethodAttributes.Static,MethodImplAttributes.IL,
            metadata.GetOrAddString("Main"),metadata.GetOrAddBlob(signature),body,MetadataTokens.ParameterHandle(1));
        metadata.AddTypeDefinition(TypeAttributes.Public,default,metadata.GetOrAddString("Program"),default,MetadataTokens.FieldDefinitionHandle(1),main);
        var image=new ManagedPEBuilder(new PEHeaderBuilder(imageCharacteristics:Characteristics.ExecutableImage|Characteristics.Dll),
            new MetadataRootBuilder(metadata),bodies,entryPoint:main,flags:CorFlags.ILOnly);
        var output=new BlobBuilder();image.Serialize(output);byte[] pe=output.ToArray();
        string runtime="{\"runtimeOptions\":{\"tfm\":\""+RuntimeCompatibility.TargetFramework+"\",\"frameworks\":[{\"name\":\"Microsoft.NETCore.App\",\"version\":\""+RuntimeCompatibility.RuntimeMajor+".0.0\"},{\"name\":\"Microsoft.WindowsDesktop.App\",\"version\":\""+RuntimeCompatibility.RuntimeMajor+".0.0\"}]}}";
        string library="RtlineDnsPilot/"+version;
        string deps="{\"runtimeTarget\":{\"name\":\"test\"},\"targets\":{\"test\":{\""+library+"\":{\"runtime\":{\"RtlineDnsPilot.dll\":{}}}}},\"libraries\":{\""+library+"\":{}}}";
        return new(StringComparer.Ordinal) {["RtlineDnsPilot.exe"]=pe.ToArray(),["RtlineDnsPilot.dll"]=pe,["RtlineDnsPilot.runtimeconfig.json"]=Encoding.UTF8.GetBytes(runtime),["RtlineDnsPilot.deps.json"]=Encoding.UTF8.GetBytes(deps)};
    }
    private static void Require(bool condition,string message) {if(!condition)throw new InvalidOperationException(message);}
    private static void Reject<T>(Action action) where T:Exception
    {try {action();}catch(T) {return;}throw new InvalidOperationException("Expected rejection: "+typeof(T).Name);}
}

