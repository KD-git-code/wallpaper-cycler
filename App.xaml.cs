using System.Windows;
using WallpaperCycle.Services;
using WallpaperCycle.Views;
using Application = System.Windows.Application;
using FormsApplication = System.Windows.Forms.Application;
using MessageBox = System.Windows.MessageBox;

namespace WallpaperCycle;

public partial class App : Application
{
    private Mutex? _mutex;
    private TrayService? _tray;
    private WallpaperScheduler? _scheduler;
    private SettingsWindow? _settingsWindow;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        FormsApplication.EnableVisualStyles();
        FormsApplication.SetCompatibleTextRenderingDefault(false);

        _mutex = new Mutex(true, @"Local\WallpaperCycle.SingleInstance", out bool created);
        if (!created)
        {
            MessageBox.Show(
                "WallpaperCycle is already running. Use the tray icon near the clock.",
                "WallpaperCycle",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
            Shutdown();
            return;

        }

        DispatcherUnhandledException += (_, args) =>
        {
            Logger.Error("Unhandled UI exception", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex)
                Logger.Error("Unhandled domain exception", ex);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Logger.Error("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        try
        {
            var settings = SettingsStore.Load();
            var library = new WallpaperLibrary(settings);
            var wallpaper = new DesktopWallpaperService();
            var fullscreen = new FullscreenDetector();
            var transition = new TransitionController(wallpaper);
            _scheduler = new WallpaperScheduler(settings, library, wallpaper, fullscreen, transition);
            _tray = new TrayService();
            _tray.OpenSettingsRequested += ShowSettings;
            _tray.NextRequested += () => _ = _scheduler.ChangeNowAsync();
            _tray.PauseToggled += paused => _scheduler.SetUserPaused(paused);
            _tray.ExitRequested += () => Shutdown();
            _scheduler.StatusChanged += status => Dispatcher.Invoke(() =>
            {
                _tray.UpdateStatus(status);
                _settingsWindow?.UpdateStatus(status);
            });
            _scheduler.Start();
            if (e.Args.Any(arg => arg.Equals("--now", StringComparison.OrdinalIgnoreCase)))
                Dispatcher.BeginInvoke(() => _ = _scheduler.ChangeNowAsync());
            Logger.Info("WallpaperCycle started.");
        }
        catch (Exception ex)
        {
            Logger.Error("Startup failed", ex);
            MessageBox.Show(
                "WallpaperCycle failed to start:\n\n" + ex.Message,
                "WallpaperCycle",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            Shutdown();
        }
    }

    private void ShowSettings()
    {
        if (_scheduler is null)
            return;

        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(_scheduler);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.Show();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _scheduler?.Dispose();
        _tray?.Dispose();
        _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
