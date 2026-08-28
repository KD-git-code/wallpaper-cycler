using System.Drawing;
using System.Drawing.Drawing2D;
using WallpaperCycle.Native;
using WallpaperCycle.Services;
using Forms = System.Windows.Forms;

namespace WallpaperCycle;

internal sealed class TrayService : IDisposable
{
    private readonly Forms.NotifyIcon _icon;
    private readonly Forms.ToolStripMenuItem _pauseItem;
    private readonly Forms.ToolStripMenuItem _pinItem;
    private readonly Icon _ownedIcon;

    public TrayService()
    {
        _ownedIcon = CreateIcon();
        _pauseItem = new Forms.ToolStripMenuItem("Pause");
        _pauseItem.Click += (_, _) =>
        {
            var paused = !_pauseItem.Checked;
            _pauseItem.Checked = paused;
            _pauseItem.Text = paused ? "Resume" : "Pause";
            PauseToggled?.Invoke(paused);
        };

        _pinItem = new Forms.ToolStripMenuItem("Pin current wallpaper");
        _pinItem.Click += (_, _) =>
        {
            var pinned = !_pinItem.Checked;
            _pinItem.Checked = pinned;
            _pinItem.Text = pinned ? "Unpin wallpaper" : "Pin current wallpaper";
            PinToggled?.Invoke(pinned);
        };

        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open settings", null, (_, _) => OpenSettingsRequested?.Invoke());
        menu.Items.Add("Change wallpaper now", null, (_, _) => NextRequested?.Invoke());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(_pinItem);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new Forms.NotifyIcon
        {
            Icon = _ownedIcon,
            Visible = true,
            Text = "WallpaperCycle",
            ContextMenuStrip = menu
        };
        _icon.DoubleClick += (_, _) => OpenSettingsRequested?.Invoke();
    }

    public event Action? OpenSettingsRequested;
    public event Action? NextRequested;
    public event Action<bool>? PauseToggled;
    public event Action<bool>? PinToggled;
    public event Action? ExitRequested;

    public void UpdateStatus(SchedulerStatus status)
    {
        _pinItem.Checked = status.IsPinned;
        _pinItem.Text = status.IsPinned ? "Unpin wallpaper" : "Pin current wallpaper";

        var next = status.NextChangeLocal is { } time
            ? time.ToString("t")
            : (status.IsPinned ? "pinned" : "paused");
        var text = "WallpaperCycle — " + status.Message + " | next " + next;
        if (text.Length > 63)
            text = text[..63];
        _icon.Text = text;
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _ownedIcon.Dispose();
    }

    private static Icon CreateIcon()
    {
        using var bitmap = new Bitmap(32, 32);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);
        using var fill = new SolidBrush(Color.FromArgb(255, 96, 205, 255));
        graphics.FillEllipse(fill, 2, 2, 28, 28);
        using var ring = new Pen(Color.White, 2.4f);
        graphics.DrawEllipse(ring, 8, 8, 16, 16);
        graphics.DrawEllipse(ring, 12, 12, 8, 8);
        var handle = bitmap.GetHicon();
        using var temp = Icon.FromHandle(handle);
        var icon = (Icon)temp.Clone();
        NativeMethods.DestroyIcon(handle);
        return icon;
    }
}
