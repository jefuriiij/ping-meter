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
