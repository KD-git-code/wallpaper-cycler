using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using WallpaperCycle.Native;
using Forms = System.Windows.Forms;
using DrawingImage = System.Drawing.Image;
using DrawingBitmap = System.Drawing.Bitmap;
using DrawingGraphics = System.Drawing.Graphics;
using DrawingRectangle = System.Drawing.Rectangle;
using DrawingColor = System.Drawing.Color;

namespace WallpaperCycle.Services;

internal sealed class TransitionController
{
    private readonly DesktopWallpaperService _wallpaper;

    public TransitionController(DesktopWallpaperService wallpaper)
    {
        _wallpaper = wallpaper;
    }

    public async Task PlayAsync(string? oldPath, string newPath, int durationMs, CancellationToken cancellationToken)
    {
        Logger.Info($"Transition start: old='{oldPath}' new='{newPath}' durationMs={durationMs}");

        var screens = Forms.Screen.AllScreens;
        if (screens.Length == 0)
        {
            _wallpaper.Apply(newPath);
            return;
        }

        var host = DesktopLayer.TryAcquire();
        Logger.Info(host is null
            ? "Desktop host: unavailable"
            : $"Desktop host: parent=0x{host.Value.Parent.ToInt64():X} insertAfter=0x{host.Value.InsertAfter.ToInt64():X}");

        var sessions = new List<MonitorSession>(screens.Length);

        try
        {
            foreach (var screen in screens)
            {
                var bounds = new RECT
                {
                    Left = screen.Bounds.Left,
                    Top = screen.Bounds.Top,
                    Right = screen.Bounds.Right,
                    Bottom = screen.Bounds.Bottom
                };

                Logger.Info($"Monitor {screen.DeviceName}: {bounds.Width}x{bounds.Height} at ({bounds.Left},{bounds.Top})");

                var oldBmp = LoadCoverBitmap(oldPath, bounds.Width, bounds.Height);
                var newBmp = LoadCoverBitmap(newPath, bounds.Width, bounds.Height);
                if (newBmp is null)
                    throw new InvalidOperationException("Failed to decode the next wallpaper: " + newPath);

                // Fallback solid if old missing.
                oldBmp ??= new DrawingBitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
                if (oldBmp.Width != bounds.Width || oldBmp.Height != bounds.Height)
                {
                    // already cover-scaled in LoadCoverBitmap
                }

                Logger.Info($"Images loaded: old={oldBmp.Width}x{oldBmp.Height} new={newBmp.Width}x{newBmp.Height}");

                var session = MonitorSession.Create(bounds, oldBmp, newBmp, host);
                sessions.Add(session);
                Logger.Info($"Session hwnd=0x{session.Hwnd.ToInt64():X} behindIcons={session.BehindIcons}");
            }

            // Present radius=0 (old wallpaper only) under the icons — several
            // frames so the cover is solid before we touch the real wallpaper.
            for (var i = 0; i < 8; i++)
            {
                foreach (var s in sessions)
                {
                    s.Present(0);
                    s.ReassertBehindIcons();
                }
                await Task.Delay(16, cancellationToken).ConfigureAwait(true);
            }

            // Phase A: commit under static OLD cover. Immediately re-blit +
            // reassert z-order so any one-frame shell paint of the NEW image
            // stays under our child (that was the start flicker).
            Logger.Info("Phase A: committing wallpaper under static old cover");
            _wallpaper.Apply(newPath);

            // Immediate aggressive re-cover right after the call
            for (var i = 0; i < 12; i++)
            {
                foreach (var s in sessions)
                {
                    s.Present(0);
                    s.ReassertBehindIcons();
                }
                await Task.Delay(8, cancellationToken).ConfigureAwait(true);
            }

            // Remaining settle — shorter now that the post-Apply burst covers the flash.
            const int settleMs = 300;
            const int stepMs = 33;
            var settleSteps = settleMs / stepMs;
            for (var i = 0; i < settleSteps; i++)
            {
                await Task.Delay(stepMs, cancellationToken).ConfigureAwait(true);
                foreach (var s in sessions)
                {
                    s.Present(0);
                    s.ReassertBehindIcons();
                }
            }
            Logger.Info("Phase A settle complete");

            // Phase B: iris is cosmetic only — real desktop is already new.
            Logger.Info("Phase B: animation begin");
            await AnimateAsync(sessions, durationMs, cancellationToken).ConfigureAwait(true);
            Logger.Info("Phase B: animation complete");

            // Hold the final frame briefly so the last iris radius is solid.
            foreach (var s in sessions)
                s.Present(s.MaxRadius);

            for (var i = 0; i < 3; i++)
            {
                await Task.Delay(30, cancellationToken).ConfigureAwait(true);
                foreach (var s in sessions)
                {
                    s.Present(s.MaxRadius);
                    s.ReassertBehindIcons();
                }
            }

            // Phase C: soft remove; desktop already matches the final frame.
            foreach (var s in sessions)
                s.HideSoft();
            await Task.Delay(50, cancellationToken).ConfigureAwait(true);

            Logger.Info("Transition finished");
        }
        catch (Exception ex)
        {
            Logger.Error("Transition failed", ex);
            throw;
        }
        finally
        {
            foreach (var s in sessions)
                s.Dispose();
            Logger.Info("Overlay windows disposed");
        }
    }

    private static async Task AnimateAsync(List<MonitorSession> sessions, int durationMs, CancellationToken cancellationToken)
    {
        var duration = TimeSpan.FromMilliseconds(Math.Max(250, durationMs));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var frameCount = 0;
        var frameBudget = TimeSpan.FromMilliseconds(1000.0 / 60.0);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var elapsed = clock.Elapsed;
            var t = Math.Clamp(elapsed.TotalMilliseconds / duration.TotalMilliseconds, 0.0, 1.0);
            var eased = 1.0 - Math.Pow(1.0 - t, 3.0);

            foreach (var s in sessions)
                s.Present(eased * s.MaxRadius);

            frameCount++;
            if (frameCount == 1 || frameCount % 15 == 0 || t >= 1.0)
                Logger.Info($"Frame {frameCount}: t={t:F3} radius={eased * sessions[0].MaxRadius:F1}");

            if (t >= 1.0)
                break;

            var next = frameBudget - (clock.Elapsed - elapsed);
            if (next > TimeSpan.Zero)
                await Task.Delay(next, cancellationToken).ConfigureAwait(true);
            else
                await Dispatcher.Yield(DispatcherPriority.Background);
        }

        Logger.Info($"Animation frames presented: {frameCount}");
    }

    private static DrawingBitmap? LoadCoverBitmap(string? path, int pixelWidth, int pixelHeight)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            Logger.Warn($"Image path missing: '{path}'");
            return null;
        }

        try
        {
            using var src = DrawingImage.FromFile(path);
            var bmp = new DrawingBitmap(pixelWidth, pixelHeight, PixelFormat.Format32bppPArgb);
            using (var g = DrawingGraphics.FromImage(bmp))
            {
                g.Clear(DrawingColor.Black);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                var scale = Math.Max((double)pixelWidth / src.Width, (double)pixelHeight / src.Height);
                var w = (int)Math.Round(src.Width * scale);
                var h = (int)Math.Round(src.Height * scale);
                var x = (pixelWidth - w) / 2;
                var y = (pixelHeight - h) / 2;
                g.DrawImage(src, x, y, w, h);
            }
            Logger.Info($"Decoded '{Path.GetFileName(path)}' -> {pixelWidth}x{pixelHeight}");
            return bmp;
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load image " + path, ex);
            return null;
        }
    }

    private sealed class MonitorSession : IDisposable
    {
        private const string WindowClassName = "WallpaperCycleTransitionHost";
        private static bool _classRegistered;
        private static NativeMethods.WndProc? _wndProcKeepAlive;

        private static void EnsureWindowClass()
        {
            if (_classRegistered)
                return;

            // Keep the delegate alive for the lifetime of the process.
            _wndProcKeepAlive = static (hWnd, msg, wParam, lParam) =>
                NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                style = 0,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_wndProcKeepAlive),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = NativeMethods.GetModuleHandle(null),
                hIcon = IntPtr.Zero,
                hCursor = IntPtr.Zero,
                hbrBackground = IntPtr.Zero, // no background erase -> no white flash
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero
            };

            var atom = NativeMethods.RegisterClassEx(ref wc);
            if (atom == 0)
            {
                var err = NativeMethods.GetLastError();
                // 1410 = class already exists
                if (err != 1410)
                    throw new InvalidOperationException("RegisterClassEx failed err=" + err);
            }
            _classRegistered = true;
            Logger.Info("Registered transition window class (no background brush).");
        }

        private readonly RECT _bounds;
        private readonly DrawingBitmap _oldBmp;
        private readonly DrawingBitmap _newBmp;
        private readonly DrawingBitmap _frame;
        private IntPtr _hwnd;
        private readonly IntPtr _screenDc;
        private readonly IntPtr _memDc;
        private readonly IntPtr _dib;
        private readonly IntPtr _bits;
        private readonly IntPtr _oldSelect;
        private bool _disposed;

        private MonitorSession(
            RECT bounds,
            DrawingBitmap oldBmp,
            DrawingBitmap newBmp,
            IntPtr hwnd,
            IntPtr screenDc,
            IntPtr memDc,
            IntPtr dib,
            IntPtr bits,
            IntPtr oldSelect,
            bool behindIcons)
        {
            _bounds = bounds;
            _oldBmp = oldBmp;
            _newBmp = newBmp;
            _hwnd = hwnd;
            _screenDc = screenDc;
            _memDc = memDc;
            _dib = dib;
            _bits = bits;
            _oldSelect = oldSelect;
            BehindIcons = behindIcons;
            MaxRadius = Math.Sqrt((bounds.Width / 2.0) * (bounds.Width / 2.0) + (bounds.Height / 2.0) * (bounds.Height / 2.0));
            _frame = new DrawingBitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppPArgb);
        }

        public IntPtr Hwnd => _hwnd;
        public double MaxRadius { get; }
        public bool BehindIcons { get; }

        public static MonitorSession Create(RECT bounds, DrawingBitmap oldBmp, DrawingBitmap newBmp, DesktopLayer.Host? host)
        {
            var w = bounds.Width;
            var h = bounds.Height;

            EnsureWindowClass();

            // Create a layered popup first; reparent to Progman if host is available.
            // WS_EX_NOREDIRECTIONBITMAP helps on Windows 11 raised-desktop mode.
            var ex = NativeMethods.WS_EX_LAYERED | NativeMethods.WS_EX_NOACTIVATE |
                     NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TRANSPARENT;
            var style = NativeMethods.WS_POPUP | NativeMethods.WS_VISIBLE;

            var hwnd = NativeMethods.CreateWindowEx(
                ex,
                WindowClassName,
                "WallpaperCycleTransition",
                style,
                bounds.Left,
                bounds.Top,
                w,
                h,
                IntPtr.Zero,
                IntPtr.Zero,
                NativeMethods.GetModuleHandle(null),
                IntPtr.Zero);

            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException(
                    "CreateWindowEx failed err=" + NativeMethods.GetLastError());

            bool behindIcons = false;
            if (host is { } desktopHost)
            {
                DesktopLayer.Attach(hwnd, desktopHost, bounds);
                behindIcons = true;
                Logger.Info("Overlay attached under desktop icons (GDI layered child).");
            }
            else
            {
                // Topmost uses UpdateLayeredWindow (app-managed). Do NOT call
                // SetLayeredWindowAttributes — that switches to system-managed mode
                // and makes ULW a no-op (invisible bridge → snap/fade on hide).
                NativeMethods.SetWindowPos(
                    hwnd,
                    NativeMethods.HWND_TOPMOST,
                    bounds.Left, bounds.Top, w, h,
                    NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW |
                    NativeMethods.SWP_FRAMECHANGED);
                Logger.Info("GDI overlay topmost (ULW mode, no LWA).");
            }

            var screenDc = NativeMethods.GetDC(IntPtr.Zero);
            var memDc = NativeMethods.CreateCompatibleDC(screenDc);

            var bmi = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = w,
                    biHeight = -h, // top-down
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = NativeMethods.BI_RGB
                }
            };

            var dib = NativeMethods.CreateDIBSection(memDc, ref bmi, NativeMethods.DIB_RGB_COLORS, out var bits, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || bits == IntPtr.Zero)
                throw new InvalidOperationException("CreateDIBSection failed.");

            var oldSelect = NativeMethods.SelectObject(memDc, dib);

            return new MonitorSession(bounds, oldBmp, newBmp, hwnd, screenDc, memDc, dib, bits, oldSelect, behindIcons);
        }

        public void Present(double radius)
        {
            if (_disposed)
                return;

            var w = _bounds.Width;
            var h = _bounds.Height;

            using (var g = DrawingGraphics.FromImage(_frame))
            {
                g.CompositingMode = CompositingMode.SourceCopy;
                g.InterpolationMode = InterpolationMode.NearestNeighbor;
                g.SmoothingMode = SmoothingMode.None;
                g.PixelOffsetMode = PixelOffsetMode.None;

                g.DrawImage(_oldBmp, 0, 0, w, h);

                if (radius > 0.5)
                {
                    g.CompositingMode = CompositingMode.SourceOver;
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    using var path = new GraphicsPath();
                    var d = (float)(radius * 2.0);
                    path.AddEllipse((float)(w / 2.0 - radius), (float)(h / 2.0 - radius), d, d);
                    g.SetClip(path);
                    g.DrawImage(_newBmp, 0, 0, w, h);
                    g.ResetClip();
                }
            }

            // Copy into the DIB selected into _memDc.
            var data = _frame.LockBits(
                new DrawingRectangle(0, 0, w, h),
                ImageLockMode.ReadOnly,
                PixelFormat.Format32bppPArgb);
            try
            {
                var rowBytes = w * 4;
                var buf = new byte[rowBytes];
                for (int y = 0; y < h; y++)
                {
                    Marshal.Copy(IntPtr.Add(data.Scan0, y * data.Stride), buf, 0, rowBytes);
                    for (int x = 0; x < w; x++)
                        buf[x * 4 + 3] = 255;
                    Marshal.Copy(buf, 0, IntPtr.Add(_bits, y * rowBytes), rowBytes);
                }
            }
            finally
            {
                _frame.UnlockBits(data);
            }

            if (BehindIcons)
            {
                // Progman child: LWA + BitBlt into window DC.
                var wndDc = NativeMethods.GetDC(_hwnd);
                if (wndDc == IntPtr.Zero)
                {
                    Logger.Warn($"GetDC failed for hwnd=0x{_hwnd.ToInt64():X}");
                    return;
                }
                try
                {
                    var ok = NativeMethods.BitBlt(wndDc, 0, 0, w, h, _memDc, 0, 0, NativeMethods.SRCCOPY);
                    if (!ok)
                        Logger.Warn($"BitBlt failed err={NativeMethods.GetLastError()} hwnd=0x{_hwnd.ToInt64():X}");
                }
                finally
                {
                    NativeMethods.ReleaseDC(_hwnd, wndDc);
                }
            }
            else
            {
                // Top-level topmost: UpdateLayeredWindow is reliable.
                var pptSrc = new POINT(0, 0);
                var blend = new BLENDFUNCTION
                {
                    BlendOp = NativeMethods.AC_SRC_OVER,
                    BlendFlags = 0,
                    SourceConstantAlpha = 255,
                    AlphaFormat = NativeMethods.AC_SRC_ALPHA
                };
                var size = new SIZE(w, h);
                var pptDst = new POINT(_bounds.Left, _bounds.Top);
                var sizePtr = Marshal.AllocHGlobal(Marshal.SizeOf<SIZE>());
                var dstPtr = Marshal.AllocHGlobal(Marshal.SizeOf<POINT>());
                try
                {
                    Marshal.StructureToPtr(size, sizePtr, false);
                    Marshal.StructureToPtr(pptDst, dstPtr, false);
                    // ULW requires app-managed layered mode — clear LWA by not using it;
                    // ensure WS_EX_LAYERED and push pixels.
                    var ok = NativeMethods.UpdateLayeredWindow(
                        _hwnd, _screenDc, dstPtr, sizePtr, _memDc,
                        ref pptSrc, 0, ref blend, NativeMethods.ULW_ALPHA);
                    if (!ok)
                        Logger.Warn($"UpdateLayeredWindow failed err={NativeMethods.GetLastError()} hwnd=0x{_hwnd.ToInt64():X}");
                }
                finally
                {
                    Marshal.FreeHGlobal(sizePtr);
                    Marshal.FreeHGlobal(dstPtr);
                }
            }
        }

        public void ReassertBehindIcons()
        {
            if (_disposed || _hwnd == IntPtr.Zero || !BehindIcons)
                return;

            var progman = NativeMethods.FindWindow("Progman", "Program Manager");
            if (progman == IntPtr.Zero)
                progman = NativeMethods.FindWindow("Progman", null);
            if (progman == IntPtr.Zero)
                return;

            var defView = NativeMethods.FindWindowEx(progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            var insertAfter = defView != IntPtr.Zero ? defView : NativeMethods.HWND_BOTTOM;

            // Force fully opaque so the cover never becomes translucent during the race.
            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 255, NativeMethods.LWA_ALPHA);
            NativeMethods.SetWindowPos(
                _hwnd,
                insertAfter,
                0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE |
                NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
        }

        public MonitorSession CreateTopMostBridge()
        {
            // Clone final frame into a top-level topmost layered window.
            var host = (DesktopLayer.Host?)null;
            var bridge = Create(_bounds, (DrawingBitmap)_oldBmp.Clone(), (DrawingBitmap)_newBmp.Clone(), host);
            // Create() with null host already places topmost; force full radius content.
            bridge.Present(bridge.MaxRadius);
            Logger.Info($"Topmost bridge hwnd=0x{bridge.Hwnd.ToInt64():X}");
            return bridge;
        }

        public void HideSoft()
        {
            if (_disposed || _hwnd == IntPtr.Zero)
                return;

            NativeMethods.SetLayeredWindowAttributes(_hwnd, 0, 0, NativeMethods.LWA_ALPHA);
            NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
        }

        public void Dispose()
        {
            if (_disposed)
                return;
            _disposed = true;

            try
            {
                if (_hwnd != IntPtr.Zero)
                {
                    NativeMethods.ShowWindow(_hwnd, NativeMethods.SW_HIDE);
                    NativeMethods.DestroyWindow(_hwnd);
                    _hwnd = IntPtr.Zero;
                }

                if (_oldSelect != IntPtr.Zero && _memDc != IntPtr.Zero)
                    NativeMethods.SelectObject(_memDc, _oldSelect);
                if (_dib != IntPtr.Zero)
                    NativeMethods.DeleteObject(_dib);
                if (_memDc != IntPtr.Zero)
                    NativeMethods.DeleteDC(_memDc);
                if (_screenDc != IntPtr.Zero)
                    NativeMethods.ReleaseDC(IntPtr.Zero, _screenDc);
            }
            catch (Exception ex)
            {
                Logger.Error("Session native dispose failed", ex);
            }

            _frame.Dispose();
            _oldBmp.Dispose();
            _newBmp.Dispose();
        }
    }
}
