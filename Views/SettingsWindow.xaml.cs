using System.IO;
using System.Windows;
using WallpaperCycle.Services;
using Forms = System.Windows.Forms;

namespace WallpaperCycle.Views;

internal partial class SettingsWindow : Window
{
    private readonly WallpaperScheduler _scheduler;
    private bool _suppressChipSync;

    public SettingsWindow(WallpaperScheduler scheduler)
    {
        _scheduler = scheduler;
        InitializeComponent();
        LoadFromSettings();
        RestoreWindowPlacement();
        Closed += OnClosed;
    }

    public void UpdateStatus(SchedulerStatus status)
    {
        var next = status.NextChangeLocal is { } time
            ? " Next change at " + time.ToString("t") + "."
            : string.Empty;
        var file = string.IsNullOrWhiteSpace(status.CurrentWallpaper)
            ? "none"
            : Path.GetFileName(status.CurrentWallpaper);
        StatusText.Text = status.Message + next + " Current: " + file;

        UpdateFolderFeedback(status.WallpaperCount, FolderBox.Text);
    }

    private void LoadFromSettings()
    {
        var s = _scheduler.Settings;
        FolderBox.Text = s.WallpaperFolder;
        IntervalSlider.Value = s.IntervalMinutes;
        DurationSlider.Value = s.AnimationDurationMs;
        FullscreenBox.IsChecked = s.PauseOnFullscreen;
        ShuffleBox.IsChecked = s.Shuffle;
        StartupBox.IsChecked = s.StartWithWindows;
        IntervalLabel.Text = s.IntervalMinutes + " min";
        DurationLabel.Text = (s.AnimationDurationMs / 1000.0).ToString("0.00") + " s";
        SyncChipsFromInterval(s.IntervalMinutes);
        UpdateFolderFeedback(CountImagesInFolder(s.WallpaperFolder), s.WallpaperFolder);
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalLabel is null) return;
        var mins = (int)e.NewValue;
        IntervalLabel.Text = mins + " min";
        if (!_suppressChipSync)
            SyncChipsFromInterval(mins);
    }

    private void Duration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DurationLabel is null) return;
        DurationLabel.Text = (e.NewValue / 1000.0).ToString("0.00") + " s";
    }

    private void ChipTest_Click(object sender, RoutedEventArgs e)
    {
        _suppressChipSync = true;
        ChipTest.IsChecked = true;
        ChipDaily.IsChecked = false;
        IntervalSlider.Value = 1;
        _suppressChipSync = false;
    }

    private void ChipDaily_Click(object sender, RoutedEventArgs e)
    {
        _suppressChipSync = true;
        ChipDaily.IsChecked = true;
        ChipTest.IsChecked = false;
        IntervalSlider.Value = 35;
        _suppressChipSync = false;
    }

    private void SyncChipsFromInterval(int minutes)
    {
        ChipTest.IsChecked = minutes == 1;
        ChipDaily.IsChecked = minutes == 35;
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the folder that contains wallpaper images",
            SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : @"C:\Wallpapers",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
        {
            FolderBox.Text = dialog.SelectedPath;
            UpdateFolderFeedback(CountImagesInFolder(dialog.SelectedPath), dialog.SelectedPath);
        }
    }

    private void Refresh_Click(object sender, RoutedEventArgs e)
    {
        var path = FolderBox.Text.Trim();
        UpdateFolderFeedback(CountImagesInFolder(path), path);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var s = _scheduler.Settings;
        s.WallpaperFolder = FolderBox.Text.Trim();
        s.IntervalMinutes = (int)IntervalSlider.Value;
        s.AnimationDurationMs = (int)DurationSlider.Value;
        s.PauseOnFullscreen = FullscreenBox.IsChecked == true;
        s.Shuffle = ShuffleBox.IsChecked == true;
        s.StartWithWindows = StartupBox.IsChecked == true;
        PersistWindowPlacement(s);
        _scheduler.ApplySettings();
        UpdateFolderFeedback(CountImagesInFolder(s.WallpaperFolder), s.WallpaperFolder);
    }

    private async void ChangeNow_Click(object sender, RoutedEventArgs e)
    {
        ChangeNowButton.IsEnabled = false;
        try
        {
            await _scheduler.ChangeNowAsync();
        }
        finally
        {
            ChangeNowButton.IsEnabled = true;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        if (e.ClickCount == 2)
        {
            // optional: ignore double-click maximize for a settings dialog
            return;
        }
        try { DragMove(); }
        catch { /* ignore if already moving */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PersistWindowPlacement(_scheduler.Settings);
        SettingsStore.Save(_scheduler.Settings);
    }

    // ── Empty / error state helpers ─────────────────────────────

    private void UpdateFolderFeedback(int count, string folder)
    {
        CountText.Text = count + " image(s) found";

        if (string.IsNullOrWhiteSpace(folder))
        {
            ShowEmptyState("No folder selected",
                "Choose a folder that contains wallpaper images.");
            return;
        }

        if (!Directory.Exists(folder))
        {
            ShowEmptyState("Folder not found",
                "The path does not exist or is not accessible. Choose a different location.");
            CountText.Text = "Folder inaccessible";
            CountText.Foreground = (System.Windows.Media.Brush)FindResource("DangerBrush");
            return;
        }

        CountText.Foreground = (System.Windows.Media.Brush)FindResource("MutedBrush");

        if (count == 0)
        {
            ShowEmptyState("No images found",
                "Add JPG, PNG, BMP, WebP or TIFF files to this folder, or choose a different location.");
        }
        else
        {
            EmptyStatePanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ShowEmptyState(string title, string message)
    {
        EmptyStateTitle.Text = title;
        EmptyStateMessage.Text = message;
        EmptyStatePanel.Visibility = Visibility.Visible;
    }

    private static int CountImagesInFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return 0;

        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".jpg", ".jpeg", ".png", ".bmp", ".jfif", ".webp", ".tif", ".tiff"
        };

        try
        {
            return Directory.EnumerateFiles(folder)
                .Count(p => extensions.Contains(Path.GetExtension(p)));
        }
        catch
        {
            return 0;
        }
    }

    // ── Window placement persistence ────────────────────────────

    private void RestoreWindowPlacement()
    {
        var s = _scheduler.Settings;
        if (s.WindowWidth > 200 && s.WindowHeight > 200)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (s.WindowLeft is double left && s.WindowTop is double top)
        {
            // Only restore if the point is on a visible screen
            var onScreen = Forms.Screen.AllScreens.Any(sc =>
                sc.WorkingArea.Contains((int)left + 20, (int)top + 20));
            if (onScreen)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = left;
                Top = top;
            }
        }
    }

    private void PersistWindowPlacement(AppSettings s)
    {
        if (WindowState == WindowState.Normal)
        {
            s.WindowLeft = Left;
            s.WindowTop = Top;
            s.WindowWidth = Width;
            s.WindowHeight = Height;
        }
    }
}
