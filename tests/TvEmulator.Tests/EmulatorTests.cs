using System.Net;
using System.Text;
using TvEmulator.Models;
using Xunit;
using TvEmulator.Services;

namespace TvEmulator.Tests;

public class EmulatorTests
{
    [Fact]
    public void FitOnUltrawideLeavesSideBars()
    {
        var rect = ViewportFit.Fit16By9(new System.Drawing.Rectangle(0, 0, 3440, 1440), 1920, 1080);
        Assert.Equal(2560, rect.Width);
        Assert.Equal(1440, rect.Height);
        Assert.Equal(440, rect.X);
        Assert.Equal(0, rect.Y);
    }

    [Fact]
    public void FitOnFullHdFillsThePanel()
    {
        var rect = ViewportFit.Fit16By9(new System.Drawing.Rectangle(0, 0, 1920, 1080), 1920, 1080);
        Assert.Equal(new PixelRect(0, 0, 1920, 1080), rect);
    }

    [Fact]
    public void WindowGrowsByTheFrameChromiumDrawsAroundThePage()
    {
        var stage = new PixelRect(100, 100, 1280, 720);
        var window = new PixelRect(100, 100, 1280, 720);
        var content = new PixelRect(111, 145, 1259, 665);

        var outer = ViewportFit.AroundContent(stage, window, content);

        Assert.Equal(new PixelRect(89, 55, 1301, 775), outer);
        Assert.Equal(stage, ViewportFit.AroundContent(stage, stage, stage));
    }

    [Fact]
    public async Task HeldPagesWaitForTheStage()
    {
        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
        using var server = new AppServer();
        server.Start(root, Path.Combine(root, "samples", "tizen-hello"), profile, pointerEnabled: false);
        server.HoldPages();

        using var client = new HttpClient();
        var page = client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        await Task.Delay(300);
        Assert.False(page.IsCompleted);

        server.StageReady();
        Assert.Contains("/__emulator/platform.js", await page.WaitAsync(TimeSpan.FromSeconds(2)));
    }

    [Fact]
    public void StripCssRemovesGapAndKeepsGridGap()
    {
        var css = "a{gap: 12px; grid-gap: 8px; margin: 1px;}";
        var result = CssQuirkRewriter.Strip(css, ["gap"]);
        Assert.DoesNotContain("gap: 12px", result);
        Assert.Contains("grid-gap: 8px", result);
        Assert.Contains("margin: 1px", result);
    }

    [Fact]
    public void EmptyQuirkListLeavesCssAlone()
    {
        var css = "a{gap:1px}";
        Assert.Equal(css, CssQuirkRewriter.Strip(css, []));
    }

    [Fact]
    public void StripStyleElementKeepsTheRestOfTheRule()
    {
        var html = "<style>a{gap:1px;color:red}</style>";
        var result = CssQuirkRewriter.StripStyleElements(html, ["gap"]);
        Assert.Contains("color:red", result);
        Assert.DoesNotContain("gap:", result);
    }

    [Fact]
    public void ResolveRejectsPathsOutsideTheApp()
    {
        var root = Path.Combine(Path.GetTempPath(), "tv-emulator-app-root");
        Directory.CreateDirectory(root);
        Assert.Null(AppFiles.Resolve(root, "/../../windows/win.ini"));
        var index = AppFiles.Resolve(root, "/index.html");
        Assert.NotNull(index);
        Assert.StartsWith(Path.GetFullPath(root), index, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProfilesUseTheirPinnedChromiumAndLeaveFlexGapAlone()
    {
        var root = RepoPaths.Root();
        var profiles = Catalog.LoadProfiles(root);
        Assert.Contains(profiles, profile => profile.Id == "tizen-2017" && profile.Engine == "chromium-47" && profile.PlatformVersion == "3.0" && profile.Player == "avplay");
        Assert.Contains(profiles, profile => profile.Id == "webos-4" && profile.Engine == "chromium-53" && profile.Player == "edge");
        Assert.Contains(profiles, profile => profile.Id == "tizen-2018" && profile.Engine == "chromium-56" && profile.Player == "avplay");
        Assert.Contains(profiles, profile => profile.Id == "tizen-2019" && profile.Engine == "chromium-63" && profile.Player == "avplay");
        Assert.Contains(profiles, profile => profile.Id == "tizen-2022" && profile.Engine == "chromium-85" && profile.PlatformVersion == "6.5" && profile.Player == "avplay");
        Assert.All(profiles, profile =>
        {
            Assert.Null(profile.MemoryMb);
            Assert.Null(profile.CpuPercent);
            Assert.Empty(profile.Quirks.StripCss);
        });

        var tizen2018 = Assert.Single(profiles, profile => profile.Id == "tizen-2018");
        Assert.Contains("Proxy", tizen2018.Quirks.DeleteGlobals);
        Assert.All(profiles.Where(profile => profile.Id != "tizen-2018"), profile => Assert.Empty(profile.Quirks.DeleteGlobals));

        var engines = Catalog.LoadEngines(root);
        Assert.Equal(5, engines.Count);
        Assert.Equal("chromium", Assert.Single(engines, item => item.Id == "chromium-47").Kind);
        Assert.DoesNotContain(engines, item => item.Kind == "electron");
        foreach (var profile in profiles)
        {
            var built = Assert.Single(engines, item => item.Id == profile.Engine);
            Assert.Equal("chromium", built.Kind);
            Assert.NotNull(Catalog.LocateEngine(root, built));
            var expectedMilestone = profile.Id switch
            {
                "tizen-2017" => 47,
                "webos-4" => 53,
                "tizen-2018" => 56,
                "tizen-2019" => 63,
                "tizen-2022" => 85,
                _ => throw new InvalidOperationException("Nieznany profil " + profile.Id)
            };
            Assert.Equal(expectedMilestone, built.Milestone);
        }

        foreach (var file in Directory.GetFiles(Path.Combine(root, "profiles"), "*.json"))
        {
            var json = File.ReadAllText(file);
            Assert.Contains("\"memoryMb\"", json, StringComparison.Ordinal);
            Assert.Contains("\"cpuPercent\"", json, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ArgumentsScaleTheLogicalViewportAndKeepTheAppSwitchLast()
    {
        var profile = new TvProfile
        {
            UserAgent = "TestAgent/1",
            Viewport = new Viewport { Width = 1920, Height = 1080 }
        };
        var engine = new EngineBuild { Args = ["--no-sandbox"] };
        var args = SessionPlanner.Arguments(engine, profile, new PixelRect(10, 20, 1600, 900), 47911, @"C:\data dir");
        Assert.Contains("--user-data-dir=C:\\data dir", args);
        Assert.Contains("--user-agent=TestAgent/1", args);
        Assert.Contains("--no-sandbox", args);
        Assert.Contains("--window-position=10,20", args);
        Assert.Contains("--window-size=1600,900", args);
        Assert.Contains("--force-device-scale-factor=0.8333", args);
        Assert.Contains("--disable-save-password-bubble", args);
        Assert.Contains("--disable-password-generation", args);
        Assert.Equal("--app=http://127.0.0.1:47911/", args[^1]);
        var hosted = SessionPlanner.Arguments(engine, profile, new PixelRect(10, 20, 1600, 900), 47911, @"C:\data dir", "/player/index.html?x=1#home");
        Assert.Equal("--app=http://127.0.0.1:47911/player/index.html?x=1#home", hosted[^1]);
        var debug = SessionPlanner.Arguments(engine, profile, new PixelRect(10, 20, 1600, 900), 47911, @"C:\data dir", debug: true);
        Assert.Contains("--remote-debugging-port=9223", debug);
        Assert.Contains("--enable-precise-memory-info", debug);
        Assert.DoesNotContain("--enable-precise-memory-info", args);
        Assert.Equal("--app=http://127.0.0.1:47911/", debug[^1]);
    }

    [Fact]
    public void ElectronArgumentsEndWithThePageUrlAndDebugPortIsOptional()
    {
        var profile = new TvProfile
        {
            UserAgent = "TestAgent/1",
            Viewport = new Viewport { Width = 1920, Height = 1080 }
        };
        var engine = new EngineBuild { Kind = "electron", Args = ["--disable-gpu"] };
        var shell = @"G:\projekty\TV-Emulator\shell\electron";
        var stage = new PixelRect(10, 20, 1600, 900);

        var args = SessionPlanner.Arguments(engine, profile, stage, 47911, @"C:\data dir", "/", shell, debug: false);

        Assert.Equal(shell, args[0]);
        Assert.Contains("--tv-switch=disable-gpu", args);
        Assert.Contains("--tv-user-agent=TestAgent/1", args);
        Assert.Contains("--tv-bounds=10,20,1600,900", args);
        Assert.Contains("--tv-scale=0.8333", args);
        Assert.Contains(@"--tv-user-data=C:\data dir", args);
        Assert.DoesNotContain("--tv-debug-port=9223", args);
        Assert.DoesNotContain("--tv-switch=enable-precise-memory-info", args);
        Assert.Equal("--tv-url=http://127.0.0.1:47911/", args[^1]);

        var debug = SessionPlanner.Arguments(engine, profile, stage, 47911, @"C:\data dir", "/", shell, debug: true);
        Assert.Contains("--tv-debug-port=9223", debug);
        Assert.Contains("--tv-switch=enable-precise-memory-info", debug);
        Assert.Equal("--tv-url=http://127.0.0.1:47911/", debug[^1]);
    }

    [Fact]
    public void AppSourceKeepsAFolderPathAndARemoteUrl()
    {
        var folder = AppSource.Parse(@"G:\apps\tizen-hello");
        Assert.False(folder.IsRemote);
        Assert.Equal("/", folder.LocalStartPath);

        var remote = AppSource.Parse("https://example.com/player/index.html?x=1#home");
        Assert.True(remote.IsRemote);
        Assert.Equal("/player/index.html?x=1#home", remote.LocalStartPath);
        Assert.Equal("https://example.com/", remote.Origin?.ToString());
    }

    [Fact]
    public async Task ServerInjectsShimAndStripsCss()
    {
        var root = RepoPaths.Root();
        var directory = Path.Combine(Path.GetTempPath(), "tv-emulator-serve-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "<head><style>.a{gap:4px;color:black}</style></head><p>hello</p>");
        await File.WriteAllTextAsync(Path.Combine(directory, "appinfo.json"), """{"id":"com.example","title":"Example"}""");
        await File.WriteAllTextAsync(Path.Combine(directory, "style.css"), "div{gap:2px;grid-gap:3px}");

        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
        profile.Quirks.StripCss = ["gap"];

        using var server = new AppServer();
        var exited = new TaskCompletionSource();
        server.ExitRequested += () => exited.TrySetResult();
        server.Start(root, directory, profile, pointerEnabled: false);

        using var client = new HttpClient();
        var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        var compact = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", "");
        Assert.Contains("window.__TV_EMULATOR_PROFILE__", html);
        Assert.Contains("/__emulator/platform.js", html);
        Assert.Contains("hello", html);
        Assert.Contains("\"platform\":\"tizen\"", compact);
        Assert.Contains("\"platformVersion\":\"3.0\"", compact);
        Assert.Contains("\"videoComposition\":\"colorKey\"", compact);
        Assert.Contains("\"id\":\"com.example\"", compact);
        Assert.DoesNotContain("gap:", html);
        Assert.Contains("color:black", html);

        var css = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/style.css");
        Assert.Contains("grid-gap:3px", css);
        Assert.DoesNotContain("gap:2px", css);

        var shim = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/$WEBAPIS/webapis/webapis.js");
        Assert.Contains("TV_EMULATOR_BOOTED", shim);
        Assert.Contains("applyUserAgent", shim);

        var outside = await client.GetAsync("http://127.0.0.1:" + server.Port + "/%2e%2e/%2e%2e/Windows/win.ini");
        Assert.Equal(HttpStatusCode.NotFound, outside.StatusCode);

        var exit = await client.PostAsync("http://127.0.0.1:" + server.Port + "/__emulator/exit", new StringContent("", Encoding.UTF8));
        Assert.Equal(HttpStatusCode.NoContent, exit.StatusCode);
        await exited.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task ElectronProfileInjectsAlphaComposition()
    {
        var root = RepoPaths.Root();
        var directory = Path.Combine(Path.GetTempPath(), "tv-emulator-alpha-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "index.html"), "<head></head><p>alpha</p>");
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2022");

        using var server = new AppServer();
        server.Start(root, directory, profile, pointerEnabled: false, debug: false, videoComposition: "alpha");
        using var client = new HttpClient();
        var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        var compact = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", "");
        Assert.Contains("\"videoComposition\":\"alpha\"", compact);
    }

    [Fact]
    public async Task TizenSampleCarriesConfigId()
    {
        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
        using var server = new AppServer();
        server.Start(root, Path.Combine(root, "samples", "tizen-hello"), profile, pointerEnabled: false);
        using var client = new HttpClient();
        var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        Assert.Contains("emulator.tizenhello", html);
        Assert.Contains("class=\"grid\"", html);
        var script = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/app.js");
        Assert.DoesNotContain("let ", script);
        Assert.DoesNotContain("class ", script);
    }

    [Fact]
    public async Task ProxyInjectsShimAndForwardsTvUserAgent()
    {
        using var upstream = new HttpListener();
        var upstreamPort = 0;
        for (var port = 47720; port < 47740; port++)
        {
            try
            {
                upstream.Prefixes.Add("http://127.0.0.1:" + port + "/");
                upstream.Start();
                upstreamPort = port;
                break;
            }
            catch (HttpListenerException)
            {
                upstream.Prefixes.Clear();
            }
        }

        Assert.NotEqual(0, upstreamPort);
        var seen = new List<string>();
        var thread = new Thread(() =>
        {
            while (upstream.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = upstream.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                lock (seen)
                    seen.Add((ctx.Request.Url?.AbsolutePath ?? "/") + " " + ctx.Request.Headers["User-Agent"]);

                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/player/index.html")
                {
                    var bytes = Encoding.UTF8.GetBytes("<head><p>remote</p></head>");
                    ctx.Response.ContentType = "text/html; charset=utf-8";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else if (path == "/player/app.js")
                {
                    var bytes = Encoding.UTF8.GetBytes("remote-script");
                    ctx.Response.ContentType = "text/javascript";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else if (path == "/player/old")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = "/player/index.html";
                    ctx.Response.ContentLength64 = 0;
                }
                else if (path == "/player/away")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.Headers["Location"] = "https://api.example/out";
                    ctx.Response.ContentLength64 = 0;
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentLength64 = 0;
                }

                ctx.Response.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2017");
        using var server = new AppServer();
        server.Start(root, AppSource.Parse("http://127.0.0.1:" + upstreamPort + "/player/index.html"), profile, false);

        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/player/index.html");
        Assert.Contains("remote", html);
        Assert.Contains("/__emulator/platform.js", html);
        Assert.Contains("Tizen 3.0", html);

        var script = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/player/app.js");
        Assert.Equal("remote-script", script);

        var sameHost = await client.GetAsync("http://127.0.0.1:" + server.Port + "/player/old");
        Assert.Equal(HttpStatusCode.Redirect, sameHost.StatusCode);
        Assert.Equal("http://127.0.0.1:" + server.Port + "/player/index.html", sameHost.Headers.Location?.ToString());

        var otherHost = await client.GetAsync("http://127.0.0.1:" + server.Port + "/player/away");
        Assert.Equal(HttpStatusCode.Redirect, otherHost.StatusCode);
        Assert.Equal("https://api.example/out", otherHost.Headers.Location?.ToString());

        lock (seen)
        {
            Assert.Contains(seen, line => line.StartsWith("/player/index.html ", StringComparison.Ordinal) && line.Contains(profile.UserAgent, StringComparison.Ordinal));
        }

        upstream.Stop();
    }

    [Fact]
    public void RewriteOriginPointsFontsAtTheLocalProxy()
    {
        var css = "url(https://app.test/font.woff2) url(//app.test/font.woff) url(https:\\/\\/app.test/a.js)";
        var rewritten = ProxyRewrite.RewriteOrigin(css, new Uri("https://app.test"), "http://127.0.0.1:9");
        Assert.DoesNotContain("app.test", rewritten);
        Assert.Contains("http://127.0.0.1:9/font.woff2", rewritten);
        Assert.Contains("http://127.0.0.1:9/font.woff", rewritten);
        Assert.Contains("http://127.0.0.1:9/a.js", rewritten);
    }

    [Fact]
    public void RewriteCookieDropsSecureAndDomain()
    {
        var cookie = ProxyRewrite.RewriteCookie("session=abc; Domain=tv.example.test; Secure; SameSite=None; Path=/");
        Assert.Contains("session=abc", cookie);
        Assert.Contains("Path=/", cookie);
        Assert.Contains("SameSite=Lax", cookie);
        Assert.DoesNotContain("Secure", cookie);
        Assert.DoesNotContain("Domain", cookie);
    }

    [Fact]
    public void PrepareDisablesThePasswordManager()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tv-emulator-prefs-" + Guid.NewGuid().ToString("N"));
        ChromeProfile.Prepare(dir);
        var json = File.ReadAllText(Path.Combine(dir, "Default", "Preferences"));
        Assert.Contains("\"credentials_enable_service\":false", json.Replace(" ", ""));
        Assert.Contains("\"password_manager_enabled\":false", json.Replace(" ", ""));
        Assert.True(File.Exists(Path.Combine(dir, "First Run")));
    }

    [Fact]
    public async Task ProxyRewritesFontsCookiesAndKeepsUpstreamWebOsLibrary()
    {
        using var upstream = new HttpListener();
        var upstreamPort = 0;
        for (var port = 47740; port < 47760; port++)
        {
            try
            {
                upstream.Prefixes.Add("http://127.0.0.1:" + port + "/");
                upstream.Start();
                upstreamPort = port;
                break;
            }
            catch (HttpListenerException)
            {
                upstream.Prefixes.Clear();
            }
        }

        Assert.NotEqual(0, upstreamPort);
        var origin = "http://127.0.0.1:" + upstreamPort;
        var thread = new Thread(() =>
        {
            while (upstream.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = upstream.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var path = ctx.Request.Url?.AbsolutePath ?? "/";
                if (path == "/static/css/styles.css")
                {
                    var bytes = Encoding.UTF8.GetBytes("url(" + origin + "/static/media/font.woff2)");
                    ctx.Response.ContentType = "text/css";
                    ctx.Response.Headers.Add("Set-Cookie", "session=abc; Domain=tv.example.test; Secure; SameSite=None; Path=/");
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else if (path == "/webOSTV.js")
                {
                    var bytes = Encoding.UTF8.GetBytes("window.webOS = upstream");
                    ctx.Response.ContentType = "text/javascript";
                    ctx.Response.ContentLength64 = bytes.Length;
                    ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    ctx.Response.ContentLength64 = 0;
                }

                ctx.Response.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2019");
        using var server = new AppServer();
        server.Start(root, AppSource.Parse(origin + "/"), profile, false);
        using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });

        var css = await client.GetAsync("http://127.0.0.1:" + server.Port + "/static/css/styles.css");
        var cssBody = await css.Content.ReadAsStringAsync();
        Assert.Contains("http://127.0.0.1:" + server.Port + "/static/media/font.woff2", cssBody);
        Assert.DoesNotContain(origin, cssBody);
        var cookie = string.Join(";", css.Headers.GetValues("Set-Cookie"));
        Assert.Contains("session=abc", cookie);
        Assert.Contains("SameSite=Lax", cookie);
        Assert.DoesNotContain("Secure", cookie);
        Assert.DoesNotContain("Domain", cookie);

        var library = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/webOSTV.js");
        Assert.Equal("window.webOS = upstream", library);
        Assert.DoesNotContain("TV_EMULATOR_BOOTED", library);

        upstream.Stop();
    }

    [Fact]
    public async Task DebugLogRecordsTheProxyStatusAndOmitsTheConsoleHookWhenOff()
    {
        using var upstream = new HttpListener();
        var upstreamPort = 0;
        for (var port = 47760; port < 47780; port++)
        {
            try
            {
                upstream.Prefixes.Add("http://127.0.0.1:" + port + "/");
                upstream.Start();
                upstreamPort = port;
                break;
            }
            catch (HttpListenerException)
            {
                upstream.Prefixes.Clear();
            }
        }

        Assert.NotEqual(0, upstreamPort);
        var thread = new Thread(() =>
        {
            while (upstream.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = upstream.GetContext();
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                var bytes = Encoding.UTF8.GetBytes("<head><p>debug</p></head>");
                ctx.Response.ContentType = "text/html; charset=utf-8";
                ctx.Response.ContentLength64 = bytes.Length;
                ctx.Response.OutputStream.Write(bytes, 0, bytes.Length);
                ctx.Response.Close();
            }
        })
        { IsBackground = true };
        thread.Start();

        var root = RepoPaths.Root();
        var profile = Catalog.LoadProfiles(root).Single(item => item.Id == "tizen-2019");
        var lines = new List<string>();
        using var server = new AppServer();
        server.Log += lines.Add;
        server.Start(root, AppSource.Parse("http://127.0.0.1:" + upstreamPort + "/"), profile, false, true);
        using var client = new HttpClient();
        var html = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/");
        var compact = System.Text.RegularExpressions.Regex.Replace(html, @"\s+", "");
        Assert.Contains("\"debug\":true", compact);
        Assert.Contains(lines, line => line.Contains("GET /", StringComparison.Ordinal) && line.Contains("200", StringComparison.Ordinal));
        var shim = await client.GetStringAsync("http://127.0.0.1:" + server.Port + "/__emulator/platform.js");
        Assert.Contains("onerror", shim);
        Assert.Contains("unhandledrejection", shim);

        using var quiet = new AppServer();
        quiet.Start(root, AppSource.Parse("http://127.0.0.1:" + upstreamPort + "/"), profile, false, false);
        var plain = await client.GetStringAsync("http://127.0.0.1:" + quiet.Port + "/");
        var plainCompact = System.Text.RegularExpressions.Regex.Replace(plain, @"\s+", "");
        Assert.Contains("\"debug\":false", plainCompact);
        Assert.DoesNotContain("\"debug\":true", plainCompact);

        var logDir = Path.Combine(Path.GetTempPath(), "tv-emulator-log-" + Guid.NewGuid().ToString("N"));
        var log = new SessionLog(logDir);
        log.Write(lines[0]);
        Assert.Contains("200", File.ReadAllText(log.FilePath));

        upstream.Stop();
    }
}
