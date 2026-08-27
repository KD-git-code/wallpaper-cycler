using Microsoft.Win32;

namespace WallpaperCycle.Services;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "WallpaperCycle";

    public static void Apply(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true)
                        ?? Registry.CurrentUser.CreateSubKey(RunKey);
        if (key is null)
            return;

        if (enabled)
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(exe))
                key.SetValue(ValueName, "\"" + exe + "\"");
        }
        else if (key.GetValue(ValueName) is not null)
        {
            key.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
