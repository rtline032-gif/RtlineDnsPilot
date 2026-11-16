namespace NetPilot;

internal sealed class ApplicationReconnectDialog : Form
{
    private readonly CheckBox _chrome = new() { Text = "Google Chrome — все вкладки", Checked = true, AutoSize = true, Margin = new Padding(0, 5, 0, 5) };
    private readonly CheckBox _codex = new() { Text = "Codex", Checked = true, AutoSize = true, Margin = new Padding(0, 5, 0, 5) };
    internal ApplicationReconnectTargets Targets => (_chrome.Checked ? ApplicationReconnectTargets.Chrome : ApplicationReconnectTargets.None)
        | (_codex.Checked ? ApplicationReconnectTargets.Codex : ApplicationReconnectTargets.None);

    public ApplicationReconnectDialog()
    {
        Text = "Переподключить приложения";
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterParent;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = false;
        ShowInTaskbar = false;
        ClientSize = new Size(610, 370);
        BackColor = Color.FromArgb(245, 247, 250);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 1, RowCount = 4 };
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(568, 0), Text =
            "Окна останутся открыты. Помощник очистит системный DNS-кэш и сбросит внешние TCP/IPv4-соединения HTTPS выбранных приложений этого пользователя в текущем сеансе Windows.\r\n\r\n"
            + "Текущие запросы и соединения других вкладок выбранного Chrome прервутся. Их потребуется повторить." }, 0, 0);
        var choices = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = new Padding(0, 12, 0, 8) };
        choices.Controls.AddRange([_chrome, _codex]);
        root.Controls.Add(choices, 0, 1);
        root.Controls.Add(new Label { AutoSize = true, MaximumSize = new Size(568, 0), ForeColor = Color.DimGray, Text =
            "IPv6, QUIC и внутренний DNS-кэш приложений не сбрасываются. Переподключение может не заменить полный перезапуск. Windows запросит разрешение администратора." }, 0, 2);
        var actions = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft, WrapContents = false, Margin = new Padding(0, 12, 0, 0) };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(8, 4, 8, 4) };
        var reconnect = new Button { Text = "Сбросить соединения", AutoSize = true, DialogResult = DialogResult.OK, Padding = new Padding(8, 4, 8, 4) };
        actions.Controls.AddRange([cancel, reconnect]);
        root.Controls.Add(actions, 0, 3);
        void UpdateChoice() => reconnect.Enabled = Targets != ApplicationReconnectTargets.None;
        _chrome.CheckedChanged += (_, _) => UpdateChoice();
        _codex.CheckedChanged += (_, _) => UpdateChoice();
        Controls.Add(root);
        AcceptButton = reconnect;
        CancelButton = cancel;
    }
}
