namespace NetPilot.Tests;

public static class BrandingUpgradeTests
{
    public static void Run(Action<string,bool,string> report)
    {
        void Check(string name,Action test) {try {test();report(name,true,"");}catch(Exception ex){report(name,false,ex.Message);}}
        void Require(bool value) {if(!value)throw new Exception("Unexpected service ownership decision.");}
        void Reject(string path) {try {ServiceInstaller.OwnedServiceExecutable(path);}catch(InvalidOperationException){return;}throw new Exception("Unowned executable accepted.");}
        Check("renamed release recognizes the exact protected legacy service path",()=>
            Require(ServiceInstaller.OwnedServiceExecutable("\""+AppPaths.LegacyInstalledExe+"\" --service")==AppPaths.LegacyInstalledExe));
        Check("renamed release recognizes its exact protected service path",()=>
            Require(ServiceInstaller.OwnedServiceExecutable("\""+AppPaths.InstalledExe+"\" --service")==AppPaths.InstalledExe));
        Check("renaming never accepts a same-named executable from another directory",()=>
            Reject("\""+Path.Combine(Path.GetTempPath(),"RtlineDnsPilot.exe")+"\" --service"));
        Check("renaming rejects extra service command arguments and an unquoted path",()=>
        {
            Reject("\""+AppPaths.LegacyInstalledExe+"\" --service --install-service");
            Reject(AppPaths.InstalledExe+" --service");
        });
        Check("renamed service configuration replaces the legacy executable and reenables automatic startup",()=>
        {
            var args=ServiceInstaller.ServiceConfigurationArguments();
            Require(args[0]=="config"&&args[1]==AppPaths.ServiceName);
            Require(args[Array.IndexOf(args,"binPath=")+1]=="\""+AppPaths.InstalledExe+"\" --service");
            Require(args[Array.IndexOf(args,"start=")+1]=="auto");
        });
    }
}
