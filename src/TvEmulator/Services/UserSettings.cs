using System.Text.Json;
using TvEmulator.Models;

namespace TvEmulator.Services;

public sealed class UserSettings
{
    public string DefaultApp { get; set; } = "";
    public List<string> HiddenProfiles { get; set; } = new();
}

public static class UserSettingsStore
{
    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "TvEmulator",
        "settings.json");

    public static UserSettings Load() => Load(FilePath);

    public static void Save(UserSettings settings) => Save(settings, FilePath);

    public static UserSettings Load(string path)
    {
        try
        {
            if (!File.Exists(path))
                return new UserSettings();

            var settings = JsonSerializer.Deserialize<UserSettings>(File.ReadAllText(path), JsonDefaults.Options)
                ?? new UserSettings();
            settings.DefaultApp ??= "";
            settings.HiddenProfiles ??= new List<string>();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return new UserSettings();
        }
    }

    public static void Save(UserSettings settings, string path)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        var clean = new UserSettings
        {
            DefaultApp = settings.DefaultApp?.Trim() ?? "",
            HiddenProfiles = (settings.HiddenProfiles ?? new List<string>())
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
        };
        File.WriteAllText(path, JsonSerializer.Serialize(clean, JsonDefaults.Options));
    }

    public static IReadOnlyList<TvProfile> VisibleProfiles(IEnumerable<TvProfile> profiles, UserSettings settings)
    {
        var hidden = new HashSet<string>(
            (settings.HiddenProfiles ?? new List<string>()).Where(id => !string.IsNullOrWhiteSpace(id)),
            StringComparer.OrdinalIgnoreCase);
        return profiles.Where(profile => !hidden.Contains(profile.Id)).ToList();
    }

    public static string StartupApp(UserSettings settings, string sampleApp)
    {
        if (!string.IsNullOrWhiteSpace(settings.DefaultApp))
            return settings.DefaultApp.Trim();
        return sampleApp;
    }

    public static string NextAppField(string currentField, string previousDefault, string sampleApp, string newDefault)
    {
        var next = string.IsNullOrWhiteSpace(newDefault) ? sampleApp : newDefault.Trim();
        var current = currentField.Trim();
        if (string.IsNullOrWhiteSpace(current))
            return next;
        if (Same(current, previousDefault))
            return next;
        if (string.IsNullOrWhiteSpace(previousDefault) && Same(current, sampleApp))
            return next;
        return current;
    }

    private static bool Same(string left, string right)
    {
        if (string.IsNullOrWhiteSpace(left) || string.IsNullOrWhiteSpace(right))
            return false;
        return string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Trim().TrimEnd('\\', '/');
}
