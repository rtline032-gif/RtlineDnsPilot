using System.Reflection;

namespace NetPilot.Tests;

public static class ChannelToggleTests
{
    public static async Task RunAsync(Action<string,bool,string> report)
    {
        try { await CheckUiAsync(); report("checkbox click and Space persist channel participation without opening the editor",true,""); }
        catch(Exception ex) { report("checkbox click and Space persist channel participation without opening the editor",false,ex.GetBaseException().Message); }
    }

    private static Task CheckUiAsync()
    {
        var complete=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread=new Thread(()=>
        {
            var root=Path.Combine(AppContext.BaseDirectory,"test-data","checkbox-"+Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            try
            {
                var config=AppConfig.Defaults(); config.MonitoringPaused=true; config.AutoSelection=AutoSelectionMode.Fast;
                foreach(var profile in config.Profiles) profile.Enabled=false;
                config.Profiles[0].Note="Historical note, not a current measurement";
                AtomicStore.Write(Path.Combine(root,"ui-config.json"),config);
                var id=config.Profiles[0].Id;
                var service=new UiPersistenceTests.FakeService {State=new AppStatus {Config=config,ServiceVersion=AppVersion.Current,
                    Statistics=[new ProfileStats {ProfileId=id,Samples=[new ProbeResult {ProfileId=id,Success=false,Stage="dns",
                        MonitoringKey=MonitoringTargets.Key(config),Detail="Actual DNS failure from the last measurement"}]}]}};
                var backend=new UiBackend(root,service);
                using var form=new MainForm(backend,new DnsShareClient(),new ApplicationUpdateClient(new NoNetwork()),new ApplicationUpdateLauncher(Path.Combine(root,"updates")));
                form.ShowInTaskbar=false;form.Opacity=0;
                form.Shown+=async (_,_)=>
                {
                    try
                    {
                        var flags=BindingFlags.Instance|BindingFlags.NonPublic;
                        var busy=typeof(MainForm).GetField("_busy",flags)!;
                        async Task Idle()
                        {
                            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
                            while((bool)busy.GetValue(form)!) await Task.Delay(10,deadline.Token);
                        }
                        await Idle();
                        var grid=(DataGridView)typeof(MainForm).GetField("_grid",flags)!.GetValue(form)!;
                        var row=grid.Rows.Cast<DataGridViewRow>().Single(r=>Equals(r.Tag,id));
                        var tooltip=row.Cells["name"].ToolTipText;
                        Require(tooltip.IndexOf("Actual DNS failure",StringComparison.Ordinal)>=0
                            && tooltip.IndexOf("Actual DNS failure",StringComparison.Ordinal)<tooltip.IndexOf("Historical note",StringComparison.Ordinal),"The name tooltip hides the actual error behind historical notes.");
                        typeof(DataGridView).GetMethod("OnCellClick",flags)!.Invoke(grid,[new DataGridViewCellEventArgs(grid.Columns["enabled"].Index,row.Index)]);
                        await Idle();
                        Require(service.State.Config.Profiles.Single(p=>p.Id==id).Enabled && service.ConfigurationCalls==1,"One checkbox click did not persist the enabled state.");
                        var selected=JsonData.Clone(backend.Status.Config);selected.ManualProfileId=selected.PinnedProfileId=id;selected.ManualUntil=DateTimeOffset.UtcNow.AddMinutes(5);
                        await backend.ConfigureAsync(selected);
                        typeof(MainForm).GetMethod("UpdateView",flags)!.Invoke(form,null);
                        row=grid.Rows.Cast<DataGridViewRow>().Single(r=>Equals(r.Tag,id));grid.CurrentCell=row.Cells["enabled"];
                        var key=new KeyEventArgs(Keys.Space);
                        typeof(Control).GetMethod("OnKeyDown",flags)!.Invoke(grid,[key]);
                        await Idle();
                        var saved=AtomicStore.Read<AppConfig>(Path.Combine(root,"ui-config.json"))!;
                        Require(!saved.Profiles.Single(p=>p.Id==id).Enabled && saved.ManualProfileId is null && saved.PinnedProfileId is null && saved.ManualUntil is null,"Space did not disable/unpin the selected channel.");
                        Require(key.Handled && saved.AutoSelection==AutoSelectionMode.Fast && saved.MonitoringPaused && saved.Profiles.Count==3
                            && saved.Profiles[1].Id==config.Profiles[1].Id && service.State.Statistics.Single().ProfileId==id,"Toggling changed unrelated settings, channel identities or history.");
                        complete.TrySetResult();
                    }
                    catch(Exception ex) {complete.TrySetException(ex);}
                    finally {form.Dispose();Application.ExitThread();}
                };
                Application.Run(form);
            }
            catch(Exception ex) {complete.TrySetException(ex);}
        }) {IsBackground=true};
        thread.SetApartmentState(ApartmentState.STA);thread.Start();
        return complete.Task.WaitAsync(TimeSpan.FromSeconds(20));
    }
    private static void Require(bool value,string reason) {if(!value) throw new InvalidOperationException(reason);}
    private sealed class NoNetwork:HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken token)
            =>throw new InvalidOperationException("External HTTP is forbidden in the checkbox test.");
    }
}
