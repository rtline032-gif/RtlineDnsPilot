namespace NetPilot;

internal sealed class ApplicationUpdateProgressForm : Form
{
    private bool _finished;
    public int ExitCode { get; private set; }=1;

    public ApplicationUpdateProgressForm(string request, bool rollback = false)
    {
        Text=rollback?"RtlineDnsPilot — откат обновления":"RtlineDnsPilot — обновление программы";
        StartPosition=FormStartPosition.CenterScreen;
        ClientSize=new Size(580,150);
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;MinimizeBox=false;
        Font=new Font("Segoe UI",10);
        var message=new Label {AutoSize=false,Location=new Point(20,18),Size=new Size(540,80),Text=rollback?"Подготовка отката…":"Подготовка обновления…"};
        var progress=new ProgressBar {Location=new Point(20,112),Size=new Size(540,16),Style=ProgressBarStyle.Marquee};
        Controls.AddRange([message,progress]);
        FormClosing+=(_,e)=>
        {
            if(!_finished && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing))
            { e.Cancel=true; }
        };
        Shown+=async (_,_)=>
        {
            try
            {
                var launcher=new ApplicationUpdateLauncher();
                var status=new Progress<ApplicationUpdateProgress>(step=> {if(!IsDisposed) message.Text=step.Message;});
                ExitCode=rollback?await launcher.CompleteRollbackAsync(request,status):await launcher.CompleteAsync(request,status);
            }
            catch(Exception ex)
            { MessageBox.Show(this,(rollback?"Откат не завершён. ":"Обновление не завершено. ")+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error); }
            finally { _finished=true;Close(); }
        };
    }
}
