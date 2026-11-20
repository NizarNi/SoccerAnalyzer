using System.Text.Json;

namespace SoccerAnalyzer;

/// <summary>
/// Finds external tools the user installed. Nothing is installed or modified here. Search order: a path the user
/// chose, the app folder, a tools folder, the CURRENT user/machine PATH (read fresh, since a running process can hold
/// a stale PATH after an install), then the usual WinGet, Chocolatey and Scoop locations.
/// </summary>
public static class ToolLocator
{
    static string SettingsFile =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SoccerAnalyzer", "tools.json");

    public static string? Find(string exeName)
    {
        foreach (var c in Candidates(exeName))
        {
            try { if (File.Exists(c)) return c; } catch (ArgumentException) { /* malformed entry */ }
        }
        return null;
    }

    static IEnumerable<string> Candidates(string exeName)
    {
        var saved = LoadSaved();
        if (saved.TryGetValue(exeName, out var chosen)) yield return chosen;

        yield return Path.Combine(AppContext.BaseDirectory, exeName);
        yield return Path.Combine(AppContext.BaseDirectory, "tools", exeName);

        var dirs = new List<string>();
        foreach (var scope in new EnvironmentVariableTarget?[] { null, EnvironmentVariableTarget.User, EnvironmentVariableTarget.Machine })
        {
            var path = scope == null ? Environment.GetEnvironmentVariable("PATH") : Environment.GetEnvironmentVariable("PATH", scope.Value);
            dirs.AddRange((path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(d => Environment.ExpandEnvironmentVariables(d.Trim().Trim('"'))));
        }
        foreach (var d in dirs.Distinct(StringComparer.OrdinalIgnoreCase)) yield return Path.Combine(d, exeName);

        string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(local, "Microsoft", "WinGet", "Links", exeName);
        yield return Path.Combine(profile, "scoop", "shims", exeName);
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "chocolatey", "bin", exeName);

        string tool = Path.GetFileNameWithoutExtension(exeName);
        string packages = Path.Combine(local, "Microsoft", "WinGet", "Packages");
        if (Directory.Exists(packages))
        {
            foreach (var pkg in SafeDirs(packages).Where(p => Path.GetFileName(p).Contains(tool, StringComparison.OrdinalIgnoreCase)
                                                             || (tool == "ffmpeg" && Path.GetFileName(p).Contains("FFmpeg", StringComparison.OrdinalIgnoreCase))))
                foreach (var f in SafeFiles(pkg, exeName)) yield return f;
        }
    }

    static IEnumerable<string> SafeDirs(string root)
    {
        try { return Directory.EnumerateDirectories(root).ToList(); } catch { return Enumerable.Empty<string>(); }
    }

    static IEnumerable<string> SafeFiles(string root, string name)
    {
        try { return Directory.EnumerateFiles(root, name, SearchOption.AllDirectories).ToList(); } catch { return Enumerable.Empty<string>(); }
    }

    public static string? FindFfmpeg() => Find("ffmpeg.exe");
    public static string? FindYtDlp() => Find("yt-dlp.exe");

    /// <summary>Remember a tool the user picked by hand.</summary>
    public static void Remember(string exeName, string fullPath)
    {
        try
        {
            var saved = LoadSaved();
            saved[exeName] = fullPath;
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            File.WriteAllText(SettingsFile, JsonSerializer.Serialize(saved, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { /* not critical: the user can pick again next time */ }
    }

    static Dictionary<string, string> LoadSaved()
    {
        try
        {
            return File.Exists(SettingsFile)
                ? JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(SettingsFile)) ?? new()
                : new();
        }
        catch { return new(); }
    }
}
