namespace NetPilot;

internal sealed class ApplicationUpdateProgressForm : Form
{
    private bool _finished;
    public int ExitCode { get; private set; }=1;

    public ApplicationUpdateProgressForm(string request)
    {
        Text="RtlineDnsPilot — обновление программы";
        StartPosition=FormStartPosition.CenterScreen;
        ClientSize=new Size(580,150);
        FormBorderStyle=FormBorderStyle.FixedDialog;
        MaximizeBox=false;MinimizeBox=false;
        Font=new Font("Segoe UI",10);
        var message=new Label {AutoSize=false,Location=new Point(20,18),Size=new Size(540,80),Text="Подготовка обновления…"};
        var progress=new ProgressBar {Location=new Point(20,112),Size=new Size(540,16),Style=ProgressBarStyle.Marquee};
        Controls.AddRange([message,progress]);
        FormClosing+=(_,e)=>
        {
            if(!_finished && e.CloseReason is not (CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing))
            { e.Cancel=true;message.Text="Дождитесь завершения обновления. Программа проверяет запуск новой версии."; }
        };
        Shown+=async (_,_)=>
        {
            try
            {
                ExitCode=await new ApplicationUpdateLauncher().CompleteAsync(request,
                    new Progress<ApplicationUpdateProgress>(step=> {if(!IsDisposed) message.Text=step.Message;}));
            }
            catch(Exception ex)
            { MessageBox.Show(this,"Обновление не завершено. "+ex.Message,Text,MessageBoxButtons.OK,MessageBoxIcon.Error); }
            finally { _finished=true;Close(); }
        };
    }
}
