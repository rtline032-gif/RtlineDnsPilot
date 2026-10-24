namespace NetPilot;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        string? Value(string key) { int i=Array.IndexOf(args,key); return i>=0&&i+1<args.Length?args[i+1]:null; }
        try
        {
            if(args.Contains("--service")) return NativeServiceHost.Run();
            if(args.Contains("--install-service")) { ServiceInstaller.InstallAsync(Value("--owner-sid")??throw new ArgumentException("Не указан SID пользователя."),forceRestart:args.Contains("--repair-service")).GetAwaiter().GetResult(); return 0; }
            if(args.Contains("--uninstall-service")) { ServiceInstaller.RestoreAsync(true).GetAwaiter().GetResult(); return 0; }
            if(args.Contains("--restore-dns"))
            {
                if(!ServiceInstaller.IsAdministrator) { ServiceClient.RecoverAsync().GetAwaiter().GetResult(); return 0; }
                ServiceInstaller.RestoreAsync(false).GetAwaiter().GetResult(); return 0;
            }
            if(args.Contains("--automatic-dns"))
            {
                string adapterId=Value("--adapter-id")??throw new ArgumentException("Для автоматического DNS нужен GUID выбранного адаптера.");
                if(!ServiceInstaller.IsAdministrator) { ServiceClient.ResetAutomaticAsync(adapterId).GetAwaiter().GetResult(); return 0; }
                ServiceInstaller.ResetAutomaticDnsAsync(adapterId,Value("--owner-sid")??System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value
                    ??throw new ArgumentException("Не определён пользователь Windows.")).GetAwaiter().GetResult(); return 0;
            }
            if(args.Contains("--probe")) { RunProbeAsync(Value("--out")??throw new ArgumentException("Для --probe нужен --out path."),Value("--profiles")).GetAwaiter().GetResult(); return 0; }
            ApplicationConfiguration.Initialize();
            if(args.Contains("--ui-smoke") || args.Contains("--journal-smoke") || args.Contains("--settings-smoke") || args.Contains("--channels-smoke"))
            {
                using var smoke=new MainForm(Value("--data-dir")??Path.Combine(Path.GetTempPath(),"NetPilot-ui-smoke"));
                var path=Value("--ui-smoke")??Value("--journal-smoke")??Value("--settings-smoke")??Value("--channels-smoke")??throw new ArgumentException("Не указан путь PNG.");
                if(args.Contains("--journal-smoke")) smoke.RenderJournalSmoke(path);
                else if(args.Contains("--settings-smoke")) smoke.RenderSettingsSmoke(path);
                else if(args.Contains("--channels-smoke")) smoke.RenderChannelsSmoke(path);
                else smoke.RenderSmoke(path);
                return 0;
            }
            if(args.Contains("--complete-application-update"))
            {
                using var updater=new ApplicationUpdateProgressForm(Value("--complete-application-update")??throw new ArgumentException("Не задано задание обновления."));
                Application.Run(updater);
                return updater.ExitCode;
            }
            var launcher=new ApplicationUpdateLauncher();
            string? warning=Value("--update-warning");
            var startupAck=Value("--update-startup-ack");
            if(startupAck is null && !args.Contains("--skip-update-route"))
            {
                if(launcher.TryLaunchCurrent(args,out var routeWarning)) return 0;
                warning??=routeWarning;
            }
            using var mutex=new Mutex(true,@"Local\NetPilot.Ui."+System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value,out bool first);
            if(!first) { MessageBox.Show("RtlineDnsPilot уже открыт. Найдите значок в области уведомлений.","RtlineDnsPilot"); return 0; }
            using var form=new MainForm(Value("--data-dir")??AppPaths.LocalData);
            if(warning is not null) form.ShowUpdateWarning(warning);
            if(startupAck is not null) form.Shown+=(_,_)=>
            {
                try { launcher.AcknowledgeStartup(startupAck); }
                catch { form.Dispose(); } // The worker observes missing ack/exit and restores the previous launch pointer.
            };
            Application.Run(form); return 0;
        }
        catch(Exception ex)
        {
            if(args.Contains("--probe") || args.Contains("--ui-smoke") || args.Contains("--journal-smoke") || args.Contains("--settings-smoke") || args.Contains("--channels-smoke"))
            {
                var error=Value("--out")??Value("--ui-smoke")??Value("--journal-smoke")??Value("--settings-smoke")??Value("--channels-smoke"); if(error!=null) File.WriteAllText(error+".error.txt",ex.ToString());
            }
            else if(args.Contains("--service")) NativeServiceHost.Log(ex.ToString());
            else MessageBox.Show(ex.Message,"RtlineDnsPilot: операция не завершена",MessageBoxButtons.OK,MessageBoxIcon.Error);
            return 1;
        }
    }
    private static async Task RunProbeAsync(string output,string? profilesFile)
    {
        using var transport=new DnsTransport(); var probe=new ProfileProbe(transport); var config=AppConfig.Defaults();
        if(profilesFile is not null) config.Profiles=UiBackend.ReadProfiles(profilesFile);
        config.Validate();
        using var parallel=new SemaphoreSlim(4);
        var results=await Task.WhenAll(config.Profiles.Where(p=>p.Enabled).Select(async p=>
        {
            await parallel.WaitAsync();
            try { return await probe.CheckAsync(p,config.ProbeUrl,TimeSpan.FromSeconds(config.TimeoutSeconds),CancellationToken.None); }
            finally { parallel.Release(); }
        }));
        AtomicStore.Write(output,new { At=DateTimeOffset.UtcNow, SystemDnsChanged=false, ProbeUrl=config.ProbeUrl,
            Scope="Прямая проверка DNS и публичного HTTPS; текущая маршрутизация/VPN сохраняется; сеанс Codex не проверяется.",Profiles=config.Profiles,Results=results });
    }
}
