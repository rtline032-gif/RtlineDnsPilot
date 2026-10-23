using System.Globalization;

namespace NetPilot;

public sealed class MainForm : Form
{
    private readonly UiBackend _backend;
    private readonly DnsShareClient _shareClient;
    private CancellationTokenSource? _shareCancellation;
    private string? _shareFeedback;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 2500 };
    private readonly ToolTip _tips = new() { AutoPopDelay = 12000 };
    private readonly NotifyIcon _tray;
    private readonly Label _headline = new() { AutoSize = true, Font = new Font("Segoe UI", 16F, FontStyle.Bold), ForeColor = Color.FromArgb(24, 45, 65), Dock = DockStyle.Fill };
    private readonly Label _description = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(63, 78, 91), Margin = new Padding(0, 6, 0, 0) };
    private readonly Label _alert = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(155, 45, 32), Margin = new Padding(0, 7, 0, 0) };
    private readonly Button _monitor = MakeButton("Пауза проверок");
    private readonly Button _probe = MakeButton("Проверить сейчас");
    private readonly Button _enable = MakeButton("Включить управление DNS");
    private readonly Button _updateService = MakeButton("Обновить службу");
    private readonly Button _reconnect = MakeButton("Проверить связь");
    private readonly Button _disable = MakeButton("Отключить управление");
    private readonly Button _restore = MakeButton("Восстановить DNS");
    private readonly Button _automaticDns = MakeButton("DNS автоматически (DHCP)");
    private readonly Button _add = MakeButton("Добавить");
    private readonly Button _edit = MakeButton("Изменить");
    private readonly Button _delete = MakeButton("Удалить");
    private readonly Button _import = MakeButton("Импорт JSON");
    private readonly Button _export = MakeButton("Экспорт JSON");
    private readonly Button _share = MakeButton("Поделиться DNS");
    private readonly Button _prefer = MakeButton("Предпочитать выбранный");
    private readonly Button _auto = MakeButton("Автовыбор");
    private readonly ContextMenuStrip _autoModeMenu = new();
    private readonly ToolStripMenuItem _autoStable = new("Самый стабильный");
    private readonly ToolStripMenuItem _autoFast = new("Самый быстрый");
    private readonly Button _apply = MakeButton("Применить настройки");
    private readonly Button _applyTargets = MakeButton("Применить объекты");
    private readonly Button _addTarget = MakeButton("Добавить объект");
    private readonly Button _editTarget = MakeButton("Изменить объект");
    private readonly Button _deleteTarget = MakeButton("Удалить объект");
    private readonly DataGridView _targetGrid = new();
    private readonly List<MonitoringEndpoint> _additionalTargets = [];
    private readonly Label _targetsHint = new() { Text = "Изменения начнут действовать после «Применить объекты».", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    private readonly DataGridView _grid = new();
    private readonly TextBox _url = new() { Dock = DockStyle.Fill, MaxLength = 2048 };
    private readonly CheckBox _monitorChatGpt = new() { Text = "ChatGPT / Codex", AutoSize = true, Checked = true, Margin = new Padding(0, 4, 24, 4) };
    private readonly CheckBox _monitorGemini = new() { Text = "Gemini", AutoSize = true, Margin = new Padding(0, 4, 24, 4) };
    private readonly CheckBox _monitorTailscale = new() { Text = "Tailscale", AutoSize = true, Margin = new Padding(0, 4, 0, 4) };
    private readonly ContextMenuStrip _channelMenu = new();
    private readonly ToolStripMenuItem _fixPermanent = new("Зафиксировать постоянно");
    private readonly ToolStripMenuItem _fixFive = new("Зафиксировать на 5 минут");
    private readonly ToolStripMenuItem _fixFifteen = new("Зафиксировать на 15 минут");
    private readonly ToolStripMenuItem _fixHour = new("Зафиксировать на 60 минут");
    private readonly ToolStripMenuItem _contextAuto = new("Вернуться к автовыбору");
    private readonly ToolStripMenuItem _contextAutoMode = new("Приоритет автовыбора");
    private readonly ToolStripMenuItem _contextAutoStable = new("Самый стабильный");
    private readonly ToolStripMenuItem _contextAutoFast = new("Самый быстрый");
    private readonly ToolStripMenuItem _contextDelete = new("Удалить канал…");
    private string? _contextProfileId;
    private readonly NumericUpDown _interval = new() { Minimum = 10, Maximum = 3600, Width = 120 };
    private readonly NumericUpDown _timeout = new() { Minimum = 2, Maximum = 30, Width = 120 };
    private readonly ComboBox _adapter = new() { Dock = DockStyle.Fill, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly Label _settingsHint = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray };
    private readonly Label _selection = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(48, 66, 81), Padding = new Padding(3, 8, 3, 6) };
    private readonly Panel _countryPanel = new() { Dock = DockStyle.Fill, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, AutoScroll = true, MaximumSize = new Size(0, 112), Margin = new Padding(0, 0, 0, 8), BackColor = Color.FromArgb(240, 245, 251) };
    private readonly Label _countries = new() { AutoSize = true, Dock = DockStyle.Top, ForeColor = Color.FromArgb(43, 65, 87), BackColor = Color.FromArgb(240, 245, 251), Padding = new Padding(8, 6, 8, 6), Margin = new Padding(0), AccessibleName = "Страны соединения по объектам" };
    private readonly TextBox _logs = new() { Dock = DockStyle.Fill, Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Both, WordWrap = false, Font = new Font("Consolas", 9F), BackColor = Color.White };
    private readonly ChannelHistoryView _history = new() { Dock = DockStyle.Fill };
    private readonly TabControl _tabs = new() { Dock = DockStyle.Fill, Margin = new Padding(0), Padding = new Point(14, 6) };
    private readonly ToolStripStatusLabel _source = new() { Spring = true, TextAlign = ContentAlignment.MiddleLeft };
    private readonly ToolStripStatusLabel _activity = new() { Text = "Запуск…" };
    private readonly Label _monitoringIndicator = MakeStateIndicator(200);
    private readonly Label _dnsIndicator = MakeStateIndicator(210);
    private readonly Label _measurementIndicator = new() { Width = 275, Height = 28, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, ForeColor = Color.FromArgb(68, 84, 98), Margin = new Padding(5, 0, 0, 0) };
    private readonly ProgressBar _readProgress = new() { Width = 60, Height = 10, Style = ProgressBarStyle.Marquee, MarqueeAnimationSpeed = 30, Visible = false };
    private readonly List<LogEntry> _uiEvents = [];
    private bool _busy;
    private bool _refreshing;
    private bool _updatingView;
    private bool _historyRefreshing;
    private bool _historyReloadRequested;
    private CancellationTokenSource? _historyCancellation;
    private string? _historyFetchKey;
    private string? _historyLastAttemptKey;
    private DateTimeOffset _historyLastAttempt;
    private bool _reallyClose;
    private bool _smoke;
    private bool _backendDisposed;
    private bool _loadingSettings;
    private bool _settingsDirty;
    private bool _settingsLoaded;
    private bool _incidentOpen;
    private DateTimeOffset _incidentAt;
    private bool _hideTipShown;
    private string _settingsKey = "";
    private string _adapterKey = "";
    private string _lastLogs = "";
    private string _serviceColumnsKey = "";
    private const string AdministratorHelp = "Windows покажет окно «Разрешить вносить изменения?» — нажмите «Да». Для обычных проверок права администратора не нужны.";

    public MainForm(string dataDirectory) : this(new UiBackend(dataDirectory)) { }

    internal MainForm(UiBackend backend) : this(backend, new DnsShareClient()) { }

    internal MainForm(UiBackend backend, DnsShareClient shareClient)
    {
        _backend = backend;
        _shareClient = shareClient;
        Text = "RtlineDnsPilot — каналы DNS и проверка HTTPS";
        Icon = SystemIcons.Application;
        Font = new Font("Segoe UI", 9F);
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(900, 710);
        Size = new Size(1460, 880);
        BackColor = Color.FromArgb(245, 247, 250);
        DoubleBuffered = true;

        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = new Padding(16, 14, 16, 0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        root.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var banner = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, AutoSize = true, Padding = new Padding(14), BackColor = Color.FromArgb(231, 239, 247), Margin = new Padding(0, 0, 0, 10) };
        banner.Controls.Add(_headline, 0, 0);
        banner.Controls.Add(_description, 0, 1);
        banner.Controls.Add(_alert, 0, 2);
        banner.SizeChanged += (_, _) => { _description.MaximumSize = new Size(Math.Max(100, banner.ClientSize.Width - 30), 0); _alert.MaximumSize = _description.MaximumSize; };
        root.Controls.Add(banner, 0, 0);
        var operationBar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 9) };
        _share.Font = new Font(Font, FontStyle.Bold);
        _share.BackColor = Color.FromArgb(220, 244, 235);
        _share.ForeColor = Color.FromArgb(25, 86, 62);
        _share.FlatAppearance.BorderColor = Color.FromArgb(61, 130, 102);
        _share.FlatAppearance.BorderSize = 2;
        _share.FlatAppearance.MouseOverBackColor = Color.FromArgb(203, 236, 221);
        _share.FlatAppearance.MouseDownBackColor = Color.FromArgb(180, 221, 201);
        _share.Margin = new Padding(10, 0, 0, 6);
        operationBar.Controls.AddRange([_monitor, _probe, _reconnect, _enable, _updateService, _disable, _restore, _automaticDns, _share]);
        root.Controls.Add(operationBar, 0, 1);
        var indicators = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 0, 0, 9) };
        var progressSpace = new Panel { Width = 68, Height = 28, Margin = new Padding(0) };
        _readProgress.Location = new Point(4, 9);
        progressSpace.Controls.Add(_readProgress);
        indicators.Controls.AddRange([_monitoringIndicator, _dnsIndicator, progressSpace, _measurementIndicator]);
        root.Controls.Add(indicators, 0, 2);

        _tabs.TabPages.Add(BuildProfilesPage());
        _tabs.TabPages.Add(BuildTargetsPage());
        _tabs.TabPages.Add(BuildSettingsPage());
        _tabs.TabPages.Add(BuildLogPage());
        root.Controls.Add(_tabs, 0, 3);
        var disclaimer = new Label
        {
            Text = "Проверяются публичные HTTPS-адреса выбранных сервисов. Вход в аккаунт, генерация и WebSocket не проверяются. DNS/HTTPS в мс — сумма времени проверок, а не скорость загрузки. VPN, прокси и маршруты RtlineDnsPilot не меняет.",
            Dock = DockStyle.Fill, AutoSize = true, ForeColor = Color.DimGray, Margin = new Padding(3, 9, 3, 7)
        };
        root.SizeChanged += (_, _) => disclaimer.MaximumSize = new Size(Math.Max(100, root.ClientSize.Width - 40), 0);
        root.Controls.Add(disclaimer, 0, 4);
        var status = new StatusStrip { Dock = DockStyle.Fill, SizingGrip = false, BackColor = BackColor, Margin = new Padding(0) };
        var exitLink = new ToolStripStatusLabel("Выход") { IsLink = true, Margin = new Padding(15, 3, 3, 2) };
        exitLink.Click += async (_, _) => await ExitAsync();
        status.Items.AddRange([_source, _activity, exitLink]);
        root.Controls.Add(status, 0, 5);
        Controls.Add(root);

        var menu = new ContextMenuStrip();
        menu.Items.Add("Показать RtlineDnsPilot", null, (_, _) => RestoreWindow());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Выход", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon { Icon = Icon, Text = "RtlineDnsPilot — наблюдение за каналами", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => RestoreWindow();
        _tips.SetToolTip(_enable, "После подтверждения: установка/запуск фоновой службы и смена DNS выбранного адаптера на 127.0.0.1 и ::1.");
        _tips.SetToolTip(_restore, "Отключить управление и восстановить исходный способ DNS и список адресов из журнала службы.");
        _tips.SetToolTip(_monitor, "Приостановить только проверки и автоматический выбор канала. Управление DNS не отключается: служба продолжает пересылать DNS через текущий канал. Для отключения нажмите «Отключить управление».");
        _tips.SetToolTip(_disable, "Отключить управление и вернуть исходные DNS. Если служба не отвечает, появится отдельное подтверждение запуска помощника восстановления и окно разрешения Windows.");
        _tips.SetToolTip(_reconnect, "Повторить запрос состояния службы сейчас. Этот запрос не меняет DNS и не запрашивает права администратора.");
        _tips.SetToolTip(_automaticDns, "После отдельного подтверждения и UAC: отключить управление RtlineDnsPilot и получать DNS выбранного адаптера автоматически от сети. Это заменит ручные DNS.");
        _tips.SetToolTip(_prefer, "Предпочтительный канал используется, пока доступен. При устойчивом отказе сохраняется аварийное переключение.");
        _tips.SetToolTip(_auto, "Выбрать приоритет автовыбора и снять ручную фиксацию или предпочтение.\r\nСамый быстрый — минимальная средняя задержка DNS + HTTPS среди каналов, у которых доступны все выбранные объекты. Это время отклика, не Мбит/с.\r\nСамый стабильный — сначала доля успешных проверок, при равенстве — задержка.\r\nДля обычной оптимизации нужны 5 успешных проверок подряд и 5 минут на текущем канале. При сравнении задержек нужен выигрыш не менее 20%. Аварийное переключение сохраняется.");
        _tips.SetToolTip(_grid, "Правая кнопка: зафиксировать канал, вернуться к авто или удалить. Синий — активный канал, зелёный/красный — свежая доступность остальных. Ошибки активного канала остаются красными. Статистика учитывает текущий набор сервисов.");
        _tips.SetToolTip(_monitorChatGpt, "Анонимная проверка публичного HTTPS ChatGPT; работоспособность авторизованного Codex не подтверждается.");
        _tips.SetToolTip(_monitorGemini, "Анонимная проверка https://gemini.google.com/. Вход в аккаунт и генерация Gemini не проверяются.");
        _tips.SetToolTip(_monitorTailscale, "HTTPS-доступность https://login.tailscale.com/ через канал. Работа VPN-туннеля, устройств и RDP не проверяется; Tailscale не перенастраивается.");
        _tips.SetToolTip(_monitoringIndicator, "Состояние автоматического наблюдения. Индикатор справа показывает получение данных, а время — последнее записанное измерение; опрос состояния не запускает сетевые проверки.");
        _tips.SetToolTip(_dnsIndicator, "Подтверждённое состояние управления системным DNS. Пауза проверок не отключает DNS-посредник.");
        _tips.SetToolTip(_share, "Одним нажатием отправить новые рабочие публичные DNS владельцу RtlineDnsPilot. Только каналы вне базового набора с успешной проверкой ChatGPT/Gemini за последние 24 часа. Имена каналов, личные сетевые данные, аккаунты и журналы не передаются. Получатель задан приложением; адрес и код вводить не нужно.");

        // Polling continues in the tray so incident notifications keep working.
        _timer.Tick += async (_, _) => await PollAsync(() => _backend.RefreshAsync());
        _history.QueryChanged += async (_, _) =>
        {
            if (_smoke) { _history.SetSyntheticReportFromStatus(_backend.Status); return; }
            await RefreshHistoryAsync(true);
        };
        _tabs.SelectedIndexChanged += async (_, _) =>
        {
            if (_tabs.SelectedIndex == 3) await RefreshHistoryAsync(true);
            else CancelHistoryRead();
        };
        Shown += async (_, _) =>
        {
            var available = Screen.FromControl(this).WorkingArea;
            if (Width > available.Width || Height > available.Height)
                Size = new Size(Math.Min(Width, available.Width), Math.Min(Height, available.Height));
            if (_smoke) return;
            if (_backend.ConfigurationWarning is { } warning) AddUiEvent(warning, "warning");
            await RunAsync(() => _backend.RefreshAsync(), "Подключение", false);
            _timer.Start();
        };
        _monitor.Click += async (_, _) => await RunAsync(() => _backend.SetMonitoringAsync(!_backend.Status.Monitoring), _backend.Status.Monitoring ? "Пауза проверок" : "Продолжение проверок");
        _probe.Click += async (_, _) => await RunAsync(() => _backend.ProbeAsync(), _backend.DnsStateUnknown ? "Разовая локальная проверка" : "Проверка каналов");
        _reconnect.Click += async (_, _) => await RunAsync(() => _backend.ReconnectAsync(), "Проверка связи со службой");
        _enable.Click += async (_, _) => await EnableAsync();
        _updateService.Click += async (_, _) =>
        {
            var repair = _backend.DnsStateUnknown;
            var operation = repair ? "Исправление службы" : "Обновление службы";
            var message = (repair ? "Исправить установленную службу RtlineDnsPilot?" : "Обновить установленную службу RtlineDnsPilot?")
                + "\r\n\r\nПомощник с разрешением Windows остановит службу, восстановит исходные DNS из журнала и установит файлы текущей версии. Каналы и история сохранятся. Управление DNS останется выключенным; после восстановления связи его можно снова включить кнопкой «Включить управление DNS». При конфликте восстановления операция остановится с ошибкой.\r\n\r\n" + AdministratorHelp;
            if (MessageBox.Show(this, message, operation, MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
            await RunAsync(() => _backend.UpdateServiceAsync(), operation);
        };
        _disable.Click += async (_, _) => await RestoreDnsAsync();
        _restore.Click += async (_, _) => await RestoreDnsAsync();
        _automaticDns.Click += async (_, _) => await ResetAutomaticDnsAsync();
        _add.Click += async (_, _) => await EditProfileAsync(true);
        _edit.Click += async (_, _) => await EditProfileAsync(false);
        _delete.Click += async (_, _) => await DeleteProfileAsync();
        _import.Click += async (_, _) => await ImportAsync();
        _export.Click += (_, _) => Export();
        _share.Click += async (_, _) => await ShareDnsAsync();
        _prefer.Click += async (_, _) => { if (SelectedProfile() is { Enabled: true } profile) await RunAsync(() => _backend.SelectAsync(profile.Id), "Предпочтительный канал"); };
        _autoModeMenu.Items.AddRange([_autoStable, _autoFast]);
        _autoModeMenu.Opening += (_, e) => { UpdateAutomaticMenu(); e.Cancel = _busy || _backend.DnsStateUnknown; };
        _auto.Click += (_, _) => { if (!_busy && !_backend.DnsStateUnknown) _autoModeMenu.Show(_auto, new Point(0, _auto.Height)); };
        _autoStable.Click += async (_, _) => await ApplyAutomaticModeAsync(AutoSelectionMode.Stable);
        _autoFast.Click += async (_, _) => await ApplyAutomaticModeAsync(AutoSelectionMode.Fast);
        _apply.Click += async (_, _) => await ApplySettingsAsync();
        _applyTargets.Click += async (_, _) => await ApplySettingsAsync();
        _grid.SelectionChanged += (_, _) => UpdateButtons();
        _grid.CellDoubleClick += async (_, e) => { if (e.RowIndex >= 0) await EditProfileAsync(false); };
        _grid.CellMouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Right && e.RowIndex >= 0)
                OpenChannelMenu(e.RowIndex, _grid.PointToClient(Cursor.Position));
        };
        _grid.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Apps && !(e.Shift && e.KeyCode == Keys.F10)) return;
            if (_grid.SelectedRows.Count == 0) return;
            var row = _grid.SelectedRows[0];
            var rectangle = _grid.GetRowDisplayRectangle(row.Index, false);
            OpenChannelMenu(row.Index, new Point(50, rectangle.Bottom));
            e.Handled = true;
        };
        _contextAutoMode.DropDownItems.AddRange([_contextAutoStable, _contextAutoFast]);
        _channelMenu.Items.AddRange([_fixPermanent, _fixFive, _fixFifteen, _fixHour, new ToolStripSeparator(), _contextAuto, _contextAutoMode, new ToolStripSeparator(), _contextDelete]);
        _fixPermanent.Click += async (_, _) => await FixContextChannelAsync(null);
        _fixFive.Click += async (_, _) => await FixContextChannelAsync(5);
        _fixFifteen.Click += async (_, _) => await FixContextChannelAsync(15);
        _fixHour.Click += async (_, _) => await FixContextChannelAsync(60);
        _contextAuto.Click += async (_, _) => await RunAsync(() => _backend.SelectAsync(null), "Автовыбор");
        _contextAutoStable.Click += async (_, _) => await ApplyAutomaticModeAsync(AutoSelectionMode.Stable);
        _contextAutoFast.Click += async (_, _) => await ApplyAutomaticModeAsync(AutoSelectionMode.Fast);
        _contextDelete.Click += async (_, _) => await DeleteProfileAsync(_contextProfileId);
        FormClosing += OnFormClosing;
        UpdateView();
    }

    private static Button MakeButton(string text)
    {
        var button = new Button { Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(8, 4, 8, 4), Margin = new Padding(0, 0, 7, 6), FlatStyle = FlatStyle.Flat, UseVisualStyleBackColor = false, BackColor = Color.White, ForeColor = Color.FromArgb(30, 49, 67) };
        button.FlatAppearance.BorderColor = Color.FromArgb(164, 180, 195);
        button.FlatAppearance.MouseOverBackColor = Color.FromArgb(234, 242, 252);
        button.FlatAppearance.MouseDownBackColor = Color.FromArgb(201, 221, 245);
        return button;
    }

    private static Label MakeStateIndicator(int width) => new() { Width = width, Height = 28, Padding = new Padding(8, 0, 8, 0), TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true, Font = new Font("Segoe UI", 9F, FontStyle.Bold), Margin = new Padding(0, 0, 7, 0) };

    private TabPage BuildProfilesPage()
    {
        var page = new TabPage("Каналы") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        actions.Controls.AddRange([_add, _edit, _delete, _import, _export, _prefer, _auto]);
        layout.Controls.Add(actions, 0, 0);
        layout.Controls.Add(_selection, 0, 1);
        _countryPanel.Controls.Add(_countries);
        layout.Controls.Add(_countryPanel, 0, 2);
        _countryPanel.SizeChanged += (_, _) => _countries.MaximumSize = new Size(Math.Max(100, _countryPanel.ClientSize.Width), 0);
        layout.SizeChanged += (_, _) =>
        {
            actions.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width), 0);
            _selection.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 8), 0);
            _countryPanel.MaximumSize = new Size(0, Math.Clamp((ClientSize.Height - 680) / 2 + 45, 45, 112));
        };
        _grid.Dock = DockStyle.Fill;
        _grid.BackgroundColor = Color.White;
        _grid.BorderStyle = BorderStyle.FixedSingle;
        _grid.ReadOnly = true;
        _grid.MultiSelect = false;
        _grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _grid.AllowUserToAddRows = false;
        _grid.AllowUserToDeleteRows = false;
        _grid.AllowUserToResizeRows = false;
        _grid.RowHeadersVisible = false;
        _grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _grid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _grid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _grid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(238, 242, 247), ForeColor = Color.FromArgb(35, 50, 66), Font = new Font(Font, FontStyle.Bold), Padding = new Padding(3, 8, 3, 8), WrapMode = DataGridViewTriState.True };
        _grid.EnableHeadersVisualStyles = false;
        _grid.DefaultCellStyle = new DataGridViewCellStyle { Padding = new Padding(3, 7, 3, 7), SelectionBackColor = Color.FromArgb(212, 230, 247), SelectionForeColor = Color.FromArgb(20, 40, 60), ForeColor = Color.FromArgb(35, 45, 55), BackColor = Color.White };
        _grid.AlternatingRowsDefaultCellStyle.BackColor = Color.FromArgb(249, 251, 253);
        _grid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "Вкл.", FillWeight = 38, MinimumWidth = 50, SortMode = DataGridViewColumnSortMode.NotSortable });
        AddColumn("name", "Канал", 155, 120);
        AddColumn("protocol", "Протокол", 65, 82);
        AddColumn("result", "Общий результат", 125, 150);
        AddColumn("dns", "DNS, Σ мс", 67, 70);
        AddColumn("https", "HTTPS, Σ мс", 78, 80);
        AddColumn("success", "Успешно, %", 85, 85);
        AddColumn("p95", "p95, мс", 67, 68);
        AddColumn("count", "Измерений", 78, 100);
        layout.Controls.Add(_grid, 0, 3);
        layout.Controls.Add(new Label { Text = "● Синий — активный канал    ◆ Ручная фиксация    ★ Предпочтение    Правая кнопка — управление каналом. Статистика и Σ мс — по выбранным сервисам.", AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = new Padding(3, 9, 3, 0) }, 0, 4);
        page.Controls.Add(layout);
        return page;
    }

    private void AddColumn(string name, string title, float weight, int minWidth)
    {
        _grid.Columns.Add(new DataGridViewTextBoxColumn { Name = name, HeaderText = title, FillWeight = weight, MinimumWidth = minWidth, SortMode = DataGridViewColumnSortMode.NotSortable });
    }

    private TabPage BuildTargetsPage()
    {
        var page = new TabPage("Что проверять") { Padding = new Padding(16), UseVisualStyleBackColor = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var builtIns = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 4 };
        for (var row = 0; row < 4; row++) builtIns.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        builtIns.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        builtIns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var targets = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        targets.Controls.AddRange([_monitorChatGpt, _monitorGemini, _monitorTailscale]);
        AddSetting(builtIns, "Встроенные объекты", targets, 0);
        AddSetting(builtIns, "HTTPS ChatGPT / Codex", _url, 1);
        AddSetting(builtIns, "HTTPS Gemini", new Label { Text = MonitoringTargets.GeminiUrl, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), ForeColor = Color.DimGray }, 2);
        AddSetting(builtIns, "HTTPS Tailscale", new Label { Text = MonitoringTargets.TailscaleUrl, AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(0, 4, 0, 0), ForeColor = Color.DimGray }, 3);
        layout.Controls.Add(builtIns, 0, 0);
        var actions = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0, 12, 0, 4) };
        actions.Controls.AddRange([_addTarget, _editTarget, _deleteTarget]);
        layout.Controls.Add(actions, 0, 1);
        _targetGrid.Dock = DockStyle.Fill;
        _targetGrid.BackgroundColor = Color.White;
        _targetGrid.ReadOnly = true;
        _targetGrid.MultiSelect = false;
        _targetGrid.SelectionMode = DataGridViewSelectionMode.FullRowSelect;
        _targetGrid.AllowUserToAddRows = false;
        _targetGrid.AllowUserToDeleteRows = false;
        _targetGrid.AllowUserToResizeRows = false;
        _targetGrid.RowHeadersVisible = false;
        _targetGrid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.Fill;
        _targetGrid.AutoSizeRowsMode = DataGridViewAutoSizeRowsMode.AllCells;
        _targetGrid.ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.AutoSize;
        _targetGrid.EnableHeadersVisualStyles = false;
        _targetGrid.ColumnHeadersDefaultCellStyle = new DataGridViewCellStyle { BackColor = Color.FromArgb(238, 242, 247), ForeColor = Color.FromArgb(35, 50, 66), Font = new Font(Font, FontStyle.Bold), Padding = new Padding(3, 8, 3, 8), WrapMode = DataGridViewTriState.True };
        _targetGrid.DefaultCellStyle.Padding = new Padding(4, 8, 4, 8);
        _targetGrid.Columns.Add(new DataGridViewCheckBoxColumn { Name = "enabled", HeaderText = "Вкл.", Width = 65, FillWeight = 35, MinimumWidth = 55, SortMode = DataGridViewColumnSortMode.NotSortable });
        _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "name", HeaderText = "Свой объект", FillWeight = 160, MinimumWidth = 150, SortMode = DataGridViewColumnSortMode.NotSortable });
        _targetGrid.Columns.Add(new DataGridViewTextBoxColumn { Name = "url", HeaderText = "Публичный HTTPS-адрес", FillWeight = 360, MinimumWidth = 260, SortMode = DataGridViewColumnSortMode.NotSortable });
        _targetGrid.SelectionChanged += (_, _) => UpdateButtons();
        _targetGrid.CellDoubleClick += (_, e) => { if (e.RowIndex >= 0 && e.ColumnIndex != 0) EditTarget(false); };
        _targetGrid.CellClick += (_, e) =>
        {
            if (_busy || e.RowIndex < 0 || e.ColumnIndex != 0) return;
            var target = _additionalTargets.FirstOrDefault(t => t.Id == _targetGrid.Rows[e.RowIndex].Tag as string);
            if (target is null) return;
            target.Enabled = !target.Enabled;
            RefreshTargetsGrid();
            SettingsChanged();
        };
        _addTarget.Click += (_, _) => EditTarget(true);
        _editTarget.Click += (_, _) => EditTarget(false);
        _deleteTarget.Click += (_, _) => DeleteTarget();
        layout.Controls.Add(_targetGrid, 0, 2);
        _targetsHint.Margin = new Padding(0, 8, 0, 8);
        layout.Controls.Add(_targetsHint, 0, 3);
        var footer = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 1 };
        footer.Controls.Add(_applyTargets, 0, 0);
        var explanation = new Label { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray,
            Text = "Выберите хотя бы один объект. Для автовыбора канал должен пройти проверки всех включённых объектов. До 6 своих объектов: название и публичный HTTPS URL без паролей и параметров.\r\nПроверяются только DNS и публичный HTTPS: вход в аккаунт, генерация и WebSocket не проверяются. Не добавляйте адреса с личными данными. Больше включённых объектов — больше проверок; одновременно выполняются не более четырёх.",
            Margin = new Padding(0, 8, 0, 0) };
        footer.Controls.Add(explanation, 0, 1);
        footer.SizeChanged += (_, _) => explanation.MaximumSize = new Size(Math.Max(100, footer.ClientSize.Width - 8), 0);
        layout.SizeChanged += (_, _) => _targetsHint.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 8), 0);
        layout.Controls.Add(footer, 0, 4);
        page.Controls.Add(layout);
        _url.TextChanged += (_, _) => SettingsChanged();
        _monitorChatGpt.CheckedChanged += (_, _) => { _url.Enabled = _monitorChatGpt.Checked; SettingsChanged(); };
        _monitorGemini.CheckedChanged += (_, _) => SettingsChanged();
        _monitorTailscale.CheckedChanged += (_, _) => SettingsChanged();
        return page;
    }

    private TabPage BuildSettingsPage()
    {
        var page = new TabPage("Проверка и сеть") { Padding = new Padding(16), UseVisualStyleBackColor = true, AutoScroll = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 7 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 176));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        AddSetting(layout, "Интервал, секунд", _interval, 0);
        AddSetting(layout, "Тайм-аут, секунд", _timeout, 1);
        var adapterRow = new TableLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, ColumnCount = 2, Margin = new Padding(0) };
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        adapterRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        adapterRow.Controls.Add(_adapter, 0, 0);
        var refresh = MakeButton("Обновить список");
        refresh.Margin = new Padding(8, 0, 0, 0);
        refresh.Click += (_, _) =>
        {
            try { UpdateAdapters(AdapterCatalog.Read(), true); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Сетевые адаптеры", MessageBoxButtons.OK, MessageBoxIcon.Error); }
        };
        adapterRow.Controls.Add(refresh, 1, 0);
        AddSetting(layout, "Сетевой адаптер", adapterRow, 2);
        _settingsHint.Margin = new Padding(0, 8, 0, 12);
        layout.Controls.Add(_settingsHint, 1, 3);
        layout.Controls.Add(_apply, 1, 4);
        var explanation = new Label
        {
            Text = "Объекты мониторинга выбираются во вкладке «Что проверять». Для автовыбора канал должен успешно проверять все включённые объекты. При смене набора нужны новые измерения; прежняя история сохраняется. Одновременно выполняются не более четырёх проверок.\r\n\r\n«Пауза проверок» останавливает проверки и автоматическое переключение. Если управление DNS включено, служба продолжает пересылать DNS через текущий канал. Для отключения управления используйте «Отключить управление».\r\n\r\nВ режиме наблюдения адаптер не изменяется. «Включить управление DNS» отдельно запрашивает подтверждение. «Восстановить DNS» возвращает сохранённые настройки; «DNS автоматически (DHCP)» получает DNS выбранного адаптера от сети и заменяет ручные адреса после отдельного подтверждения.\r\n\r\n" + AdministratorHelp + "\r\n\r\n401/403/429 и ошибки сервера — неуспешные проверки. Проверяется публичный HTTPS без аккаунтов: вход, генерация ChatGPT/Codex/Gemini и WebSocket не проверяются. Существующие соединения и DNS-кеш приложений могут задерживать переход.",
            AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.DimGray, Margin = new Padding(0, 18, 0, 0)
        };
        layout.Controls.Add(explanation, 0, 5);
        layout.SetColumnSpan(explanation, 2);
        layout.SizeChanged += (_, _) => { explanation.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 8), 0); _settingsHint.MaximumSize = new Size(Math.Max(100, layout.ClientSize.Width - 190), 0); };
        page.Controls.Add(layout);
        _interval.ValueChanged += (_, _) => SettingsChanged();
        _timeout.ValueChanged += (_, _) => SettingsChanged();
        _adapter.SelectedIndexChanged += (_, _) => SettingsChanged();
        return page;
    }

    private static void AddSetting(TableLayoutPanel panel, string text, Control control, int row)
    {
        panel.Controls.Add(new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left | AnchorStyles.Top, Margin = new Padding(0, 8, 10, 8) }, 0, row);
        control.Margin = new Padding(0, 5, 0, 6);
        panel.Controls.Add(control, 1, row);
    }

    private TabPage BuildLogPage()
    {
        var page = new TabPage("Журнал") { Padding = new Padding(10), UseVisualStyleBackColor = true };
        var views = new TabControl { Dock = DockStyle.Fill, Padding = new Point(12, 5) };
        var charts = new TabPage("Графики каналов") { Padding = new Padding(0) };
        charts.Controls.Add(_history);
        views.TabPages.Add(charts);
        var events = new TabPage("События") { Padding = new Padding(6), UseVisualStyleBackColor = true };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2 };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true };
        var copy = MakeButton("Скопировать журнал");
        copy.Click += (_, _) => { if (_logs.Text.Length > 0) { try { Clipboard.SetText(_logs.Text); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "Буфер обмена"); } } };
        bar.Controls.Add(copy);
        bar.Controls.Add(new Label { Text = "Состояния, ошибки и переключения. DNS-имена посещаемых сайтов не записываются.", AutoSize = true, Margin = new Padding(7, 9, 0, 0), ForeColor = Color.DimGray });
        layout.Controls.Add(bar, 0, 0);
        layout.Controls.Add(_logs, 0, 1);
        events.Controls.Add(layout);
        views.TabPages.Add(events);
        page.Controls.Add(views);
        return page;
    }

    // Status polling must not lock every command while an unresponsive service reaches its deadline.
    internal async Task PollAsync(Func<Task> refresh)
    {
        if (_busy || _refreshing || IsDisposed) return;
        _refreshing = true;
        UpdateReadIndicator();
        try { await refresh(); }
        catch (Exception ex) { AddUiEvent("Обновление состояния: " + ex.Message, "error"); }
        finally
        {
            _refreshing = false;
            if (!IsDisposed) UpdateReadIndicator();
            if (!IsDisposed && !_busy)
            {
                UpdateView();
                _activity.Text = _shareFeedback ?? "Обновлено " + DateTime.Now.ToString("HH:mm:ss");
            }
        }
    }

    private async Task RunAsync(Func<Task> operation, string activity, bool showError = true)
    {
        if (_busy || IsDisposed) return;
        CancelHistoryRead();
        _busy = true;
        _shareFeedback = null;
        _activity.ForeColor = SystemColors.ControlText;
        _activity.Text = activity + (_refreshing ? " · ожидание текущего запроса…" : "…");
        UpdateButtons();
        UpdateReadIndicator();
        try { await operation(); }
        catch (Exception ex)
        {
            AddUiEvent(activity + ": " + ex.Message, "error");
            if (showError && !_reallyClose) MessageBox.Show(this, ex.Message, activity, MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        finally
        {
            _busy = false;
            if (!IsDisposed)
            {
                UpdateView();
                _activity.Text = _shareFeedback ?? "Обновлено " + DateTime.Now.ToString("HH:mm:ss");
            }
        }
    }

    private bool HistoryIsVisible => Visible && WindowState != FormWindowState.Minimized && _tabs.SelectedIndex == 3;

    private string HistoryRequestKey()
    {
        var status = _backend.Status;
        var targetKey = MonitoringTargets.Key(status.Config);
        var source = _backend.DnsStateUnknown ? (_backend.ShowingLocalDiagnostics ? "local-diagnostic" : "unknown")
            : _backend.ServiceConnected ? "service" : "local";
        return source + "|" + JsonData.Encode(_history.Query) + "|" + targetKey + "|"
            + string.Join("|", status.Config.Profiles.Select(p => p.Id + ":" + ChartHistoryArchive.ContextKey(p, targetKey)).OrderBy(s => s, StringComparer.Ordinal));
    }

    private void CancelHistoryRead()
    {
        try { _historyCancellation?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    internal async Task RefreshHistoryAsync(bool force = false)
    {
        if (_smoke || IsDisposed || _reallyClose || _busy || !HistoryIsVisible) return;
        var key = HistoryRequestKey();
        if (_historyRefreshing)
        {
            if (force || key != _historyFetchKey) { _historyReloadRequested = true; CancelHistoryRead(); }
            return;
        }
        var now = DateTimeOffset.UtcNow;
        if (!force && key == _historyLastAttemptKey && now - _historyLastAttempt < TimeSpan.FromSeconds(20)) return;
        _historyLastAttemptKey = key;
        _historyLastAttempt = now;
        _historyFetchKey = key;
        _historyRefreshing = true;
        UpdateReadIndicator();
        using var cancellation = new CancellationTokenSource();
        _historyCancellation = cancellation;
        _history.SetLoading();
        try
        {
            var report = await _backend.ReadHistoryAsync(_history.Query, cancellation.Token);
            if (!IsDisposed && !_reallyClose && HistoryIsVisible && key == HistoryRequestKey()) _history.SetReport(report);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!IsDisposed && !_reallyClose && HistoryIsVisible && key == HistoryRequestKey()) _history.SetHistoryError(ex.Message);
        }
        finally
        {
            _historyCancellation = null;
            _historyRefreshing = false;
            if (!IsDisposed) UpdateReadIndicator();
            if (_historyReloadRequested)
            {
                _historyReloadRequested = false;
                if (!IsDisposed && !_reallyClose && !_busy) _ = RefreshHistoryAsync(true);
            }
        }
    }

    internal async Task ShareDnsAsync()
    {
        if (_busy || IsDisposed || _reallyClose) return;
        await RunAsync(async () =>
        {
            using var cancellation = new CancellationTokenSource();
            _shareCancellation = cancellation;
            try
            {
                var offer = await _backend.ReadShareCandidatesAsync(cancellation.Token);
                if (IsDisposed || _reallyClose) return;
                if (offer.Profiles.Count == 0)
                {
                    ShareFeedback("Новых рабочих DNS для отправки нет. Базовые каналы не отправляются.", false, "DNS: новых рабочих каналов нет.");
                    if (!string.IsNullOrWhiteSpace(offer.Warning)) AddUiEvent("Поделиться DNS: " + offer.Warning, "warning");
                    return;
                }
                _activity.Text = "Отправка DNS владельцу RtlineDnsPilot…";
                var result = await _shareClient.SendToOwnerAsync(offer.Profiles, cancellation.Token);
                if (IsDisposed || _reallyClose) return;
                var message = "DNS: добавлено " + result.Accepted + ", уже получено " + result.Duplicates + ".";
                if (result.Unconfirmed > 0) message += " Без подтверждения: " + result.Unconfirmed + "; повторно не отправлялись.";
                ShareFeedback(message, result.Unconfirmed > 0, "DNS: добавлено " + result.Accepted + ", уже получено " + result.Duplicates
                    + (result.Unconfirmed > 0 ? ", без подтверждения " + result.Unconfirmed : "") + ".");
            }
            catch (DnsShareException ex) { if (!IsDisposed && !_reallyClose) ShareFeedback(ex.Message, true,
                ex.MayHaveBeenSent ? "DNS: подтверждение не получено; повтор не отправлялся." : null); }
            catch (ArgumentException) { if (!IsDisposed && !_reallyClose) ShareFeedback("DNS не прошли проверку данных для отправки. Ничего не отправлено.", true); }
            catch (OperationCanceledException) { if (!IsDisposed && !_reallyClose) ShareFeedback("Отправка DNS отменена.", true); }
            catch (Exception ex) { if (!IsDisposed && !_reallyClose) ShareFeedback("Не удалось поделиться DNS: " + ex.Message, true); }
            finally { _shareCancellation = null; }
        }, "Подготовка новых рабочих DNS", false);
    }

    private void ShareFeedback(string message, bool error, string? summary = null)
    {
        var concise = summary ?? message;
        _shareFeedback = concise.Length <= 100 ? concise : concise[..97] + "…";
        _activity.Text = _shareFeedback;
        _activity.ForeColor = error ? Color.FromArgb(160, 43, 34) : Color.FromArgb(35, 100, 69);
        _activity.ToolTipText = message;
        AddUiEvent("Поделиться DNS: " + message, error ? "warning" : "info");
        UpdateLogs(_backend.Status);
    }

    private void UpdateView()
    {
        if (_updatingView || IsDisposed) return;
        _updatingView = true;
        _grid.SuspendLayout();
        try { UpdateViewCore(); }
        finally { _grid.ResumeLayout(); _updatingView = false; }
        UpdateButtons();
        UpdateStateIndicators();
        UpdateReadIndicator();
        if (HistoryIsVisible && !_smoke && !_busy) _ = RefreshHistoryAsync();
    }

    private void UpdateViewCore()
    {
        var status = _backend.Status;
        var active = _backend.DnsStateUnknown ? "не подтверждён" : status.Config.Profiles.FirstOrDefault(p => p.Id == status.ActiveProfileId)?.Name ?? "ещё не выбран";
        var adapter = status.Adapters.FirstOrDefault(a => a.Id.Equals(status.Config.AdapterId, StringComparison.OrdinalIgnoreCase))?.Name ?? "не выбран";
        if (_backend.DnsStateUnknown)
        {
            _headline.Text = "Служба не отвечает · состояние DNS не подтверждено";
            var lastContact = _backend.LastServiceContact is { } contact ? contact.ToLocalTime().ToString("dd.MM HH:mm:ss") : "ещё не было";
            var retry = _backend.NextServiceRetry is { } nextRetry
                ? "Новая автоматическая попытка через " + Math.Max(0, (int)Math.Ceiling((nextRetry - DateTimeOffset.UtcNow).TotalSeconds)) + " сек."
                : "Следующая попытка — при обновлении состояния.";
            _description.Text = "Последняя успешная связь: " + lastContact + ". " + retry
                + "\r\n«Проверить связь» — повторить запрос сейчас. «Исправить службу» — восстановить её с выключением управления DNS. «Отключить управление» — вернуть исходные DNS."
                + "\r\nДля последних двух действий приложение запросит разрешение Windows. " + AdministratorHelp;
        }
        else if (status.ServiceConnected && status.DnsRecoveryPending)
        {
            _headline.Text = "Восстановление DNS не завершено";
            _description.Text = "Текущее состояние DNS адаптера не подтверждено. Повторите «Восстановить DNS» после устранения указанной ошибки; новое включение управления заблокировано.";
        }
        else if (_backend.RecoveryConfirmed)
        {
            _headline.Text = "Восстановление завершено · управление DNS выключено";
            _description.Text = "Помощник завершил восстановление из журнала и остановил службу. Доступен локальный режим наблюдения.";
        }
        else if (status.SystemDnsManaged)
        {
            _headline.Text = status.Monitoring ? "DNS Windows управляется локальной службой" : "Проверки на паузе · DNS остаётся под управлением";
            _description.Text = $"Адаптер: {adapter}. Канал: {active}. " + (status.Monitoring
                ? "Служба работает в фоне, в том числе при закрытом окне."
                : "Служба продолжает пересылать DNS через этот канал. Проверки и автоматическое переключение остановлены; нажмите «Продолжить проверки» для возобновления.");
        }
        else
        {
            _headline.Text = status.ServiceConnected ? status.Monitoring ? "Служба проверяет каналы · управление DNS выключено" : "Проверки службы на паузе · управление DNS выключено"
                : status.Monitoring ? "Режим наблюдения · DNS не меняется" : "Проверки на паузе · DNS не меняется";
            _description.Text = status.ServiceConnected
                ? status.Monitoring ? $"Каналы проверяет фоновая служба. Выбранный для управления адаптер: {adapter}."
                    : "Нажмите «Продолжить проверки» для возобновления работы фоновой службы. Управление DNS выключено."
                : status.Monitoring ? "RtlineDnsPilot проверяет DNS и доступность HTTPS через каждый канал. Системные сетевые настройки в этом режиме не применяются."
                : "Автоматические проверки остановлены. Доступны запуск наблюдения и разовая проверка; системные сетевые настройки не применяются.";
        }
        var messages = new List<string>();
        if (status.AllUnavailable && status.Config.Profiles.Any(p => p.Enabled)) messages.Add("Все включённые каналы недоступны по последним проверкам. " + (status.Monitoring ? "Проверки продолжаются." : "Проверки сейчас на паузе."));
        if (!string.IsNullOrWhiteSpace(status.Error)) messages.Add(status.Error);
        if (_backend.DnsStateUnknown && !string.IsNullOrWhiteSpace(_backend.ConnectionError)) messages.Add(_backend.ConnectionError);
        if (!string.IsNullOrWhiteSpace(_backend.ConfigurationWarning)) messages.Add(_backend.ConfigurationWarning);
        _alert.Text = string.Join("\r\n", messages.Distinct());
        _alert.Visible = _alert.Text.Length > 0;
        var selectedId = SelectedProfile()?.Id;
        var firstVisible = _grid.FirstDisplayedScrollingRowIndex;
        var now = DateTimeOffset.UtcNow;
        _grid.Rows.Clear();
        SyncServiceColumns(status.Config);
        foreach (var profile in status.Config.Profiles)
        {
            var statistics = MonitoringTargets.View(status.Statistics.FirstOrDefault(s => s.ProfileId == profile.Id), status.Config);
            var last = statistics?.Last;
            var availability = Availability(profile.Enabled, last, status.Config, now);
            var label = availability switch
            {
                ChannelAvailability.Disabled => "Выключен",
                ChannelAvailability.Unknown => "Нет измерений",
                ChannelAvailability.Outdated => "Нет свежих данных",
                ChannelAvailability.Available => "Доступен" + (last!.HttpStatus is { } code ? " · HTTP " + code : ""),
                _ => "Недоступен" + (last!.HttpStatus is { } code ? " · HTTP " + code : string.IsNullOrWhiteSpace(last.Stage) ? "" : " · " + last.Stage)
            };
            if (last is not null) label += " · " + last.At.ToLocalTime().ToString("HH:mm:ss");
            var name = (!_backend.DnsStateUnknown && profile.Id == status.ActiveProfileId ? "● " : "") + (profile.Id == status.Config.ManualProfileId && MonitoringTargets.ManualIsActive(status.Config, now) ? "◆ " : "")
                + (profile.Id == status.Config.PinnedProfileId ? "★ " : "") + profile.Name;
            var index = _grid.Rows.Add();
            var row = _grid.Rows[index];
            row.Cells["enabled"].Value = profile.Enabled;
            row.Cells["name"].Value = name;
            row.Cells["protocol"].Value = profile.Mode == DnsMode.Doh ? "DoH" : "UDP/TCP";
            row.Cells["result"].Value = label;
            row.Cells["dns"].Value = last is null ? "—" : Milliseconds(last.DnsMs);
            row.Cells["https"].Value = last is null ? "—" : Milliseconds(last.HttpsMs);
            row.Cells["success"].Value = statistics is { Samples.Count: > 0 } ? statistics.SuccessPercent.ToString("0.0", CultureInfo.CurrentCulture) : "—";
            row.Cells["p95"].Value = statistics is not null && statistics.Samples.Any(s => s.Success) ? Milliseconds(statistics.P95Ms) : "—";
            row.Cells["count"].Value = statistics?.Samples.Count.ToString() ?? "0";
            row.Tag = profile.Id;
            row.Cells["name"].ToolTipText = profile.Name + (!string.IsNullOrEmpty(profile.Note) ? "\r\n" + profile.Note : "");
            row.Cells["result"].ToolTipText = last is null ? "Проверок ещё не было." : $"{last.At.ToLocalTime():dd.MM.yyyy HH:mm:ss}\r\n{(availability == ChannelAvailability.Outdated ? "Нет свежих данных. Запустите новую проверку.\r\n" : "")}{last.Stage}: {last.Detail}";
            row.Cells["count"].ToolTipText = statistics is null ? "Проверок ещё не было." : $"В окне статистики: {statistics.Samples.Count}. Всего проверок: {statistics.TotalChecks}, успешных: {statistics.TotalSuccesses}.";
            ApplyAvailabilityColors(row, availability, !_backend.DnsStateUnknown && profile.Id == status.ActiveProfileId);
            foreach (var target in MonitoringTargets.Selected(status.Config))
            {
                var result = MonitoringTargets.Service(last, target.Id);
                var cell = row.Cells[ServiceColumn(target.Id)];
                cell.Value = ServiceLabel(profile.Enabled, result, status.Config, now);
                cell.ToolTipText = target.Name + "\r\n" + target.Url + (result is null ? "\r\nНет измерений для текущего набора объектов." : $"\r\n{result.At.ToLocalTime():dd.MM.yyyy HH:mm:ss}\r\nDNS: {Milliseconds(result.DnsMs)} мс; HTTPS: {Milliseconds(result.HttpsMs)} мс\r\n{result.Stage}: {result.Detail}")
                    + "\r\n" + CountryTooltip(result, status.Config, now, profile.Enabled && availability != ChannelAvailability.Outdated);
                if (availability == ChannelAvailability.Unavailable && result is { Success: false })
                {
                    cell.Style.ForeColor = Color.FromArgb(160, 32, 27);
                    cell.Style.SelectionForeColor = Color.FromArgb(139, 26, 22);
                }
            }
            if (profile.Id == selectedId) row.Selected = true;
        }
        if (selectedId is not null && !_grid.Rows.Cast<DataGridViewRow>().Any(r => Equals(r.Tag, selectedId)) && _grid.Rows.Count > 0) _grid.Rows[0].Selected = true;
        // A taller recovery banner can temporarily leave no complete row at minimum window height.
        if (firstVisible >= 0 && firstVisible < _grid.Rows.Count && _grid.DisplayedRowCount(false) > 0) _grid.FirstDisplayedScrollingRowIndex = firstVisible;
        var preferred = status.Config.Profiles.FirstOrDefault(p => p.Id == status.Config.PinnedProfileId)?.Name;
        var fixedName = status.Config.Profiles.FirstOrDefault(p => p.Id == status.Config.ManualProfileId)?.Name;
        var selection = fixedName is not null && MonitoringTargets.ManualIsActive(status.Config, now)
            ? "ручная фиксация «" + fixedName + "» " + (status.Config.ManualUntil is { } until ? "до " + until.ToLocalTime().ToString("dd.MM HH:mm:ss") : "постоянно") + "; автоматическое переключение отключено"
            : preferred is null ? "автоматический — " + AutomaticModeName(status.Config.AutoSelection).ToLowerInvariant()
                + (status.Config.AutoSelection == AutoSelectionMode.Fast ? " (время отклика DNS + HTTPS)" : " (успешность проверок)")
                : "предпочтительный «" + preferred + "»; аварийное переключение включено";
        _selection.Text = _backend.DnsStateUnknown
            ? "Активный канал службы не подтверждён. " + (_backend.ShowingLocalDiagnostics
                ? "Показаны результаты разовой локальной проверки; системный DNS не менялся."
                : "Показаны последние данные службы; «Проверить локально» выполнит только разовую проверку.")
            : (_backend.PendingServiceConfiguration ? "Настройки выбора ожидают применения: " : "Выбор: ") + selection + ". Активный канал: " + active + "."
                + (string.IsNullOrWhiteSpace(status.SelectionReason) ? "" : "\r\nПричина: " + status.SelectionReason);
        _tips.SetToolTip(_selection, _backend.DnsStateUnknown ? "Активный канал и состояние DNS подтвердятся после восстановления связи со службой."
            : string.IsNullOrWhiteSpace(status.SelectionReason) ? "Причина выбора появится после нового решения монитора." : status.SelectionReason);
        UpdateCountries(status, now);
        UpdateSettings(status);
        _history.UpdateHistory(status);
        UpdateLogs(status);
        UpdateNotifications(status);
        _source.Text = status.ServiceConnected ? "Источник: фоновая служба Windows" : _backend.DnsStateUnknown
            ? _backend.ShowingLocalDiagnostics ? "Локальная разовая проверка · DNS не подтверждён"
                : _backend.LastServiceContact is null ? "Служба ещё не ответила · DNS не подтверждён" : "Последние данные службы · DNS не подтверждён"
            : "Источник: локальный монитор · без прав администратора";
        _tray.Text = status.DnsRecoveryPending ? "RtlineDnsPilot — восстановление DNS не завершено" : status.SystemDnsManaged ? "RtlineDnsPilot — управление DNS включено" : _backend.DnsStateUnknown ? "RtlineDnsPilot — нет связи со службой" : "RtlineDnsPilot — наблюдение за каналами";
    }

    private static string Milliseconds(double value) => double.IsFinite(value) && value > 0 ? value.ToString("0", CultureInfo.CurrentCulture) : "—";

    private static string ServiceColumn(string id) => "service_" + id;

    private void UpdateCountries(AppStatus status, DateTimeOffset now)
    {
        var profile = _backend.DnsStateUnknown ? null : status.Config.Profiles.FirstOrDefault(p => p.Id == status.ActiveProfileId && p.Enabled);
        var last = profile is null ? null : MonitoringTargets.View(status.Statistics.FirstOrDefault(s => s.ProfileId == profile.Id), status.Config)?.Last;
        var fresh = profile is not null && Availability(true, last, status.Config, now) is ChannelAvailability.Available or ChannelAvailability.Unavailable;
        var targets = MonitoringTargets.Selected(status.Config);
        var heading = _backend.DnsStateUnknown ? "Страны для объектов: активный канал службы не подтверждён."
            : profile is null ? "Страны для объектов: активный включённый канал ещё не выбран."
            : "Страны, которые сайты сообщили при проверке через «" + profile.Name.ReplaceLineEndings(" ") + "»:";
        var values = targets.Select(target => target.Name.ReplaceLineEndings(" ") + ": "
            + CountryLabel(MonitoringTargets.Service(last, target.Id), status.Config, now, fresh));
        _countries.Text = heading + "\r\n" + string.Join("   ·   ", values)
            + "\r\nАнонимные HTTPS-проверки; это не регион аккаунта." + (status.SystemDnsManaged || _backend.DnsStateUnknown ? "" : " Управление DNS выключено.");
        _tips.SetToolTip(_countries, string.Join("\r\n\r\n", targets.Select(target => target.Name + "\r\n"
            + CountryTooltip(MonitoringTargets.Service(last, target.Id), status.Config, now, fresh)))
            + "\r\n\r\nСтрана показывается только по явному ответу самого сайта. Если сайт не сообщает её, выводится «Не определена». Старые измерения не используются.");
    }

    private static bool HasFreshCountry(ServiceProbeResult? result, AppConfig config, DateTimeOffset now, bool allowed)
    {
        if (!allowed || result is null || result.CountrySource is not (ConnectionCountrySource.SiteTrace or ConnectionCountrySource.ClientCountryHeader)) return false;
        var age = now - result.At;
        return age >= TimeSpan.Zero && age <= TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config));
    }

    private static string CountryLabel(ServiceProbeResult? result, AppConfig config, DateTimeOffset now, bool allowed) =>
        ConnectionCountry.Display(HasFreshCountry(result, config, now, allowed) ? result!.CountryCode : null);

    private static string CountryTooltip(ServiceProbeResult? result, AppConfig config, DateTimeOffset now, bool allowed)
    {
        var text = "Страна анонимной проверки: " + CountryLabel(result, config, now, allowed);
        if (HasFreshCountry(result, config, now, allowed) && result!.CountryCode is not null)
            text += "\r\nИсточник: " + ConnectionCountry.SourceLabel(result.CountrySource) + "; " + result.At.ToLocalTime().ToString("dd.MM HH:mm:ss");
        return text;
    }

    private void SyncServiceColumns(AppConfig config)
    {
        var targets = MonitoringTargets.Selected(config);
        var key = JsonData.Encode(targets.Select(t => new { t.Id, t.Name }));
        if (_serviceColumnsKey == key) return;
        foreach (var column in _grid.Columns.Cast<DataGridViewColumn>().Where(c => c.Name.StartsWith("service_", StringComparison.Ordinal)).ToArray())
            _grid.Columns.Remove(column);
        for (var index = 0; index < targets.Length; index++)
            _grid.Columns.Insert(4 + index, new DataGridViewTextBoxColumn { Name = ServiceColumn(targets[index].Id), HeaderText = targets[index].Name, FillWeight = 135, MinimumWidth = 165, SortMode = DataGridViewColumnSortMode.NotSortable });
        _serviceColumnsKey = key;
    }

    private static string ServiceLabel(bool enabled, ServiceProbeResult? result, AppConfig config, DateTimeOffset now)
    {
        if (!enabled) return "Выключен";
        if (result is null) return "Нет измерений";
        var age = now - result.At;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config))) return "Нет свежих данных";
        return MonitoringTargets.ServiceSummary(result) + " · " + result.At.ToLocalTime().ToString("HH:mm:ss");
    }

    private enum ChannelAvailability { Disabled, Unknown, Outdated, Available, Unavailable }

    private static ChannelAvailability Availability(bool enabled, ProbeResult? last, AppConfig config, DateTimeOffset now)
    {
        if (!enabled) return ChannelAvailability.Disabled;
        if (last is null) return ChannelAvailability.Unknown;
        var age = now - last.At;
        if (age < TimeSpan.Zero || age > TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(config))) return ChannelAvailability.Outdated;
        return last.Success ? ChannelAvailability.Available : ChannelAvailability.Unavailable;
    }

    private static void ApplyAvailabilityColors(DataGridViewRow row, ChannelAvailability availability, bool active)
    {
        var (background, selectedBackground, foreground, result, selectedResult) = availability switch
        {
            ChannelAvailability.Available => (Color.FromArgb(235, 248, 240), Color.FromArgb(195, 232, 209), Color.FromArgb(35, 63, 49), Color.FromArgb(20, 115, 56), Color.FromArgb(14, 86, 40)),
            ChannelAvailability.Unavailable => (Color.FromArgb(254, 239, 237), Color.FromArgb(246, 205, 200), Color.FromArgb(89, 43, 43), Color.FromArgb(169, 40, 31), Color.FromArgb(137, 30, 24)),
            ChannelAvailability.Disabled => (Color.FromArgb(242, 244, 246), Color.FromArgb(215, 221, 226), Color.FromArgb(120, 130, 140), Color.FromArgb(120, 130, 140), Color.FromArgb(85, 98, 110)),
            _ => (Color.FromArgb(247, 249, 251), Color.FromArgb(224, 230, 236), Color.FromArgb(93, 108, 121), Color.FromArgb(93, 108, 121), Color.FromArgb(65, 81, 96))
        };
        row.DefaultCellStyle.BackColor = active ? Color.FromArgb(220, 235, 253) : background;
        row.DefaultCellStyle.SelectionBackColor = active ? Color.FromArgb(178, 210, 246) : selectedBackground;
        row.DefaultCellStyle.ForeColor = active ? Color.FromArgb(23, 53, 89) : foreground;
        row.DefaultCellStyle.SelectionForeColor = active ? Color.FromArgb(17, 44, 76) : foreground;
        row.Cells["result"].Style.ForeColor = result;
        row.Cells["result"].Style.SelectionForeColor = selectedResult;
    }

    private ChannelProfile? SelectedProfile() => _grid.SelectedRows.Count == 0 ? null : _backend.Status.Config.Profiles.FirstOrDefault(p => p.Id == _grid.SelectedRows[0].Tag as string);

    // Capture the clicked identity before showing the menu; a later poll cannot retarget its action.
    private void OpenChannelMenu(int rowIndex, Point location)
    {
        if (PrepareChannelMenu(rowIndex)) _channelMenu.Show(_grid, location);
    }

    internal bool PrepareChannelMenu(int rowIndex)
    {
        if (_busy || rowIndex < 0 || rowIndex >= _grid.Rows.Count) return false;
        var row = _grid.Rows[rowIndex];
        _grid.ClearSelection();
        row.Selected = true;
        _grid.CurrentCell = row.Cells["name"];
        _contextProfileId = row.Tag as string;
        var profile = _backend.Status.Config.Profiles.FirstOrDefault(p => p.Id == _contextProfileId);
        if (profile is null) return false;
        foreach (var item in new[] { _fixPermanent, _fixFive, _fixFifteen, _fixHour }) item.Enabled = profile.Enabled;
        _contextAuto.Enabled = _backend.Status.Config.PinnedProfileId is not null || _backend.Status.Config.ManualProfileId is not null;
        _contextDelete.Enabled = _backend.Status.Config.Profiles.Count > 1;
        return true;
    }

    private async Task FixContextChannelAsync(int? minutes)
    {
        var id = _contextProfileId;
        if (id is null || !_backend.Status.Config.Profiles.Any(p => p.Id == id && p.Enabled)) return;
        await RunAsync(() => _backend.FixChannelAsync(id, minutes), minutes is null ? "Постоянная фиксация канала" : "Фиксация канала на " + minutes + " минут");
    }

    private static string AutomaticModeName(AutoSelectionMode mode) => mode == AutoSelectionMode.Fast ? "Самый быстрый" : "Самый стабильный";
    private static string AutomaticModeShort(AutoSelectionMode mode) => mode == AutoSelectionMode.Fast ? "скорость" : "стабильность";
    private Task ApplyAutomaticModeAsync(AutoSelectionMode mode) => RunAsync(() => _backend.SelectAutomaticAsync(mode), "Автовыбор: " + AutomaticModeName(mode).ToLowerInvariant());

    private void UpdateAutomaticMenu()
    {
        var mode = _backend.Status.Config.AutoSelection;
        var enabled = !_busy && !_backend.DnsStateUnknown;
        foreach (var item in new[] { _autoStable, _contextAutoStable }) { item.Checked = mode == AutoSelectionMode.Stable; item.Enabled = enabled; }
        foreach (var item in new[] { _autoFast, _contextAutoFast }) { item.Checked = mode == AutoSelectionMode.Fast; item.Enabled = enabled; }
        _contextAutoMode.Enabled = enabled;
        _autoStable.ToolTipText = _contextAutoStable.ToolTipText = "Сначала доля успешных проверок всех выбранных объектов; при равенстве — время отклика.";
        _autoFast.ToolTipText = _contextAutoFast.ToolTipText = "Минимальная средняя задержка DNS + HTTPS среди доступных каналов. Это время отклика, не скорость загрузки.";
        _contextAuto.ToolTipText = "Снять фиксацию и предпочтение, сохранив приоритет: " + AutomaticModeName(mode).ToLowerInvariant() + ".";
    }

    private void UpdateButtons()
    {
        if (_updatingView) return;
        var status = _backend.Status;
        foreach (var button in new[] { _probe, _import, _export }) button.Enabled = !_busy;
        _monitor.Enabled = !_busy && !_backend.DnsStateUnknown;
        _monitor.Text = _backend.DnsStateUnknown ? "Проверки службы неизвестны" : status.Monitoring ? "Пауза проверок" : "Продолжить проверки";
        _probe.Text = _backend.DnsStateUnknown ? "Проверить локально" : "Проверить сейчас";
        _reconnect.Visible = _backend.ServiceInstalled && !_backend.ServiceConnected;
        _reconnect.Enabled = !_busy;
        _enable.Enabled = !_busy && !status.SystemDnsManaged && !status.DnsRecoveryPending && !_backend.DnsStateUnknown;
        _enable.Text = status.SystemDnsManaged && !_backend.DnsStateUnknown ? "✓ Управление DNS включено" : "Включить управление DNS";
        _updateService.Visible = _backend.ServiceUpdateAvailable;
        _updateService.Enabled = !_busy;
        _updateService.Text = _backend.DnsStateUnknown ? "Исправить службу" : "Обновить службу";
        _tips.SetToolTip(_updateService, _backend.DnsStateUnknown
            ? "Помощник с разрешением Windows восстановит или обновит файлы службы и выключит управление DNS. Каналы и история сохранятся; управление затем можно включить отдельно."
            : "Обновить файлы службы с разрешением Windows. DNS сначала восстановятся из журнала; каналы и история сохранятся, управление останется выключенным.");
        _disable.Enabled = !_busy && (status.ServiceConnected || _backend.DnsStateUnknown);
        _restore.Enabled = _disable.Enabled;
        _automaticDns.Enabled = !_busy && _adapter.SelectedItem is AdapterInfo;
        _edit.Enabled = !_busy && SelectedProfile() is not null;
        _delete.Enabled = !_busy && SelectedProfile() is not null && status.Config.Profiles.Count > 1;
        _prefer.Enabled = !_busy && SelectedProfile() is { Enabled: true };
        _adapter.Enabled = !_busy && !status.SystemDnsManaged && !status.DnsRecoveryPending;
        _apply.Enabled = !_busy && (_settingsDirty || _backend.PendingServiceConfiguration);
        _applyTargets.Enabled = _apply.Enabled;
        _auto.Enabled = !_busy && !_backend.DnsStateUnknown;
        _add.Enabled = !_busy && status.Config.Profiles.Count < 32;
        _addTarget.Enabled = !_busy && _additionalTargets.Count < 6;
        _editTarget.Enabled = !_busy && SelectedTarget() is not null;
        _deleteTarget.Enabled = _editTarget.Enabled;
        _monitorChatGpt.Enabled = !_busy;
        _monitorGemini.Enabled = !_busy;
        _monitorTailscale.Enabled = !_busy;
        _share.Enabled = !_busy && (!_backend.DnsStateUnknown || _backend.ShowingLocalDiagnostics);
        _url.Enabled = !_busy && _monitorChatGpt.Checked;
        _targetGrid.Enabled = !_busy;
        var automatic = !_backend.DnsStateUnknown && !_backend.PendingServiceConfiguration && status.Config.PinnedProfileId is null && !MonitoringTargets.ManualIsActive(status.Config, DateTimeOffset.UtcNow);
        _auto.Text = (automatic ? "✓ " : "") + "Авто: " + AutomaticModeShort(status.Config.AutoSelection) + " ▾";
        UpdateAutomaticMenu();
        ApplyButtonState(_monitor, !_backend.DnsStateUnknown && !status.Monitoring, Color.FromArgb(255, 239, 202), Color.FromArgb(174, 124, 26));
        ApplyButtonState(_enable, !_backend.DnsStateUnknown && status.SystemDnsManaged, Color.FromArgb(214, 232, 254), Color.FromArgb(54, 111, 177));
        ApplyButtonState(_auto, automatic, Color.FromArgb(214, 232, 254), Color.FromArgb(54, 111, 177));
        ApplyButtonState(_prefer, !_backend.DnsStateUnknown && SelectedProfile()?.Id == status.Config.PinnedProfileId && status.Config.PinnedProfileId is not null,
            Color.FromArgb(235, 226, 252), Color.FromArgb(118, 79, 167));
    }

    private static void ApplyButtonState(Button button, bool selected, Color fill, Color border)
    {
        var background = selected ? fill : Color.White;
        var outline = selected ? border : Color.FromArgb(164, 180, 195);
        if (button.BackColor != background) button.BackColor = background;
        if (button.FlatAppearance.BorderColor != outline) button.FlatAppearance.BorderColor = outline;
        var thickness = selected ? 2 : 1;
        if (button.FlatAppearance.BorderSize != thickness) button.FlatAppearance.BorderSize = thickness;
    }

    private void UpdateStateIndicators()
    {
        var status = _backend.Status;
        _monitoringIndicator.Text = _backend.DnsStateUnknown ? "Проверки: не подтверждены" : status.Monitoring ? "Проверки: включены" : "Проверки: ПАУЗА";
        _monitoringIndicator.BackColor = _backend.DnsStateUnknown ? Color.FromArgb(236, 240, 244) : status.Monitoring ? Color.FromArgb(225, 244, 232) : Color.FromArgb(255, 239, 202);
        _monitoringIndicator.ForeColor = _backend.DnsStateUnknown ? Color.FromArgb(83, 98, 113) : status.Monitoring ? Color.FromArgb(28, 99, 57) : Color.FromArgb(119, 80, 12);
        _dnsIndicator.Text = _backend.DnsStateUnknown ? "DNS: состояние неизвестно" : status.DnsRecoveryPending ? "DNS: нужно восстановление" : status.SystemDnsManaged ? "DNS: управление включено" : "DNS: управление выключено";
        _dnsIndicator.BackColor = _backend.DnsStateUnknown || status.DnsRecoveryPending ? Color.FromArgb(253, 232, 228) : status.SystemDnsManaged ? Color.FromArgb(214, 232, 254) : Color.FromArgb(236, 240, 244);
        _dnsIndicator.ForeColor = _backend.DnsStateUnknown || status.DnsRecoveryPending ? Color.FromArgb(137, 46, 34) : status.SystemDnsManaged ? Color.FromArgb(34, 77, 130) : Color.FromArgb(83, 98, 113);
    }

    private void UpdateReadIndicator()
    {
        _readProgress.Visible = _busy || _refreshing || _historyRefreshing;
        if (_busy) _measurementIndicator.Text = "Выполняется действие…";
        else if (_refreshing) _measurementIndicator.Text = "Получение состояния…";
        else if (_historyRefreshing) _measurementIndicator.Text = "Загрузка истории…";
        else
        {
            var status = _backend.Status;
            var now = DateTimeOffset.UtcNow;
            var last = status.Statistics.Select(s => MonitoringTargets.View(s, status.Config)?.Last).Where(s => s is not null && s.At <= now)
                .Select(s => s!.At).OrderDescending().Cast<DateTimeOffset?>().FirstOrDefault();
            _measurementIndicator.Text = last is null ? "Измерений пока нет" : "Последнее измерение: " + last.Value.ToLocalTime().ToString("HH:mm:ss")
                + (now - last.Value > TimeSpan.FromSeconds(MonitoringTargets.FreshnessSeconds(status.Config)) ? " · устарело" : "");
        }
        _tips.SetToolTip(_measurementIndicator, _measurementIndicator.Text + "\r\nИндикатор показывает получение состояния или выполнение команды. Окончание опроса службы не означает завершение новой сетевой проверки.");
    }

    private void UpdateSettings(AppStatus status)
    {
        var key = JsonData.Encode(new { status.Config.ProbeUrl, status.Config.MonitorChatGpt, status.Config.MonitorGemini, status.Config.MonitorTailscale, status.Config.AdditionalTargets, status.Config.IntervalSeconds, status.Config.TimeoutSeconds, status.Config.AdapterId });
        if (!_settingsLoaded || (!_settingsDirty && key != _settingsKey))
        {
            _loadingSettings = true;
            _url.Text = status.Config.ProbeUrl;
            _monitorChatGpt.Checked = status.Config.MonitorChatGpt;
            _monitorGemini.Checked = status.Config.MonitorGemini;
            _monitorTailscale.Checked = status.Config.MonitorTailscale;
            _additionalTargets.Clear();
            _additionalTargets.AddRange(JsonData.Clone(status.Config.AdditionalTargets));
            RefreshTargetsGrid();
            _interval.Value = Math.Clamp(status.Config.IntervalSeconds, 10, 3600);
            _timeout.Value = Math.Clamp(status.Config.TimeoutSeconds, 2, 30);
            UpdateAdapters(status.Adapters, true, status.Config.AdapterId);
            _settingsKey = key;
            _settingsLoaded = true;
            _loadingSettings = false;
        }
        else UpdateAdapters(status.Adapters);
        _settingsHint.Text = status.DnsRecoveryPending ? "Сначала завершите восстановление DNS. Смена адаптера и новое включение управления недоступны."
            : _backend.PendingServiceConfiguration ? "Список каналов сохранён локально. «Применить настройки» передаст его службе; включение управления DNS выполняется отдельно."
            : _settingsDirty ? "Есть несохранённые настройки. «Применить» обновит параметры проверок; управление DNS включается отдельно."
            : status.SystemDnsManaged ? "Для смены адаптера сначала отключите управление и восстановите DNS." : "Адаптер будет изменён только после отдельного подтверждения управления DNS.";
        _targetsHint.Text = _settingsDirty ? "Есть несохранённые изменения. Нажмите «Применить объекты»; будут сохранены и параметры вкладки «Проверка и сеть»."
            : "Выбранные объекты: " + string.Join(", ", MonitoringTargets.Selected(status.Config).Select(t => t.Name)) + ". При изменении набора нужны новые измерения; история сохраняется.";
    }

    private void UpdateAdapters(List<AdapterInfo> adapters, bool force = false, string? selectedId = null)
    {
        var key = string.Join("|", adapters.Select(a => a.Id + a.Name + a.Up));
        if (!force && key == _adapterKey) return;
        var selected = selectedId ?? (_adapter.SelectedItem as AdapterInfo)?.Id ?? _backend.Status.Config.AdapterId;
        var wasLoading = _loadingSettings;
        _loadingSettings = true;
        _adapter.BeginUpdate();
        _adapter.Items.Clear();
        _adapter.Items.AddRange(adapters.OrderByDescending(a => a.Up && a.HasGateway).ThenBy(a => a.Name).Cast<object>().ToArray());
        _adapter.SelectedItem = _adapter.Items.Cast<AdapterInfo>().FirstOrDefault(a => a.Id.Equals(selected, StringComparison.OrdinalIgnoreCase));
        if (_adapter.SelectedIndex < 0 && string.IsNullOrEmpty(selected) && _adapter.Items.Count > 0) _adapter.SelectedIndex = 0;
        _adapter.EndUpdate();
        _adapterKey = key;
        _loadingSettings = wasLoading;
    }

    private void SettingsChanged()
    {
        if (_loadingSettings) return;
        _settingsDirty = true;
        _settingsHint.Text = "Есть несохранённые настройки. Управление DNS включается отдельной кнопкой.";
        _targetsHint.Text = "Есть несохранённые изменения. Нажмите «Применить объекты»; будут сохранены и параметры вкладки «Проверка и сеть».";
        UpdateButtons();
    }

    private AppConfig ReadSettings(bool validate = true)
    {
        var config = JsonData.Clone(_backend.Status.Config);
        config.ProbeUrl = _url.Text.Trim();
        config.MonitorChatGpt = _monitorChatGpt.Checked;
        config.MonitorGemini = _monitorGemini.Checked;
        config.MonitorTailscale = _monitorTailscale.Checked;
        config.AdditionalTargets = JsonData.Clone(_additionalTargets);
        config.IntervalSeconds = (int)_interval.Value;
        config.TimeoutSeconds = (int)_timeout.Value;
        config.AdapterId = (_adapter.SelectedItem as AdapterInfo)?.Id ?? "";
        if (validate) config.Validate();
        return config;
    }

    private async Task ApplySettingsAsync() => await RunAsync(async () =>
    {
        await _backend.ConfigureAsync(ReadSettings());
        _settingsDirty = false;
    }, "Сохранение настроек");

    private MonitoringEndpoint? SelectedTarget() => _targetGrid.SelectedRows.Count == 0 ? null
        : _additionalTargets.FirstOrDefault(t => t.Id == _targetGrid.SelectedRows[0].Tag as string);

    private void RefreshTargetsGrid()
    {
        var selected = SelectedTarget()?.Id;
        _targetGrid.Rows.Clear();
        foreach (var target in _additionalTargets)
        {
            var row = _targetGrid.Rows[_targetGrid.Rows.Add(target.Enabled, target.Name, target.Url)];
            row.Tag = target.Id;
            row.Cells["url"].ToolTipText = target.Url;
            if (target.Id == selected) row.Selected = true;
        }
        UpdateButtons();
    }

    private void EditTarget(bool add)
    {
        if (_busy || (add && _additionalTargets.Count >= 6)) return;
        var source = add ? new MonitoringEndpoint { Id = Guid.NewGuid().ToString("N"), Name = "", Url = "https://", Enabled = true } : SelectedTarget();
        if (source is null) return;
        using var dialog = new Form { Text = add ? "Добавить объект мониторинга" : "Изменить объект мониторинга", Font = Font,
            StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MinimizeBox = false, MaximizeBox = false,
            ClientSize = new Size(620, 270), ShowInTaskbar = false };
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(16), ColumnCount = 2, RowCount = 5 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 110));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var name = new TextBox { Text = source.Name, Dock = DockStyle.Fill, MaxLength = 100 };
        var url = new TextBox { Text = source.Url, Dock = DockStyle.Fill, MaxLength = 2048 };
        var enabled = new CheckBox { Text = "Проверять этот объект", Checked = source.Enabled, AutoSize = true };
        AddSetting(layout, "Название", name, 0);
        AddSetting(layout, "HTTPS URL", url, 1);
        layout.Controls.Add(enabled, 1, 2);
        var hint = new Label { Text = "Публичный HTTPS-адрес без паролей, параметров и фрагмента. Проверка выполняется без аккаунта. 401/403/429 считаются неуспешным результатом.",
            AutoSize = true, ForeColor = Color.DimGray, MaximumSize = new Size(470, 0), Margin = new Padding(0, 12, 0, 12) };
        layout.Controls.Add(hint, 1, 3);
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, FlowDirection = FlowDirection.RightToLeft };
        var save = MakeButton("Сохранить");
        var cancel = MakeButton("Отмена");
        cancel.DialogResult = DialogResult.Cancel;
        buttons.Controls.AddRange([cancel, save]);
        layout.Controls.Add(buttons, 1, 4);
        dialog.Controls.Add(layout);
        dialog.AcceptButton = save;
        dialog.CancelButton = cancel;
        MonitoringEndpoint? edited = null;
        save.Click += (_, _) =>
        {
            var value = new MonitoringEndpoint { Id = source.Id, Name = name.Text.Trim(), Url = url.Text.Trim(), Enabled = enabled.Checked };
            try
            {
                var candidate = ReadSettings(false);
                candidate.AdditionalTargets.RemoveAll(t => t.Id == value.Id);
                candidate.AdditionalTargets.Add(value);
                candidate.Validate();
                edited = value;
                dialog.DialogResult = DialogResult.OK;
            }
            catch (Exception ex) { MessageBox.Show(dialog, ex.Message, "Объект мониторинга", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
        };
        if (dialog.ShowDialog(this) != DialogResult.OK || edited is null) return;
        var index = _additionalTargets.FindIndex(t => t.Id == edited.Id);
        if (index >= 0) _additionalTargets[index] = edited;
        else _additionalTargets.Add(edited);
        RefreshTargetsGrid();
        SettingsChanged();
    }

    private void DeleteTarget()
    {
        if (_busy || SelectedTarget() is not { } target) return;
        if (MessageBox.Show(this, $"Удалить объект мониторинга «{target.Name}»?\r\nИзменение начнёт действовать после «Применить объекты». Сохранённая история останется.",
            "Удаление объекта", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        _additionalTargets.RemoveAll(t => t.Id == target.Id);
        RefreshTargetsGrid();
        SettingsChanged();
    }

    private async Task EnableAsync()
    {
        AppConfig config;
        try
        {
            config = ReadSettings();
            if (_adapter.SelectedItem is not AdapterInfo || !config.Profiles.Any(p => p.Enabled)) throw new InvalidOperationException("Выберите сетевой адаптер и включите хотя бы один канал.");
        }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Управление DNS", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
        var adapter = (AdapterInfo)_adapter.SelectedItem!;
        var text = $"Включить управление DNS адаптера «{adapter.Name}»?\r\n\r\nRtlineDnsPilot установит, обновит или запустит фоновую службу Windows. Каналы и история сохраняются. DNS этого адаптера будут направлены на локальный посредник: IPv4 127.0.0.1 и, при включённом IPv6, ::1. Исходные настройки сохраняются для восстановления.\r\n\r\nСлужба продолжит работу после закрытия окна. Для возврата используйте «Отключить управление» или «Восстановить DNS». VPN, прокси, маршруты и IP-адреса не изменяются.\r\n\r\nПри установке или обновлении службы потребуется разрешение. " + AdministratorHelp;
        if (MessageBox.Show(this, text, "Изменение системного DNS", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync(async () => { await _backend.EnableManagementAsync(config); _settingsDirty = false; }, "Включение управления DNS");
    }

    private async Task RestoreDnsAsync()
    {
        if (!_backend.ServiceConnected && _backend.DnsStateUnknown)
        {
            var text = "Служба не отвечает; текущее управление DNS не подтверждено.\r\n\r\nЗапустить помощник восстановления? Он остановит службу и восстановит исходные DNS-настройки из её журнала. При конфликте с изменениями другой программы восстановление сообщит об ошибке.\r\n\r\nПосле успешного восстановления управление останется выключенным; локальные проверки продолжатся.\r\n\r\n" + AdministratorHelp;
            if (MessageBox.Show(this, text, "Восстановление без связи со службой", MessageBoxButtons.YesNo, MessageBoxIcon.Warning, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        await RunAsync(() => _backend.RestoreAsync(), "Восстановление DNS");
    }

    private async Task ResetAutomaticDnsAsync()
    {
        if (_busy || _adapter.SelectedItem is not AdapterInfo adapter) return;
        var id = adapter.Id;
        var text = $"Получать DNS автоматически для адаптера «{adapter.Name}»?\r\nGUID: {id}\r\n\r\nУправление DNS RtlineDnsPilot будет отключено. Ручные DNS выбранного адаптера будут заменены автоматическими настройками сети для IPv4 и включённого IPv6. Это действие не восстанавливает прежние ручные DNS. IP-адрес, шлюз, VPN и прокси не меняются.\r\n\r\n" + AdministratorHelp;
        if (MessageBox.Show(this, text, "Сброс DNS на автоматическое получение", MessageBoxButtons.YesNo, MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync(() => _backend.ResetAutomaticDnsAsync(id), "Автоматическое получение DNS");
    }

    private async Task EditProfileAsync(bool add)
    {
        if (_busy) return;
        var source = add ? new ChannelProfile() : SelectedProfile();
        if (source is null) return;
        using var dialog = new ProfileDialog(source, add, _backend.Status.Config.Profiles);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        await RunAsync(async () =>
        {
            var config = JsonData.Clone(_backend.Status.Config);
            if (add) config.Profiles.Add(dialog.Profile);
            else config.Profiles[config.Profiles.FindIndex(p => p.Id == source.Id)] = dialog.Profile;
            if (config.PinnedProfileId == source.Id && !dialog.Profile.Enabled) config.PinnedProfileId = null;
            if (config.ManualProfileId == source.Id && !dialog.Profile.Enabled) { config.ManualProfileId = null; config.ManualUntil = null; }
            await _backend.ConfigureAsync(config);
        }, "Сохранение канала");
    }

    private async Task DeleteProfileAsync(string? profileId = null)
    {
        var profile = profileId is null ? SelectedProfile() : _backend.Status.Config.Profiles.FirstOrDefault(p => p.Id == profileId);
        if (_busy || profile is null || _backend.Status.Config.Profiles.Count <= 1) return;
        if (MessageBox.Show(this, $"Удалить канал «{profile.Name}»? Если он выбран, служба будет использовать оставшиеся включённые каналы.", "Удаление канала", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync(async () =>
        {
            var config = JsonData.Clone(_backend.Status.Config);
            config.Profiles.RemoveAll(p => p.Id == profile.Id);
            if (config.PinnedProfileId == profile.Id) config.PinnedProfileId = null;
            if (config.ManualProfileId == profile.Id) { config.ManualProfileId = null; config.ManualUntil = null; }
            await _backend.ConfigureAsync(config);
        }, "Удаление канала");
    }

    private async Task ImportAsync()
    {
        using var dialog = new OpenFileDialog { Title = "Импорт каналов", Filter = "Каналы JSON (*.json)|*.json|Все файлы (*.*)|*.*", CheckFileExists = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        List<ChannelProfile> profiles;
        try { profiles = UiBackend.ReadProfiles(dialog.FileName); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Импорт каналов", MessageBoxButtons.OK, MessageBoxIcon.Error); return; }
        if (MessageBox.Show(this, $"Заменить текущий список ({_backend.Status.Config.Profiles.Count}) каналами из файла ({profiles.Count})?\r\nПараметры проверки и адаптер сохранятся. Будет включён автовыбор.", "Импорт каналов", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        await RunAsync(async () =>
        {
            var config = JsonData.Clone(_backend.Status.Config);
            config.Profiles = profiles;
            config.PinnedProfileId = null;
            config.ManualProfileId = null;
            config.ManualUntil = null;
            await _backend.ConfigureAsync(config);
        }, "Импорт каналов");
    }

    private void Export()
    {
        using var dialog = new SaveFileDialog { Title = "Экспорт каналов", Filter = "Каналы JSON (*.json)|*.json", FileName = "RtlineDnsPilot-profiles.json", DefaultExt = "json", AddExtension = true, OverwritePrompt = true };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { AtomicStore.Write(dialog.FileName, _backend.Status.Config.Profiles); AddUiEvent("Каналы экспортированы: " + dialog.FileName); UpdateLogs(_backend.Status); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Экспорт каналов", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }

    private void AddUiEvent(string message, string kind = "info")
    {
        if (_uiEvents.LastOrDefault()?.Message == message) return;
        _uiEvents.Add(new LogEntry { Kind = kind, Message = message });
        if (_uiEvents.Count > 150) _uiEvents.RemoveAt(0);
    }

    private void UpdateLogs(AppStatus status)
    {
        var text = string.Join(Environment.NewLine, status.Events.Concat(_uiEvents).OrderBy(e => e.At).TakeLast(250).Select(e => $"{e.At.ToLocalTime():dd.MM HH:mm:ss}  [{e.Kind}] {e.Message}"));
        if (text == _lastLogs) return;
        _lastLogs = text;
        _logs.Text = text;
        _logs.SelectionStart = _logs.TextLength;
        _logs.ScrollToCaret();
    }

    private void UpdateNotifications(AppStatus status)
    {
        if (_smoke) return;
        var enabled = status.Config.Profiles.Where(p => p.Enabled).ToList();
        var measured = enabled.Count > 0 && enabled.All(p => status.Statistics.Any(s => s.ProfileId == p.Id && MonitoringTargets.View(s, status.Config)?.Last is not null));
        if (status.AllUnavailable && measured && !_incidentOpen)
        {
            _incidentOpen = true;
            _incidentAt = DateTimeOffset.UtcNow;
            _tray.ShowBalloonTip(8000, "RtlineDnsPilot: все каналы недоступны", "Последние проверки не прошли. Наблюдение продолжается; подробности в окне и журнале.", ToolTipIcon.Warning);
        }
        else if (_incidentOpen && !status.AllUnavailable && enabled.Any(p => status.Statistics.Any(s => s.ProfileId == p.Id && MonitoringTargets.View(s, status.Config)?.Last is { Success: true } last && last.At > _incidentAt)))
        {
            _incidentOpen = false;
            _tray.ShowBalloonTip(6000, "RtlineDnsPilot: доступность восстановлена", "Хотя бы один канал снова прошёл DNS и HTTPS всех выбранных объектов.", ToolTipIcon.Info);
        }
    }

    private void RestoreWindow()
    {
        Show();
        if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
        Activate();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        if (_reallyClose || _smoke || e.CloseReason is CloseReason.WindowsShutDown or CloseReason.TaskManagerClosing) return;
        e.Cancel = true;
        Hide();
        if (!_hideTipShown)
        {
            _hideTipShown = true;
            _tray.ShowBalloonTip(4500, "RtlineDnsPilot работает в области уведомлений", "Двойной щелчок по значку откроет окно. Для завершения выберите «Выход» в меню значка.", ToolTipIcon.Info);
        }
    }

    private async Task ExitAsync()
    {
        if (_busy) { RestoreWindow(); MessageBox.Show(this, "Дождитесь завершения текущего действия.", "Выход из RtlineDnsPilot"); return; }
        if (_backend.Status.SystemDnsManaged || _backend.Status.DnsRecoveryPending || _backend.DnsStateUnknown)
        {
            var message = _backend.Status.DnsRecoveryPending
                ? "Восстановление DNS не завершено; текущее состояние адаптера не подтверждено. Выход закроет окно и не завершит восстановление. Выйти?"
                : _backend.DnsStateUnknown
                ? "Состояние DNS службы сейчас неизвестно. Выход закроет окно; он не отключит службу и не восстановит DNS. Выйти?"
                : "Управление DNS продолжится в фоновой службе. Выход закроет только окно. Для восстановления сначала нажмите «Отключить управление». Выйти?";
            if (MessageBox.Show(this, message, "Выход из RtlineDnsPilot", MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
        }
        _timer.Stop();
        CancelHistoryRead();
        await _backend.DisposeAsync();
        _backendDisposed = true;
        _reallyClose = true;
        Close();
    }

    /// <summary>Renders the initial window without service RPC, monitoring, installation or DNS changes.</summary>
    public void RenderSmoke(string path)
    {
        _smoke = true;
        _timer.Stop();
        _activity.Text = "Предпросмотр · без проверок";
        Show();
        Application.DoEvents();
        UpdateView();
        if (_tabs.SelectedIndex == 3) _history.SetSyntheticReportFromStatus(_backend.Status);
        using var bitmap = new Bitmap(Width, Height);
        DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (directory is not null) Directory.CreateDirectory(directory);
        bitmap.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        Hide();
    }

    /// <summary>Renders configured channels in the journal without service RPC, probes or DNS changes.</summary>
    public void RenderJournalSmoke(string path)
    {
        _smoke = true;
        if (!_backend.Status.Statistics.Any(s => s.Samples.Count > 0)) PrepareSmokeData();
        _tabs.SelectedIndex = 3;
        RenderSmoke(path);
    }

    /// <summary>Shows monitoring objects using synthetic in-memory data, with no service requests or probes.</summary>
    public void RenderSettingsSmoke(string path)
    {
        _smoke = true;
        PrepareSmokeData();
        _tabs.SelectedIndex = 1;
        RenderSmoke(path);
    }

    /// <summary>Shows blue active, green available and red failed rows using synthetic measurements only.</summary>
    public void RenderChannelsSmoke(string path)
    {
        _smoke = true;
        PrepareSmokeData();
        _tabs.SelectedIndex = 0;
        RenderSmoke(path);
    }

    private void PrepareSmokeData()
    {
        var status = _backend.Status;
        status.Config.MonitorChatGpt = true;
        status.Config.MonitorGemini = true;
        status.Config.MonitorTailscale = false;
        status.Config.AdditionalTargets = [new MonitoringEndpoint { Id = "11111111222233334444555555555555", Name = "Пример своего объекта", Url = "https://example.org/", Enabled = true }];
        status.Config.AdapterId = "11111111-2222-3333-4444-555555555555";
        status.Adapters = [new AdapterInfo { Id = status.Config.AdapterId, Name = "Демонстрационный адаптер", Up = true, HasGateway = true }];
        status.Config.ManualProfileId = status.Config.Profiles[0].Id;
        status.Config.ManualUntil = DateTimeOffset.UtcNow.AddMinutes(15);
        status.SelectionReason = "Ручная фиксация до " + status.Config.ManualUntil.Value.ToLocalTime().ToString("HH:mm:ss") + ". Автоматическое переключение отключено.";
        status.ActiveProfileId = status.Config.Profiles[0].Id;
        status.Statistics.Clear();
        var now = DateTimeOffset.UtcNow;
        for (var profileIndex = 0; profileIndex < Math.Min(3, status.Config.Profiles.Count); profileIndex++)
        {
            var profile = status.Config.Profiles[profileIndex];
            var statistics = new ProfileStats { ProfileId = profile.Id };
            for (var sampleIndex = 0; sampleIndex < 24; sampleIndex++)
            {
                var at = now.AddSeconds((sampleIndex - 23) * 20);
                var services = MonitoringTargets.Selected(status.Config).Select((target, targetIndex) =>
                {
                    var success = profileIndex == 1 || (profileIndex == 0 ? targetIndex != 1 || sampleIndex % 4 != 3 : targetIndex != 0);
                    return new ServiceProbeResult { ServiceId = target.Id, At = at, Success = success, DnsMs = 15 + sampleIndex % 7 * 3,
                        HttpsMs = success ? 95 + profileIndex * 40 + sampleIndex % 6 * 18 : 340 + sampleIndex * 8,
                        HttpStatus = success ? 200 : 403, Stage = "http", Detail = "Искусственный результат для проверки интерфейса. Сетевой запрос не выполнялся.",
                        CountryCode = targetIndex == 0 ? profileIndex == 1 ? "DE" : "US" : null,
                        CountrySource = targetIndex == 0 ? ConnectionCountrySource.SiteTrace : ConnectionCountrySource.None };
                }).ToArray();
                statistics.Add(MonitoringTargets.Combine(profile.Id, status.Config, services));
            }
            status.Statistics.Add(statistics);
        }
        // An old ChatGPT-only result must never become evidence for Gemini or a newly added object.
        if (status.Config.Profiles.Count > 3)
        {
            var legacy = new ProfileStats { ProfileId = status.Config.Profiles[3].Id };
            legacy.Add(new ProbeResult { ProfileId = legacy.ProfileId, At = now, Success = true, DnsMs = 10, HttpsMs = 100, HttpStatus = 200, Stage = "http", Detail = "Старая проверка только ChatGPT." });
            status.Statistics.Add(legacy);
        }
        _settingsLoaded = false;
        _settingsDirty = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _timer.Stop();
            CancelHistoryRead();
            _shareCancellation?.Cancel();
            _shareClient.Dispose();
            _timer.Dispose();
            _tray.Visible = false;
            _tray.Dispose();
            _tips.Dispose();
            _channelMenu.Dispose();
            _autoModeMenu.Dispose();
            if (!_backendDisposed) { _backendDisposed = true; _ = _backend.DisposeAsync(); }
        }
        base.Dispose(disposing);
    }
}
