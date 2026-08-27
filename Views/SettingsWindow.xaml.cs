using System.IO;
using System.Windows;
using WallpaperCycle.Services;
using Forms = System.Windows.Forms;

namespace WallpaperCycle.Views;

internal partial class SettingsWindow : Window
{
    private readonly WallpaperScheduler _scheduler;

    public SettingsWindow(WallpaperScheduler scheduler)
    {
        _scheduler = scheduler;
        InitializeComponent();
        LoadFromSettings();
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
        CountText.Text = status.WallpaperCount + " image(s) found";
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
    }

    private void Interval_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (IntervalLabel is not null)
            IntervalLabel.Text = ((int)e.NewValue) + " min";
    }

    private void Duration_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (DurationLabel is not null)
            DurationLabel.Text = (e.NewValue / 1000.0).ToString("0.00") + " s";
    }

    private void PresetTest_Click(object sender, RoutedEventArgs e) => IntervalSlider.Value = 1;

    private void PresetProd_Click(object sender, RoutedEventArgs e) => IntervalSlider.Value = 35;

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new Forms.FolderBrowserDialog
        {
            Description = "Choose the folder that contains wallpaper images",
            SelectedPath = Directory.Exists(FolderBox.Text) ? FolderBox.Text : @"C:\Wallpapers",
            UseDescriptionForTitle = true
        };
        if (dialog.ShowDialog() == Forms.DialogResult.OK)
            FolderBox.Text = dialog.SelectedPath;
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
        _scheduler.ApplySettings();
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
}
