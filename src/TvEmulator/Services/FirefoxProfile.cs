namespace TvEmulator.Services;

public static class FirefoxProfile
{
    public static void Prepare(string profileDir, string userAgent)
    {
        Directory.CreateDirectory(profileDir);
        var escaped = userAgent.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var prefs = string.Join("\n",
            "user_pref(\"browser.shell.checkDefaultBrowser\", false);",
            "user_pref(\"browser.rights.3.shown\", true);",
            "user_pref(\"browser.startup.homepage_override.mstone\", \"ignore\");",
            "user_pref(\"startup.homepage_welcome_url\", \"\");",
            "user_pref(\"startup.homepage_welcome_url.additional\", \"\");",
            "user_pref(\"toolkit.telemetry.enabled\", false);",
            "user_pref(\"toolkit.telemetry.prompted\", 2);",
            "user_pref(\"datareporting.policy.dataSubmissionEnabled\", false);",
            "user_pref(\"datareporting.policy.dataSubmissionPolicyAcceptedVersion\", 2);",
            "user_pref(\"general.useragent.override\", \"" + escaped + "\");",
            "");
        File.WriteAllText(Path.Combine(profileDir, "user.js"), prefs);
    }
}