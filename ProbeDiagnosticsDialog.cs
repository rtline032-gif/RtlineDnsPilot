namespace NetPilot;

internal sealed class ProbeDiagnosticsDialog : Form
{
    private readonly Func<CancellationToken, Task<ChannelDiagnosticsSnapshot>> _read;
    private readonly CancellationTokenSource _cancellation = new();
    private readonly TextBox _report = new()
    {
        Name = "ProbeDiagnosticsReport", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F),
        BackColor = Color.White, Text = "Чтение сохранённого результата…"
    };
    private readonly Button _copy = new() { Name = "CopyProbeDiagnostics", Text = "Копировать", AutoSize = true, Enabled = false, Padding = new Padding(8, 4, 8, 4) };
    private bool _started;
    private bool _cancellationDisposed;

    internal ProbeDiagnosticsDialog(string profileName, Func<CancellationToken, Task<ChannelDiagnosticsSnapshot>> read)
    {
        _read = read ?? throw new ArgumentNullException(nameof(read));
        Text = "Диагностика канала — " + profileName;
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(620, 420);
        Size = new Size(1000, 720);
        ShowInTaskbar = false;
        MinimizeBox = false;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 3, ColumnCount = 1, Padding = new Padding(12) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var hint = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 0, 0, 8),
            Text = "Последнее сохранённое измерение. Просмотр не запускает проверку и не меняет DNS, VPN или службу."
        };
        root.Controls.Add(hint, 0, 0);
        root.SizeChanged += (_, _) => hint.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - root.Padding.Horizontal - 6), 0);
        root.Controls.Add(_report, 0, 1);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 4, 8, 4) };
        actions.Controls.AddRange([close, _copy]);
        root.Controls.Add(actions, 0, 2);
        Controls.Add(root);
        CancelButton = close;
        _copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(_report.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            { MessageBox.Show(this, "Буфер обмена занят. Повторите копирование.", "Диагностика канала", MessageBoxButtons.OK, MessageBoxIcon.Information); }
        };
        Shown += async (_, _) => await ReadOnceAsync();
    }

    private async Task ReadOnceAsync()
    {
        if (_started) return;
        _started = true;
        var token = _cancellation.Token;
        try
        {
            var snapshot = await _read(token);
            if (!IsDisposed && !token.IsCancellationRequested) _report.Text = ChannelDiagnostics.Format(snapshot);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !token.IsCancellationRequested)
                _report.Text = "Диагностика недоступна.\r\n\r\n" + ex.Message;
        }
        if (!IsDisposed && !token.IsCancellationRequested)
        {
            _report.SelectionStart = 0;
            _report.SelectionLength = 0;
            _copy.Enabled = true;
        }
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (!_cancellationDisposed) _cancellation.Cancel();
        base.OnFormClosed(e);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_cancellationDisposed)
        { _cancellationDisposed = true; _cancellation.Cancel(); _cancellation.Dispose(); }
        base.Dispose(disposing);
    }
}
