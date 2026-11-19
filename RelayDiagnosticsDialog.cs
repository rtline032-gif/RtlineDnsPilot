namespace NetPilot;

internal sealed class RelayDiagnosticsDialog : Form
{
    private readonly CancellationTokenSource _stop = new();
    private readonly TextBox _report = new()
    {
        Name = "RelayDiagnosticsReport", Dock = DockStyle.Fill, Multiline = true, ReadOnly = true,
        ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 10F),
        BackColor = Color.White, Text = "Чтение метаданных запросов посредника…"
    };
    private readonly Button _copy = new() { Name = "CopyRelayDiagnostics", Text = "Копировать", AutoSize = true, Enabled = false };
    private bool _disposed;

    internal RelayDiagnosticsDialog(Func<CancellationToken, Task<RelayDiagnosticsSnapshot>> read)
    {
        Text = "Диагностика DNS-посредника"; StartPosition = FormStartPosition.CenterParent;
        Size = new Size(1020, 660); MinimumSize = new Size(700, 440);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, RowCount = 2, ColumnCount = 1, Padding = new Padding(12) };
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.Controls.Add(_report, 0, 0);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(0, 8, 0, 0) };
        var close = new Button { Text = "Закрыть", AutoSize = true, DialogResult = DialogResult.Cancel };
        buttons.Controls.AddRange([close, _copy]); layout.Controls.Add(buttons, 0, 1); Controls.Add(layout); CancelButton = close;
        _copy.Click += (_, _) =>
        {
            try { if (_report.TextLength > 0) Clipboard.SetText(_report.Text); }
            catch (System.Runtime.InteropServices.ExternalException)
            { MessageBox.Show(this,"Буфер обмена занят. Повторите копирование.","Диагностика посредника",MessageBoxButtons.OK,MessageBoxIcon.Information); }
        };
        Shown += async (_, _) =>
        {
            try
            {
                var snapshot = await read(_stop.Token);
                if (IsDisposed || _stop.IsCancellationRequested) return;
                RelayDiagnostics.Validate(snapshot); _report.Text = RelayDiagnostics.Format(snapshot); _copy.Enabled = true;
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
            catch (Exception ex) { if (!IsDisposed) _report.Text = "Диагностика не получена. " + ex.Message; }
        };
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    { if (!_disposed) _stop.Cancel(); base.OnFormClosed(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed) { _disposed = true; _stop.Cancel(); _stop.Dispose(); }
        base.Dispose(disposing);
    }
}
