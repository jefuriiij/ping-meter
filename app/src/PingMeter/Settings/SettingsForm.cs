using PingMeter.Config;
using PingMeter.Update;

namespace PingMeter.Settings;

/// <summary>
/// Plain-English settings dialog: everyday options on the General tab, technical knobs on
/// Advanced. Every setting gets a gray helper line (always visible) plus a longer hover
/// tooltip. The UI speaks seconds; the config file stays in milliseconds.
/// </summary>
internal sealed class SettingsForm : Form
{
    private static readonly Color GoodColor = Color.FromArgb(102, 187, 106);
    private static readonly Color WarnColor = Color.FromArgb(255, 179, 0);
    private static readonly Color BadColor = Color.FromArgb(239, 83, 80);

    private readonly AppConfig _working;
    private readonly ToolTip _help = new() { AutoPopDelay = 20_000, InitialDelay = 400, ReshowDelay = 200 };

    private readonly ListBox _targets = new() { IntegralHeight = false, Height = 110, Dock = DockStyle.Fill };
    private readonly TextBox _newTarget = new() { Dock = DockStyle.Fill, PlaceholderText = "type a website, e.g. google.com" };
    private readonly NumericUpDown _intervalSec = MakeNumeric(0.3m, 60, 0.5m, decimals: 1);
    private readonly NumericUpDown _timeoutSec = MakeNumeric(0.1m, 10, 0.5m, decimals: 1);
    private readonly NumericUpDown _window = MakeNumeric(10, 600, 10);
    private readonly NumericUpDown _green = MakeNumeric(1, 5_000, 5);
    private readonly NumericUpDown _yellow = MakeNumeric(1, 10_000, 5);
    private readonly NumericUpDown _offset = MakeNumeric(0, 1000, 4);
    private readonly NumericUpDown _retention = MakeNumeric(1, 365, 5);
    private readonly CheckBox _sparkline = MakeCheck("Show mini graph");
    private readonly CheckBox _transparent = MakeCheck("See-through background");
    private readonly CheckBox _autostart = MakeCheck("Start automatically when I turn on my PC");
    private readonly CheckBox _autoUpdate = MakeCheck("Tell me when a new version is available");
    private readonly CheckBox _eventLog = MakeCheck("Keep a diary of connection problems");
    private readonly CheckBox _csvLog = MakeCheck("Also record every single ping (CSV file)");
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };

    public event Action<AppConfig>? ConfigSaved;

    public SettingsForm(AppConfig current)
    {
        _working = current.Clone();

        Text = $"PingMeter Settings — v{UpdateChecker.CurrentVersion}";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(440, 648);

        _monitors.Items.AddRange(["Main screen only", "Second screen(s) only", "Every screen"]);

        BuildLayout();
        LoadFrom(_working);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        // Explicit SystemColors.Control instead of visual-style backgrounds: the dark color
        // mode remaps SystemColors, but visual-style tab bodies would stay light.
        var general = new TabPage("General") { Padding = new Padding(12), AutoScroll = true, BackColor = SystemColors.Control };
        var advanced = new TabPage("Advanced") { Padding = new Padding(12), AutoScroll = true, BackColor = SystemColors.Control };
        tabs.TabPages.Add(general);
        tabs.TabPages.Add(advanced);

        general.Controls.Add(BuildGeneralStack());
        advanced.Controls.Add(BuildAdvancedStack());

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            Padding = new Padding(8),
        };
        var ok = MakeButton("OK", (_, _) => { Save(); Close(); });
        var cancel = MakeButton("Cancel", (_, _) => Close());
        var apply = MakeButton("Apply", (_, _) => Save());
        buttons.Controls.Add(ok);
        buttons.Controls.Add(apply);
        buttons.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.Add(tabs);
        Controls.Add(buttons);
        tabs.BringToFront();
    }

    private Control BuildGeneralStack()
    {
        var stack = MakeStack();

        AddRow(stack, BuildTargetsBlock());

        AddRow(stack, SettingRow("Ping every", _intervalSec, "seconds",
            helper: "How often a ping is sent.",
            tip: "Example: 1.0 = one ping per second, like running 'ping -t' in a terminal. Lower = updates faster, with slightly more network chatter."));

        AddRow(stack, BuildColorsBlock());

        AddRow(stack, CheckRow(_sparkline,
            helper: "A tiny bar graph of the recent pings next to the number.",
            tip: "Each bar is one ping — taller means slower. Red full-height bars are lost pings."));

        AddRow(stack, CheckRow(_autostart,
            helper: "PingMeter appears in the taskbar every time Windows starts.",
            tip: "Adds PingMeter to your Windows startup apps. Untick to stop it starting by itself."));

        return stack;
    }

    private Control BuildAdvancedStack()
    {
        var stack = MakeStack();

        AddRow(stack, SettingRow("Give up after", _timeoutSec, "seconds",
            helper: "No reply within this time counts as a lost ping — shown as red T/O.",
            tip: "If a reply takes longer than this, PingMeter stops waiting and counts that ping as lost."));

        AddRow(stack, SettingRow("Statistics period", _window, "pings",
            helper: "Min / avg / max and loss % in the hover tooltip use this many recent pings.",
            tip: "Example: 60 pings at one per second = the last minute of history."));

        AddRow(stack, SettingRow("Where to show", _monitors, null,
            helper: "Which taskbar(s) get the ping display.",
            tip: "'Every screen' needs Windows' own \"Show taskbar on all displays\" setting to be turned on."));

        AddRow(stack, SettingRow("Move left", _offset, "pixels",
            helper: "Nudge the display away from the clock.",
            tip: "Only needed if something overlaps — PingMeter already moves out of the way of other taskbar tools like TrafficMonitor automatically."));

        AddRow(stack, CheckRow(_transparent,
            helper: "Show just the number, with no dark box behind it.",
            tip: "Blends the readout into the taskbar. Hovering works everywhere, but right-clicks need to land on the visible number or graph."));

        AddRow(stack, CheckRow(_autoUpdate,
            helper: "Checks quietly once a day; you can also check anytime from the right-click menu.",
            tip: "PingMeter asks GitHub once a day whether a newer version exists. Nothing about you or your PC is sent."));

        AddRow(stack, CheckRow(_eventLog,
            helper: "Timeouts, slow spells and hourly summaries — open via 'View connection log' in the menu.",
            tip: "Written to a daily text file. Great for checking \"was my internet unstable last night?\""));

        AddRow(stack, CheckRow(_csvLog,
            helper: "Spreadsheet-style data for graphing — about 3.5 MB per day.",
            tip: "One line per ping with the exact time and milliseconds — import into Excel or Google Sheets."));

        AddRow(stack, SettingRow("Delete logs older than", _retention, "days",
            helper: "Old log files are removed automatically to save disk space.",
            tip: "Applies to both the diary and the CSV files."));

        return stack;
    }

    private Control BuildTargetsBlock()
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };

        var title = new Label { Text = "Websites or servers to ping", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        block.Controls.Add(title);

        var grid = new TableLayoutPanel { ColumnCount = 2, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var add = MakeButton("Add", (_, _) => AddTarget());
        var remove = MakeButton("Remove", (_, _) =>
        {
            if (_targets.SelectedIndex >= 0)
                _targets.Items.RemoveAt(_targets.SelectedIndex);
        });
        var up = MakeButton("Up", (_, _) => MoveSelected(-1));
        var down = MakeButton("Down", (_, _) => MoveSelected(+1));

        _newTarget.KeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.Enter)
            {
                AddTarget();
                e.Handled = e.SuppressKeyPress = true;
            }
        };

        grid.Controls.Add(_newTarget, 0, 0);
        grid.Controls.Add(add, 1, 0);
        grid.Controls.Add(_targets, 0, 1);
        grid.SetRowSpan(_targets, 3);
        grid.Controls.Add(remove, 1, 1);
        grid.Controls.Add(up, 1, 2);
        grid.Controls.Add(down, 1, 3);
        block.Controls.Add(grid);

        var helper = MakeHelper("PingMeter keeps measuring how long these take to reply. Switch the active one from the right-click menu.");
        block.Controls.Add(helper);

        SetTip("Add any website (google.com) or IP address (1.1.1.1). Only one is pinged at a time — pick which from the right-click menu on the widget or tray icon.",
            title, _targets, _newTarget, helper);
        return block;
    }

    private Control BuildColorsBlock()
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };

        var title = new Label { Text = "Taskbar colors", AutoSize = true, Margin = new Padding(0, 0, 0, 4) };
        block.Controls.Add(title);

        block.Controls.Add(ColorRow(GoodColor, "Green — fast: below", _green, "ms",
            tip: "Replies faster than this are shown in green: a good connection."));
        block.Controls.Add(ColorRow(WarnColor, "Yellow — okay: below", _yellow, "ms",
            tip: "Replies between the green limit and this show yellow; anything slower shows red."));
        block.Controls.Add(ColorRow(BadColor, "Red — slow, or no reply", null, null,
            tip: "Everything above the yellow limit, plus lost pings (T/O)."));

        var helper = MakeHelper("The taskbar number changes color based on these limits.");
        block.Controls.Add(helper);
        SetTip("The taskbar number changes color based on these limits.", title, helper);
        return block;
    }

    private Control ColorRow(Color swatchColor, string text, NumericUpDown? numeric, string? unit, string tip)
    {
        var row = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(8, 2, 0, 2) };
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        row.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var swatch = new Panel
        {
            Size = new Size(14, 14),
            BackColor = swatchColor,
            BorderStyle = BorderStyle.FixedSingle,
            Anchor = AnchorStyles.Left,
            Margin = new Padding(0, 0, 6, 0),
        };
        var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left };
        row.Controls.Add(swatch, 0, 0);
        row.Controls.Add(label, 1, 0);
        if (numeric != null)
        {
            numeric.Anchor = AnchorStyles.Right;
            row.Controls.Add(numeric, 2, 0);
            row.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left }, 3, 0);
            SetTip(tip, label, numeric);
        }
        else
        {
            SetTip(tip, label);
        }
        return row;
    }

    // ------------------------------------------------------ row/help helpers

    private static Padding RowMargin => new(0, 0, 0, 14);

    private static TableLayoutPanel MakeStack() => new()
    {
        Dock = DockStyle.Top,
        ColumnCount = 1,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
    };

    private static void AddRow(TableLayoutPanel stack, Control row)
    {
        stack.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        stack.Controls.Add(row, 0, stack.RowStyles.Count - 1);
    }

    private Control SettingRow(string title, Control control, string? unit, string helper, string tip)
    {
        var panel = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleLabel = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 0, 8, 0) };
        panel.Controls.Add(titleLabel, 0, 0);
        control.Anchor = AnchorStyles.Right;
        panel.Controls.Add(control, 1, 0);
        if (unit != null)
            panel.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(4, 0, 0, 0) }, 2, 0);

        var helperLabel = MakeHelper(helper);
        panel.Controls.Add(helperLabel, 0, 1);
        panel.SetColumnSpan(helperLabel, 3);

        SetTip(tip, titleLabel, control, helperLabel);
        return panel;
    }

    private Control CheckRow(CheckBox box, string helper, string tip)
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };
        box.Margin = new Padding(0);
        panel.Controls.Add(box);
        var helperLabel = MakeHelper(helper);
        helperLabel.Margin = new Padding(18, 2, 0, 0); // align under the checkbox text
        panel.Controls.Add(helperLabel);
        SetTip(tip, box, helperLabel);
        return panel;
    }

    private static Label MakeHelper(string text) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(388, 0), // force wrapping inside the tab
        ForeColor = SystemColors.GrayText,
        Margin = new Padding(0, 3, 0, 0),
    };

    private void SetTip(string tip, params Control[] controls)
    {
        foreach (var control in controls)
            _help.SetToolTip(control, tip);
    }

    private static CheckBox MakeCheck(string text) => new() { Text = text, AutoSize = true };

    private static NumericUpDown MakeNumeric(decimal min, decimal max, decimal step, int decimals = 0) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        DecimalPlaces = decimals,
        Width = 82,
    };

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, Width = 82 };
        button.Click += onClick;
        return button;
    }

    // ------------------------------------------------------------ data flow

    private void AddTarget()
    {
        string host = _newTarget.Text.Trim();
        if (host.Length == 0)
            return;
        bool exists = _targets.Items.Cast<string>().Any(t => string.Equals(t, host, StringComparison.OrdinalIgnoreCase));
        if (!exists)
            _targets.Items.Add(host);
        _newTarget.Clear();
    }

    private void MoveSelected(int delta)
    {
        int index = _targets.SelectedIndex;
        int target = index + delta;
        if (index < 0 || target < 0 || target >= _targets.Items.Count)
            return;
        object item = _targets.Items[index];
        _targets.Items.RemoveAt(index);
        _targets.Items.Insert(target, item);
        _targets.SelectedIndex = target;
    }

    private void LoadFrom(AppConfig config)
    {
        _targets.Items.Clear();
        foreach (string target in config.Targets)
            _targets.Items.Add(target);
        _intervalSec.Value = ClampTo(_intervalSec, config.IntervalMs / 1000m);
        _timeoutSec.Value = ClampTo(_timeoutSec, config.TimeoutMs / 1000m);
        _window.Value = ClampTo(_window, config.StatsWindow);
        _green.Value = ClampTo(_green, config.GreenBelowMs);
        _yellow.Value = ClampTo(_yellow, config.YellowBelowMs);
        _sparkline.Checked = config.ShowSparkline;
        _transparent.Checked = config.TransparentBackground;
        _monitors.SelectedIndex = (int)config.Monitors;
        _offset.Value = ClampTo(_offset, config.HorizontalOffsetPx);
        _autostart.Checked = config.StartWithWindows;
        _autoUpdate.Checked = config.AutoCheckUpdates;
        _eventLog.Checked = config.EventLogEnabled;
        _csvLog.Checked = config.SampleCsvEnabled;
        _retention.Value = ClampTo(_retention, config.LogRetentionDays);
    }

    private static decimal ClampTo(NumericUpDown numeric, decimal value) =>
        Math.Clamp(value, numeric.Minimum, numeric.Maximum);

    private void Save()
    {
        var config = _working.Clone(); // keeps ActiveTarget and update-check state
        config.Targets = _targets.Items.Cast<string>().ToList();
        config.IntervalMs = (int)(_intervalSec.Value * 1000);
        config.TimeoutMs = (int)(_timeoutSec.Value * 1000);
        config.StatsWindow = (int)_window.Value;
        config.GreenBelowMs = (int)_green.Value;
        config.YellowBelowMs = (int)_yellow.Value;
        config.ShowSparkline = _sparkline.Checked;
        config.TransparentBackground = _transparent.Checked;
        config.Monitors = (MonitorSelection)_monitors.SelectedIndex;
        config.HorizontalOffsetPx = (int)_offset.Value;
        config.StartWithWindows = _autostart.Checked;
        config.AutoCheckUpdates = _autoUpdate.Checked;
        config.EventLogEnabled = _eventLog.Checked;
        config.SampleCsvEnabled = _csvLog.Checked;
        config.LogRetentionDays = (int)_retention.Value;
        config.Normalize();
        _working.CopyFrom(config);
        LoadFrom(_working); // reflect normalization back into the UI
        ConfigSaved?.Invoke(config);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _help.Dispose();
        base.Dispose(disposing);
    }
}
