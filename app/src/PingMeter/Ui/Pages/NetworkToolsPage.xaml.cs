using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Threading;
using PingMeter.Config;
using PingMeter.Network;
using Wpf.Ui.Controls;

namespace PingMeter.Ui.Pages;

/// <summary>
/// WPF port of the Network tools tab: connection repair and IPv4 DNS switching.
/// Results appear in an InfoBar and the activity log rather than modal popups; only the
/// destructive full reset asks for confirmation.
/// </summary>
public partial class NetworkToolsPage : System.Windows.Controls.UserControl
{
    private sealed record PresetItem(string Label, string? Primary, string? Secondary, bool IsSaved, bool IsCustom);

    private static readonly PresetItem[] BuiltInPresets =
    [
        new("Automatic (from your router)", null, null, false, false),
        new("Cloudflare (1.1.1.1) — fast, private", "1.1.1.1", "1.0.0.1", false, false),
        new("Google (8.8.8.8)", "8.8.8.8", "8.8.4.4", false, false),
        new("Quad9 (9.9.9.9) — blocks malware sites", "9.9.9.9", "149.112.112.112", false, false),
    ];

    private readonly List<PresetItem> _presets = [];
    private DispatcherTimer? _restartCountdown;
    private int _restartSecondsLeft;

    /// <summary>Raised before a repair runs so the owner can pause pinging.</summary>
    public event Action? RepairStarted;

    /// <summary>Raised after a repair with a one-line summary for the connection log.</summary>
    public event Action<string, bool>? RepairCompleted;

    private SettingsViewModel? ViewModel => DataContext as SettingsViewModel;

    public NetworkToolsPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RebuildPresets(0);
            RefreshDnsCurrent();
        };
    }

    // ------------------------------------------------------------- repairs

    private async void OnQuickFix(object sender, RoutedEventArgs e)
    {
        SetBusy(true, "Working…", indeterminate: true);
        Log("Quick fix — clearing DNS cache…");
        RepairStarted?.Invoke();

        var result = await NetworkRepair.RunQuickFixAsync();
        bool ok = result.Outcome == RepairOutcome.Success;

        Log(ok ? "✓ DNS cache cleared" : $"✗ Failed: {result.Error}");
        RepairCompleted?.Invoke(ok ? "quick fix: DNS cache cleared" : $"quick fix failed: {result.Error}", false);
        SetBusy(false, "");
        ShowInfo(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
            ok ? "DNS cache cleared" : "Quick fix failed",
            ok ? "If pages were failing to load, try again now." : result.Error ?? "");
    }

    private async void OnFullReset(object sender, RoutedEventArgs e)
    {
        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = "Reset Windows networking?",
            Content = "This runs the full 5-step repair: clear DNS, drop and renew your IP address, "
                      + "and reset Winsock and TCP/IP.\n\nYour connection will drop for a few seconds, "
                      + "Windows will ask for permission, and a restart is needed to finish.",
            PrimaryButtonText = "Reset now",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        SetBusy(true, "Working — answer the Windows permission prompt…", indeterminate: true);
        Log("Full reset started");
        Log("Waiting for Windows permission (UAC)…");
        RepairStarted?.Invoke();

        var progress = new Progress<RepairProgress>(p =>
        {
            Progress.IsIndeterminate = false;
            Progress.Value = Math.Min(100, p.Completed * 100d / Math.Max(1, p.Total));
            if (p.LastResult is { } step)
                Log(StepLine(step));
            StatusText.Text = p.CurrentStep is { } next
                ? $"Step {Math.Min(p.Completed + 1, p.Total)} of {p.Total} — {next}…"
                : "Finishing…";
        });
        var result = await NetworkRepair.RunFullResetAsync(progress);

        int okCount = result.Steps.Count(s => s.Ok);
        string summary = result.Outcome switch
        {
            RepairOutcome.Cancelled => "full reset cancelled (no admin permission)",
            RepairOutcome.Failed => $"full reset failed: {result.Error}",
            _ => $"full network reset: {okCount}/{result.Steps.Count} steps ok"
                 + (result.RestartNeeded ? ", restart pending" : ""),
        };
        Log(summary);
        RepairCompleted?.Invoke(summary, true);
        SetBusy(false, "");

        switch (result.Outcome)
        {
            case RepairOutcome.Cancelled:
                ShowInfo(InfoBarSeverity.Informational, "Reset cancelled", "Windows permission was not granted — nothing was changed.");
                break;
            case RepairOutcome.Failed:
                ShowInfo(InfoBarSeverity.Error, "Reset failed", result.Error ?? "");
                break;
            default:
                if (result.RestartNeeded)
                {
                    RestartBar.Message = "The Winsock and TCP/IP resets only take effect after a restart. Save your work first.";
                    RestartBar.IsOpen = true;
                }
                else
                {
                    ShowInfo(InfoBarSeverity.Warning, "Finished with warnings",
                        "The steps that require a restart didn't succeed, so no restart is needed.");
                }
                break;
        }
    }

    private static string StepLine(RepairStepResult step) =>
        $"{(step.Ok ? "✓" : "✗")} {step.Step}{(step.RequiresRestart ? " (requires restart)" : "")}";

    // ------------------------------------------------------------- restart

    private void OnRestartNow(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("shutdown.exe",
                "/r /t 15 /c \"Restarting to finish PingMeter's network reset\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Exception ex)
        {
            Log($"✗ Couldn't schedule the restart: {ex.Message}");
            ShowInfo(InfoBarSeverity.Error, "Couldn't restart", "Please restart your PC manually.");
            return;
        }

        Log("Restart scheduled (15 seconds)");
        _restartSecondsLeft = 15;
        RestartNowButton.Visibility = Visibility.Collapsed;
        RestartCancelButton.Content = "Cancel restart";
        RestartBar.Title = "Restarting in 15 seconds…";
        RestartBar.Message = "Save your work. Not ready? Cancel the restart.";

        _restartCountdown = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _restartCountdown.Tick += (_, _) =>
        {
            _restartSecondsLeft--;
            RestartBar.Title = _restartSecondsLeft > 0
                ? $"Restarting in {_restartSecondsLeft} seconds…"
                : "Restarting now…";
            if (_restartSecondsLeft <= 0)
                _restartCountdown?.Stop();
        };
        _restartCountdown.Start();
    }

    /// <summary>Also serves as "Cancel restart" once a restart has been scheduled.</summary>
    private void OnRestartLater(object sender, RoutedEventArgs e)
    {
        if (_restartCountdown is not null)
        {
            _restartCountdown.Stop();
            _restartCountdown = null;
            try
            {
                Process.Start(new ProcessStartInfo("shutdown.exe", "/a") { UseShellExecute = false, CreateNoWindow = true });
                Log("Restart cancelled — remember to restart later to finish the reset");
            }
            catch
            {
                Log("✗ Couldn't cancel the restart");
            }
        }
        RestartBar.IsOpen = false;
        RestartNowButton.Visibility = Visibility.Visible;
        RestartCancelButton.Content = "Later";
        RestartBar.Title = "Restart required";
    }

    // ----------------------------------------------------------------- DNS

    private void RefreshDnsCurrent()
    {
        var status = DnsInfo.GetActive();
        DnsCurrent.Text = status is null
            ? "Current: unknown — no active network found"
            : $"Current: {(status.Servers.Count > 0 ? string.Join(", ", status.Servers) : "none")} "
              + $"({(status.IsManual ? "manual" : "automatic")}) on {status.AdapterName}";
    }

    private void RebuildPresets(int selectIndex)
    {
        _presets.Clear();
        _presets.AddRange(BuiltInPresets);
        foreach (var saved in ViewModel?.DnsPresets ?? [])
        {
            string label = saved.Secondary is null
                ? $"{saved.Name} ({saved.Primary})"
                : $"{saved.Name} ({saved.Primary} + {saved.Secondary})";
            _presets.Add(new PresetItem(label, saved.Primary, saved.Secondary, IsSaved: true, IsCustom: false));
        }
        _presets.Add(new PresetItem("Custom…", "", "", IsSaved: false, IsCustom: true));

        PresetBox.ItemsSource = _presets.Select(p => p.Label).ToList();
        PresetBox.SelectedIndex = Math.Clamp(selectIndex, 0, _presets.Count - 1);
    }

    private PresetItem Selected => _presets[Math.Max(0, PresetBox.SelectedIndex)];

    private void OnPresetChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (PresetBox.SelectedIndex < 0 || _presets.Count == 0)
            return;
        var item = Selected;
        bool automatic = item.Primary is null;
        if (!item.IsCustom)
        {
            DnsPrimary.Text = item.Primary ?? "";
            DnsSecondary.Text = item.Secondary ?? "";
        }
        DnsPrimary.IsReadOnly = DnsSecondary.IsReadOnly = !item.IsCustom;
        DnsPrimary.IsEnabled = DnsSecondary.IsEnabled = !automatic;
        SavePresetButton.IsEnabled = item.IsCustom;
        DeletePresetButton.IsEnabled = item.IsSaved;
    }

    private async void OnApplyDns(object sender, RoutedEventArgs e)
    {
        var status = DnsInfo.GetActive();
        if (status is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No active network", "Couldn't find an active network adapter — are you connected?");
            return;
        }

        bool automatic = Selected.Primary is null;
        string? primary = null, secondary = null;
        if (!automatic)
        {
            primary = DnsPrimary.Text.Trim();
            secondary = DnsSecondary.Text.Trim();
            if (secondary.Length == 0)
                secondary = null;
            if (!IsIPv4(primary) || (secondary != null && !IsIPv4(secondary)))
            {
                ShowInfo(InfoBarSeverity.Error, "That doesn't look like an IPv4 address",
                    "A DNS address looks like 1.1.1.1 — four numbers (0–255) separated by dots.");
                return;
            }
        }

        SetBusy(true, "Working — answer the Windows permission prompt…", indeterminate: true);
        Log(automatic
            ? $"Switching DNS to automatic on {status.AdapterName}…"
            : $"Setting DNS to {primary}{(secondary != null ? $" / {secondary}" : "")} on {status.AdapterName}…");
        RepairStarted?.Invoke();

        var result = await NetworkRepair.RunSetDnsAsync(status.InterfaceIndex, primary, secondary);
        string summary = result.Outcome switch
        {
            RepairOutcome.Cancelled => "DNS change cancelled (no admin permission)",
            RepairOutcome.Failed => $"DNS change failed: {result.Error}",
            RepairOutcome.PartialFailure => "DNS change finished with warnings",
            _ => automatic ? "DNS switched to automatic" : $"DNS set to {primary}{(secondary != null ? $" / {secondary}" : "")}",
        };
        foreach (var step in result.Steps)
            Log(StepLine(step));
        Log(summary);
        RepairCompleted?.Invoke(summary, false);
        SetBusy(false, "");
        RefreshDnsCurrent();

        ShowInfo(result.Outcome switch
        {
            RepairOutcome.Cancelled => InfoBarSeverity.Informational,
            RepairOutcome.Failed => InfoBarSeverity.Error,
            RepairOutcome.PartialFailure => InfoBarSeverity.Warning,
            _ => InfoBarSeverity.Success,
        }, summary, DnsCurrent.Text);
    }

    private void OnSavePreset(object sender, RoutedEventArgs e)
    {
        string primary = DnsPrimary.Text.Trim();
        string? secondary = DnsSecondary.Text.Trim();
        if (secondary.Length == 0)
            secondary = null;
        if (!IsIPv4(primary) || (secondary != null && !IsIPv4(secondary)))
        {
            ShowInfo(InfoBarSeverity.Warning, "Nothing to save yet",
                "Enter a Main address (and optionally a Backup) first — a DNS address looks like 1.1.1.1.");
            return;
        }

        string suggested = secondary is null ? primary : $"{primary} + {secondary}";
        string? name = PromptDialog.Show(Window.GetWindow(this), "Save DNS preset", "Name this combination:", suggested);
        if (string.IsNullOrWhiteSpace(name) || ViewModel is null)
            return;

        ViewModel.SaveDnsPreset(name.Trim(), primary, secondary);
        int index = BuiltInPresets.Length +
                    ViewModel.DnsPresets.ToList().FindIndex(p => string.Equals(p.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        RebuildPresets(index);
        Log($"Saved DNS preset \"{name.Trim()}\"");
    }

    private async void OnDeletePreset(object sender, RoutedEventArgs e)
    {
        if (!Selected.IsSaved || ViewModel is null)
            return;
        int savedIndex = PresetBox.SelectedIndex - BuiltInPresets.Length;
        if (savedIndex < 0 || savedIndex >= ViewModel.DnsPresets.Count)
            return;
        string name = ViewModel.DnsPresets[savedIndex].Name;

        var confirm = new Wpf.Ui.Controls.MessageBox
        {
            Title = $"Delete \"{name}\"?",
            Content = "This only removes it from the list — your current DNS settings don't change.",
            PrimaryButtonText = "Delete",
            CloseButtonText = "Cancel",
        };
        if (await confirm.ShowDialogAsync() != Wpf.Ui.Controls.MessageBoxResult.Primary)
            return;

        ViewModel.RemoveDnsPreset(savedIndex);
        RebuildPresets(0);
        Log($"Deleted DNS preset \"{name}\"");
    }

    private static bool IsIPv4(string value) =>
        IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork;

    // ------------------------------------------------------------- helpers

    private void SetBusy(bool busy, string status, bool indeterminate = false)
    {
        QuickFixButton.IsEnabled = !busy;
        FullResetButton.IsEnabled = !busy;
        ApplyDnsButton.IsEnabled = !busy;
        SavePresetButton.IsEnabled = !busy && Selected.IsCustom;
        DeletePresetButton.IsEnabled = !busy && Selected.IsSaved;
        StatusText.Text = status;
        Progress.IsIndeterminate = indeterminate;
        Progress.Value = 0;
        Progress.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }

    private void Log(string line)
    {
        ActivityLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        ActivityLog.ScrollToEnd();
    }
}
