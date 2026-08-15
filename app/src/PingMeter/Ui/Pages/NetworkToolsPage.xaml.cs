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
    private sealed record DohPair(DohSetting Primary, DohSetting Secondary);

    private sealed record PresetItem(string Label, string? Primary, string? Secondary, bool IsSaved, bool IsCustom, DohPair? Doh = null);

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

    /// <summary>Dropdown order matches <see cref="DohMode"/>, and the Windows 11 wording.</summary>
    private static readonly string[] DohLabels =
    [
        "Off",
        "On (automatic template)",
        "On (manual template)",
    ];

    /// <summary>Templates Windows ships with, so those resolvers can use "automatic".</summary>
    private static readonly Dictionary<string, string> KnownTemplates = new(StringComparer.OrdinalIgnoreCase)
    {
        ["1.1.1.1"] = "https://cloudflare-dns.com/dns-query",
        ["1.0.0.1"] = "https://cloudflare-dns.com/dns-query",
        ["8.8.8.8"] = "https://dns.google/dns-query",
        ["8.8.4.4"] = "https://dns.google/dns-query",
        ["9.9.9.9"] = "https://dns.quad9.net/dns-query",
        ["149.112.112.112"] = "https://dns.quad9.net/dns-query",
    };

    public NetworkToolsPage()
    {
        InitializeComponent();
        foreach (var box in new[] { PrimaryDoh, SecondaryDoh })
            box.ItemsSource = DohLabels;
        Loaded += (_, _) =>
        {
            RebuildPresets(0);
            RefreshDnsCurrent();
            LoadCurrentDoh();
        };
    }

    /// <summary>Show what the adapter is actually using right now.</summary>
    private void LoadCurrentDoh()
    {
        var status = DnsInfo.GetActive();
        if (status is null)
            return;
        SetDohControls(PrimaryDoh, PrimaryDohTemplate,
            status.Servers.Count > 0 ? DnsInfo.GetDoh(status.AdapterId, status.Servers[0]) : new DohSetting(DohMode.Off, null));
        SetDohControls(SecondaryDoh, SecondaryDohTemplate,
            status.Servers.Count > 1 ? DnsInfo.GetDoh(status.AdapterId, status.Servers[1]) : new DohSetting(DohMode.Off, null));
    }

    private static void SetDohControls(System.Windows.Controls.ComboBox box, Wpf.Ui.Controls.TextBox template, DohSetting setting)
    {
        box.SelectedIndex = (int)setting.Mode;
        template.Text = setting.Template ?? "";
        template.Visibility = setting.Mode == DohMode.Manual ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The manual-template box only makes sense for the manual option.</summary>
    private void OnDohChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (sender == PrimaryDoh)
            PrimaryDohTemplate.Visibility = PrimaryDoh.SelectedIndex == (int)DohMode.Manual ? Visibility.Visible : Visibility.Collapsed;
        else if (sender == SecondaryDoh)
            SecondaryDohTemplate.Visibility = SecondaryDoh.SelectedIndex == (int)DohMode.Manual ? Visibility.Visible : Visibility.Collapsed;
    }

    private static DohMode ModeOf(System.Windows.Controls.ComboBox box) =>
        box.SelectedIndex < 0 ? DohMode.Off : (DohMode)box.SelectedIndex;

    private static DohMode ParseMode(string? stored) =>
        Enum.TryParse<DohMode>(stored, ignoreCase: true, out var mode) ? mode : DohMode.Off;

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
            var doh = new DohPair(
                new DohSetting(ParseMode(saved.PrimaryDoh), saved.PrimaryDohTemplate),
                new DohSetting(ParseMode(saved.SecondaryDoh), saved.SecondaryDohTemplate));
            _presets.Add(new PresetItem(label, saved.Primary, saved.Secondary, IsSaved: true, IsCustom: false, doh));
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

        // DoH belongs to the servers, so it follows the chosen preset.
        PrimaryDoh.IsEnabled = SecondaryDoh.IsEnabled = !automatic;
        if (item.IsSaved && item.Doh is { } saved)
        {
            SetDohControls(PrimaryDoh, PrimaryDohTemplate, saved.Primary);
            SetDohControls(SecondaryDoh, SecondaryDohTemplate, saved.Secondary);
        }
        else if (!item.IsCustom && !automatic)
        {
            // Built-in resolvers: Windows already knows their templates, so offer automatic.
            SetDohControls(PrimaryDoh, PrimaryDohTemplate, new DohSetting(DohMode.Automatic, null));
            SetDohControls(SecondaryDoh, SecondaryDohTemplate,
                item.Secondary is null ? new DohSetting(DohMode.Off, null) : new DohSetting(DohMode.Automatic, null));
        }
        else if (automatic)
        {
            SetDohControls(PrimaryDoh, PrimaryDohTemplate, new DohSetting(DohMode.Off, null));
            SetDohControls(SecondaryDoh, SecondaryDohTemplate, new DohSetting(DohMode.Off, null));
        }
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

        // Encryption travels with the addresses so one permission prompt covers everything.
        DohMode primaryDoh = ModeOf(PrimaryDoh), secondaryDoh = ModeOf(SecondaryDoh);
        if (!automatic && !ValidateTemplates(primaryDoh, PrimaryDohTemplate.Text, secondaryDoh, secondary is null ? null : SecondaryDohTemplate.Text))
            return;

        SetBusy(true, "Working — answer the Windows permission prompt…", indeterminate: true);
        Log(automatic
            ? $"Switching DNS to automatic on {status.AdapterName}…"
            : $"Setting DNS to {primary}{(secondary != null ? $" / {secondary}" : "")} on {status.AdapterName}…");
        if (!automatic && (primaryDoh != DohMode.Off || secondaryDoh != DohMode.Off))
            Log($"DNS over HTTPS: main {DohLabels[(int)primaryDoh]}{(secondary != null ? $", backup {DohLabels[(int)secondaryDoh]}" : "")}");
        RepairStarted?.Invoke();

        var request = new DnsRequest(
            status.InterfaceIndex,
            status.AdapterId,
            automatic,
            primary is null ? null : new DnsServerRequest(primary, primaryDoh, TemplateFor(primary, primaryDoh, PrimaryDohTemplate.Text)),
            secondary is null ? null : new DnsServerRequest(secondary, secondaryDoh, TemplateFor(secondary, secondaryDoh, SecondaryDohTemplate.Text)));
        var result = await NetworkRepair.RunSetDnsAsync(request);
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

        ViewModel.SaveDnsPreset(name.Trim(), primary, secondary,
            ModeOf(PrimaryDoh).ToString(), PrimaryDohTemplate.Text.Trim(),
            ModeOf(SecondaryDoh).ToString(), SecondaryDohTemplate.Text.Trim());
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

    /// <summary>Manual mode needs a real https template; automatic uses the one Windows knows.</summary>
    private static string? TemplateFor(string server, DohMode mode, string typed) => mode switch
    {
        DohMode.Manual => typed.Trim(),
        DohMode.Automatic => KnownTemplates.GetValueOrDefault(server),
        _ => null,
    };

    private bool ValidateTemplates(DohMode primaryMode, string primaryTemplate, DohMode secondaryMode, string? secondaryTemplate)
    {
        foreach (var (mode, template) in new[] { (primaryMode, primaryTemplate), (secondaryMode, secondaryTemplate ?? "") })
        {
            if (mode != DohMode.Manual)
                continue;
            string value = template.Trim();
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                ShowInfo(InfoBarSeverity.Error, "That template doesn't look right",
                    "A DNS-over-HTTPS template is an https address, like https://dns.google/dns-query.");
                return false;
            }
        }
        return true;
    }

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
