using System.IO;

namespace WallpaperCycle.Services;

internal sealed class WallpaperLibrary
{
    private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".jpg", ".jpeg", ".png", ".bmp", ".jfif", ".webp", ".tif", ".tiff"
    };

    private readonly AppSettings _settings;
    private readonly object _gate = new();
    private List<string> _files = [];

    public WallpaperLibrary(AppSettings settings)
    {
        _settings = settings;
        Refresh();
    }

    public int Count
    {
        get
        {
            lock (_gate)
                return _files.Count;
        }
    }

    public void Refresh()
    {
        var folder = _settings.WallpaperFolder;
        var files = new List<string>();
        try
        {
            if (Directory.Exists(folder))
            {
                files.AddRange(Directory.EnumerateFiles(folder)
                    .Where(path => Extensions.Contains(Path.GetExtension(path)))
                    .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
            }
        }
        catch (Exception ex)
        {
            Logger.Error("Failed to enumerate wallpapers", ex);
        }

        lock (_gate)
            _files = files;
    }

    public string? PickNext(string? currentPath)
    {
        Refresh();
        List<string> snapshot;
        lock (_gate)
            snapshot = _files.ToList();

        if (snapshot.Count == 0)
            return null;

        if (snapshot.Count == 1)
            return snapshot[0];

        var candidates = snapshot
            .Where(path => !PathsEqual(path, currentPath))
            .ToList();
        if (candidates.Count == 0)
            candidates = snapshot;

        if (_settings.Shuffle)
            return candidates[Random.Shared.Next(candidates.Count)];

        var index = 0;
        if (!string.IsNullOrWhiteSpace(currentPath))
        {
            var current = snapshot.FindIndex(path => PathsEqual(path, currentPath));
            index = current >= 0 ? (current + 1) % snapshot.Count : 0;
            return snapshot[index];
        }

        return snapshot[index];
    }

    public static bool PathsEqual(string? a, string? b)
    {
        if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
            return false;
        return string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);
    }
}
