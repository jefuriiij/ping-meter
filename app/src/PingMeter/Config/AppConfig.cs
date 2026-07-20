namespace PingMeter.Config;

public enum MonitorSelection
{
    Primary,
    Secondary,
    All,
}

public sealed class AppConfig
{
    public List<string> Targets { get; set; } = ["google.com", "1.1.1.1", "facebook.com"];
    public string ActiveTarget { get; set; } = "google.com";
    public int IntervalMs { get; set; } = 1000;
    public int TimeoutMs { get; set; } = 1000;
    public int GreenBelowMs { get; set; } = 50;
    public int YellowBelowMs { get; set; } = 120;
    public MonitorSelection Monitors { get; set; } = MonitorSelection.All;
    public bool ShowSparkline { get; set; } = true;
    public bool TransparentBackground { get; set; }
    public int StatsWindow { get; set; } = 60;
    public bool StartWithWindows { get; set; }
    public bool AutoCheckUpdates { get; set; } = true;
    public bool EventLogEnabled { get; set; } = true;
    public bool SampleCsvEnabled { get; set; }
    public int LogRetentionDays { get; set; } = 30;

    // App state, not user settings — carried in the same file for simplicity.
    public DateTime LastUpdateCheckUtc { get; set; }
    public string? LastNotifiedVersion { get; set; }

    public void Normalize()
    {
        Targets = Targets
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .Select(t => t.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Targets.Count == 0)
            Targets.Add("google.com");
        if (!Targets.Contains(ActiveTarget, StringComparer.OrdinalIgnoreCase))
            ActiveTarget = Targets[0];

        IntervalMs = Math.Clamp(IntervalMs, 250, 60_000);
        TimeoutMs = Math.Clamp(TimeoutMs, 100, 10_000);
        GreenBelowMs = Math.Clamp(GreenBelowMs, 1, 5_000);
        YellowBelowMs = Math.Clamp(YellowBelowMs, GreenBelowMs, 10_000);
        StatsWindow = Math.Clamp(StatsWindow, 10, 600);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
    }

    public AppConfig Clone()
    {
        var copy = (AppConfig)MemberwiseClone();
        copy.Targets = [.. Targets];
        return copy;
    }

    public void CopyFrom(AppConfig other)
    {
        Targets = [.. other.Targets];
        ActiveTarget = other.ActiveTarget;
        IntervalMs = other.IntervalMs;
        TimeoutMs = other.TimeoutMs;
        GreenBelowMs = other.GreenBelowMs;
        YellowBelowMs = other.YellowBelowMs;
        Monitors = other.Monitors;
        ShowSparkline = other.ShowSparkline;
        TransparentBackground = other.TransparentBackground;
        StatsWindow = other.StatsWindow;
        StartWithWindows = other.StartWithWindows;
        AutoCheckUpdates = other.AutoCheckUpdates;
        EventLogEnabled = other.EventLogEnabled;
        SampleCsvEnabled = other.SampleCsvEnabled;
        LogRetentionDays = other.LogRetentionDays;
        LastUpdateCheckUtc = other.LastUpdateCheckUtc;
        LastNotifiedVersion = other.LastNotifiedVersion;
    }
}
