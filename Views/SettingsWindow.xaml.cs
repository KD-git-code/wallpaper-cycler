using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using WallpaperCycle.Services;
using Forms = System.Windows.Forms;

namespace WallpaperCycle.Views;

internal sealed class WallpaperListItem
{
    public required string Path { get; init; }
    public required string Name { get; init; }
    public BitmapImage? Thumbnail { get; init; }
}

internal partial class SettingsWindow : Window
{
    private readonly WallpaperScheduler _scheduler;
    private bool _suppressChipSync;
    private DateTime _popupClosedUtc;
    private bool _previewAnimating;
    private readonly ObservableCollection<WallpaperListItem> _listItems = new();

    public SettingsWindow(WallpaperScheduler scheduler)
    {
        _scheduler = scheduler;
        InitializeComponent();
        WallpaperList.ItemsSource = _listItems;
        SelectorPopup.Closed += SelectorPopup_Closed;
        LoadFromSettings();
        RestoreWindowPlacement();
        RefreshCurrentPreview(_scheduler.Settings.LastWallpaperPath);
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
        UpdatePinUi(status.IsPinned);
        RefreshCurrentPreview(status.CurrentWallpaper);
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
        UpdatePinUi(s.IsPinned);
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

    private void Pin_Click(object sender, RoutedEventArgs e)
    {
        var next = !_scheduler.Settings.IsPinned;
        _scheduler.SetPinned(next);
        UpdatePinUi(next);
    }

    private void UpdatePinUi(bool pinned)
    {
        if (PinButtonLabel is null) return;
        PinButtonLabel.Text = pinned ? "Unpin" : "Pin";
        PinButton.ToolTip = pinned
            ? "Unpin — resume auto-rotation"
            : "Pin this wallpaper (stops auto-rotation)";
    }

    private void Selector_Click(object sender, RoutedEventArgs e)
    {
        // StaysOpen=False closes the popup on the same click that hits this button,
        // which would immediately reopen it. Ignore opens that follow a close by <200ms.
        if (SelectorPopup.IsOpen)
        {
            SelectorPopup.IsOpen = false;
            return;
        }

        if ((DateTime.UtcNow - _popupClosedUtc).TotalMilliseconds < 200)
            return;

        RebuildWallpaperList();
        SelectorPopup.IsOpen = true;
    }

    private void SelectorPopup_Closed(object? sender, EventArgs e)
    {
        _popupClosedUtc = DateTime.UtcNow;
    }

    private async void WallpaperItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement fe || fe.Tag is not string path)
            return;

        SelectorPopup.IsOpen = false;

        if (WallpaperLibrary.PathsEqual(path, _scheduler.Settings.LastWallpaperPath))
            return;

        ChangeNowButton.IsEnabled = false;
        SelectorButton.IsEnabled = false;
        try
        {
            var oldPath = _scheduler.Settings.LastWallpaperPath;
            var duration = _scheduler.Settings.AnimationDurationMs;
            var preview = PlayPreviewIrisAsync(oldPath, path, duration);
            var desktop = _scheduler.ChangeToAsync(path);
            await Task.WhenAll(preview, desktop);
        }
        finally
        {
            ChangeNowButton.IsEnabled = true;
            SelectorButton.IsEnabled = true;
        }
    }

    private void RebuildWallpaperList()
    {
        _listItems.Clear();
        foreach (var path in _scheduler.Library.GetAll())
        {
            _listItems.Add(new WallpaperListItem
            {
                Path = path,
                Name = Path.GetFileName(path),
                Thumbnail = TryLoadThumbnail(path, 96)
            });
        }
    }

    private void RefreshCurrentPreview(string? path)
    {
        if (_previewAnimating)
            return;
        if (CurrentNameText is null || PreviewImage is null || PreviewPlaceholder is null)
            return;

        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            CurrentNameText.Text = "No wallpaper selected";
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        CurrentNameText.Text = Path.GetFileName(path);
        var bmp = TryLoadThumbnail(path, 480);
        if (bmp is null)
        {
            PreviewImage.Source = null;
            PreviewImage.Visibility = Visibility.Collapsed;
            PreviewPlaceholder.Text = "Preview unavailable";
            PreviewPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        PreviewImage.Clip = null;
        PreviewImage.Source = bmp;
        PreviewImage.Visibility = Visibility.Visible;
        PreviewPlaceholder.Visibility = Visibility.Collapsed;
        if (PreviewImageOld is not null)
        {
            PreviewImageOld.Source = null;
            PreviewImageOld.Visibility = Visibility.Collapsed;
        }
    }


    /// <summary>
    /// Cosmetic iris only — mirrors desktop duration/easing, does not touch TransitionController.
    /// </summary>
    private async Task PlayPreviewIrisAsync(string? oldPath, string newPath, int durationMs)
    {
        if (PreviewImage is null || PreviewFrame is null)
            return;

        _previewAnimating = true;
        try
        {
            var newBmp = TryLoadThumbnail(newPath, 480);
            if (newBmp is null)
                return;

            var oldBmp = PreviewImage.Source as BitmapSource
                         ?? (string.IsNullOrWhiteSpace(oldPath) ? null : TryLoadThumbnail(oldPath, 480));

            PreviewFrame.UpdateLayout();
            var w = PreviewFrame.ActualWidth;
            var h = PreviewFrame.ActualHeight;
            if (w < 1 || h < 1)
            {
                w = 400;
                h = 180;
            }

            if (PreviewImageOld is not null)
            {
                PreviewImageOld.Source = oldBmp;
                PreviewImageOld.Visibility = oldBmp is null ? Visibility.Collapsed : Visibility.Visible;
            }

            PreviewImage.Source = newBmp;
            PreviewImage.Visibility = Visibility.Visible;
            if (PreviewPlaceholder is not null)
                PreviewPlaceholder.Visibility = Visibility.Collapsed;

            var center = new System.Windows.Point(w / 2.0, h / 2.0);
            var maxRadius = Math.Sqrt((w / 2.0) * (w / 2.0) + (h / 2.0) * (h / 2.0));
            var clip = new EllipseGeometry(center, 0, 0);
            PreviewImage.Clip = clip;

            var duration = TimeSpan.FromMilliseconds(Math.Max(250, durationMs));
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

            var animX = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = ease };
            var animY = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = ease };

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            animX.Completed += (_, _) => tcs.TrySetResult();

            clip.BeginAnimation(EllipseGeometry.RadiusXProperty, animX);
            clip.BeginAnimation(EllipseGeometry.RadiusYProperty, animY);

            await tcs.Task.ConfigureAwait(true);

            PreviewImage.Clip = null;
            if (PreviewImageOld is not null)
            {
                PreviewImageOld.Source = null;
                PreviewImageOld.Visibility = Visibility.Collapsed;
            }
            if (CurrentNameText is not null)
                CurrentNameText.Text = Path.GetFileName(newPath);
        }
        catch
        {
            // Preview is best-effort; desktop transition is authoritative.
        }
        finally
        {
            _previewAnimating = false;
            RefreshCurrentPreview(newPath);
        }
    }

    private static BitmapImage? TryLoadThumbnail(string path, int decodeWidth)
    {
        try
        {
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = new Uri(path, UriKind.Absolute);
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
            bmp.DecodePixelWidth = decodeWidth;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TitleBar_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ChangedButton != System.Windows.Input.MouseButton.Left)
            return;
        if (e.ClickCount == 2)
            return;
        try { DragMove(); }
        catch { /* ignore */ }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        PersistWindowPlacement(_scheduler.Settings);
        SettingsStore.Save(_scheduler.Settings);
    }

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
