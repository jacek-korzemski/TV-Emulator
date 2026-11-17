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

        if (string.Equals(engine.Kind, "firefox", StringComparison.Ordinal))
            return Directory.EnumerateFiles(dir, "firefox.exe", SearchOption.AllDirectories).FirstOrDefault();

        if (string.Equals(engine.Kind, "webkit", StringComparison.Ordinal))
            return Directory.EnumerateFiles(dir, "tv-webkit.exe", SearchOption.AllDirectories).FirstOrDefault();

        foreach (var relative in new[] { "chrome-win32\\chrome.exe", "chrome-win\\chrome.exe", "chrome-win64\\chrome.exe" })
        {
            var candidate = Path.Combine(dir, relative);
            if (File.Exists(candidate))
                return candidate;
        }

        return Directory.EnumerateFiles(dir, "chrome.exe", SearchOption.AllDirectories).FirstOrDefault();
    }

    public static string? LocateChrome(string root, string engineId) =>
        LocateEngine(root, new EngineBuild { Id = engineId, Kind = "chromium" });

    public static string EngineName(EngineBuild? engine, string fallback)
    {
        if (engine is null)
            return fallback;
        if (string.Equals(engine.Kind, "webkit", StringComparison.Ordinal))
            return "WebKit 538.1";
        if (string.Equals(engine.Kind, "firefox", StringComparison.Ordinal))
            return "Firefox " + engine.Milestone;
        return "Chromium " + engine.Milestone;
    }

    public static string CompatibilityLine(TvProfile profile, EngineBuild? engine)
    {
        var name = EngineName(engine, profile.Engine);
        var line = string.IsNullOrWhiteSpace(profile.Player) ? name : name + " · " + profile.Player;
        return "tryb zgodności · " + line;
    }
}
