using System.Text.Json;
using TvEmulator.Models;

namespace TvEmulator.Services;

public static class Catalog
{
    public static IReadOnlyList<TvProfile> LoadProfiles(string root)
    {
        var dir = Path.Combine(root, "profiles");
        return Directory.GetFiles(dir, "*.json")
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path => JsonSerializer.Deserialize<TvProfile>(File.ReadAllText(path), JsonDefaults.Options)
                ?? throw new InvalidDataException(path))
            .ToList();
    }

    public static IReadOnlyList<EngineBuild> LoadEngines(string root)
    {
        var path = Path.Combine(root, "engines", "builds.json");
        return JsonSerializer.Deserialize<List<EngineBuild>>(File.ReadAllText(path), JsonDefaults.Options)
            ?? [];
    }

    public static string? LocateEngine(string root, EngineBuild engine)
    {
        var dir = Path.Combine(root, "engines", engine.Id);
        if (!Directory.Exists(dir))
            return null;

        if (string.Equals(engine.Kind, "electron", StringComparison.Ordinal))
            return Directory.EnumerateFiles(dir, "electron.exe", SearchOption.AllDirectories).FirstOrDefault();

        foreach (var relative in new[] { "chrome-win32\\chrome.exe", "chrome-win\\chrome.exe" })
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        return Directory.EnumerateFiles(dir, "chrome.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string? LocateChrome(string root, string engineId) =>
        LocateEngine(root, new EngineBuild { Id = engineId, Kind = "chromium" });
}
