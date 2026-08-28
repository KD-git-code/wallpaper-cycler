using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace WallpaperCycle.Services;

internal sealed class SchedulerStatus
{
    public required string Message { get; init; }
    public required bool UserPaused { get; init; }
    public required bool FullscreenPaused { get; init; }
    public required bool IsPinned { get; init; }
    public required DateTime? NextChangeLocal { get; init; }
    public required string? CurrentWallpaper { get; init; }
    public required int WallpaperCount { get; init; }
}

internal sealed class WallpaperScheduler : IDisposable
{
    private readonly AppSettings _settings;
    private readonly WallpaperLibrary _library;
    private readonly DesktopWallpaperService _wallpaper;
    private readonly FullscreenDetector _fullscreen;
    private readonly TransitionController _transition;
    private readonly DispatcherTimer _timer;
    private readonly FileSystemWatcher? _watcher;
    private readonly SemaphoreSlim _runGate = new(1, 1);
    private DateTime _nextChangeUtc;
    private bool _userPaused;
    private bool _disposed;

    public WallpaperScheduler(
        AppSettings settings,
        WallpaperLibrary library,
        DesktopWallpaperService wallpaper,
        FullscreenDetector fullscreen,
        TransitionController transition)
    {
        _settings = settings;
        _library = library;
        _wallpaper = wallpaper;
        _fullscreen = fullscreen;
        _transition = transition;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Tick();

        SystemEvents.SessionSwitch += OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;

        try
        {
            if (Directory.Exists(_settings.WallpaperFolder))
            {
                _watcher = new FileSystemWatcher(_settings.WallpaperFolder)
                {
                    IncludeSubdirectories = false,
                    EnableRaisingEvents = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size
                };
                _watcher.Created += (_, _) => _library.Refresh();
                _watcher.Deleted += (_, _) => _library.Refresh();
                _watcher.Renamed += (_, _) => _library.Refresh();
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Could not watch wallpaper folder", ex);
        }
    }

    public AppSettings Settings => _settings;
    public WallpaperLibrary Library => _library;

    public event Action<SchedulerStatus>? StatusChanged;

    public void Start()
    {
        if (string.IsNullOrWhiteSpace(_settings.LastWallpaperPath))
            _settings.LastWallpaperPath = _wallpaper.GetCurrentWallpaper();

        ScheduleFromNow();
        _timer.Start();
        PushStatus(_settings.IsPinned ? "Pinned" : "Running");
    }

    public void ApplySettings()
    {
        SettingsStore.Save(_settings);
        _library.Refresh();
        ScheduleFromNow();
        PushStatus("Settings saved");
    }

    public void SetUserPaused(bool paused)
    {
        _userPaused = paused;
        if (!paused)
            ScheduleFromNow();
        PushStatus(paused ? "Paused" : (_settings.IsPinned ? "Pinned" : "Running"));
    }

    public void SetPinned(bool pinned)
    {
        _settings.IsPinned = pinned;
        SettingsStore.Save(_settings);
        if (!pinned)
            ScheduleFromNow();
        PushStatus(pinned ? "Pinned" : (_userPaused ? "Paused" : "Running"));
    }

    public Task ChangeNowAsync() => ChangeNowAsync(force: true);

    /// <summary>Apply a specific wallpaper via the normal transition pipeline.</summary>
    public Task ChangeToAsync(string path) => ApplyPathAsync(path, force: true);

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _timer.Stop();
        _watcher?.Dispose();
        _runGate.Dispose();
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
    }

    private void Tick()
    {
        if (_disposed)
            return;

        if (_userPaused)
        {
            PushStatus("Paused");
            return;
        }

        if (_settings.IsPinned)
        {
            PushStatus("Pinned");
            return;
        }

        if (_settings.PauseOnFullscreen && _fullscreen.IsFullscreenAppRunning())
        {
            PushStatus("Paused — fullscreen app detected");
            return;
        }

        if (DateTime.UtcNow >= _nextChangeUtc)
            _ = ChangeNowAsync(force: false);
        else
            PushStatus("Running");
    }

    private async Task ChangeNowAsync(bool force)
    {
        if (_disposed)
            return;

        if (!force && _userPaused)
            return;

        if (!force && _settings.IsPinned)
            return;

        if (!force && _settings.PauseOnFullscreen && _fullscreen.IsFullscreenAppRunning())
        {
            PushStatus("Paused — fullscreen app detected");
            return;
        }

        _library.Refresh();
        if (_library.Count == 0)
        {
            PushStatus("No images found in the wallpaper folder");
            ScheduleFromNow();
            return;
        }

        var next = _library.PickNext(_settings.LastWallpaperPath);
        if (next is null)
        {
            PushStatus("No images found in the wallpaper folder");
            return;
        }

        await ApplyPathAsync(next, force).ConfigureAwait(true);
    }

    private async Task ApplyPathAsync(string next, bool force)
    {
        if (_disposed)
            return;

        if (!File.Exists(next))
        {
            PushStatus("Selected image no longer exists");
            return;
        }

        // Manual selection still respects fullscreen unless forced by user action
        if (!force && _settings.PauseOnFullscreen && _fullscreen.IsFullscreenAppRunning())
        {
            PushStatus("Paused — fullscreen app detected");
            return;
        }

        if (!await _runGate.WaitAsync(0).ConfigureAwait(true))
            return;

        try
        {
            PushStatus("Animating wallpaper transition");
            var oldPath = _settings.LastWallpaperPath;
            if (string.IsNullOrWhiteSpace(oldPath) || !File.Exists(oldPath))
                oldPath = _wallpaper.GetCurrentWallpaper();

            // TransitionController is intentionally untouched — same PlayAsync entry point.
            await _transition.PlayAsync(oldPath, next, _settings.AnimationDurationMs, CancellationToken.None)
                .ConfigureAwait(true);

            _settings.LastWallpaperPath = next;
            SettingsStore.Save(_settings);
            ScheduleFromNow();
            PushStatus(_settings.IsPinned ? "Pinned" : "Running");
            Logger.Info("Applied wallpaper: " + next);
        }
        catch (Exception ex)
        {
            Logger.Error("Wallpaper change failed", ex);
            ScheduleFromNow();
            PushStatus("Last change failed — see log");
        }
        finally
        {
            _runGate.Release();
        }
    }

    private void ScheduleFromNow()
    {
        _nextChangeUtc = DateTime.UtcNow.AddMinutes(_settings.IntervalMinutes);
    }

    private void OnSessionSwitch(object sender, SessionSwitchEventArgs e)
    {
        if (e.Reason == SessionSwitchReason.SessionUnlock)
            ScheduleFromNow();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e)
    {
        Logger.Info("Display settings changed.");
    }

    private void PushStatus(string message)
    {
        var fullscreen = !_userPaused && !_settings.IsPinned
            && _settings.PauseOnFullscreen && _fullscreen.IsFullscreenAppRunning();
        StatusChanged?.Invoke(new SchedulerStatus
        {
            Message = message,
            UserPaused = _userPaused,
            FullscreenPaused = fullscreen,
            IsPinned = _settings.IsPinned,
            NextChangeLocal = (_userPaused || _settings.IsPinned) ? null : _nextChangeUtc.ToLocalTime(),
            CurrentWallpaper = _settings.LastWallpaperPath,
            WallpaperCount = _library.Count
        });
    }
}
