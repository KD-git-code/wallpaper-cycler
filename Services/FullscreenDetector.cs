using System.Diagnostics;
using System.Text;
using WallpaperCycle.Native;

namespace WallpaperCycle.Services;

internal sealed class FullscreenDetector
{
    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "NotifyIconOverflowWindow",
        "Windows.UI.Core.CoreWindow",
        "XamlExplorerHostIslandWindow",
        "ForegroundStaging",
        "MultitaskingViewFrame",
        "TaskSwitcherWnd",
        "TopLevelWindowForOverflowXamlIsland"
    };

    public bool IsFullscreenAppRunning()
    {
        if (NativeMethods.SHQueryUserNotificationState(out var state) == 0)
        {
            if (state is QueryUserNotificationState.QUNS_RUNNING_D3D_FULL_SCREEN
                or QueryUserNotificationState.QUNS_BUSY
                or QueryUserNotificationState.QUNS_PRESENTATION_MODE
                or QueryUserNotificationState.QUNS_NOT_PRESENT)
            {
                return true;
            }
        }

        var hwnd = NativeMethods.GetForegroundWindow();
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindowVisible(hwnd))
            return false;

        if (IsCloaked(hwnd))
            return false;

        NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == 0 || pid == Environment.ProcessId)
            return false;

        var className = GetClassName(hwnd);
        if (IgnoredClasses.Contains(className))
            return false;

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (name.Equals("explorer", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("SearchHost", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("ShellExperienceHost", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        catch
        {
            return false;
        }

        if (!NativeMethods.GetWindowRect(hwnd, out var windowRect))
            return false;

        var monitor = NativeMethods.MonitorFromWindow(hwnd, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = MarshalSizeOfMonitorInfo() };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info))
            return false;

        return Covers(windowRect, info.rcMonitor);
    }

    private static bool Covers(RECT window, RECT monitor)
    {
        const int tolerance = 8;
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
               && cloaked != 0;
    }

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static int MarshalSizeOfMonitorInfo() => System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>();
}
