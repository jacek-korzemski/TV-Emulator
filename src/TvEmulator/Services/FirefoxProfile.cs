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
            // The color key is a GDI hole. Hardware layers paint over it, the same
            // reason every Chromium engine starts with --disable-gpu.
            "user_pref(\"layers.acceleration.disabled\", true);",
            "user_pref(\"gfx.direct2d.disabled\", true);",
            "");
        File.WriteAllText(Path.Combine(profileDir, "user.js"), prefs);

        // Firefox 44 has no kiosk flag. userChrome.css hides the navigation bar
        // inside the client area; the host already strips the Windows frame.
        var chromeDir = Path.Combine(profileDir, "chrome");
        Directory.CreateDirectory(chromeDir);
        var css = string.Join("\n",
            "@namespace url(\"http://www.mozilla.org/keymaster/gatekeeper/there.is.only.xul\");",
            "",
            "#navigator-toolbox,",
            "#titlebar,",
            "#browser-bottombox,",
            "#status-bar {",
            "  visibility: collapse !important;",
            "}",
            "");
        File.WriteAllText(
            Path.Combine(chromeDir, "userChrome.css"),
            css,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }
}