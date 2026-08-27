using WallpaperCycle.Native;

namespace WallpaperCycle.Services;

internal static class DesktopLayer
{
    public readonly record struct Host(IntPtr Parent, IntPtr InsertAfter);

    public static Host? TryAcquire()
    {
        var progman = NativeMethods.FindWindow("Progman", "Program Manager");
        if (progman == IntPtr.Zero)
            progman = NativeMethods.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Logger.Warn("Progman window was not found.");
            return null;
        }

        Logger.Info($"Progman=0x{progman.ToInt64():X}");

        NativeMethods.SendMessageTimeout(
            progman,
            NativeMethods.WM_SPAWN_WORKER,
            new IntPtr(0xD),
            new IntPtr(0x1),
            NativeMethods.SMTO_NORMAL,
            1000,
            out _);

        NativeMethods.SendMessageTimeout(
            progman,
            NativeMethods.WM_SPAWN_WORKER,
            IntPtr.Zero,
            IntPtr.Zero,
            NativeMethods.SMTO_NORMAL,
            1000,
            out _);

        var raised = (NativeMethods.GetWindowExStyle(progman) & NativeMethods.WS_EX_NOREDIRECTIONBITMAP) != 0;
        var defView = FindDefView(progman);
        var workerW = FindWallpaperWorkerW(progman, defView);

        Logger.Info($"RaisedDesktop={raised} DefView=0x{defView.ToInt64():X} WorkerW=0x{workerW.ToInt64():X}");

        if (raised)
        {
            // Win11 raised desktop: child of Progman, z-ordered under DefView (icons)
            // and above the wallpaper WorkerW. Microsoft: use LWA alpha=255.
            var parent = progman;
            var insertAfter = defView != IntPtr.Zero ? defView : workerW;
            if (insertAfter == IntPtr.Zero)
                insertAfter = NativeMethods.HWND_BOTTOM;
            return new Host(parent, insertAfter);
        }

        if (workerW != IntPtr.Zero)
            return new Host(workerW, IntPtr.Zero);

        return new Host(progman, defView != IntPtr.Zero ? defView : NativeMethods.HWND_BOTTOM);
    }

    public static void Attach(IntPtr hwnd, Host host, RECT monitorBounds)
    {
        var style = NativeMethods.GetWindowStyle(hwnd);
        style &= ~NativeMethods.WS_POPUP;
        style |= NativeMethods.WS_CHILD | NativeMethods.WS_VISIBLE |
                 NativeMethods.WS_CLIPSIBLINGS | NativeMethods.WS_CLIPCHILDREN;
        NativeMethods.SetWindowStyle(hwnd, style);

        var ex = NativeMethods.GetWindowExStyle(hwnd);
        NativeMethods.SetWindowExStyle(
            hwnd,
            ex | NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE |
            NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT);

        NativeMethods.SetParent(hwnd, host.Parent);

        // Required on raised-desktop Progman: system-managed layered with opaque alpha.
        // Content is then painted via BitBlt/StretchDIBits into the window DC.
        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, NativeMethods.LWA_ALPHA);

        NativeMethods.GetWindowRect(host.Parent, out var parentRect);
        var x = monitorBounds.Left - parentRect.Left;
        var y = monitorBounds.Top - parentRect.Top;

        var flags = NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                    NativeMethods.SWP_FRAMECHANGED;
        var insertAfter = host.InsertAfter;
        if (insertAfter == IntPtr.Zero)
            flags |= NativeMethods.SWP_NOZORDER;

        NativeMethods.SetWindowPos(
            hwnd,
            insertAfter,
            x,
            y,
            monitorBounds.Width,
            monitorBounds.Height,
            flags);

        Logger.Info($"Attach hwnd=0x{hwnd.ToInt64():X} parent=0x{host.Parent.ToInt64():X} pos=({x},{y}) size={monitorBounds.Width}x{monitorBounds.Height} (LWA mode)");
    }

    private static IntPtr FindDefView(IntPtr progman)
    {
        var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView != IntPtr.Zero)
            return defView;

        IntPtr found = IntPtr.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            var child = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (child != IntPtr.Zero)
            {
                found = child;
                return false;
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr FindWallpaperWorkerW(IntPtr progman, IntPtr defView)
    {
        IntPtr worker = IntPtr.Zero;
        var child = IntPtr.Zero;
        while ((child = NativeMethods.FindWindowEx(progman, child, "WorkerW", null)) != IntPtr.Zero)
        {
            var nestedDefView = NativeMethods.FindWindowEx(child, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (nestedDefView == IntPtr.Zero)
                worker = child;
        }

        if (worker != IntPtr.Zero)
            return worker;

        IntPtr workerAfterDefView = IntPtr.Zero;
        NativeMethods.EnumWindows((top, _) =>
        {
            var shellView = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (shellView != IntPtr.Zero)
                workerAfterDefView = NativeMethods.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            return true;
        }, IntPtr.Zero);

        if (workerAfterDefView != IntPtr.Zero)
            return workerAfterDefView;

        return NativeMethods.FindWindowEx(progman, defView, "WorkerW", null);
    }
}
