using System.Drawing.Drawing2D;
using System.Globalization;

namespace NetPilot;

/// <summary>Read-only aggregated chart history, separate from the selection sample window.</summary>
public sealed class ChannelHistoryView : UserControl
{
    private readonly Panel _scroll = new() { Dock = DockStyle.Fill, AutoScroll = true };
    private readonly Dictionary<string, ChannelHistoryCard> _cards = new(StringComparer.OrdinalIgnoreCase);
    private readonly NumericUpDown _period = new() { Minimum = 1, Maximum = 24, Value = 1, Width = 65 };
    private readonly ComboBox _periodUnit = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 90 };
    private readonly NumericUpDown _bucket = new() { Minimum = 1, Maximum = 60, Value = 5, Width = 65 };
    private readonly Label _stepHint = new() { AutoSize = true, ForeColor = Color.FromArgb(86, 104, 120), Margin = new Padding(8, 5, 0, 0) };
    private readonly Label _hint = new() { AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(86, 104, 120), Margin = new Padding(0, 4, 0, 10) };
    private AppStatus? _status;
    private HistoryReport? _report;
    private HistoryQuery _query = new();
    private string _context = "";
    private string? _error;
    private string[] _order = [];
    private bool _loading;
    private bool _arranging;
    private bool _adjustingQuery;
    private bool _synthetic;

    public HistoryQuery Query => new() { PeriodMinutes = _query.PeriodMinutes, BucketMinutes = _query.BucketMinutes };
    public event EventHandler? QueryChanged;

    public ChannelHistoryView()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        BackColor = Color.FromArgb(245, 247, 250);
        _periodUnit.Items.AddRange(["минут", "часов", "дней"]);
        _periodUnit.SelectedIndex = 1;
        _period.AccessibleName = "Общий период графика";
        _periodUnit.AccessibleName = "Единица периода графика";
        _bucket.AccessibleName = "Минут в одном столбике";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, Padding = new Padding(10), Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(new Label { Text = "История доступности и времени ответа", Font = new Font("Segoe UI", 11F, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill, ForeColor = Color.FromArgb(24, 45, 65), Margin = new Padding(0, 0, 0, 8) }, 0, 0);
        var controls = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSize = true, WrapContents = true, Margin = new Padding(0) };
        controls.Controls.AddRange([
            new Label { Text = "Период:", AutoSize = true, Margin = new Padding(0, 5, 4, 0) }, _period, _periodUnit,
            new Label { Text = "Один столбик:", AutoSize = true, Margin = new Padding(15, 5, 4, 0) }, _bucket,
            new Label { Text = "минут", AutoSize = true, Margin = new Padding(3, 5, 0, 0) }, _stepHint
        ]);
        layout.Controls.Add(controls, 0, 1);
        layout.Controls.Add(_hint, 0, 2);
        layout.Controls.Add(_scroll, 0, 3);
        Controls.Add(layout);
        _period.ValueChanged += (_, _) => ChangeQuery();
        _periodUnit.SelectedIndexChanged += (_, _) => ChangeQuery();
        _bucket.ValueChanged += (_, _) => ChangeQuery();
        _scroll.SizeChanged += (_, _) => ArrangeCards();
        SizeChanged += (_, _) => _hint.MaximumSize = new Size(Math.Max(100, ClientSize.Width - Scale(30)), 0);
        _stepHint.Text = "Частота проверок не меняется.";
        Render();
    }

    private void ChangeQuery()
    {
        if (_adjustingQuery) return;
        _adjustingQuery = true;
        var raised = false;
        try
        {
            var multiplier = _periodUnit.SelectedIndex switch { 1 => 60, 2 => 1440, _ => 1 };
            _period.Maximum = 1440 / multiplier;
            var period = (int)_period.Value * multiplier;
            var minimum = Math.Max(1, (int)Math.Ceiling(period / 299.0));
            var previousBucket = (int)_bucket.Value;
            _bucket.Minimum = 1;
            _bucket.Maximum = period;
            _bucket.Minimum = minimum;
            _bucket.Value = Math.Clamp(previousBucket, minimum, period);
            raised = previousBucket < minimum;
            _stepHint.Text = (raised ? "Шаг увеличен до " + minimum + " мин. " : "")
                + (minimum > 1 ? "Минимум " + minimum + " мин: до 300 столбиков. " : "") + "Частота проверок не меняется.";
            var next = new HistoryQuery { PeriodMinutes = period, BucketMinutes = (int)_bucket.Value };
            next.Validate();
            if (SameQuery(next, _query)) return;
            _query = next;
            _report = null;
            _synthetic = false;
            _error = null;
            _loading = true;
        }
        finally { _adjustingQuery = false; }
        Render();
        QueryChanged?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateHistory(AppStatus status)
    {
        var key = MonitoringTargets.Key(status.Config);
        var context = (status.ServiceConnected ? "service|" : "local|") + key + "|" + string.Join("|", status.Config.Profiles
            .Select(p => p.Id + ":" + ChartHistoryArchive.ContextKey(p, key)).OrderBy(s => s, StringComparer.OrdinalIgnoreCase));
        if (_context != context)
        {
            _context = context;
            _report = null;
            _error = null;
            _loading = false;
            _synthetic = false;
        }
        _status = status;
        Render();
    }

    public void SetReport(HistoryReport report)
    {
        if (_status is null || !SameQuery(report.Query, _query) || report.MonitoringKey != MonitoringTargets.Key(_status.Config)
            || report.To <= report.From) return;
        try { report.Query.Validate(); }
        catch (ArgumentException) { return; }
        var profiles = _status.Config.Profiles.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (report.Channels.Any(series => !ids.Add(series.ProfileId) || !profiles.TryGetValue(series.ProfileId, out var profile)
            || series.ContextKey != ChartHistoryArchive.ContextKey(profile, report.MonitoringKey))) return;
        _report = report;
        _error = null;
        _loading = false;
        _synthetic = false;
        Render();
    }

    public void SetLoading()
    {
        _loading = true;
        _error = null;
        Render();
    }

    public void SetHistoryError(string message)
    {
        _loading = false;
        _report = null;
        _synthetic = false;
        _error = message;
        Render();
    }

    // Explicitly called only by isolated smoke fixtures; never a replacement for a service archive.
    internal void SetSyntheticReportFromStatus(AppStatus status)
    {
        UpdateHistory(status);
        var now = status.Statistics.SelectMany(s => s.Samples).Select(s => s.At).DefaultIfEmpty(DateTimeOffset.UtcNow).Max();
        var archive = new ChartHistoryArchive();
        archive.Seed(status.Config, status.Statistics, now);
        SetReport(archive.Query(status.Config, Query, now));
        _synthetic = true;
        Render();
    }

    private static bool SameQuery(HistoryQuery left, HistoryQuery right) => left.PeriodMinutes == right.PeriodMinutes && left.BucketMinutes == right.BucketMinutes;
    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);

    private void Render()
    {
        var source = _synthetic ? "Синтетический пример; сетевых проверок нет" : _status?.ServiceConnected == true ? "Архив фоновой службы" : "Локальный архив";
        if (_report is { } report)
        {
            _hint.ForeColor = Color.FromArgb(86, 104, 120);
            _hint.Text = (_loading ? "Обновление… " : "") + source + ". Период: " + Interval(report.From, report.To) + ". "
                + "Среднее DNS + HTTPS; красный столбик — хотя бы один сбой. Пропуск — нет измерений. Текущий столбик может быть неполным. Стабильные каналы сверху."
                + (string.IsNullOrWhiteSpace(report.Warning) ? "" : " " + report.Warning);
        }
        else
        {
            _hint.ForeColor = _error is null ? Color.FromArgb(86, 104, 120) : Color.FromArgb(160, 43, 34);
            _hint.Text = _error is not null ? "История недоступна: " + _error : _loading ? "Загрузка архива за выбранный период…" : "История за выбранный период ещё не загружена. Измерения из таблицы не подменяют архив графика.";
        }
        if (_status is null) return;
        var profiles = _status.Config.Profiles;
        var wanted = profiles.Select(p => p.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var id in _cards.Keys.Where(id => !wanted.Contains(id)).ToArray())
        {
            var card = _cards[id];
            _scroll.Controls.Remove(card);
            card.Dispose();
            _cards.Remove(id);
        }
        var placeholder = _error is not null ? "История не получена. Подробности над графиками." : _loading ? "Загрузка архива…" : "История за выбранный период ещё не получена.";
        foreach (var profile in profiles)
        {
            if (!_cards.TryGetValue(profile.Id, out var card))
            {
                card = new ChannelHistoryCard();
                _cards.Add(profile.Id, card);
                _scroll.Controls.Add(card);
            }
            card.UpdateSeries(profile, _report?.Channels.FirstOrDefault(s => s.ProfileId == profile.Id), _status.ActiveProfileId == profile.Id, _report, placeholder);
        }
        var order = ChartHistoryArchive.SortProfiles(profiles, _report ?? new HistoryReport()).Select(p => p.Id).ToArray();
        if (!_order.SequenceEqual(order, StringComparer.OrdinalIgnoreCase))
        {
            _order = order;
            ArrangeCards();
        }
    }

    private static string Interval(DateTimeOffset from, DateTimeOffset to) => from.ToLocalTime().ToString("dd.MM HH:mm") + " — " + to.ToLocalTime().ToString("dd.MM HH:mm");

    private void ArrangeCards()
    {
        if (_arranging || _scroll.ClientSize.Width <= 0) return;
        _arranging = true;
        try
        {
            var gap = Scale(12);
            var available = Math.Max(1, _scroll.ClientSize.Width - gap);
            var columns = _order.Length > 1 && available >= Scale(840) ? 2 : 1;
            var width = (available - gap * (columns - 1)) / columns;
            var height = Scale(286);
            var rows = (_order.Length + columns - 1) / columns;
            _scroll.AutoScrollMinSize = new Size(0, rows == 0 ? 0 : rows * (height + gap));
            var offset = _scroll.AutoScrollPosition;
            for (var index = 0; index < _order.Length; index++)
                _cards[_order[index]].SetBounds(offset.X + index % columns * (width + gap), offset.Y + index / columns * (height + gap), width, height);
            _scroll.PerformLayout();
        }
        finally { _arranging = false; }
    }
}

internal sealed class ChannelHistoryCard : Control
{
    private static readonly Color Ink = Color.FromArgb(30, 49, 67);
    private static readonly Color Muted = Color.FromArgb(100, 116, 131);
    private static readonly Color DnsColor = Color.FromArgb(67, 129, 214);
    private static readonly Color HttpsColor = Color.FromArgb(51, 164, 161);
    private static readonly Color ErrorColor = Color.FromArgb(191, 65, 65);
    private readonly ToolTip _tip = new() { AutoPopDelay = 16000, InitialDelay = 200, ReshowDelay = 100 };
    private readonly List<(Rectangle Bounds, int Index)> _bars = [];
    private readonly Font _titleFont = new("Segoe UI", 11F, FontStyle.Bold);
    private readonly Font _smallFont = new("Segoe UI", 8.5F);
    private readonly Font _valueFont = new("Segoe UI", 10F, FontStyle.Bold);
    private Bar[] _samples = [];
    private DateTimeOffset _from;
    private DateTimeOffset _to;
    private int _bucketMinutes;
    private string _name = "";
    private string _protocol = "";
    private string _placeholder = "";
    private bool _enabled;
    private bool _active;
    private int _hover = -1;

    private sealed record Bar(DateTimeOffset From, DateTimeOffset To, DateTimeOffset LastAt, int Count, int SuccessCount, double DnsTotalMs, double HttpsTotalMs, bool LastSuccess)
    {
        public int FailedCount => Count - SuccessCount;
        public double MeanDnsMs => Count == 0 ? 0 : ValidMs(DnsTotalMs) / Count;
        public double MeanHttpsMs => Count == 0 ? 0 : ValidMs(HttpsTotalMs) / Count;
        public double MeanMs => ChartHistoryArchive.SumDuration(MeanDnsMs, MeanHttpsMs);
    }

    public ChannelHistoryCard()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        BackColor = Color.FromArgb(245, 247, 250);
        Cursor = Cursors.Default;
        TabStop = false;
        AccessibleRole = AccessibleRole.Chart;
    }

    public void UpdateSeries(ChannelProfile profile, ChannelHistorySeries? statistics, bool active, HistoryReport? report, string placeholder)
    {
        var samples = (statistics?.Bars ?? []).Where(s => s.Count > 0 && s.To > s.From)
            .Select(s => new Bar(s.From, s.To, s.LastAt, s.Count, s.SuccessCount, s.DnsTotalMs, s.HttpsTotalMs, s.LastSuccess)).OrderBy(s => s.From).ToArray();
        var protocol = profile.Mode == DnsMode.Doh ? "DoH" : "DNS · UDP/TCP";
        var from = report?.From ?? default;
        var to = report?.To ?? default;
        var bucket = report?.Query.BucketMinutes ?? 0;
        var nextPlaceholder = report is null ? placeholder : "За выбранный период измерений нет.";
        if (_name == profile.Name && _protocol == protocol && _enabled == profile.Enabled && _active == active && _from == from && _to == to
            && _bucketMinutes == bucket && _placeholder == nextPlaceholder && _samples.SequenceEqual(samples)) return;
        _name = profile.Name;
        _protocol = protocol;
        _enabled = profile.Enabled;
        _active = active;
        _from = from;
        _to = to;
        _bucketMinutes = bucket;
        _placeholder = nextPlaceholder;
        _samples = samples;
        _hover = -1;
        _tip.Hide(this);
        AccessibleName = "История канала «" + _name + "»";
        AccessibleDescription = (_enabled ? "Включён. " : "Выключен. ") + PeriodSummary() + ". " + MeanSummary() + ". " + CoverageSummary();
        Invalidate();
    }

    private int Scale(int value) => (int)Math.Round(value * DeviceDpi / 96.0);
    private static double ValidMs(double value) => double.IsFinite(value) && value >= 0 ? value : 0;
    private static string Ms(double value) => double.IsFinite(value) && value >= 0 ? value.ToString("0.#", CultureInfo.CurrentCulture) + " мс" : "не измерено";
    private long TotalChecks => _samples.Sum(s => (long)s.Count);
    private long TotalSuccesses => _samples.Sum(s => (long)s.SuccessCount);

    private string PeriodSummary() => TotalChecks == 0 ? "Измерений за период нет" : TotalChecks + " проверок · "
        + (100.0 * TotalSuccesses / TotalChecks).ToString("0.#", CultureInfo.CurrentCulture) + "% успешно · сбоев: " + (TotalChecks - TotalSuccesses);
    private string MeanSummary() => TotalChecks == 0 ? "Среднее пока не измерено" : "Среднее: "
        + Ms(_samples.Aggregate(0.0, (total, s) => ChartHistoryArchive.SumDuration(total, ChartHistoryArchive.SumDuration(ValidMs(s.DnsTotalMs), ValidMs(s.HttpsTotalMs)))) / TotalChecks);
    private string CoverageSummary() => _samples.Length == 0 ? _placeholder : "Заполнено интервалов: " + _samples.Length + " · "
        + _samples.Min(s => s.From).ToLocalTime().ToString("dd.MM HH:mm") + " — " + _samples.Max(s => s.To).ToLocalTime().ToString("dd.MM HH:mm");

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        var bounds = new Rectangle(Scale(1), Scale(1), Math.Max(1, Width - Scale(3)), Math.Max(1, Height - Scale(3)));
        using var path = RoundedRectangle(bounds, Scale(12));
        using var fill = new SolidBrush(Color.White);
        using var border = new Pen(_active ? Color.FromArgb(133, 173, 214) : Color.FromArgb(219, 227, 235), Scale(1));
        g.FillPath(fill, path);
        g.DrawPath(border, path);
        var left = Scale(17);
        var right = Width - Scale(17);
        var status = !_enabled ? "Выключен" : TotalChecks == 0 ? "Нет данных" : TotalSuccesses == TotalChecks ? "Без сбоев" : "Есть сбои";
        var statusColor = !_enabled || TotalChecks == 0 ? Muted : TotalSuccesses == TotalChecks ? Color.FromArgb(36, 124, 104) : ErrorColor;
        var statusFill = !_enabled || TotalChecks == 0 ? Color.FromArgb(238, 242, 246) : TotalSuccesses == TotalChecks ? Color.FromArgb(229, 245, 239) : Color.FromArgb(253, 236, 234);
        var pillWidth = TextRenderer.MeasureText(status, _smallFont).Width + Scale(16);
        var pill = new Rectangle(right - pillWidth, Scale(16), pillWidth, Scale(24));
        using (var pillPath = RoundedRectangle(pill, Scale(8)))
        using (var brush = new SolidBrush(statusFill)) g.FillPath(brush, pillPath);
        DrawText(g, status, _smallFont, statusColor, pill, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
        DrawText(g, _name, _titleFont, Ink, new Rectangle(left, Scale(15), Math.Max(1, pill.Left - left - Scale(10)), Scale(26)), TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter);
        DrawText(g, _protocol + (_active ? " · активный канал" : "") + (!_enabled && _samples.Length > 0 ? " · сохранённая история" : ""), _smallFont, Muted, new Rectangle(left, Scale(45), right - left, Scale(20)), TextFormatFlags.EndEllipsis);
        DrawText(g, MeanSummary(), _valueFont, Ink, new Rectangle(left, Scale(70), right - left, Scale(23)), TextFormatFlags.EndEllipsis);
        _bars.Clear();
        if (_samples.Length == 0)
        {
            using var emptyBrush = new SolidBrush(Color.FromArgb(247, 250, 253));
            var empty = new Rectangle(left, Scale(105), right - left, Scale(103));
            using (var emptyPath = RoundedRectangle(empty, Scale(8))) g.FillPath(emptyBrush, emptyPath);
            DrawText(g, _placeholder, Font, Muted, new Rectangle(left + Scale(8), Scale(126), right - left - Scale(16), Scale(52)), TextFormatFlags.HorizontalCenter | TextFormatFlags.WordBreak);
        }
        else DrawChart(g, new Rectangle(Scale(48), Scale(111), Math.Max(1, Width - Scale(67)), Scale(78)));
        DrawText(g, PeriodSummary(), _smallFont, Muted, new Rectangle(left, Scale(215), right - left, Scale(20)), TextFormatFlags.EndEllipsis);
        DrawText(g, CoverageSummary(), _smallFont, Muted, new Rectangle(left, Scale(235), right - left, Scale(20)), TextFormatFlags.EndEllipsis);
        DrawLegend(g, left, Scale(260));
    }

    private bool Partial(Bar sample)
    {
        if (_bucketMinutes <= 0 || sample.To != _to) return false;
        var seconds = (long)_bucketMinutes * 60;
        var start = DateTimeOffset.FromUnixTimeSeconds(_to.ToUnixTimeSeconds() / seconds * seconds);
        return _to > start && sample.From >= start;
    }

    private void DrawChart(Graphics g, Rectangle plot)
    {
        var maximum = NiceMaximum(_samples.Max(s => s.MeanMs));
        using var grid = new Pen(Color.FromArgb(231, 237, 243), Scale(1));
        for (var tick = 0; tick <= 2; tick++)
        {
            var y = plot.Bottom - plot.Height * tick / 2;
            g.DrawLine(grid, plot.Left, y, plot.Right, y);
            DrawText(g, (maximum * tick / 2).ToString("0", CultureInfo.CurrentCulture), _smallFont, Muted, new Rectangle(Scale(4), y - Scale(9), plot.Left - Scale(10), Scale(18)), TextFormatFlags.Right | TextFormatFlags.VerticalCenter);
        }
        DrawText(g, "мс", _smallFont, Muted, new Rectangle(Scale(11), plot.Top - Scale(18), Scale(29), Scale(18)), TextFormatFlags.Right);
        var span = Math.Max(1, (_to - _from).Ticks);
        using var dnsBrush = new SolidBrush(DnsColor);
        using var httpsBrush = new SolidBrush(HttpsColor);
        using var errorBrush = new SolidBrush(ErrorColor);
        using var partialPen = new Pen(Color.FromArgb(85, 101, 120), Scale(1)) { DashStyle = DashStyle.Dash };
        for (var index = 0; index < _samples.Length; index++)
        {
            var sample = _samples[index];
            var a = Math.Clamp((sample.From - _from).Ticks / (double)span, 0, 1);
            var b = Math.Clamp((sample.To - _from).Ticks / (double)span, 0, 1);
            var slotLeft = plot.Left + (float)(a * plot.Width);
            var slotRight = plot.Left + (float)(b * plot.Width);
            var barWidth = Math.Max(1, (slotRight - slotLeft) * .76f);
            var x = (slotLeft + slotRight) / 2f;
            var dnsHeight = (float)(sample.MeanDnsMs / maximum * plot.Height);
            var httpsHeight = (float)(sample.MeanHttpsMs / maximum * plot.Height);
            var totalHeight = dnsHeight + httpsHeight;
            var y = plot.Bottom - Math.Max(totalHeight, Scale(sample.FailedCount > 0 ? 7 : 2));
            if (sample.FailedCount > 0)
                g.FillRectangle(errorBrush, x - barWidth / 2, y, barWidth, plot.Bottom - y);
            else
            {
                if (dnsHeight > 0) g.FillRectangle(dnsBrush, x - barWidth / 2, plot.Bottom - dnsHeight, barWidth, dnsHeight);
                if (httpsHeight > 0) g.FillRectangle(httpsBrush, x - barWidth / 2, plot.Bottom - totalHeight, barWidth, httpsHeight);
                if (totalHeight < Scale(2)) g.FillRectangle(httpsBrush, x - barWidth / 2, plot.Bottom - Scale(2), barWidth, Scale(2));
            }
            if (Partial(sample)) g.DrawRectangle(partialPen, x - barWidth / 2, y - Scale(2), barWidth, plot.Bottom - y + Scale(2));
            _bars.Add((new Rectangle((int)(x - Math.Max(barWidth, Scale(5)) / 2), plot.Top - Scale(4), (int)Math.Max(barWidth, Scale(5)), plot.Height + Scale(8)), index));
        }
        var format = _from.ToLocalTime().Date == _to.ToLocalTime().Date ? "HH:mm" : "dd.MM HH:mm";
        var labelWidth = Math.Min(plot.Width / 3, Scale(105));
        DrawText(g, _from.ToLocalTime().ToString(format), _smallFont, Muted, new Rectangle(plot.Left, plot.Bottom + Scale(3), labelWidth, Scale(18)), TextFormatFlags.Left);
        DrawText(g, _from.AddTicks(span / 2).ToLocalTime().ToString(format), _smallFont, Muted, new Rectangle(plot.Left + plot.Width / 2 - labelWidth / 2, plot.Bottom + Scale(3), labelWidth, Scale(18)), TextFormatFlags.HorizontalCenter);
        DrawText(g, _to.ToLocalTime().ToString(format), _smallFont, Muted, new Rectangle(plot.Right - labelWidth, plot.Bottom + Scale(3), labelWidth, Scale(18)), TextFormatFlags.Right);
    }

    private void DrawLegend(Graphics g, int x, int y)
    {
        void Item(string text, Color color, int logicalWidth)
        {
            using var brush = new SolidBrush(color);
            g.FillRectangle(brush, x, y + Scale(4), Scale(9), Scale(9));
            DrawText(g, text, _smallFont, Muted, new Rectangle(x + Scale(14), y, Scale(logicalWidth - 14), Scale(20)), TextFormatFlags.Left);
            x += Scale(logicalWidth);
        }
        Item("DNS", DnsColor, 62);
        Item("HTTPS", HttpsColor, 79);
        Item("Сбой в интервале", ErrorColor, 135);
        if (_samples.Any(Partial)) DrawText(g, "┄ неполный", _smallFont, Muted, new Rectangle(x, y, Math.Max(1, Width - x - Scale(12)), Scale(20)), TextFormatFlags.EndEllipsis);
    }

    private static double NiceMaximum(double value)
    {
        if (!double.IsFinite(value) || value <= 0) return 100;
        var power = Math.Pow(10, Math.Floor(Math.Log10(Math.Max(10, value))));
        var part = value / power;
        return (part <= 1 ? 1 : part <= 2 ? 2 : part <= 5 ? 5 : 10) * power;
    }

    internal string BarTooltip(int index)
    {
        if (index < 0 || index >= _samples.Length) return "";
        var sample = _samples[index];
        return _name + "\r\nИнтервал: " + sample.From.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss") + " — " + sample.To.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz")
            + "\r\nПроверок: " + sample.Count + "; успешно: " + sample.SuccessCount + "; сбоев: " + sample.FailedCount
            + "\r\nСреднее DNS: " + Ms(sample.MeanDnsMs) + "; HTTPS: " + Ms(sample.MeanHttpsMs) + "\r\nСреднее DNS + HTTPS: " + Ms(sample.MeanMs)
            + (Partial(sample) ? "\r\nТекущий столбик ещё неполный." : "")
            + "\r\nПоследнее измерение: " + sample.LastAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss zzz");
    }

    private static GraphicsPath RoundedRectangle(Rectangle rectangle, int radius)
    {
        var path = new GraphicsPath();
        var size = Math.Max(1, Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height)));
        path.AddArc(rectangle.Left, rectangle.Top, size, size, 180, 90);
        path.AddArc(rectangle.Right - size, rectangle.Top, size, size, 270, 90);
        path.AddArc(rectangle.Right - size, rectangle.Bottom - size, size, size, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - size, size, size, 90, 90);
        path.CloseFigure();
        return path;
    }

    private static void DrawText(Graphics g, string text, Font font, Color color, Rectangle bounds, TextFormatFlags flags) =>
        TextRenderer.DrawText(g, text, font, bounds, color, flags | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping);

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var index = _bars.Where(b => b.Bounds.Contains(e.Location)).Select(b => b.Index).DefaultIfEmpty(-1).Last();
        if (_hover == index) return;
        _hover = index;
        _tip.Hide(this);
        if (index >= 0) _tip.Show(BarTooltip(index), this, Math.Min(e.X + Scale(14), Math.Max(0, Width - Scale(340))), e.Y + Scale(18), 16000);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = -1;
        _tip.Hide(this);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _tip.Dispose(); _titleFont.Dispose(); _smallFont.Dispose(); _valueFont.Dispose(); }
        base.Dispose(disposing);
    }
}
