using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using PingMeter.Config;

namespace PingMeter.Ui;

/// <summary>
/// Bindable view of <see cref="AppConfig"/> for the WPF settings window. Works on a clone,
/// so nothing is applied until Save. Keeps the app-wide convention that the UI speaks
/// seconds while the config file stores milliseconds.
/// </summary>
internal sealed class SettingsViewModel : INotifyPropertyChanged
{
    private readonly AppConfig _working;

    public SettingsViewModel(AppConfig current)
    {
        _working = current.Clone();
        Targets = new ObservableCollection<string>(_working.Targets);
    }

    public ObservableCollection<string> Targets { get; }

    public string NewTarget
    {
        get;
        set => Set(ref field, value);
    } = "";

    // double, not decimal: WPF-UI's NumberBox.Value is double? and the binding silently
    // fails to write back through a decimal property.
    public double IntervalSeconds
    {
        get => _working.IntervalMs / 1000d;
        set => SetConfig(v => _working.IntervalMs = (int)Math.Round(v * 1000), value, _working.IntervalMs / 1000d);
    }

    public double GreenBelowMs
    {
        get => _working.GreenBelowMs;
        set => SetConfig(v => _working.GreenBelowMs = (int)v, value, _working.GreenBelowMs);
    }

    public double YellowBelowMs
    {
        get => _working.YellowBelowMs;
        set => SetConfig(v => _working.YellowBelowMs = (int)v, value, _working.YellowBelowMs);
    }

    public bool ShowSparkline
    {
        get => _working.ShowSparkline;
        set => SetConfig(v => _working.ShowSparkline = v, value, _working.ShowSparkline);
    }

    public bool ShowLossOnWidget
    {
        get => _working.ShowLossOnWidget;
        set => SetConfig(v => _working.ShowLossOnWidget = v, value, _working.ShowLossOnWidget);
    }

    public bool StartWithWindows
    {
        get => _working.StartWithWindows;
        set => SetConfig(v => _working.StartWithWindows = v, value, _working.StartWithWindows);
    }

    // ---- Advanced ----

    public double TimeoutSeconds
    {
        get => _working.TimeoutMs / 1000d;
        set => SetConfig(v => _working.TimeoutMs = (int)Math.Round(v * 1000), value, _working.TimeoutMs / 1000d);
    }

    public double StatsWindow
    {
        get => _working.StatsWindow;
        set => SetConfig(v => _working.StatsWindow = (int)v, value, _working.StatsWindow);
    }

    /// <summary>Index into the "Where to show" list; maps to <see cref="MonitorSelection"/>.</summary>
    public int MonitorIndex
    {
        get => (int)_working.Monitors;
        set => SetConfig(v => _working.Monitors = (MonitorSelection)v, value, (int)_working.Monitors);
    }

    public double HorizontalOffsetPx
    {
        get => _working.HorizontalOffsetPx;
        set => SetConfig(v => _working.HorizontalOffsetPx = (int)v, value, _working.HorizontalOffsetPx);
    }

    public bool TransparentBackground
    {
        get => _working.TransparentBackground;
        set => SetConfig(v => _working.TransparentBackground = v, value, _working.TransparentBackground);
    }

    public bool AutoCheckUpdates
    {
        get => _working.AutoCheckUpdates;
        set => SetConfig(v => _working.AutoCheckUpdates = v, value, _working.AutoCheckUpdates);
    }

    public bool EventLogEnabled
    {
        get => _working.EventLogEnabled;
        set => SetConfig(v => _working.EventLogEnabled = v, value, _working.EventLogEnabled);
    }

    public bool SampleCsvEnabled
    {
        get => _working.SampleCsvEnabled;
        set => SetConfig(v => _working.SampleCsvEnabled = v, value, _working.SampleCsvEnabled);
    }

    public double LogRetentionDays
    {
        get => _working.LogRetentionDays;
        set => SetConfig(v => _working.LogRetentionDays = (int)v, value, _working.LogRetentionDays);
    }

    // ---- Saved DNS combinations ----

    /// <summary>Raised when the saved list changes, so the owner can persist it immediately.</summary>
    public event Action<List<DnsPreset>>? DnsPresetsChanged;

    public IReadOnlyList<DnsPreset> DnsPresets => _working.DnsPresets;

    /// <summary>Adds or overwrites a saved combination and persists straight away.</summary>
    public void SaveDnsPreset(string name, string primary, string? secondary)
    {
        var existing = _working.DnsPresets.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Primary = primary;
            existing.Secondary = secondary;
        }
        else
        {
            _working.DnsPresets.Add(new DnsPreset { Name = name, Primary = primary, Secondary = secondary });
        }
        _working.Normalize();
        DnsPresetsChanged?.Invoke(_working.DnsPresets);
    }

    public void RemoveDnsPreset(int index)
    {
        if (index < 0 || index >= _working.DnsPresets.Count)
            return;
        _working.DnsPresets.RemoveAt(index);
        DnsPresetsChanged?.Invoke(_working.DnsPresets);
    }

    public void AddTarget()
    {
        string host = NewTarget.Trim();
        if (host.Length == 0)
            return;
        if (!Targets.Any(t => string.Equals(t, host, StringComparison.OrdinalIgnoreCase)))
            Targets.Add(host);
        NewTarget = "";
    }

    public void RemoveTarget(string? host)
    {
        if (host != null)
            Targets.Remove(host);
    }

    /// <summary>The config to hand to the app's existing ConfigSaved pipeline.</summary>
    public AppConfig BuildConfig()
    {
        var config = _working.Clone();
        config.Targets = [.. Targets];
        config.Normalize();
        return config;
    }

    private void SetConfig<T>(Action<T> apply, T value, T current, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, current))
            return;
        apply(value);
        OnPropertyChanged(name);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
            return;
        field = value;
        OnPropertyChanged(name);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string? name) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
