using System.IO;
using System.Text.Json;

namespace WallpaperCycle.Services;

internal sealed class AppSettings
{
    public string WallpaperFolder { get; set; } = @"C:\Wallpapers";
    public int IntervalMinutes { get; set; } = 1;
    public int AnimationDurationMs { get; set; } = 900;
    public bool PauseOnFullscreen { get; set; } = true;
    public bool Shuffle { get; set; } = true;
    public bool StartWithWindows { get; set; } = false;
    public string? LastWallpaperPath { get; set; }
}

internal static class SettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperCycle");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    Normalize(loaded);
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to load settings; using defaults", ex);
        }

        var defaults = new AppSettings();
        Save(defaults);
        return defaults;
    }

    public static void Save(AppSettings settings)
    {
        Normalize(settings);
        Directory.CreateDirectory(DirectoryPath);
        File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, JsonOptions));
        StartupRegistration.Apply(settings.StartWithWindows);
    }

    private static void Normalize(AppSettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.WallpaperFolder))
            settings.WallpaperFolder = @"C:\Wallpapers";
        settings.IntervalMinutes = Math.Clamp(settings.IntervalMinutes, 1, 180);
        settings.AnimationDurationMs = Math.Clamp(settings.AnimationDurationMs, 250, 2500);
    }
}
