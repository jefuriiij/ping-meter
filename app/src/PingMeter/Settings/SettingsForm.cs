using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PingMeter.Config;
using PingMeter.Network;
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
    private readonly Font _headerFont;

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
    private readonly CheckBox _showLoss = MakeCheck("Show packet loss %");
    private readonly CheckBox _transparent = MakeCheck("See-through background");
    private readonly CheckBox _autostart = MakeCheck("Start automatically when I turn on my PC");
    private readonly CheckBox _autoUpdate = MakeCheck("Tell me when a new version is available");
    private readonly CheckBox _eventLog = MakeCheck("Keep a diary of connection problems");
    private readonly CheckBox _csvLog = MakeCheck("Also record every single ping (CSV file)");
    private readonly ComboBox _monitors = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 170 };
    private sealed record PresetItem(string Label, string? Primary, string? Secondary, bool IsSaved, bool IsCustom);

    private static readonly PresetItem[] BuiltInDnsPresets =
    [
        new("Automatic (from your router)", null, null, false, false),
        new("Cloudflare (1.1.1.1) — fast, private", "1.1.1.1", "1.0.0.1", false, false),
        new("Google (8.8.8.8)", "8.8.8.8", "8.8.4.4", false, false),
        new("Quad9 (9.9.9.9) — blocks malware sites", "9.9.9.9", "149.112.112.112", false, false),
    ];

    /// <summary>Built-ins + the user's saved combinations + "Custom…", in dropdown order.</summary>
    private readonly List<PresetItem> _presetItems = [];

    private readonly Button _quickFix = MakeActionButton("Quick fix — clear DNS cache");
    private readonly Button _fullReset = MakeActionButton("Full reset — rebuild the connection");
    private readonly Label _dnsCurrent = new() { AutoSize = true, ForeColor = SystemColors.GrayText, Margin = new Padding(0, 0, 0, 6) };
    private readonly ComboBox _dnsPreset = new() { DropDownStyle = ComboBoxStyle.DropDownList, Width = 250 };
    private readonly TextBox _dnsPrimary = new() { Width = 110 };
    private readonly TextBox _dnsSecondary = new() { Width = 110, PlaceholderText = "optional" };
    private readonly Button _applyDns = MakeActionButton("Apply DNS");
    private readonly Button _savePreset = MakeActionButton("Save as preset…");
    private readonly Button _deletePreset = MakeActionButton("Delete preset");
    private readonly Label _repairStatus = new() { AutoSize = true, ForeColor = SystemColors.GrayText };
    private readonly ProgressBar _repairProgress = new()
    {
        Dock = DockStyle.Top,
        Height = 14,
        Visible = false,
        Margin = new Padding(0, 0, 0, 6),
    };
    private readonly TextBox _repairLog = new()
    {
        Multiline = true,
        ReadOnly = true,
        ScrollBars = ScrollBars.Vertical,
        Height = 150,
        Dock = DockStyle.Top,
        Margin = new Padding(0),
    };
    private TabControl? _tabs;

    public event Action<AppConfig>? ConfigSaved;

    /// <summary>Raised before a repair runs — the owner pauses pinging to keep the log clean.</summary>
    public event Action? RepairStarted;

    /// <summary>Raised after a repair (summary for the log, whether it was the full reset).</summary>
    public event Action<string, bool>? RepairCompleted;

    /// <summary>
    /// Raised when the saved DNS presets change. Persisted immediately by the owner so a
    /// saved preset survives closing the dialog with Cancel.
    /// </summary>
    public event Action<List<DnsPreset>>? DnsPresetsChanged;

    public SettingsForm(AppConfig current)
    {
        _working = current.Clone();
        _headerFont = new Font(Font, FontStyle.Bold);

        Text = $"PingMeter Settings v{UpdateChecker.CurrentVersion}";
        // Resizable and never taller than the screen: a winget moderator found the old
        // fixed 648px dialog running off the bottom of a small VM screen with no way to
        // resize it. Tab pages scroll (AutoScroll) and the button strip is docked to the
        // form, so the dialog stays usable at any height.
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = true;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoScaleMode = AutoScaleMode.Dpi;
        AutoScaleDimensions = new SizeF(96F, 96F);
        MinimumSize = new Size(460, 300);
        ClientSize = new Size(440, 648);

        _monitors.Items.AddRange(["Main screen only", "Second screen(s) only", "Every screen"]);

        BuildLayout();
        LoadFrom(_working);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        FitToWorkArea();
    }

    /// <summary>
    /// Shrink the dialog to fit the monitor it opened on (DPI scaling has already been
    /// applied by now) and pull it fully inside the work area, so the OK/Apply/Cancel
    /// strip can never sit below the screen edge on small displays.
    /// </summary>
    private void FitToWorkArea()
    {
        Rectangle work = Screen.FromControl(this).WorkingArea;
        int maxHeight = Math.Max(MinimumSize.Height, work.Height - 40);
        int maxWidth = Math.Max(MinimumSize.Width, work.Width - 40);

        var size = new Size(Math.Min(Width, maxWidth), Math.Min(Height, maxHeight));
        if (size != Size)
        {
            Size = size;
            // Re-center: CenterScreen positioned us using the pre-clamp height.
            Location = new Point(work.X + (work.Width - Width) / 2, work.Y + (work.Height - Height) / 2);
        }

        int x = Math.Min(Math.Max(Left, work.Left), work.Right - Width);
        int y = Math.Min(Math.Max(Top, work.Top), work.Bottom - Height);
        if (x != Left || y != Top)
            Location = new Point(x, y);
    }

    // ---------------------------------------------------------------- layout

    private void BuildLayout()
    {
        var tabs = new TabControl { Dock = DockStyle.Fill };
        _tabs = tabs;
        // Explicit SystemColors.Control instead of visual-style backgrounds: the dark color
        // mode remaps SystemColors, but visual-style tab bodies would stay light.
        var general = new TabPage("General") { Padding = new Padding(12), AutoScroll = true, BackColor = SystemColors.Control };
        var advanced = new TabPage("Advanced") { Padding = new Padding(12), AutoScroll = true, BackColor = SystemColors.Control };
        var tools = new TabPage("Network tools") { Padding = new Padding(12), AutoScroll = true, BackColor = SystemColors.Control };
        tabs.TabPages.Add(general);
        tabs.TabPages.Add(advanced);
        tabs.TabPages.Add(tools);

        general.Controls.Add(BuildGeneralStack());
        advanced.Controls.Add(BuildAdvancedStack());
        tools.Controls.Add(BuildNetworkToolsStack());

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

        AddRow(stack, SectionHeader("Websites or servers to ping", first: true));
        AddRow(stack, BuildTargetsBlock());

        AddRow(stack, SettingRow("Ping every", _intervalSec, "seconds",
            helper: "How often a ping is sent.",
            tip: "Example: 1.0 = one ping per second, like running 'ping -t' in a terminal. Lower = updates faster, with slightly more network chatter."));

        AddRow(stack, SectionHeader("Taskbar colors"));
        AddRow(stack, BuildColorsBlock());

        AddRow(stack, SectionHeader("Display"));
        AddRow(stack, CheckRow(_sparkline,
            helper: "A tiny bar graph of the recent pings next to the number.",
            tip: "Each bar is one ping — taller means slower. Red full-height bars are lost pings."));

        AddRow(stack, CheckRow(_showLoss,
            helper: "When pings go missing, a red percentage appears next to the number.",
            tip: "Packet loss = pings that never got an answer. The % covers the statistics period; when nothing is lost, nothing extra is shown. Hover the widget for lifetime totals."));

        AddRow(stack, SectionHeader("Startup"));
        AddRow(stack, CheckRow(_autostart,
            helper: "PingMeter appears in the taskbar every time Windows starts.",
            tip: "Adds PingMeter to your Windows startup apps. Untick to stop it starting by itself."));

        return stack;
    }

    private Control BuildAdvancedStack()
    {
        var stack = MakeStack();

        AddRow(stack, SectionHeader("Connection", first: true));
        AddRow(stack, SettingRow("Give up after", _timeoutSec, "seconds",
            helper: "No reply within this time counts as a lost ping — shown as red T/O.",
            tip: "If a reply takes longer than this, PingMeter stops waiting and counts that ping as lost."));

        AddRow(stack, SettingRow("Statistics period", _window, "pings",
            helper: "Min / avg / max and loss % in the hover tooltip use this many recent pings.",
            tip: "Example: 60 pings at one per second = the last minute of history."));

        AddRow(stack, SectionHeader("Placement"));
        AddRow(stack, SettingRow("Where to show", _monitors, null,
            helper: "Which taskbar(s) get the ping display.",
            tip: "'Every screen' needs Windows' own \"Show taskbar on all displays\" setting to be turned on."));

        AddRow(stack, SettingRow("Move left", _offset, "pixels",
            helper: "Nudge the display away from the clock.",
            tip: "Only needed if something overlaps — PingMeter already moves out of the way of other taskbar tools automatically."));

        AddRow(stack, CheckRow(_transparent,
            helper: "Show just the number, with no dark box behind it.",
            tip: "Blends the readout into the taskbar. Hovering works everywhere, but right-clicks need to land on the visible number or graph."));

        AddRow(stack, SectionHeader("Logging & updates"));
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

    private Control BuildNetworkToolsStack()
    {
        var stack = MakeStack();

        AddRow(stack, SectionHeader("Repair your connection", first: true));
        var intro = MakeHelper("When the internet acts up — connected but nothing loads — these clear Windows' network caches and rebuild the connection.");
        intro.Margin = new Padding(0, 0, 0, 14);
        AddRow(stack, intro);

        _quickFix.Click += async (_, _) => await RunQuickFixAsync();
        AddRow(stack, ActionRow(_quickFix,
            helper: "Fixes most \"website not found\" problems. Instant and safe — no admin prompt, no restart.",
            tip: "Runs ipconfig /flushdns: wipes the cached website addresses so Windows looks them up fresh."));

        _fullReset.Click += async (_, _) => await RunFullResetAsync();
        AddRow(stack, ActionRow(_fullReset,
            helper: "The full 5-step repair: clear DNS, new IP address, reset Winsock and TCP/IP. Windows will ask for permission, and a restart is needed afterwards.",
            tip: "Runs ipconfig /flushdns, /release, /renew, netsh winsock reset and netsh int ip reset — the classic fix for \"connected, but no internet\". Your connection drops for a few seconds while it runs."));

        AddRow(stack, SectionHeader("DNS server"));
        AddRow(stack, BuildDnsBlock());

        AddRow(stack, SectionHeader("Activity"));
        AddRow(stack, _repairProgress);
        AddRow(stack, _repairStatus);
        AddRow(stack, _repairLog);
        SetTip("A running record of what these tools did — every line is also written to the connection log.", _repairLog);

        return stack;
    }

    /// <summary>"✓/✗ Step name", with an explicit reminder on steps that only apply after a reboot.</summary>
    private static string StepLine(RepairStepResult step) =>
        $"{(step.Ok ? "✓" : "✗")} {step.Step}{(step.RequiresRestart ? " (requires restart)" : "")}";

    private void AppendRepairLog(string line)
    {
        _repairLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
    }

    private void ShowProgress(bool marquee)
    {
        if (marquee)
        {
            _repairProgress.Style = ProgressBarStyle.Marquee;
            _repairProgress.MarqueeAnimationSpeed = 30;
        }
        else
        {
            _repairProgress.Style = ProgressBarStyle.Continuous;
            _repairProgress.Value = 0;
        }
        _repairProgress.Visible = true;
    }

    private void HideProgress() => _repairProgress.Visible = false;

    private Control BuildDnsBlock()
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };

        block.Controls.Add(_dnsCurrent);

        _dnsPreset.SelectedIndexChanged += (_, _) => OnDnsPresetChanged();
        block.Controls.Add(_dnsPreset);

        var fields = new TableLayoutPanel { ColumnCount = 4, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0, 6, 0, 6) };
        for (int i = 0; i < 4; i++)
            fields.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        fields.Controls.Add(new Label { Text = "Main", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 4, 0) }, 0, 0);
        fields.Controls.Add(_dnsPrimary, 1, 0);
        fields.Controls.Add(new Label { Text = "Backup", AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(12, 4, 4, 0) }, 2, 0);
        fields.Controls.Add(_dnsSecondary, 3, 0);
        block.Controls.Add(fields);

        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = new Padding(0, 0, 0, 2) };
        _applyDns.Click += async (_, _) => await ApplyDnsAsync();
        _savePreset.Click += (_, _) => SaveCurrentAsPreset();
        _deletePreset.Click += (_, _) => DeleteSelectedPreset();
        _savePreset.Margin = _deletePreset.Margin = new Padding(8, 0, 0, 0);
        buttons.Controls.Add(_applyDns);
        buttons.Controls.Add(_savePreset);
        buttons.Controls.Add(_deletePreset);
        block.Controls.Add(buttons);

        var helper = MakeHelper("DNS is the phone book that turns website names into addresses. Pick a preset or enter your own — you can save a combination for later, and switching back to Automatic undoes everything.");
        block.Controls.Add(helper);
        SetTip("Changes the IPv4 DNS of your active network adapter. Windows asks for permission. A different DNS can make browsing faster or block malware sites — and \"Automatic\" always restores what your router provides.",
            _dnsPreset, _applyDns, helper, _dnsCurrent);
        SetTip("Save the Main/Backup pair above under a name of your choice, so you can pick it from the list next time.", _savePreset);
        SetTip("Remove the selected saved combination from the list. Built-in presets can't be deleted.", _deletePreset);

        RebuildPresetList(selectIndex: 0);
        RefreshDnsCurrent();
        return block;
    }

    /// <summary>Rebuild the dropdown from built-ins + saved presets + "Custom…".</summary>
    private void RebuildPresetList(int selectIndex)
    {
        _presetItems.Clear();
        _presetItems.AddRange(BuiltInDnsPresets);
        foreach (var saved in _working.DnsPresets)
        {
            string label = saved.Secondary is null
                ? $"{saved.Name} ({saved.Primary})"
                : $"{saved.Name} ({saved.Primary} + {saved.Secondary})";
            _presetItems.Add(new PresetItem(label, saved.Primary, saved.Secondary, IsSaved: true, IsCustom: false));
        }
        _presetItems.Add(new PresetItem("Custom…", "", "", IsSaved: false, IsCustom: true));

        _dnsPreset.Items.Clear();
        foreach (var item in _presetItems)
            _dnsPreset.Items.Add(item.Label);
        _dnsPreset.SelectedIndex = Math.Clamp(selectIndex, 0, _presetItems.Count - 1);
    }

    private PresetItem SelectedPreset => _presetItems[Math.Max(0, _dnsPreset.SelectedIndex)];

    private void SaveCurrentAsPreset()
    {
        string primary = _dnsPrimary.Text.Trim();
        string? secondary = _dnsSecondary.Text.Trim();
        if (secondary.Length == 0)
            secondary = null;
        if (!IsValidIPv4(primary) || (secondary != null && !IsValidIPv4(secondary)))
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = "Nothing to save yet",
                Text = "Enter a Main address (and optionally a Backup) first — a DNS address looks like 1.1.1.1.",
                Icon = TaskDialogIcon.Warning,
            });
            return;
        }

        string suggested = secondary is null ? primary : $"{primary} + {secondary}";
        string? name = PromptDialog.Show(this, "Save DNS preset", "Name this combination:", suggested);
        if (string.IsNullOrWhiteSpace(name))
            return;
        name = name.Trim();

        var existing = _working.DnsPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            var overwrite = new TaskDialogButton("Overwrite");
            var page = new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = $"\"{name}\" already exists",
                Text = "Replace the saved combination with the addresses above?",
                Icon = TaskDialogIcon.Warning,
                Buttons = { overwrite, TaskDialogButton.Cancel },
            };
            if (TaskDialog.ShowDialog(this, page) != overwrite)
                return;
            existing.Primary = primary;
            existing.Secondary = secondary;
        }
        else
        {
            _working.DnsPresets.Add(new DnsPreset { Name = name, Primary = primary, Secondary = secondary });
        }

        _working.Normalize();
        DnsPresetsChanged?.Invoke(_working.DnsPresets); // persist now, so Cancel can't lose it
        int index = BuiltInDnsPresets.Length + _working.DnsPresets.FindIndex(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        RebuildPresetList(index);
    }

    private void DeleteSelectedPreset()
    {
        var selected = SelectedPreset;
        if (!selected.IsSaved)
            return;
        int savedIndex = _dnsPreset.SelectedIndex - BuiltInDnsPresets.Length;
        if (savedIndex < 0 || savedIndex >= _working.DnsPresets.Count)
            return;

        var remove = new TaskDialogButton("Delete");
        var page = new TaskDialogPage
        {
            Caption = "PingMeter",
            Heading = $"Delete \"{_working.DnsPresets[savedIndex].Name}\"?",
            Text = "This only removes it from the list — your current DNS settings don't change.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { remove, TaskDialogButton.Cancel },
        };
        if (TaskDialog.ShowDialog(this, page) != remove)
            return;

        _working.DnsPresets.RemoveAt(savedIndex);
        DnsPresetsChanged?.Invoke(_working.DnsPresets);
        RebuildPresetList(0);
    }

    private void RefreshDnsCurrent()
    {
        var status = DnsInfo.GetActive();
        _dnsCurrent.Text = status is null
            ? "Current: unknown — no active network found"
            : $"Current: {(status.Servers.Count > 0 ? string.Join(", ", status.Servers) : "none")} ({(status.IsManual ? "manual" : "automatic")}) on {status.AdapterName}";
    }

    private void OnDnsPresetChanged()
    {
        if (_dnsPreset.SelectedIndex < 0)
            return;
        var item = SelectedPreset;
        bool automatic = item.Primary is null;
        if (!item.IsCustom)
        {
            _dnsPrimary.Text = item.Primary ?? "";
            _dnsSecondary.Text = item.Secondary ?? "";
        }
        _dnsPrimary.ReadOnly = _dnsSecondary.ReadOnly = !item.IsCustom;
        _dnsPrimary.Enabled = _dnsSecondary.Enabled = !automatic;
        _savePreset.Enabled = item.IsCustom;
        _deletePreset.Enabled = item.IsSaved;
    }

    private async Task ApplyDnsAsync()
    {
        var status = DnsInfo.GetActive();
        if (status is null)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = "No active network",
                Text = "Couldn't find an active network adapter — are you connected?",
                Icon = TaskDialogIcon.Warning,
            });
            return;
        }

        bool automatic = SelectedPreset.Primary is null;
        string? primary = null, secondary = null;
        if (!automatic)
        {
            primary = _dnsPrimary.Text.Trim();
            secondary = _dnsSecondary.Text.Trim();
            if (secondary.Length == 0)
                secondary = null;
            if (!IsValidIPv4(primary) || (secondary != null && !IsValidIPv4(secondary)))
            {
                TaskDialog.ShowDialog(this, new TaskDialogPage
                {
                    Caption = "PingMeter",
                    Heading = "That doesn't look like an IPv4 address",
                    Text = "A DNS address looks like 1.1.1.1 — four numbers (0–255) separated by dots.",
                    Icon = TaskDialogIcon.Error,
                });
                return;
            }
        }

        SetRepairBusy(true, "Working — answer the Windows permission prompt…");
        ShowProgress(marquee: true);
        AppendRepairLog(automatic
            ? $"Switching DNS to automatic on {status.AdapterName}…"
            : $"Setting DNS to {primary}{(secondary != null ? $" / {secondary}" : "")} on {status.AdapterName}…");
        RepairStarted?.Invoke();

        // No DoH controls in this dialog: pass null so existing encryption settings survive.
        var result = await NetworkRepair.RunSetDnsAsync(new DnsRequest(
            status.InterfaceIndex,
            status.AdapterId,
            automatic,
            primary is null ? null : new DnsServerRequest(primary, null, null),
            secondary is null ? null : new DnsServerRequest(secondary, null, null)));

        string logSummary = result.Outcome switch
        {
            RepairOutcome.Cancelled => "DNS change cancelled (no admin permission)",
            RepairOutcome.Failed => $"DNS change failed: {result.Error}",
            RepairOutcome.PartialFailure => "DNS change finished with warnings",
            _ => automatic ? "DNS switched to automatic" : $"DNS set to {primary}{(secondary != null ? $" / {secondary}" : "")}",
        };
        RepairCompleted?.Invoke(logSummary, false);
        if (IsDisposed)
            return;
        foreach (var step in result.Steps)
            AppendRepairLog(StepLine(step));
        AppendRepairLog(logSummary);
        HideProgress();
        SetRepairBusy(false, "");
        RefreshDnsCurrent();

        switch (result.Outcome)
        {
            case RepairOutcome.Cancelled:
                TaskDialog.ShowDialog(this, new TaskDialogPage
                {
                    Caption = "PingMeter",
                    Heading = "DNS change cancelled",
                    Text = "Windows permission was not granted — nothing was changed.",
                    Icon = TaskDialogIcon.Information,
                });
                break;
            case RepairOutcome.Failed:
                TaskDialog.ShowDialog(this, new TaskDialogPage
                {
                    Caption = "PingMeter",
                    Heading = "DNS change failed",
                    Text = result.Error,
                    Icon = TaskDialogIcon.Error,
                });
                break;
            default:
                TaskDialog.ShowDialog(this, new TaskDialogPage
                {
                    Caption = "PingMeter",
                    Heading = "DNS updated",
                    Text = _dnsCurrent.Text + (result.Outcome == RepairOutcome.PartialFailure
                        ? "\n\nSome steps reported warnings — see the Activity log."
                        : ""),
                    Icon = TaskDialogIcon.ShieldSuccessGreenBar,
                });
                break;
        }
    }

    private static bool IsValidIPv4(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    private Control ActionRow(Button button, string helper, string tip)
    {
        var panel = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };
        button.Margin = new Padding(0);
        panel.Controls.Add(button);
        var helperLabel = MakeHelper(helper);
        panel.Controls.Add(helperLabel);
        SetTip(tip, button, helperLabel);
        return panel;
    }

    private async Task RunQuickFixAsync()
    {
        SetRepairBusy(true, "Working…");
        ShowProgress(marquee: true);
        AppendRepairLog("Quick fix — clearing DNS cache…");
        RepairStarted?.Invoke();
        var result = await NetworkRepair.RunQuickFixAsync();
        bool ok = result.Outcome == RepairOutcome.Success;
        RepairCompleted?.Invoke(ok ? "quick fix: DNS cache cleared" : $"quick fix failed: {result.Error}", false);
        if (IsDisposed)
            return;
        AppendRepairLog(ok ? "✓ DNS cache cleared" : $"✗ Failed: {result.Error}");
        HideProgress();
        SetRepairBusy(false, "");
        TaskDialog.ShowDialog(this, new TaskDialogPage
        {
            Caption = "PingMeter",
            Heading = ok ? "DNS cache cleared" : "Quick fix failed",
            Text = ok
                ? "Cached website addresses were wiped — if pages were failing to load, try again now.\n\nStill broken? Run the full reset below."
                : result.Error,
            Icon = ok ? TaskDialogIcon.Information : TaskDialogIcon.Error,
        });
    }

    private async Task RunFullResetAsync()
    {
        var proceed = new TaskDialogButton("Reset now");
        var confirm = new TaskDialogPage
        {
            Caption = "PingMeter",
            Heading = "Reset Windows networking?",
            Text = "This runs the full 5-step repair: clear DNS, drop and renew your IP address, and reset Winsock and TCP/IP.\n\n" +
                   "Your connection will drop for a few seconds, Windows will ask for permission, and a restart is needed to finish.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { proceed, TaskDialogButton.Cancel },
        };
        if (TaskDialog.ShowDialog(this, confirm) != proceed)
            return;

        SetRepairBusy(true, "Working — answer the Windows permission prompt…");
        ShowProgress(marquee: true);
        AppendRepairLog("Full reset started");
        AppendRepairLog("Waiting for Windows permission (UAC)…");
        RepairStarted?.Invoke();

        var progress = new Progress<RepairProgress>(p =>
        {
            if (IsDisposed)
                return;
            if (_repairProgress.Style != ProgressBarStyle.Continuous)
                _repairProgress.Style = ProgressBarStyle.Continuous;
            _repairProgress.Value = Math.Min(100, p.Completed * 100 / Math.Max(1, p.Total));
            if (p.LastResult is { } step)
                AppendRepairLog(StepLine(step));
            _repairStatus.Text = p.CurrentStep is { } next
                ? $"Step {Math.Min(p.Completed + 1, p.Total)} of {p.Total} — {next}…"
                : "Finishing…";
        });
        var result = await NetworkRepair.RunFullResetAsync(progress);

        int okCount = result.Steps.Count(s => s.Ok);
        string logSummary = result.Outcome switch
        {
            RepairOutcome.Cancelled => "full reset cancelled (no admin permission)",
            RepairOutcome.Failed => $"full reset failed: {result.Error}",
            _ => $"full network reset: {okCount}/{result.Steps.Count} steps ok" + (result.RestartNeeded ? ", restart pending" : ""),
        };
        RepairCompleted?.Invoke(logSummary, true);
        if (IsDisposed)
            return;
        AppendRepairLog(result.Outcome switch
        {
            RepairOutcome.Cancelled => "Cancelled — Windows permission was not granted",
            RepairOutcome.Failed => $"✗ Failed: {result.Error}",
            _ => $"Done — {okCount}/{result.Steps.Count} steps ok" + (result.RestartNeeded ? ", restart required" : ""),
        });
        HideProgress();
        SetRepairBusy(false, "");

        if (result.Outcome is RepairOutcome.Cancelled or RepairOutcome.Failed)
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = result.Outcome == RepairOutcome.Cancelled ? "Reset cancelled" : "Reset failed",
                Text = result.Outcome == RepairOutcome.Cancelled
                    ? "Windows permission was not granted — nothing was changed."
                    : result.Error,
                Icon = result.Outcome == RepairOutcome.Cancelled ? TaskDialogIcon.Information : TaskDialogIcon.Error,
            });
            return;
        }

        string details = string.Join("\n", result.Steps.Select(StepLine));
        if (result.RestartNeeded)
        {
            var restartNow = new TaskDialogButton("Restart now");
            var restartLater = new TaskDialogButton("Restart later");
            var page = new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = result.Outcome == RepairOutcome.Success ? "Network reset complete" : "Network reset finished with warnings",
                Text = details + "\n\nA restart is required to finish the reset. Save your work first.",
                Icon = result.Outcome == RepairOutcome.Success ? TaskDialogIcon.ShieldSuccessGreenBar : TaskDialogIcon.Warning,
                Buttons = { restartNow, restartLater },
            };
            if (TaskDialog.ShowDialog(this, page) == restartNow)
                StartRestartCountdown();
        }
        else
        {
            TaskDialog.ShowDialog(this, new TaskDialogPage
            {
                Caption = "PingMeter",
                Heading = "Network reset finished with warnings",
                Text = details + "\n\nThe steps that require a restart didn't succeed, so no restart is needed. You can try again, or run the commands manually as administrator.",
                Icon = TaskDialogIcon.Warning,
            });
        }
    }

    /// <summary>
    /// Schedule the restart, then show a countdown dialog with a single big
    /// "Cancel restart" button — no terminal commands, the app aborts it for you.
    /// </summary>
    private void StartRestartCountdown()
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe",
                "/r /t 10 /c \"Restarting to finish PingMeter's network reset\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch
        {
            AppendRepairLog("✗ Couldn't schedule the restart — please restart your PC manually");
            return;
        }
        AppendRepairLog("Restart scheduled (10 seconds)");

        var cancelRestart = new TaskDialogButton("Cancel restart");
        var page = new TaskDialogPage
        {
            Caption = "PingMeter",
            Heading = "Restarting in 10 seconds…",
            Text = "Save your work — the computer is about to restart.\nNot ready? Click \"Cancel restart\".",
            Icon = TaskDialogIcon.Warning,
            Buttons = { cancelRestart },
            AllowCancel = true, // closing the dialog counts as cancelling, the safe direction
        };

        int remaining = 10;
        using var countdown = new System.Windows.Forms.Timer { Interval = 1000 };
        countdown.Tick += (_, _) =>
        {
            remaining--;
            page.Heading = remaining > 0 ? $"Restarting in {remaining} seconds…" : "Restarting now…";
        };
        countdown.Start();
        TaskDialogButton chosen = TaskDialog.ShowDialog(this, page);
        countdown.Stop();

        if (chosen == cancelRestart || chosen == TaskDialogButton.Cancel)
        {
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/a")
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                AppendRepairLog("Restart cancelled — remember to restart later to finish the reset");
            }
            catch
            {
                AppendRepairLog("✗ Couldn't cancel the restart");
            }
        }
    }

    private void SetRepairBusy(bool busy, string status)
    {
        _quickFix.Enabled = !busy;
        _fullReset.Enabled = !busy;
        _applyDns.Enabled = !busy;
        _savePreset.Enabled = !busy && SelectedPreset.IsCustom;
        _deletePreset.Enabled = !busy && SelectedPreset.IsSaved;
        _repairStatus.Text = status;
    }

    public void SelectTab(int index)
    {
        if (_tabs != null && index >= 0 && index < _tabs.TabPages.Count)
            _tabs.SelectedIndex = index;
    }

    private static Button MakeActionButton(string text) => new()
    {
        Text = text,
        AutoSize = true,
        Padding = new Padding(10, 4, 10, 4),
    };

    private Control BuildTargetsBlock()
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };

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
            _targets, _newTarget, helper);
        return block;
    }

    private Control BuildColorsBlock()
    {
        var block = new TableLayoutPanel { ColumnCount = 1, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };

        block.Controls.Add(ColorRow(GoodColor, "Green — fast: below", _green, "ms",
            tip: "Replies faster than this are shown in green: a good connection."));
        block.Controls.Add(ColorRow(WarnColor, "Yellow — okay: below", _yellow, "ms",
            tip: "Replies between the green limit and this show yellow; anything slower shows red."));
        block.Controls.Add(ColorRow(BadColor, "Red — slow, or no reply", null, null,
            tip: "Everything above the yellow limit, plus lost pings (T/O)."));

        var helper = MakeHelper("The taskbar number changes color based on these limits.");
        block.Controls.Add(helper);
        SetTip("The taskbar number changes color based on these limits.", helper);
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
            Margin = new Padding(0, 4, 6, 0),
        };
        var label = new Label { Text = text, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 0, 0) };
        row.Controls.Add(swatch, 0, 0);
        row.Controls.Add(label, 1, 0);
        if (numeric != null)
        {
            // unit then field, so the inputs align with the rest of the tab
            row.Controls.Add(new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 6, 0) }, 2, 0);
            numeric.Anchor = AnchorStyles.Right;
            row.Controls.Add(numeric, 3, 0);
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

    /// <summary>
    /// One setting row: "Title ......... unit [field]". The input is the last column and
    /// anchored right, so every field on a tab lines up in a single column regardless of
    /// how wide its unit label is.
    /// </summary>
    private Control SettingRow(string title, Control control, string? unit, string helper, string tip)
    {
        var panel = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Dock = DockStyle.Top, Margin = RowMargin };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

        var titleLabel = new Label { Text = title, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 4, 8, 0) };
        panel.Controls.Add(titleLabel, 0, 0);

        Label? unitLabel = null;
        if (unit != null)
        {
            unitLabel = new Label { Text = unit, AutoSize = true, Anchor = AnchorStyles.Right, Margin = new Padding(0, 4, 6, 0) };
            panel.Controls.Add(unitLabel, 1, 0);
        }
        control.Anchor = AnchorStyles.Right;
        panel.Controls.Add(control, 2, 0);

        var helperLabel = MakeHelper(helper);
        panel.Controls.Add(helperLabel, 0, 1);
        panel.SetColumnSpan(helperLabel, 3);

        SetTip(tip, titleLabel, control, helperLabel);
        if (unitLabel != null)
            SetTip(tip, unitLabel);
        return panel;
    }

    /// <summary>Bold section title with a hairline rule under it, used on every tab.</summary>
    private Control SectionHeader(string title, bool first = false)
    {
        var panel = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Top,
            Margin = new Padding(0, first ? 0 : 10, 0, 8),
        };
        panel.Controls.Add(new Label
        {
            Text = title,
            AutoSize = true,
            Font = _headerFont,
            UseMnemonic = false, // otherwise "Logging & updates" renders as "Logging  updates"
            Margin = new Padding(0, 0, 0, 3),
        });
        panel.Controls.Add(new Panel
        {
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = SystemColors.ControlDark,
            Margin = new Padding(0),
        });
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
        UseMnemonic = false,            // "&" is literal text here, not a keyboard shortcut
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
        _showLoss.Checked = config.ShowLossOnWidget;
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
        config.ShowLossOnWidget = _showLoss.Checked;
        config.TransparentBackground = _transparent.Checked;
        config.Monitors = (MonitorSelection)_monitors.SelectedIndex;
        config.HorizontalOffsetPx = (int)_offset.Value;
        config.StartWithWindows = _autostart.Checked;
        config.AutoCheckUpdates = _autoUpdate.Checked;
        config.EventLogEnabled = _eventLog.Checked;
        config.SampleCsvEnabled = _csvLog.Checked;
        config.LogRetentionDays = (int)_retention.Value;
        config.DnsPresets = _working.DnsPresets; // saved presets persist independently of OK/Apply
        config.Normalize();
        _working.CopyFrom(config);
        LoadFrom(_working); // reflect normalization back into the UI
        ConfigSaved?.Invoke(config);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _help.Dispose();
            _headerFont.Dispose();
        }
        base.Dispose(disposing);
    }

    /// <summary>Small modal text prompt — WinForms has no built-in InputBox.</summary>
    private sealed class PromptDialog : Form
    {
        private readonly TextBox _input = new() { Dock = DockStyle.Fill };

        private PromptDialog(string title, string question, string initial)
        {
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            AutoScaleMode = AutoScaleMode.Dpi;
            ClientSize = new Size(340, 116);
            _input.Text = initial;
            _input.SelectAll();

            var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Padding = new Padding(12) };
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            layout.Controls.Add(new Label { Text = question, AutoSize = true, Margin = new Padding(0, 0, 0, 6) }, 0, 0);
            layout.Controls.Add(_input, 0, 1);

            var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Fill, AutoSize = true, Margin = new Padding(0, 10, 0, 0) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 82 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 82 };
            buttons.Controls.Add(ok);
            buttons.Controls.Add(cancel);
            layout.Controls.Add(buttons, 0, 2);

            Controls.Add(layout);
            AcceptButton = ok;
            CancelButton = cancel;
        }

        /// <summary>Returns the entered text, or null if cancelled or left empty.</summary>
        public static string? Show(IWin32Window owner, string title, string question, string initial)
        {
            using var dialog = new PromptDialog(title, question, initial);
            return dialog.ShowDialog(owner) == DialogResult.OK && dialog._input.Text.Trim().Length > 0
                ? dialog._input.Text.Trim()
                : null;
        }
    }
}
