using System.Diagnostics;
using Microsoft.Win32;
using PingMeter.Config;
using PingMeter.Logging;
using PingMeter.Network;
using PingMeter.Ping;
using PingMeter.Settings;
using PingMeter.Taskbar;
using PingMeter.Update;
using PingMeter.Widget;

namespace PingMeter.App;

internal sealed class TrayContext : ApplicationContext
{
    private enum StatusBucket
    {
        Unknown,
        Good,
        Warn,
        Bad,
    }

    private const int SparklinePoints = 24;

    private readonly AppConfig _config;
    private readonly PingEngine _engine;
    private readonly NotifyIcon _tray;
    private readonly TaskbarWatcher _watcher;
    private readonly ContextMenuStrip _menu = new();
    private readonly List<TaskbarEmbedder> _embedders = [];
    private readonly System.Windows.Forms.Timer _taskbarCountTimer;
    private readonly Dictionary<StatusBucket, Icon> _icons;
    private readonly EventLogger _eventLog = new();
    private readonly SampleCsvLogger _csvLog = new();
    private readonly StabilityTracker _tracker;
    private readonly System.Windows.Forms.Timer _updateTimer;
    private readonly System.Windows.Forms.Timer _dnsTimer;
    private DnsStatus? _dnsStatus;
    private StatusBucket _iconBucket = (StatusBucket)(-1);
    private SettingsForm? _settingsForm;
    private int _lastTaskbarCount = -1;
    private string? _pendingUpdateUrl;
    private DateTime _lastSweepDate = DateTime.Now.Date;

    public TrayContext()
    {
        _config = ConfigStore.Load();
        ApplyAutostart(_config.StartWithWindows);

        _eventLog.Enabled = _config.EventLogEnabled;
        _csvLog.Enabled = _config.SampleCsvEnabled;
        EventLogger.SweepOldLogs(_config.LogRetentionDays);
        _tracker = new StabilityTracker(_eventLog, _config);

        _engine = new PingEngine(_config.ActiveTarget, _config.IntervalMs, _config.TimeoutMs, _config.StatsWindow);
        _engine.SampleReceived += PushSnapshot;
        _engine.SampleAdded += OnSampleAdded;

        RebuildMenu();

        _icons = new Dictionary<StatusBucket, Icon>
        {
            [StatusBucket.Unknown] = MakeCircleIcon(Color.FromArgb(140, 140, 140)),
            [StatusBucket.Good] = MakeCircleIcon(Color.FromArgb(102, 187, 106)),
            [StatusBucket.Warn] = MakeCircleIcon(Color.FromArgb(255, 179, 0)),
            [StatusBucket.Bad] = MakeCircleIcon(Color.FromArgb(239, 83, 80)),
        };
        _tray = new NotifyIcon
        {
            Icon = _icons[StatusBucket.Unknown],
            Text = "PingMeter",
            ContextMenuStrip = _menu,
            Visible = true,
        };
        _tray.DoubleClick += (_, _) => OpenSettings();
        _tray.BalloonTipClicked += (_, _) =>
        {
            if (_pendingUpdateUrl is { } url)
                OpenUrl(url);
        };

        _watcher = new TaskbarWatcher();
        _watcher.TaskbarsChanged += RebuildEmbedders;

        // Toggling "show taskbar on all displays" fires no system event — poll the count.
        _taskbarCountTimer = new System.Windows.Forms.Timer { Interval = 2000 };
        _taskbarCountTimer.Tick += (_, _) =>
        {
            if (TaskbarLocator.CountTaskbars() != _lastTaskbarCount)
                _watcher.Trigger();
        };
        _taskbarCountTimer.Start();

        // Hourly: daily log sweep + (at most daily) update check. The 30 s one-shot gives
        // the first auto-check a chance shortly after startup without delaying launch.
        _updateTimer = new System.Windows.Forms.Timer { Interval = 60 * 60 * 1000 };
        _updateTimer.Tick += (_, _) =>
        {
            SweepLogsIfNewDay();
            _ = AutoCheckForUpdatesAsync();
        };
        _updateTimer.Start();
        var startupCheck = new System.Windows.Forms.Timer { Interval = 30_000 };
        startupCheck.Tick += (_, _) =>
        {
            startupCheck.Stop();
            startupCheck.Dispose();
            _ = AutoCheckForUpdatesAsync();
        };
        startupCheck.Start();

        // DNS shown in the tooltip changes rarely — a relaxed poll keeps it current.
        _dnsTimer = new System.Windows.Forms.Timer { Interval = 15_000 };
        _dnsTimer.Tick += (_, _) => _dnsStatus = DnsInfo.GetActive();
        _dnsTimer.Start();
        _dnsStatus = DnsInfo.GetActive();

        RebuildEmbedders();
        _engine.Start();
        _eventLog.Info($"PingMeter v{UpdateChecker.CurrentVersion} started (target {_config.ActiveTarget}, interval {_config.IntervalMs} ms)");
    }

    private void OnSampleAdded(PingSample sample)
    {
        _tracker.Process(_engine.Target, sample);
        _csvLog.Log(_engine.Target, sample);
    }

    private void SweepLogsIfNewDay()
    {
        if (DateTime.Now.Date == _lastSweepDate)
            return;
        _lastSweepDate = DateTime.Now.Date;
        EventLogger.SweepOldLogs(_config.LogRetentionDays);
    }

    private void RebuildMenu()
    {
        _menu.Items.Clear();
        foreach (string target in _config.Targets)
        {
            var item = new ToolStripMenuItem(target)
            {
                Checked = string.Equals(target, _config.ActiveTarget, StringComparison.OrdinalIgnoreCase),
            };
            string captured = target;
            item.Click += (_, _) => SwitchTarget(captured);
            _menu.Items.Add(item);
        }
        _menu.Items.Add(new ToolStripSeparator());

        var pause = new ToolStripMenuItem("Pause") { Checked = _engine.IsPaused };
        pause.Click += (_, _) =>
        {
            _engine.SetPaused(!_engine.IsPaused);
            _eventLog.Info(_engine.IsPaused ? "paused" : "resumed");
            RebuildMenu();
        };
        _menu.Items.Add(pause);

        var reset = new ToolStripMenuItem("Reset");
        reset.Click += (_, _) =>
        {
            _engine.Reset();
            _tracker.Reset();
            _eventLog.Info("ping reset");
            RebuildMenu(); // Reset also resumes, so the Pause checkmark may need clearing
        };
        _menu.Items.Add(reset);

        var fixInternet = new ToolStripMenuItem("Fix internet…");
        fixInternet.Click += (_, _) => OpenSettings(tab: 2); // Network tools tab
        _menu.Items.Add(fixInternet);

        _menu.Items.Add(new ToolStripSeparator());

        var viewLog = new ToolStripMenuItem("View connection log");
        viewLog.Click += (_, _) => OpenTodayLog();
        _menu.Items.Add(viewLog);

        var openLogs = new ToolStripMenuItem("Open logs folder");
        openLogs.Click += (_, _) => OpenLogsFolder();
        _menu.Items.Add(openLogs);

        var checkUpdates = new ToolStripMenuItem("Check for updates…");
        checkUpdates.Click += async (_, _) => await CheckForUpdatesManualAsync();
        _menu.Items.Add(checkUpdates);

        var settings = new ToolStripMenuItem("Settings…");
        settings.Click += (_, _) => OpenSettings();
        _menu.Items.Add(settings);

        _menu.Items.Add(new ToolStripSeparator());
        var exit = new ToolStripMenuItem("Exit");
        exit.Click += (_, _) => ExitThread();
        _menu.Items.Add(exit);
    }

    private void SwitchTarget(string host)
    {
        _config.ActiveTarget = host;
        ConfigStore.Save(_config);
        _engine.SetTarget(host);
        _tracker.Reset();
        _eventLog.Info($"target switched to {host}");
        RebuildMenu();
    }

    private static void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch
        {
            // browser launch is best-effort
        }
    }

    private static void OpenTodayLog()
    {
        try
        {
            Directory.CreateDirectory(EventLogger.LogsDir);
            string file = EventLogger.TodayEventFile;
            if (!File.Exists(file))
                File.WriteAllText(file, string.Empty);
            Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private static void OpenLogsFolder()
    {
        try
        {
            Directory.CreateDirectory(EventLogger.LogsDir);
            Process.Start(new ProcessStartInfo(EventLogger.LogsDir) { UseShellExecute = true });
        }
        catch
        {
        }
    }

    private async Task AutoCheckForUpdatesAsync()
    {
        if (!_config.AutoCheckUpdates)
            return;
        if (DateTime.UtcNow - _config.LastUpdateCheckUtc < TimeSpan.FromHours(24))
            return;

        var result = await UpdateChecker.CheckAsync(); // resumes on the UI thread
        _config.LastUpdateCheckUtc = DateTime.UtcNow;

        if (result is UpdateResult.UpdateAvailable update &&
            _config.LastNotifiedVersion != update.Latest.ToString())
        {
            _config.LastNotifiedVersion = update.Latest.ToString();
            _pendingUpdateUrl = update.Url;
            _eventLog.Info($"update available: v{update.Latest} (running v{UpdateChecker.CurrentVersion})");
            _tray.ShowBalloonTip(10_000, "PingMeter update available",
                $"Version {update.Latest} is available (you have {UpdateChecker.CurrentVersion}). Click to download.",
                ToolTipIcon.Info);
        }
        ConfigStore.Save(_config);
    }

    private async Task CheckForUpdatesManualAsync()
    {
        var result = await UpdateChecker.CheckAsync();
        _config.LastUpdateCheckUtc = DateTime.UtcNow;
        ConfigStore.Save(_config);

        switch (result)
        {
            case UpdateResult.UpToDate upToDate:
                MessageBox.Show($"You're on the latest version (v{upToDate.Current}).",
                    "PingMeter", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;
            case UpdateResult.UpdateAvailable update:
                if (MessageBox.Show(
                        $"Version {update.Latest} is available (you have {UpdateChecker.CurrentVersion}).\n\nOpen the download page?",
                        "PingMeter", MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
                {
                    OpenUrl(update.Url);
                }
                break;
            case UpdateResult.Failed failed:
                MessageBox.Show($"Couldn't check for updates:\n{failed.Reason}",
                    "PingMeter", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private void RebuildEmbedders()
    {
        foreach (var embedder in _embedders)
            embedder.Dispose();
        _embedders.Clear();

        var taskbars = new List<TaskbarInfo>();
        if (_config.Monitors is MonitorSelection.Primary or MonitorSelection.All)
        {
            if (TaskbarLocator.FindPrimary() is { } primary)
                taskbars.Add(primary);
        }
        if (_config.Monitors is MonitorSelection.Secondary or MonitorSelection.All)
            taskbars.AddRange(TaskbarLocator.FindSecondaries());

        // "Secondary only" with no secondary taskbar present: fall back to primary
        // so the widget never silently disappears.
        if (taskbars.Count == 0 && TaskbarLocator.FindPrimary() is { } fallback)
            taskbars.Add(fallback);

        foreach (var taskbar in taskbars)
        {
            var widget = new WidgetForm(_config, _menu);
            widget.SettingsRequested += OpenSettings;
            var embedder = new TaskbarEmbedder(taskbar, widget, _config);
            embedder.TaskbarLost += _watcher.Trigger;
            _embedders.Add(embedder);
            embedder.Attach();
        }

        _lastTaskbarCount = TaskbarLocator.CountTaskbars();
        PushSnapshot();
    }

    private void PushSnapshot()
    {
        var snapshot = _engine.Stats.GetSnapshot(SparklinePoints);
        bool paused = _engine.IsPaused;
        string target = _engine.Target;

        foreach (var embedder in _embedders)
            embedder.Widget.UpdateSnapshot(snapshot, paused, target, _dnsStatus?.Summary);

        string status = paused ? "paused"
            : snapshot.Current is { } c ? (c.IsLost ? "timeout" : $"{c.RoundtripMs} ms")
            : "…";
        string text = $"PingMeter — {target}: {status}";
        _tray.Text = text.Length <= 120 ? text : text[..120];

        SetTrayIcon(BucketFor(snapshot, paused));
    }

    private StatusBucket BucketFor(StatsSnapshot snapshot, bool paused)
    {
        if (paused || snapshot.Current is not { } current)
            return StatusBucket.Unknown;
        if (current.IsLost)
            return StatusBucket.Bad;
        long ms = current.RoundtripMs!.Value;
        return ms < _config.GreenBelowMs ? StatusBucket.Good
            : ms < _config.YellowBelowMs ? StatusBucket.Warn
            : StatusBucket.Bad;
    }

    private void SetTrayIcon(StatusBucket bucket)
    {
        if (bucket == _iconBucket)
            return;
        _iconBucket = bucket;
        _tray.Icon = _icons[bucket];
    }

    private void OpenSettings() => OpenSettings(tab: null);

    private void OpenSettings(int? tab)
    {
        if (_settingsForm is { IsDisposed: false })
        {
            if (tab is { } existing)
                _settingsForm.SelectTab(existing);
            _settingsForm.Activate();
            return;
        }
        _settingsForm = new SettingsForm(_config);
        _settingsForm.ConfigSaved += ApplySettings;
        _settingsForm.RepairStarted += OnRepairStarted;
        _settingsForm.RepairCompleted += OnRepairCompleted;
        if (tab is { } index)
            _settingsForm.SelectTab(index);
        _settingsForm.Show();
    }

    private void OnRepairStarted()
    {
        _eventLog.Info("network repair started — pinging paused");
        _engine.SetPaused(true);
        RebuildMenu();
    }

    private void OnRepairCompleted(string summary, bool fullReset)
    {
        _eventLog.Info($"network repair: {summary}");
        _dnsStatus = DnsInfo.GetActive(); // DNS may have just changed
        // Fresh stats (and unpause) so the user watches the connection come back clean.
        _engine.Reset();
        _tracker.Reset();
        RebuildMenu();
    }

    private void ApplySettings(AppConfig updated)
    {
        updated.Normalize();
        bool monitorsChanged = updated.Monitors != _config.Monitors;
        bool statsChanged = updated.StatsWindow != _config.StatsWindow;
        bool targetChanged = !string.Equals(updated.ActiveTarget, _config.ActiveTarget, StringComparison.OrdinalIgnoreCase);

        // The dialog edited a clone from when it opened — don't let it roll back
        // update-check state written by the background checker since then.
        updated.LastUpdateCheckUtc = _config.LastUpdateCheckUtc;
        updated.LastNotifiedVersion = _config.LastNotifiedVersion;

        _config.CopyFrom(updated);
        ConfigStore.Save(_config);

        _eventLog.Enabled = _config.EventLogEnabled;
        _csvLog.Enabled = _config.SampleCsvEnabled;
        _eventLog.Info("settings updated");

        _engine.IntervalMs = _config.IntervalMs;
        _engine.TimeoutMs = _config.TimeoutMs;
        if (statsChanged)
            _engine.Stats.Resize(_config.StatsWindow);
        if (targetChanged)
            _engine.SetTarget(_config.ActiveTarget);
        ApplyAutostart(_config.StartWithWindows);
        RebuildMenu();

        if (monitorsChanged)
        {
            RebuildEmbedders();
        }
        else
        {
            foreach (var embedder in _embedders)
                embedder.Widget.RefreshConfig();
        }
        PushSnapshot();
    }

    private static void ApplyAutostart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
            if (enable && Environment.ProcessPath is { } exe)
                key.SetValue("PingMeter", $"\"{exe}\"");
            else
                key.DeleteValue("PingMeter", throwOnMissingValue: false);
        }
        catch
        {
            // autostart is best-effort
        }
    }

    private static Icon MakeCircleIcon(Color color)
    {
        using var bitmap = new Bitmap(16, 16);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(color);
            g.FillEllipse(brush, 2, 2, 12, 12);
        }
        IntPtr hIcon = bitmap.GetHicon();
        try
        {
            using var temp = Icon.FromHandle(hIcon);
            return (Icon)temp.Clone(); // clone owns its own handle
        }
        finally
        {
            NativeMethods.DestroyIcon(hIcon);
        }
    }

    protected override void ExitThreadCore()
    {
        _eventLog.Info("PingMeter exiting");
        _dnsTimer.Stop();
        _dnsTimer.Dispose();
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _taskbarCountTimer.Stop();
        _taskbarCountTimer.Dispose();
        _tray.Visible = false;
        _engine.Dispose();
        foreach (var embedder in _embedders)
            embedder.Dispose();
        _embedders.Clear();
        _watcher.Dispose();
        _settingsForm?.Dispose();
        _tray.Dispose();
        _menu.Dispose();
        foreach (var icon in _icons.Values)
            icon.Dispose();
        base.ExitThreadCore();
    }
}
