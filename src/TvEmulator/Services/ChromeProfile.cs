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
        profile["exited_cleanly"] = true;
        profile["exit_type"] = "Normal";
        var content = profile["default_content_setting_values"] as JsonObject ?? new JsonObject();
        content["notifications"] = 2;
        content["popups"] = 2;
        profile["default_content_setting_values"] = content;
        root["profile"] = profile;
        var translate = root["translate"] as JsonObject ?? new JsonObject();
        translate["enabled"] = false;
        root["translate"] = translate;
        File.WriteAllText(prefsPath, root.ToJsonString());
    }
}
