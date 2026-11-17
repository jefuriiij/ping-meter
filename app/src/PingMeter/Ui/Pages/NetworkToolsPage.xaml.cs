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

    private sealed record PresetItem(
        string Label, string? Primary, string? Secondary, bool IsSaved, bool IsCustom,
        DohPair? Doh = null, string? Ipv6Primary = null, string? Ipv6Secondary = null);

    // IPv6 addresses from each provider's own documentation (Cloudflare: developers.cloudflare.com/1.1.1.1/ip-addresses).
    private static readonly PresetItem[] BuiltInPresets =
    [
        new("Automatic (from your router)", null, null, false, false),
        new("Cloudflare (1.1.1.1), fast and private", "1.1.1.1", "1.0.0.1", false, false,
            Ipv6Primary: "2606:4700:4700::1111", Ipv6Secondary: "2606:4700:4700::1001"),
        new("Google (8.8.8.8)", "8.8.8.8", "8.8.4.4", false, false,
            Ipv6Primary: "2001:4860:4860::8888", Ipv6Secondary: "2001:4860:4860::8844"),
        new("Quad9 (9.9.9.9), blocks malware sites", "9.9.9.9", "149.112.112.112", false, false,
            Ipv6Primary: "2620:fe::fe", Ipv6Secondary: "2620:fe::9"),
    ];

    private readonly List<PresetItem> _presets = [];

    /// <summary>False when IPv6 is turned off for the active adapter, so there is no IPv6 DNS to set.</summary>
    private bool _ipv6Available;
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
        ["2606:4700:4700::1111"] = "https://cloudflare-dns.com/dns-query",
        ["2606:4700:4700::1001"] = "https://cloudflare-dns.com/dns-query",
        ["2001:4860:4860::8888"] = "https://dns.google/dns-query",
        ["2001:4860:4860::8844"] = "https://dns.google/dns-query",
        ["2620:fe::fe"] = "https://dns.quad9.net/dns-query",
        ["2620:fe::9"] = "https://dns.quad9.net/dns-query",
    };

    public NetworkToolsPage()
    {
        InitializeComponent();
        foreach (var box in new[] { PrimaryDoh, SecondaryDoh })
            box.ItemsSource = DohLabels;
        Loaded += (_, _) =>
        {
            RefreshDnsCurrent();
            var status = DnsInfo.GetActive();
            // Start the toggle where the adapter is: on when IPv6 DNS is already set by hand.
            Ipv6Toggle.IsChecked = _ipv6Available && status?.Ipv6IsManual == true;
            RebuildPresets(0);
            SelectCurrentProvider(status);
            LoadCurrentDoh();
            UpdateIpv6Controls();
        };
    }

    /// <summary>
    /// Open on what the adapter really uses: the matching provider, or Custom filled with the
    /// current addresses. Opening on Automatic while DNS is manual made Apply undo it.
    /// </summary>
    private void SelectCurrentProvider(DnsStatus? status)
    {
        if (status is null || !status.IsManual || status.Servers.Count == 0)
            return; // Automatic is already selected
        string main = status.Servers[0];
        string? backup = status.Servers.Count > 1 ? status.Servers[1] : null;
        string? v6Main = status.Ipv6IsManual ? status.Ipv6Servers.FirstOrDefault() : null;

        int index = _presets.FindIndex(p =>
            !p.IsCustom && p.Primary == main && p.Secondary == backup
            // A saved preset also carries its IPv6 choice, so that has to match as well.
            && (!p.IsSaved || p.Ipv6Primary == v6Main));
        if (index >= 0)
        {
            PresetBox.SelectedIndex = index;
            return;
        }

        PresetBox.SelectedIndex = _presets.Count - 1; // Custom…
        DnsPrimary.Text = main;
        DnsSecondary.Text = backup ?? "";
        if (v6Main != null)
        {
            Ipv6Primary.Text = v6Main;
            Ipv6Secondary.Text = status.Ipv6Servers.Count > 1 ? status.Ipv6Servers[1] : "";
        }
    }

    private void OnIpv6Toggled(object sender, RoutedEventArgs e) => UpdateIpv6Controls();

    private bool Ipv6On => _ipv6Available && Ipv6Toggle.IsChecked == true;

    /// <summary>Fields show only while the toggle is on; nothing to change for Automatic or without IPv6.</summary>
    private void UpdateIpv6Controls()
    {
        bool automatic = _presets.Count > 0 && Selected.Primary is null;
        Ipv6Toggle.IsEnabled = _ipv6Available && !automatic;
        Ipv6Fields.Visibility = Ipv6On ? Visibility.Visible : Visibility.Collapsed;
        Ipv6Primary.IsEnabled = Ipv6Secondary.IsEnabled = !automatic;
        Ipv6Hint.Text = !_ipv6Available
            ? "IPv6 is turned off for this network adapter in Windows, so there is nothing to set."
            : automatic
                ? "Automatic sets IPv6 DNS back to automatic too."
                : "Uses the same encryption as the server above it. When off, Windows picks IPv6 DNS automatically.";
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
        Log("Quick fix: clearing DNS cache…");
        RepairStarted?.Invoke();

        var result = await NetworkRepair.RunQuickFixAsync();
        bool ok = result.Outcome == RepairOutcome.Success;
        bool cancelled = result.Outcome == RepairOutcome.Cancelled;
        string error = result.Error
            ?? (result.Steps.FirstOrDefault(s => !s.Ok) is { } failed
                ? $"ipconfig /flushdns exited with code {failed.ExitCode}"
                : "unknown error");

        Log(ok ? "✓ DNS cache cleared" : cancelled ? "Cancelled. Windows permission was not granted." : $"✗ Failed: {error}");
        RepairCompleted?.Invoke(
            ok ? "quick fix: DNS cache cleared" : cancelled ? "quick fix cancelled (no admin permission)" : $"quick fix failed: {error}",
            false);
        SetBusy(false, "");
        if (cancelled)
            ShowInfo(InfoBarSeverity.Informational, "Quick fix cancelled", "Windows permission was not granted, so nothing changed.");
        else
            ShowInfo(ok ? InfoBarSeverity.Success : InfoBarSeverity.Error,
                ok ? "DNS cache cleared" : "Quick fix failed",
                ok ? "If pages were failing to load, try again now." : error);
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

        SetBusy(true, "Waiting for you to answer the Windows permission prompt…", indeterminate: true);
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
                ? $"Step {Math.Min(p.Completed + 1, p.Total)} of {p.Total}: {next}…"
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
                ShowInfo(InfoBarSeverity.Informational, "Reset cancelled", "Windows permission was not granted, so nothing changed.");
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
                Log("Restart cancelled. Restart later to finish the reset.");
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
        _ipv6Available = status?.Ipv6InterfaceIndex != null;
        if (status is null)
        {
            DnsCurrent.Text = "Current: unknown (no active network found)";
            return;
        }

        string v6 = status.Ipv6InterfaceIndex is null
            ? "turned off for this adapter"
            : status.Ipv6Servers.Count == 0
                ? (status.Ipv6IsManual ? "none (manual)" : "automatic, none received")
                : $"{string.Join(", ", status.Ipv6Servers)} ({(status.Ipv6IsManual ? "manual" : "automatic")})";
        DnsCurrent.Text =
            $"Current: {(status.Servers.Count > 0 ? string.Join(", ", status.Servers) : "none")} "
            + $"({(status.IsManual ? "manual" : "automatic")}) on {status.AdapterName}\n"
            + $"IPv6: {v6}";
    }

    private void RebuildPresets(int selectIndex)
    {
        _presets.Clear();
        _presets.AddRange(BuiltInPresets);
        foreach (var saved in ViewModel?.DnsPresets ?? [])
        {
            string label = (saved.Secondary is null
                ? $"{saved.Name} ({saved.Primary})"
                : $"{saved.Name} ({saved.Primary} + {saved.Secondary})")
                + (saved.Ipv6Primary is null ? "" : " + IPv6");
            var doh = new DohPair(
                new DohSetting(ParseMode(saved.PrimaryDoh), saved.PrimaryDohTemplate),
                new DohSetting(ParseMode(saved.SecondaryDoh), saved.SecondaryDohTemplate));
            _presets.Add(new PresetItem(label, saved.Primary, saved.Secondary, IsSaved: true, IsCustom: false, doh,
                saved.Ipv6Primary, saved.Ipv6Secondary));
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

        // IPv6 servers follow the preset the same way the IPv4 ones do.
        if (!item.IsCustom)
        {
            Ipv6Primary.Text = item.Ipv6Primary ?? "";
            Ipv6Secondary.Text = item.Ipv6Secondary ?? "";
        }
        Ipv6Primary.IsReadOnly = Ipv6Secondary.IsReadOnly = !item.IsCustom;
        if (item.IsSaved && _ipv6Available)
            Ipv6Toggle.IsChecked = item.Ipv6Primary != null; // a saved preset remembers its IPv6 choice
        UpdateIpv6Controls();
    }

    /// <summary>
    /// Reads the IPv6 fields when the toggle is on. Off (or no IPv6) gives two nulls and ok;
    /// on with a bad address shows why and gives false.
    /// </summary>
    private bool TryReadIpv6(out string? primary, out string? secondary)
    {
        primary = secondary = null;
        if (!Ipv6On)
            return true;
        string p = Ipv6Primary.Text.Trim(), s = Ipv6Secondary.Text.Trim();
        if (!IsIPv6(p) || (s.Length > 0 && !IsIPv6(s)))
        {
            ShowInfo(InfoBarSeverity.Error, "That doesn't look like an IPv6 address",
                "An IPv6 DNS address looks like 2606:4700:4700::1111. Fill in IPv6 main, or turn off \"Also set IPv6 DNS\".");
            return false;
        }
        primary = IPAddress.Parse(p).ToString();
        secondary = s.Length > 0 ? IPAddress.Parse(s).ToString() : null;
        return true;
    }

    private async void OnApplyDns(object sender, RoutedEventArgs e)
    {
        var status = DnsInfo.GetActive();
        if (status is null)
        {
            ShowInfo(InfoBarSeverity.Warning, "No active network", "Couldn't find an active network adapter. Are you connected?");
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
                    "A DNS address looks like 1.1.1.1: four numbers from 0 to 255, separated by dots.");
                return;
            }
        }

        string? v6Primary = null, v6Secondary = null;
        if (!automatic && !TryReadIpv6(out v6Primary, out v6Secondary))
            return;
        bool setV6 = v6Primary != null;

        // Encryption travels with the addresses so one permission prompt covers everything.
        // An IPv6 server shares the choice of the IPv4 server in its column.
        DohMode primaryDoh = ModeOf(PrimaryDoh), secondaryDoh = ModeOf(SecondaryDoh);
        bool backupUsed = secondary != null || v6Secondary != null;
        if (!automatic && !ValidateTemplates(primaryDoh, PrimaryDohTemplate.Text, secondaryDoh, backupUsed ? SecondaryDohTemplate.Text : null))
            return;

        SetBusy(true, "Waiting for you to answer the Windows permission prompt…", indeterminate: true);
        Log(automatic
            ? $"Switching DNS to automatic on {status.AdapterName}…"
            : $"Setting DNS to {Pair(primary, secondary)} on {status.AdapterName}…");
        if (setV6)
            Log($"Setting IPv6 DNS to {Pair(v6Primary, v6Secondary)}…");
        else if (!automatic && status.Ipv6InterfaceIndex != null)
            Log("IPv6 DNS goes back to automatic");
        if (!automatic && (primaryDoh != DohMode.Off || secondaryDoh != DohMode.Off))
            Log($"DNS over HTTPS: main {DohLabels[(int)primaryDoh]}{(backupUsed ? $", backup {DohLabels[(int)secondaryDoh]}" : "")}");
        RepairStarted?.Invoke();

        var request = new DnsRequest(
            status.InterfaceIndex,
            status.AdapterId,
            automatic,
            ServerRequest(primary, primaryDoh, PrimaryDohTemplate.Text),
            ServerRequest(secondary, secondaryDoh, SecondaryDohTemplate.Text),
            status.Ipv6InterfaceIndex,
            setV6,
            ServerRequest(v6Primary, primaryDoh, PrimaryDohTemplate.Text),
            ServerRequest(v6Secondary, secondaryDoh, SecondaryDohTemplate.Text));
        var result = await NetworkRepair.RunSetDnsAsync(request);
        string summary = result.Outcome switch
        {
            RepairOutcome.Cancelled => "DNS change cancelled (no admin permission)",
            RepairOutcome.Failed => $"DNS change failed: {result.Error}",
            RepairOutcome.PartialFailure => "DNS change finished with warnings",
            _ => automatic ? "DNS switched to automatic"
                : $"DNS set to {Pair(primary, secondary)}{(setV6 ? $", IPv6 to {Pair(v6Primary, v6Secondary)}" : "")}",
        };
        foreach (var step in result.Steps)
            Log(StepLine(step));
        Log(summary);
        RepairCompleted?.Invoke(summary, false);
        SetBusy(false, "");
        RefreshDnsCurrent();
        UpdateIpv6Controls();

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
                "Enter a Main address first (a Backup is optional). A DNS address looks like 1.1.1.1.");
            return;
        }

        if (!TryReadIpv6(out string? v6Primary, out string? v6Secondary))
            return;

        string suggested = secondary is null ? primary : $"{primary} + {secondary}";
        string? name = PromptDialog.Show(Window.GetWindow(this), "Save DNS preset", "Name this combination:", suggested);
        if (string.IsNullOrWhiteSpace(name) || ViewModel is null)
            return;

        ViewModel.SaveDnsPreset(new DnsPreset
        {
            Name = name.Trim(),
            Primary = primary,
            Secondary = secondary,
            PrimaryDoh = ModeOf(PrimaryDoh).ToString(),
            PrimaryDohTemplate = BlankToNull(PrimaryDohTemplate.Text),
            SecondaryDoh = ModeOf(SecondaryDoh).ToString(),
            SecondaryDohTemplate = BlankToNull(SecondaryDohTemplate.Text),
            Ipv6Primary = v6Primary,
            Ipv6Secondary = v6Secondary,
        });
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
            Content = "This only removes it from the list. Your current DNS settings don't change.",
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

    private static bool IsIPv6(string value) =>
        !value.Contains('%') && IPAddress.TryParse(value, out var ip) && ip.AddressFamily == AddressFamily.InterNetworkV6;

    private static string? BlankToNull(string value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Pair(string? main, string? backup) => backup is null ? $"{main}" : $"{main} / {backup}";

    private static DnsServerRequest? ServerRequest(string? address, DohMode mode, string typedTemplate) =>
        address is null ? null : new DnsServerRequest(address, mode, TemplateFor(address, mode, typedTemplate));

    /// <summary>Manual mode needs a real https template; automatic uses the one Windows knows.</summary>
    private static string? TemplateFor(string server, DohMode mode, string typed) => mode switch
    {
        DohMode.Manual => typed.Trim(),
        // Look up the canonical form, so "2606:4700:4700:0:0:0:0:1111" still matches.
        DohMode.Automatic => KnownTemplates.GetValueOrDefault(
            IPAddress.TryParse(server, out var ip) ? ip.ToString() : server),
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
        StatusText.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
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
        // The bar sits at the top; Apply DNS is far below it, so scroll the result into view.
        Info.BringIntoView();
    }

    private void Log(string line)
    {
        ActivitySection.Visibility = Visibility.Visible;
        ActivityLog.AppendText($"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}");
        ActivityLog.ScrollToEnd();
    }
}
