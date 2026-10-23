namespace NetPilot;

public sealed class ProfileDialog : Form
{
    private readonly ChannelProfile _source;
    private readonly ChannelProfile[] _existingProfiles;
    private readonly TextBox _name = new() { Dock = DockStyle.Fill, MaxLength = 100 };
    private readonly CheckBox _enabled = new() { Text = "Участвует в проверках и выборе", AutoSize = true };
    private readonly Label _mode = new() { AutoSize = true, Dock = DockStyle.Fill };
    private readonly TextBox _servers = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, AcceptsReturn = true };
    private readonly TextBox _doh = new() { Dock = DockStyle.Fill, PlaceholderText = "Необязательно: https://.../dns-query" };
    private readonly NumericUpDown _port = new() { Minimum = 1, Maximum = 65535, Value = 53, Width = 100 };
    private readonly TextBox _note = new() { Multiline = true, ScrollBars = ScrollBars.Vertical, Dock = DockStyle.Fill, MaxLength = 2000 };
    private readonly Label _addressHint = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    public ChannelProfile Profile { get; private set; }

    public ProfileDialog(ChannelProfile profile, bool isNew = false, IEnumerable<ChannelProfile>? existingProfiles = null)
    {
        _source = JsonData.Clone(profile);
        _existingProfiles = existingProfiles?.ToArray() ?? [];
        Profile = JsonData.Clone(profile);
        Text = isNew ? "Добавить канал" : "Изменить канал";
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Font = new Font("Segoe UI", 9F);
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(560, 600);
        Size = new Size(640, 660);
        ShowIcon = false;
        MinimizeBox = false;
        MaximizeBox = false;

        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(18), ColumnCount = 2, RowCount = 10 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 126));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 108));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        AddField(layout, "Название", _name, 0);
        layout.Controls.Add(_enabled, 1, 1);
        AddField(layout, "Протокол", _mode, 2);
        AddField(layout, "IP-адреса", _servers, 3);
        layout.Controls.Add(_addressHint, 1, 4);
        AddField(layout, "DoH URL", _doh, 5);
        AddField(layout, "Порт DNS", _port, 6);
        AddField(layout, "Примечание", _note, 7);
        var hint = new Label
        {
            AutoSize = true, Dock = DockStyle.Fill, MaximumSize = new Size(540, 0), ForeColor = Color.DimGray,
            Text = "Оставьте DoH URL пустым для обычного DNS. Если URL указан, используется HTTPS с проверкой сертификата, без автоматического перехода на открытый DNS. IP-адреса уточняйте по официальным данным провайдера.",
            Margin = new Padding(0, 12, 0, 12)
        };
        layout.Controls.Add(hint, 0, 8);
        layout.SetColumnSpan(hint, 2);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = new Button { Text = "Сохранить", AutoSize = true, Padding = new Padding(12, 4, 12, 4) };
        var cancel = new Button { Text = "Отмена", AutoSize = true, DialogResult = DialogResult.Cancel, Padding = new Padding(12, 4, 12, 4) };
        actions.Controls.Add(save);
        actions.Controls.Add(cancel);
        layout.Controls.Add(actions, 0, 9);
        layout.SetColumnSpan(actions, 2);
        Controls.Add(layout);
        AcceptButton = save;
        CancelButton = cancel;
        _name.Text = profile.Name;
        _enabled.Checked = profile.Enabled;
        _servers.Text = string.Join(Environment.NewLine, profile.Servers ?? []);
        _doh.Text = ProfileEditor.GetDohUrlForEditing(profile);
        _port.Value = profile.DnsPort;
        _note.Text = profile.Note;
        _doh.TextChanged += (_, _) => UpdateMode();
        save.Click += (_, _) => Save();
        UpdateMode();
    }

    private static void AddField(TableLayoutPanel panel, string name, Control control, int row)
    {
        panel.Controls.Add(new Label { Text = name, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 7, 8, 7) }, 0, row);
        control.Margin = new Padding(0, 4, 0, 5);
        panel.Controls.Add(control, 1, row);
    }

    private void UpdateMode()
    {
        var doh = !string.IsNullOrWhiteSpace(_doh.Text);
        _mode.Text = doh ? "DNS over HTTPS (DoH) — по URL" : "Обычный DNS — UDP / TCP";
        _port.Enabled = !doh;
        _addressHint.Text = doh
            ? "Bootstrap IP провайдера, по одному в строке. Имя и SNI берутся из DoH URL."
            : "Адреса DNS-серверов, по одному в строке. При усечённом UDP-ответе используется TCP.";
    }

    private void Save()
    {
        try
        {
            var profile = ProfileEditor.Build(_source, _name.Text, _enabled.Checked, _servers.Text, _doh.Text,
                (int)_port.Value, _note.Text, _existingProfiles);
            Profile = profile;
            DialogResult = DialogResult.OK;
            Close();
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Проверьте канал", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    }
}
