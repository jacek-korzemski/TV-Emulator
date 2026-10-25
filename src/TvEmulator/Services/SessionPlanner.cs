using System.Globalization;
using TvEmulator.Models;

namespace TvEmulator.Services;

public static class SessionPlanner
{
    public static IReadOnlyList<string> Arguments(
        EngineBuild engine,
        TvProfile profile,
        PixelRect rect,
        int port,
        string userDataDir,
        string startPath = "/",
        string? shellDir = null,
        bool debug = false)
    {
        if (string.IsNullOrWhiteSpace(startPath))
            startPath = "/";
        if (!startPath.StartsWith('/'))
            startPath = "/" + startPath;

        var logicalWidth = Math.Max(1, profile.Viewport.Width);
        var scale = rect.Width / (double)logicalWidth;
        var scaleText = scale.ToString("0.####", CultureInfo.InvariantCulture);
        var url = "http://127.0.0.1:" + port + startPath;

        if (string.Equals(engine.Kind, "electron", StringComparison.Ordinal))
        {
            if (string.IsNullOrWhiteSpace(shellDir))
                throw new InvalidOperationException("Brak powłoki Electron.");

            var electron = new List<string> { shellDir };
            if (engine.Args is not null)
            {
                foreach (var argument in engine.Args)
                {
                    if (argument.StartsWith("--", StringComparison.Ordinal))
                        electron.Add("--tv-switch=" + argument.Substring(2));
                }
            }

            electron.Add("--tv-user-agent=" + profile.UserAgent);
            electron.Add("--tv-bounds=" + rect.X + "," + rect.Y + "," + rect.Width + "," + rect.Height);
            electron.Add("--tv-scale=" + scaleText);
            electron.Add("--tv-user-data=" + userDataDir);
            if (debug)
            {
                electron.Add("--tv-switch=enable-precise-memory-info");
                electron.Add("--tv-debug-port=9223");
            }
            electron.Add("--tv-url=" + url);
            return electron;
        }

        var args = new List<string>
        {
            "--user-data-dir=" + userDataDir,
            "--no-first-run",
            "--no-default-browser-check",
            "--disable-sync",
            "--disable-extensions",
            "--disable-translate",
            "--disable-background-networking",
            "--noerrdialogs",
            "--no-proxy-server",
            "--disable-save-password-bubble",
            "--disable-password-generation",
            "--force-device-scale-factor=" + scaleText,
            "--user-agent=" + profile.UserAgent,
            "--window-position=" + rect.X + "," + rect.Y,
            "--window-size=" + rect.Width + "," + rect.Height
        };

        if (engine.Args is not null)
            args.AddRange(engine.Args);

        if (debug)
        {
            args.Add("--enable-precise-memory-info");
            args.Add("--remote-debugging-port=9223");
        }

        args.Add("--app=" + url);
        return args;
    }
}
