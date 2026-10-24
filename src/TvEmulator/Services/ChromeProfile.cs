using System.Text.Json.Nodes;

namespace TvEmulator.Services;

public static class ChromeProfile
{
    public static void Prepare(string userDataDir)
    {
        Directory.CreateDirectory(userDataDir);
        File.WriteAllText(Path.Combine(userDataDir, "First Run"), "");

        var profileDir = Path.Combine(userDataDir, "Default");
        Directory.CreateDirectory(profileDir);
        var prefsPath = Path.Combine(profileDir, "Preferences");
        var root = File.Exists(prefsPath)
            ? JsonNode.Parse(File.ReadAllText(prefsPath)) as JsonObject ?? new JsonObject()
            : new JsonObject();
        root["credentials_enable_service"] = false;
        var profile = root["profile"] as JsonObject ?? new JsonObject();
        profile["password_manager_enabled"] = false;
        root["profile"] = profile;
        File.WriteAllText(prefsPath, root.ToJsonString());
    }
}
