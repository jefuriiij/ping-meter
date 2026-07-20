using PingMeter.Config;
using PingMeter.Widget;

namespace PingMeter.Taskbar;

/// <summary>
/// Owns one widget's embedding into one taskbar: SetParent into Shell_TrayWnd /
/// Shell_SecondaryTrayWnd, position next to the tray area, keep repositioning as the
/// taskbar changes, and fall back to a topmost overlay if embedding is refused.
/// (Technique ported from TrafficMonitor's CWin11TaskbarDlg.)
/// </summary>
internal sealed class TaskbarEmbedder : IDisposable
{
    private const int RepositionIntervalMs = 250;
    private const int FallbackRetryTicks = 40; // retry a failed embed every ~10 s

    private readonly TaskbarInfo _taskbar;
    private readonly AppConfig _config;
    private readonly System.Windows.Forms.Timer _timer;
    private bool _embedded;
    private int _ticksSinceEmbedAttempt;
    private NativeMethods.RECT _lastTaskbarRect, _lastNotifyRect, _lastStartRect;
    private int _lastForeignLeft = int.MaxValue;
    private uint _dpi = 96;
    private bool _disposed;

    public WidgetForm Widget { get; }

    /// <summary>The taskbar HWND died (explorer crashed) — owner should rebuild everything.</summary>
    public event Action? TaskbarLost;

    public TaskbarEmbedder(TaskbarInfo taskbar, WidgetForm widget, AppConfig config)
    {
        _taskbar = taskbar;
        Widget = widget;
        _config = config;
        _timer = new System.Windows.Forms.Timer { Interval = RepositionIntervalMs };
        _timer.Tick += (_, _) => Tick();
    }

    public void Attach()
    {
        _ = Widget.Handle; // force handle creation before reparenting
        _dpi = TaskbarDpi();
        Widget.UpdateDpi(_dpi);
        TryEmbed();
        Reposition(force: true);
        Widget.Show(); // ShowWithoutActivation, so no focus steal
        Reposition(force: true); // re-assert in case showing touched the bounds
        _timer.Start();
    }

    private uint TaskbarDpi()
    {
        uint dpi = NativeMethods.GetDpiForWindow(_taskbar.Handle);
        return dpi == 0 ? 96u : dpi;
    }

    private void TryEmbed()
    {
        _ticksSinceEmbedAttempt = 0;
        NativeMethods.SetParent(Widget.Handle, _taskbar.Handle);
        _embedded = NativeMethods.GetAncestor(Widget.Handle, NativeMethods.GA_PARENT) == _taskbar.Handle;
        if (!_embedded)
        {
            // Overlay fallback: float topmost over where the widget would sit.
            NativeMethods.SetWindowPos(Widget.Handle, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void Tick()
    {
        if (_disposed)
            return;

        if (!NativeMethods.IsWindow(_taskbar.Handle))
        {
            _timer.Stop();
            TaskbarLost?.Invoke();
            return;
        }

        // Explorer can silently rip the widget out of the taskbar; re-embed if so.
        if (_embedded && NativeMethods.GetAncestor(Widget.Handle, NativeMethods.GA_PARENT) != _taskbar.Handle)
            TryEmbed();
        else if (!_embedded && ++_ticksSinceEmbedAttempt >= FallbackRetryTicks)
            TryEmbed();

        uint dpi = TaskbarDpi();
        bool dpiChanged = dpi != _dpi;
        if (dpiChanged)
        {
            _dpi = dpi;
            Widget.UpdateDpi(dpi);
        }

        Reposition(force: dpiChanged || Widget.LayoutDirty);
    }

    public void Reposition(bool force)
    {
        if (!NativeMethods.GetWindowRect(_taskbar.Handle, out var rcTaskbar))
            return;
        NativeMethods.RECT rcNotify = default, rcStart = default;
        if (_taskbar.TrayNotify != IntPtr.Zero)
            NativeMethods.GetWindowRect(_taskbar.TrayNotify, out rcNotify);
        if (_taskbar.Start != IntPtr.Zero)
            NativeMethods.GetWindowRect(_taskbar.Start, out rcStart);

        int foreignLeft = FindLeftmostInjectedWidgetX(rcTaskbar);

        if (!force &&
            rcTaskbar.SameAs(_lastTaskbarRect) &&
            rcNotify.SameAs(_lastNotifyRect) &&
            rcStart.SameAs(_lastStartRect) &&
            foreignLeft == _lastForeignLeft)
        {
            return;
        }
        _lastTaskbarRect = rcTaskbar;
        _lastNotifyRect = rcNotify;
        _lastStartRect = rcStart;
        _lastForeignLeft = foreignLeft;

        int barHeight = rcTaskbar.Height;
        // On Win11 22H2+ touch devices Shell_TrayWnd is taller than the visible bar; the
        // Start button height tracks the visible part (TrafficMonitor's fix).
        int visibleHeight = rcStart.Height > 0 && rcStart.Height <= barHeight ? rcStart.Height : barHeight;

        var size = Widget.ComputeSize(visibleHeight);

        // Sit immediately left of the tray area. Win11 secondary taskbars have no
        // TrayNotifyWnd — reserve ~88 DIP for the clock, as TrafficMonitor does.
        int notifyX = rcNotify.Width > 0
            ? rcNotify.Left - rcTaskbar.Left
            : rcTaskbar.Width - Dpi(88);
        // Other taskbar-injected widgets (e.g. TrafficMonitor) claim the same spot —
        // anchor left of the leftmost one instead of stacking on top of it.
        int anchorX = Math.Min(notifyX, foreignLeft);
        int x = anchorX - size.Width - Dpi(4) - Dpi(_config.HorizontalOffsetPx);
        int y = (visibleHeight - size.Height) / 2 + (barHeight - visibleHeight);

        if (_embedded)
        {
            NativeMethods.MoveWindow(Widget.Handle, x, y, size.Width, size.Height, true);
        }
        else
        {
            NativeMethods.SetWindowPos(Widget.Handle, NativeMethods.HWND_TOPMOST,
                rcTaskbar.Left + x, rcTaskbar.Top + y, size.Width, size.Height,
                NativeMethods.SWP_NOACTIVATE);
        }
        Widget.LayoutDirty = false;
    }

    /// <summary>
    /// Leftmost X (taskbar-relative) of any other injected widget in the taskbar's right
    /// region, or int.MaxValue when there is none. Injected widgets (TrafficMonitor, etc.)
    /// are popup-style windows reparented into the taskbar — genuine shell elements have
    /// WS_CHILD, so the style bit cleanly separates the two.
    /// </summary>
    private int FindLeftmostInjectedWidgetX(NativeMethods.RECT rcTaskbar)
    {
        int best = int.MaxValue;
        NativeMethods.EnumChildWindows(_taskbar.Handle, (hwnd, _) =>
        {
            if (hwnd == Widget.Handle)
                return true;
            if (NativeMethods.GetAncestor(hwnd, NativeMethods.GA_PARENT) != _taskbar.Handle)
                return true; // only direct children; skips the XAML subtree
            if (!NativeMethods.IsWindowVisible(hwnd))
                return true;
            long style = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_STYLE).ToInt64();
            if ((style & NativeMethods.WS_CHILD) != 0)
                return true; // genuine shell element
            if (!NativeMethods.GetWindowRect(hwnd, out var rc))
                return true;
            int left = rc.Left - rcTaskbar.Left;
            // Sanity: only widget-sized windows parked in the right half count.
            if (left <= rcTaskbar.Width / 2 || rc.Width <= 0 || rc.Width > rcTaskbar.Width / 2)
                return true;
            best = Math.Min(best, left);
            return true;
        }, IntPtr.Zero);
        return best;
    }

    private int Dpi(int px) => (int)Math.Round(px * _dpi / 96.0);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _timer.Dispose();
        if (NativeMethods.IsWindow(Widget.Handle))
            NativeMethods.SetParent(Widget.Handle, IntPtr.Zero);
        Widget.Dispose();
    }
}
