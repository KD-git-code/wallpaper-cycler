using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using WallpaperCycle.Native;

namespace WallpaperCycle.Services;

internal enum DesktopWallpaperPosition
{
    Center = 0,
    Tile = 1,
    Stretch = 2,
    Fit = 3,
    Fill = 4,
    Span = 5
}

[ComImport]
[Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IDesktopWallpaper
{
    void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);
    void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);
    void GetMonitorDevicePathAt(uint monitorIndex, [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);
    void GetMonitorDevicePathCount(out uint count);
    void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
    void SetBackgroundColor(uint color);
    void GetBackgroundColor(out uint color);
    void SetPosition(DesktopWallpaperPosition position);
    void GetPosition(out DesktopWallpaperPosition position);
    void SetSlideshow(IntPtr items);
    void GetSlideshow(out IntPtr items);
    void SetSlideshowOptions(uint options, uint slideshowTick);
    void GetSlideshowOptions(out uint options, out uint slideshowTick);
    void AdvanceSlideshow([MarshalAs(UnmanagedType.LPWStr)] string? monitorID, uint direction);
    void GetStatus(out uint state);
    void Enable([MarshalAs(UnmanagedType.Bool)] bool enable);
}

[ComImport]
[Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD")]
internal class DesktopWallpaperCom { }

internal sealed class DesktopWallpaperService
{
    private readonly IDesktopWallpaper _api = (IDesktopWallpaper)new DesktopWallpaperCom();

    public string? GetCurrentWallpaper()
    {
        try
        {
            _api.GetMonitorDevicePathCount(out var count);
            if (count == 0)
            {
                _api.GetWallpaper(null, out var any);
                return string.IsNullOrWhiteSpace(any) ? null : any;
            }

            _api.GetMonitorDevicePathAt(0, out var id);
            _api.GetWallpaper(id, out var path);
            return string.IsNullOrWhiteSpace(path) ? null : path;
        }
        catch (Exception ex)
        {
            Logger.Error("GetCurrentWallpaper failed", ex);
            return null;
        }
    }

    /// <summary>
    /// Write desktop style keys only — does not call SPI or IDesktopWallpaper,
    /// so the visible desktop does not change (no flicker).
    /// </summary>
    public void PrepareSilent(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Wallpaper file was not found.", imagePath);

        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop", writable: true);
            if (key is not null)
            {
                key.SetValue("WallpaperStyle", "10");
                key.SetValue("TileWallpaper", "0");
                key.SetValue("Wallpaper", imagePath);
            }
            Logger.Info("Silent registry prepare: " + imagePath);
        }
        catch (Exception ex)
        {
            Logger.Error("PrepareSilent registry write failed", ex);
        }
    }

    public void Apply(string imagePath)
    {
        if (!File.Exists(imagePath))
            throw new FileNotFoundException("Wallpaper file was not found.", imagePath);

        // Still write the registry keys (style + path) – this is silent.
        PrepareSilent(imagePath);

        try
        {
            _api.SetPosition(DesktopWallpaperPosition.Fill);

            _api.GetMonitorDevicePathCount(out var count);
            if (count == 0)
            {
                _api.SetWallpaper(null, imagePath);
            }
            else
            {
                for (uint i = 0; i < count; i++)
                {
                    _api.GetMonitorDevicePathAt(i, out var id);
                    _api.SetWallpaper(id, imagePath);
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("IDesktopWallpaper.SetWallpaper failed", ex);
        }

        // IMPORTANT: do NOT call SPI_SETDESKWALLPAPER with SPIF_SENDCHANGE.
        // IDesktopWallpaper.SetWallpaper is already sufficient to update the live
        // desktop. Calling SPI with SENDCHANGE forces an immediate, visible
        // repaint that races with the overlay and produces the start flicker.
        // If persistence on very old Windows is required, use only UPDATEINIFILE:
        //
        // NativeMethods.SystemParametersInfo(
        //     NativeMethods.SPI_SETDESKWALLPAPER,
        //     0,
        //     imagePath,
        //     NativeMethods.SPIF_UPDATEINIFILE);

        Logger.Info("Wallpaper commit requested (silent): " + imagePath);
    }

    public void ApplyInstant(string imagePath) => Apply(imagePath);
}
