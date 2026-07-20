using PingMeter.Config;

namespace PingMeter.Settings;

internal sealed class SettingsForm : Form
{
    private readonly AppConfig _working;

    private readonly ListBox _targets = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    private readonly TextBox _newTarget = new() { Dock = DockStyle.Fill };
    private readonly NumericUpDown _interval = MakeNumeric(250, 60_000, 250);
    private readonly NumericUpDown _timeout = MakeNumeric(100, 10_000, 100);
    private readonly NumericUpDown _window = MakeNumeric(10, 600, 10);
    private readonly NumericUpDown _green = MakeNumeric(1, 5_000, 5);
    private readonly NumericUpDown _yellow = MakeNumeric(1, 10_000, 5);
    private readonly CheckBox _sparkline = new() { Text = "Show sparkline graph", AutoSize = true };
    private readonly CheckBox _transparent = new() { Text = "Transparent background (text only)", AutoSize = true };
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Dock = DockStyle.Fill };
    private readonly CheckBox _autostart = new() { Text = "Start with Windows", AutoSize = true };

    public event Action<AppConfig>? ConfigSaved;

    public SettingsForm(AppConfig current)
    {
        _working = current.Clone();

        Text = "PingMeter Settings";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        ClientSize = new Size(400, 640);

        _monitors.Items.AddRange(["Primary taskbar only", "Secondary taskbar(s) only", "All taskbars"]);

        BuildLayout();
        LoadFrom(_working);
    }

    private void BuildLayout()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 5,
            Padding = new Padding(12),
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 196));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 122));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 152));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 96));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildTargetsGroup(), 0, 0);
        root.Controls.Add(BuildPingGroup(), 0, 1);
        root.Controls.Add(BuildDisplayGroup(), 0, 2);
        root.Controls.Add(BuildPlacementGroup(), 0, 3);
        root.Controls.Add(BuildButtons(), 0, 4);
        Controls.Add(root);
    }

    private GroupBox BuildTargetsGroup()
    {
        var group = MakeGroup("Ping targets (right-click the tray icon to switch)");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 5, Padding = new Padding(8) };
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

        _newTarget.PlaceholderText = "host name or IP, e.g. 1.1.1.1";
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
        grid.SetRowSpan(_targets, 4);
        grid.Controls.Add(remove, 1, 1);
        grid.Controls.Add(up, 1, 2);
        grid.Controls.Add(down, 1, 3);

        group.Height = 190;
        group.Controls.Add(grid);
        return group;
    }

    private GroupBox BuildPingGroup()
    {
        var group = MakeGroup("Ping");
        group.Controls.Add(MakeLabeledRows(
            ("Interval (ms)", _interval),
            ("Timeout (ms)", _timeout),
            ("Stats window (samples)", _window)));
        group.Height = 118;
        return group;
    }

    private GroupBox BuildDisplayGroup()
    {
        var group = MakeGroup("Display");
        var grid = MakeLabeledRows(
            ("Green below (ms)", _green),
            ("Yellow below (ms)", _yellow));
        grid.RowCount += 2;
        grid.Controls.Add(_sparkline, 0, 2);
        grid.SetColumnSpan(_sparkline, 2);
        grid.Controls.Add(_transparent, 0, 3);
        grid.SetColumnSpan(_transparent, 2);
        group.Controls.Add(grid);
        group.Height = 148;
        return group;
    }

    private GroupBox BuildPlacementGroup()
    {
        var group = MakeGroup("Placement");
        var grid = MakeLabeledRows(("Show on", _monitors));
        grid.RowCount += 1;
        grid.Controls.Add(_autostart, 0, 1);
        grid.SetColumnSpan(_autostart, 2);
        group.Controls.Add(grid);
        group.Height = 92;
        return group;
    }

    private Control BuildButtons()
    {
        var panel = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Fill,
            AutoSize = true,
            Margin = new Padding(0, 8, 0, 0),
        };
        var ok = MakeButton("OK", (_, _) =>
        {
            Save();
            Close();
        });
        var cancel = MakeButton("Cancel", (_, _) => Close());
        var apply = MakeButton("Apply", (_, _) => Save());
        panel.Controls.Add(ok);
        panel.Controls.Add(apply);
        panel.Controls.Add(cancel);
        AcceptButton = ok;
        CancelButton = cancel;
        return panel;
    }

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
        _interval.Value = config.IntervalMs;
        _timeout.Value = config.TimeoutMs;
        _window.Value = config.StatsWindow;
        _green.Value = config.GreenBelowMs;
        _yellow.Value = config.YellowBelowMs;
        _sparkline.Checked = config.ShowSparkline;
        _transparent.Checked = config.TransparentBackground;
        _monitors.SelectedIndex = (int)config.Monitors;
        _autostart.Checked = config.StartWithWindows;
    }

    private void Save()
    {
        var config = _working.Clone(); // keeps ActiveTarget
        config.Targets = _targets.Items.Cast<string>().ToList();
        config.IntervalMs = (int)_interval.Value;
        config.TimeoutMs = (int)_timeout.Value;
        config.StatsWindow = (int)_window.Value;
        config.GreenBelowMs = (int)_green.Value;
        config.YellowBelowMs = (int)_yellow.Value;
        config.ShowSparkline = _sparkline.Checked;
        config.TransparentBackground = _transparent.Checked;
        config.Monitors = (MonitorSelection)_monitors.SelectedIndex;
        config.StartWithWindows = _autostart.Checked;
        config.Normalize();
        _working.CopyFrom(config);
        LoadFrom(_working); // reflect normalization back into the UI
        ConfigSaved?.Invoke(config);
    }

    private static NumericUpDown MakeNumeric(int min, int max, int step) => new()
    {
        Minimum = min,
        Maximum = max,
        Increment = step,
        Width = 100,
    };

    private static Button MakeButton(string text, EventHandler onClick)
    {
        var button = new Button { Text = text, Width = 82 };
        button.Click += onClick;
        return button;
    }

    private static GroupBox MakeGroup(string title) => new()
    {
        Text = title,
        Dock = DockStyle.Fill,
        Margin = new Padding(0, 0, 0, 8),
    };

    private static TableLayoutPanel MakeLabeledRows(params (string Label, Control Control)[] rows)
    {
        var grid = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 2,
            RowCount = rows.Length,
            Padding = new Padding(8),
        };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 55));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 45));
        for (int i = 0; i < rows.Length; i++)
        {
            grid.Controls.Add(new Label
            {
                Text = rows[i].Label,
                AutoSize = true,
                Anchor = AnchorStyles.Left,
                TextAlign = ContentAlignment.MiddleLeft,
            }, 0, i);
            grid.Controls.Add(rows[i].Control, 1, i);
        }
        return grid;
    }
}
