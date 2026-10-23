using System.Security.AccessControl;
using System.Security.Principal;

namespace NetPilot.Tests;

public static class InstallSafetyTests
{
    public static void Run(Action<string,bool,string> report)
    {
        var admin=new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid,null);
        var system=new SecurityIdentifier(WellKnownSidType.LocalSystemSid,null);
        var users=new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid,null);
        DirectorySecurity Trusted()
        {
            var acl=new DirectorySecurity(); acl.SetOwner(admin); acl.SetAccessRuleProtection(true,false);
            acl.AddAccessRule(new FileSystemAccessRule(admin,FileSystemRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(system,FileSystemRights.FullControl,AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.ReadAndExecute,AccessControlType.Allow));
            return acl;
        }
        void Check(string name,Func<bool> test)
        { try { bool ok=test(); report(name,ok,ok?"":"Unexpected trust decision."); } catch(Exception e) {report(name,false,e.Message);} }
        Check("administrator-owned read-only-user state directory is trusted",()=>ServiceInstaller.IsTrustedAcl(Trusted()));
        Check("user-writable recovery state directory is never trusted",()=>
        { var acl=Trusted(); acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.Modify,AccessControlType.Allow)); return !ServiceInstaller.IsTrustedAcl(acl); });
        Check("user ownership cannot authorize an elevated recovery journal",()=>
        { var acl=Trusted(); acl.SetOwner(users); return !ServiceInstaller.IsTrustedAcl(acl); });
        Check("user change-permissions permission invalidates privileged state",()=>
        { var acl=Trusted(); acl.AddAccessRule(new FileSystemAccessRule(users,FileSystemRights.ChangePermissions,AccessControlType.Allow)); return !ServiceInstaller.IsTrustedAcl(acl); });
    }
}
