using Microsoft.Win32;

namespace PingMeter.Taskbar;

/// <summary>
/// Hidden top-level window that hears the shell's "TaskbarCreated" broadcast (explorer
/// restarts) and display-layout changes, then asks the owner to rebuild all embedders.
/// Broadcasts are only delivered to top-level windows, so this cannot live on the
/// (reparented) widget itself — same reason TrafficMonitor listens on its main window.
/// </summary>
internal sealed class TaskbarWatcher : NativeWindow, IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);

    private static readonly uint WmTaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    private readonly System.Windows.Forms.Timer _debounce;

    public event Action? TaskbarsChanged;

    public TaskbarWatcher()
    {
        CreateHandle(new CreateParams
        {
            Caption = "PingMeter.TaskbarWatcher",
            Style = WS_POPUP, // never shown
        });
        _debounce = new System.Windows.Forms.Timer { Interval = 500 };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            TaskbarsChanged?.Invoke();
        };
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    /// <summary>Request a (debounced) rebuild — also used by embedders that lost their taskbar.</summary>
    public void Trigger()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Trigger();

    protected override void WndProc(ref Message m)
    {
        if ((uint)m.Msg == WmTaskbarCreated)
            Trigger();
        base.WndProc(ref m);
    }

    public void Dispose()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _debounce.Dispose();
        DestroyHandle();
    }
}
