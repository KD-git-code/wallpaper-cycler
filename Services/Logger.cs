using System.IO;
using System.Text;

namespace WallpaperCycle.Services;

internal static class Logger
{
    private static readonly object Gate = new();
    private static readonly string LogDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WallpaperCycle");
    private static readonly string LogPath = Path.Combine(LogDir, "app.log");

    public static void Info(string message) => Write("INFO", message, null);

    public static void Warn(string message) => Write("WARN", message, null);

    public static void Error(string message, Exception? ex = null) => Write("ERROR", message, ex);

    private static void Write(string level, string message, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(LogDir);
            var line = new StringBuilder()
                .Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
                .Append(" [").Append(level).Append("] ")
                .Append(message);
            if (ex is not null)
                line.Append(" | ").Append(ex);
            line.AppendLine();
            lock (Gate)
            {
                File.AppendAllText(LogPath, line.ToString());
            }
        }
        catch
        {
            // Logging must never crash the app.
        }
    }
}
