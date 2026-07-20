namespace PingMeter.Taskbar;

internal sealed class TaskbarInfo
{
    public required IntPtr Handle { get; init; }

    /// <summary>Clock/tray area. IntPtr.Zero on Win11 secondary taskbars.</summary>
    public IntPtr TrayNotify { get; init; }

    /// <summary>Start button; its height tracks the visible bar on Win11 22H2+ tall taskbars.</summary>
    public IntPtr Start { get; init; }

    public bool IsSecondary { get; init; }
}

internal static class TaskbarLocator
{
    public static TaskbarInfo? FindPrimary()
    {
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        return taskbar == IntPtr.Zero ? null : Describe(taskbar, isSecondary: false);
    }

    public static List<TaskbarInfo> FindSecondaries()
    {
        var handles = new List<IntPtr>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (NativeMethods.GetWindowClassName(hwnd) == "Shell_SecondaryTrayWnd")
                handles.Add(hwnd);
            return true;
        }, IntPtr.Zero);

        handles.Sort((a, b) =>
        {
            NativeMethods.GetWindowRect(a, out var ra);
            NativeMethods.GetWindowRect(b, out var rb);
            int byLeft = ra.Left.CompareTo(rb.Left);
            return byLeft != 0 ? byLeft : ra.Top.CompareTo(rb.Top);
        });

        return handles.Select(h => Describe(h, isSecondary: true)).ToList();
    }

    public static int CountTaskbars() => (FindPrimary() != null ? 1 : 0) + FindSecondaries().Count;

    /// <summary>
    /// True when the primary taskbar is the Win11 XAML one (vs Win10 / ExplorerPatcher classic).
    /// TrafficMonitor's detection: Win11 build + a DesktopWindowContentBridge child window.
    /// </summary>
    public static bool IsWin11XamlTaskbar()
    {
        if (Environment.OSVersion.Version.Build < 21996)
            return false;
        IntPtr taskbar = NativeMethods.FindWindow("Shell_TrayWnd", null);
        return taskbar != IntPtr.Zero &&
               NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "Windows.UI.Composition.DesktopWindowContentBridge", null) != IntPtr.Zero;
    }

    private static TaskbarInfo Describe(IntPtr taskbar, bool isSecondary) => new()
    {
        Handle = taskbar,
        TrayNotify = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "TrayNotifyWnd", null),
        Start = NativeMethods.FindWindowEx(taskbar, IntPtr.Zero, "Start", null),
        IsSecondary = isSecondary,
    };
}
