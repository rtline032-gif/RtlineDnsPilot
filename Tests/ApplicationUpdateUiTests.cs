using System.Net;
using System.Reflection;
using System.Text;

namespace NetPilot.Tests;

public static class ApplicationUpdateUiTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        try
        {
            await OnUiThread(async (form,handler,configPath,original,_) =>
            {
                var button=Find(form).OfType<Button>().Single(x=>x.Name=="UpdateApplication");
                Require(button.Enabled && button.Text=="Обновить программу", "The application update action is missing or unavailable.");
                Require(form.Text.Contains(AppVersion.Current,StringComparison.Ordinal), "The window does not identify its installed version.");
                Require(handler.Requests==0,"Opening the application automatically contacted GitHub.");
                var tabs=Find(form).OfType<TabControl>().First();
                foreach(TabPage page in tabs.TabPages)
                { tabs.SelectedTab=page;Require(button.Visible,"The update button disappears on another tab."); }
                var method=typeof(MainForm).GetMethod("UpdateApplicationAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
                await (Task)method.Invoke(form,null)!;
                var activity=(ToolStripStatusLabel)typeof(MainForm).GetField("_activity",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
                Require(handler.Requests==1 && activity.Text.Contains("Новых версий нет",StringComparison.Ordinal),"The button did not perform one manual check with a clear result.");
                await Task.Delay(3000);
                Require(handler.Requests==1 && activity.Text.Contains("Новых версий нет",StringComparison.Ordinal),"Periodic service refresh hid the update result or repeated the GitHub check.");
                Require(File.ReadAllText(configPath)==original,"Checking updates changed the user's configuration.");
                Require(!Directory.Exists(Path.Combine(Path.GetDirectoryName(configPath)!,"updates")),"A no-update result started staging an installation.");
            });
            report("application update button is visible on every tab and checks manually without changing data",true,"");
        }
        catch(Exception ex) {report("application update button is visible on every tab and checks manually without changing data",false,ex.Message);}
        try
        {
            await OnUiThread(async (form,handler,configPath,original,service)=>
            {
                var method=typeof(MainForm).GetMethod("UpdateApplicationAsync",BindingFlags.NonPublic|BindingFlags.Instance)!;
                await (Task)method.Invoke(form,null)!;
                var activity=(ToolStripStatusLabel)typeof(MainForm).GetField("_activity",BindingFlags.NonPublic|BindingFlags.Instance)!.GetValue(form)!;
                Require(handler.Requests==1 && service.Installations==1 && service.Version==AppVersion.Current,"An already-new GUI left its older installed service unchanged.");
                Require(activity.Text.Contains("служба обновлена",StringComparison.Ordinal),"The single update action did not report the service result.");
                Require(File.ReadAllText(configPath)==original,"Service-only updating reset the user's channel configuration.");
            },withOldService:true);
            report("application update action also upgrades an older service when the GUI is already current",true,"");
        }
        catch(Exception ex) {report("application update action also upgrades an older service when the GUI is already current",false,ex.Message);}
    }

    private static Task OnUiThread(Func<MainForm,Handler,string,string,Connection,Task> check,bool withOldService=false)
    {
        var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var root=Path.Combine(Path.GetTempPath(),"Rtline-update-ui-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config=AppConfig.Defaults();config.MonitoringPaused=true;foreach(var profile in config.Profiles) profile.Enabled=false;
                var configPath=Path.Combine(root,"ui-config.json");AtomicStore.Write(configPath,config);
                var before=File.ReadAllText(configPath);
                var service=new Connection(config,withOldService);
                var backend=new UiBackend(root,service);
                var handler=new Handler();
                using var form=new MainForm(backend,new DnsShareClient(),new ApplicationUpdateClient(handler),new ApplicationUpdateLauncher(Path.Combine(root,"updates")));
                form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async (_,_)=>
                {
                    try
                    {
                        var busy=typeof(MainForm).GetField("_busy",BindingFlags.Instance|BindingFlags.NonPublic)!;
                        using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                        while((bool)busy.GetValue(form)!) await Task.Delay(20,deadline.Token);
                        await check(form,handler,configPath,before,service);
                        complete.TrySetResult();
                    }
                    catch(Exception ex) {complete.TrySetException(ex);}
                    finally {form.Dispose();Application.ExitThread();}
                };
                Application.Run(form);
            }
            catch(Exception ex) {complete.TrySetException(ex);}
            finally
            {
                var parent=Path.GetFullPath(Path.GetTempPath());var full=Path.GetFullPath(root);
                if(full.StartsWith(Path.TrimEndingDirectorySeparator(parent)+Path.DirectorySeparatorChar,StringComparison.OrdinalIgnoreCase)
                    && Path.GetFileName(full).StartsWith("Rtline-update-ui-",StringComparison.Ordinal))
                    try {Directory.Delete(full,true);}catch(IOException) { }
            }
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private static IEnumerable<Control> Find(Control control)
    { foreach(Control child in control.Controls) {yield return child;foreach(var nested in Find(child)) yield return nested;} }
    private static void Require(bool ok,string message) {if(!ok) throw new InvalidOperationException(message);}
    private sealed class Handler:HttpMessageHandler
    {
        public int Requests {get;private set;}
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
        {
            Requests++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {Content=new StringContent("[]",Encoding.UTF8,"application/json")});
        }
    }
    private sealed class Connection(AppConfig config,bool installed):IUiServiceConnection
    {
        public bool IsInstalled=>installed;
        public string Version {get;private set;}="0.1.9";
        public int Installations {get;private set;}
        public Task<RpcReply> SendAsync(RpcRequest request,CancellationToken token)
        {
            if(!installed) throw new IOException("No service in isolated UI test.");
            if(request.Command!="status") throw new InvalidOperationException("Unexpected service mutation in UI test.");
            return Task.FromResult(new RpcReply {Ok=true,Status=new AppStatus {Config=JsonData.Clone(config),ServiceVersion=Version,ServiceConnected=true,Monitoring=false,SystemDnsManaged=false}});
        }
        public Task InstallAsync() {Installations++;Version=AppVersion.Current;return Task.CompletedTask;}
        public Task RecoverAsync()=>throw new InvalidOperationException("DNS recovery is forbidden in UI tests.");
    }
}
