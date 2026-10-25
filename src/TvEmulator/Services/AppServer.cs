using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using TvEmulator.Models;

namespace TvEmulator.Services;

public sealed class AppServer : IDisposable
{
    private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        [".html"] = "text/html; charset=utf-8",
        [".htm"] = "text/html; charset=utf-8",
        [".js"] = "text/javascript; charset=utf-8",
        [".css"] = "text/css; charset=utf-8",
        [".json"] = "application/json; charset=utf-8",
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".gif"] = "image/gif",
        [".svg"] = "image/svg+xml",
        [".webp"] = "image/webp",
        [".woff"] = "font/woff",
        [".woff2"] = "font/woff2",
        [".mp4"] = "video/mp4",
        [".webm"] = "video/webm",
        [".wasm"] = "application/wasm",
        [".xml"] = "application/xml; charset=utf-8",
        [".txt"] = "text/plain; charset=utf-8"
    };

    private static readonly Dictionary<string, string> PlaneFiles = new(StringComparer.Ordinal)
    {
        ["/__emulator/videoplane.html"] = "videoplane.html",
        ["/__emulator/videoplane.js"] = "videoplane.js",
        ["/__emulator/vendor/codem-isoboxer.min.js"] = Path.Combine("vendor", "codem-isoboxer.min.js"),
        ["/__emulator/vendor/shaka-player.compiled.js"] = Path.Combine("vendor", "shaka-player.compiled.js")
    };

    private static readonly TimeSpan EventPoll = TimeSpan.FromSeconds(20);

    private static readonly Regex AvplayerObject = new(
        "<object\\b(?=[^>]*\\btype\\s*=\\s*[\"']?application/avplayer\\b)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex BroadcastObject = new(
        "<object\\b(?=[^>]*\\btype\\s*=\\s*[\"']?video/broadcast\\b)[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private const string BootPage =
        """
        <!DOCTYPE html>
        <html>
        <head>
        <meta charset="utf-8">
        <style>
        html, body { margin: 0; background: #000; overflow: hidden; width: 100%; height: 100%; }
        </style>
        </head>
        <body>
        <script>
        (function () {
          function tick() {
            var xhr = new XMLHttpRequest();
            xhr.open("POST", "/__emulator/boot", true);
            xhr.setRequestHeader("Content-Type", "application/json");
            xhr.onload = function () {
              var data = {};
              try {
                data = JSON.parse(xhr.responseText || "{}");
              } catch (ignore) {
              }
              if (data.url) {
                location.replace(data.url);
                return;
              }
              setTimeout(tick, 150);
            };
            xhr.onerror = function () {
              setTimeout(tick, 200);
            };
            xhr.send(JSON.stringify({ w: window.innerWidth, h: window.innerHeight }));
          }
          tick();
        })();
        </script>
        </body>
        </html>
        """;

    private const int BootTolerancePx = 8;
    private const long BootGiveUpMs = 5000;

    private HttpListener? _listener;
    private Thread? _thread;
    private readonly HttpClient _http = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };

    private string _appDirectory = "";
    private Uri? _origin;
    private Uri? _page;
    private JsonNode? _remoteAppInfo;
    private TvProfile? _profile;
    private bool _pointerEnabled;
    private bool _debug;
    private string _videoComposition = "colorKey";
    private string _shim = "";
    private string _shimDirectory = "";
    private readonly object _appInfoGate = new();
    private readonly object _bootGate = new();
    private readonly ManualResetEventSlim _stageReady = new(true);
    private string _startPath = "/";
    private long _bootSeenAt;
    private int _bootMatches;
    private bool _bootReleased;
    private int _pending;

    private static readonly TimeSpan StageWait = TimeSpan.FromSeconds(3);

    public int Port { get; private set; }
    public AvplayBridge Avplay { get; } = new();

    /// <summary>
    /// Holds app HTML until <see cref="StageReady"/> (or a few seconds). Chromium calls
    /// <see cref="StageReady"/> from the boot gate once the page viewport matches the profile.
    /// Other engines call it when the window frame has been fitted.
    /// </summary>
    public void HoldPages()
    {
        _stageReady.Reset();
        lock (_bootGate)
        {
            _bootSeenAt = 0;
            _bootMatches = 0;
            _bootReleased = false;
        }
    }

    public void StageReady() => _stageReady.Set();
    public event Action? ExitRequested;
    public event Action<string>? Log;
    public event Action<string>? DebugReported;

    public void Start(string root, string appDirectory, TvProfile profile, bool pointerEnabled, bool debug = false, string videoComposition = "colorKey") =>
        Start(root, new AppSource(Path.GetFullPath(appDirectory), null), profile, pointerEnabled, debug, videoComposition);

    public void Start(string root, AppSource source, TvProfile profile, bool pointerEnabled, bool debug = false, string videoComposition = "colorKey")
    {
        _appDirectory = source.Directory ?? "";
        _page = source.Page;
        _origin = source.Origin;
        _startPath = NormalizeStartPath(source.LocalStartPath);
        _profile = profile;
        _pointerEnabled = pointerEnabled;
        _debug = debug;
        _videoComposition = string.IsNullOrWhiteSpace(videoComposition) ? "colorKey" : videoComposition;
        _shimDirectory = Path.Combine(root, "shims");
        _shim = File.ReadAllText(Path.Combine(_shimDirectory, "platform.js"));
        Avplay.Trace += line =>
        {
            if (_debug)
                Log?.Invoke(line);
        };
        (_listener, Port) = Bind();
        _thread = new Thread(Loop) { IsBackground = true, Name = "TvEmulatorHttp" };
        _thread.Start();
    }

    public void Dispose()
    {
        Avplay.Close();
        _stageReady.Set();
        var listener = _listener;
        _listener = null;
        try
        {
            listener?.Stop();
        }
        catch (ObjectDisposedException)
        {
        }
        catch (HttpListenerException)
        {
        }

        try
        {
            listener?.Close();
        }
        catch (ObjectDisposedException)
        {
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        var wait = System.Diagnostics.Stopwatch.StartNew();
        while (Volatile.Read(ref _pending) > 0 && wait.Elapsed < TimeSpan.FromSeconds(2))
            Thread.Sleep(15);
        _http.Dispose();
    }

    private static (HttpListener Listener, int Port) Bind()
    {
        for (var port = 47900; port < 48000; port++)
        {
            var listener = new HttpListener();
            listener.Prefixes.Add("http://127.0.0.1:" + port + "/");
            try
            {
                listener.Start();
                return (listener, port);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }

        throw new InvalidOperationException("Brak wolnego portu localhost dla aplikacji.");
    }

    private void Loop()
    {
        var listener = _listener;
        if (listener is null)
            return;

        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = listener.GetContext();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            Interlocked.Increment(ref _pending);
            try
            {
                ThreadPool.QueueUserWorkItem(state =>
                {
                    var queued = (HttpListenerContext)state!;
                    try
                    {
                        Handle(queued);
                    }
                    catch (Exception ex)
                    {
                        try
                        {
                            Note(queued.Request.HttpMethod + " " + (queued.Request.Url?.PathAndQuery ?? "/") + " error " + ex.Message);
                            Write(queued, 500, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(ex.Message));
                        }
                        catch (HttpListenerException)
                        {
                        }
                        catch (InvalidOperationException)
                        {
                        }
                    }
                    finally
                    {
                        Interlocked.Decrement(ref _pending);
                    }
                }, context);
            }
            catch
            {
                Interlocked.Decrement(ref _pending);
                throw;
            }
        }
    }

    private void Handle(HttpListenerContext context)
    {
        var path = context.Request.Url?.AbsolutePath ?? "/";
        if (path == SessionPlanner.BootPath && context.Request.HttpMethod == "GET")
        {
            Write(context, 200, "text/html; charset=utf-8", Encoding.UTF8.GetBytes(BootPage));
            return;
        }

        if (path == SessionPlanner.BootReportPath && context.Request.HttpMethod == "POST")
        {
            HandleBoot(context);
            return;
        }

        if (path == "/__emulator/exit" && context.Request.HttpMethod == "POST")
        {
            Write(context, 204, "text/plain", []);
            ExitRequested?.Invoke();
            return;
        }

        if (path == "/__emulator/log" && context.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream, context.Request.ContentEncoding);
            Log?.Invoke(reader.ReadToEnd());
            Write(context, 204, "text/plain", []);
            return;
        }

        if (path == "/__emulator/debug" && context.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var body = reader.ReadToEnd();
            if (body.Length > 64_000)
                body = body.Substring(0, 64_000);
            DebugReported?.Invoke(body);
            Write(context, 204, "text/plain", []);
            return;
        }

        if (path == "/__emulator/avplay" && context.Request.HttpMethod == "POST")
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            WriteJson(context, Avplay.Accept(reader.ReadToEnd()));
            return;
        }

        if (path == "/__emulator/media" && context.Request.HttpMethod == "POST")
        {
            WriteJson(context, AcceptMedia(context));
            return;
        }

        if (path == "/__emulator/avplay/events" && context.Request.HttpMethod == "GET")
        {
            var since = long.TryParse(context.Request.QueryString["since"], out var parsed) ? parsed : -1;
            WriteJson(context, Avplay.Wait(since, EventPoll));
            return;
        }

        if (PlaneFiles.TryGetValue(path, out var planeFile))
        {
            var full = Path.Combine(_shimDirectory, planeFile);
            if (!File.Exists(full))
            {
                Write(context, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Brak pliku płaszczyzny wideo."));
                return;
            }

            var planeType = ContentTypes.TryGetValue(Path.GetExtension(full), out var foundType) ? foundType : "application/octet-stream";
            Write(context, 200, planeType, File.ReadAllBytes(full));
            return;
        }

        if (path == "/__emulator/platform.js"
            || path.Contains("$WEBAPIS", StringComparison.OrdinalIgnoreCase)
            || (_origin is null && AppFiles.IsPlatformScript(path)))
        {
            Write(context, 200, "text/javascript; charset=utf-8", Encoding.UTF8.GetBytes(_shim));
            Note(context.Request.HttpMethod + " " + path + " 200 text/javascript " + Encoding.UTF8.GetByteCount(_shim));
            return;
        }

        var profile = _profile ?? throw new InvalidOperationException("Brak profilu.");
        if (_origin is not null)
        {
            Proxy(context, profile);
            return;
        }

        var relative = path == "/" ? "/index.html" : path;
        var file = AppFiles.Resolve(_appDirectory, relative);
        if (file is null || !File.Exists(file) || Directory.Exists(file))
        {
            Note(context.Request.HttpMethod + " " + relative + " 404");
            Write(context, 404, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes("Nie ma takiego pliku."));
            return;
        }

        var extension = Path.GetExtension(file);
        if (extension.Equals(".html", StringComparison.OrdinalIgnoreCase) || extension.Equals(".htm", StringComparison.OrdinalIgnoreCase))
        {
            var html = File.ReadAllText(file);
            html = CssQuirkRewriter.StripStyleElements(html, profile.Quirks.StripCss);
            html = Inject(html, profile);
            var htmlBytes = Encoding.UTF8.GetBytes(html);
            Note(context.Request.HttpMethod + " " + relative + " 200 text/html " + htmlBytes.Length);
            Write(context, 200, "text/html; charset=utf-8", htmlBytes);
            return;
        }

        if (extension.Equals(".css", StringComparison.OrdinalIgnoreCase))
        {
            var css = CssQuirkRewriter.Strip(File.ReadAllText(file), profile.Quirks.StripCss);
            var cssBytes = Encoding.UTF8.GetBytes(css);
            Note(context.Request.HttpMethod + " " + relative + " 200 text/css " + cssBytes.Length);
            Write(context, 200, "text/css; charset=utf-8", cssBytes);
            return;
        }

        var type = ContentTypes.TryGetValue(extension, out var found) ? found : "application/octet-stream";
        var fileBytes = File.ReadAllBytes(file);
        Note(context.Request.HttpMethod + " " + relative + " 200 " + type + " " + fileBytes.Length);
        Write(context, 200, type, fileBytes);
    }

    private string Inject(string html, TvProfile profile)
    {
        var payload = new
        {
            platform = profile.Platform,
            player = profile.Player,
            platformVersion = profile.PlatformVersion,
            userAgent = profile.UserAgent,
            keys = profile.Keys,
            pointerEnabled = _pointerEnabled,
            debug = _debug,
            videoComposition = _videoComposition,
            viewport = profile.Viewport,
            product = profile.Product,
            quirks = profile.Quirks,
            appInfo = ResolveAppInfo()
        };
        var json = JsonSerializer.Serialize(payload, JsonDefaults.Options);
        if (!_stageReady.Wait(StageWait))
            _stageReady.Set();
        if (profile.Platform == "tizen")
            html = FillAvplayerObjects(html);
        if (profile.Platform == "hbbtv")
            html = FillBroadcastObjects(html);
        var snippet = "<script>window.__TV_EMULATOR_PROFILE__=" + json + ";</script><script src=\"/__emulator/platform.js\"></script>";
        var head = Regex.Match(html, "<head\\b[^>]*>", RegexOptions.IgnoreCase);
        if (head.Success)
            return html.Insert(head.Index + head.Length, snippet);
        return snippet + html;
    }

    // Chromium 47 looks up a plugin for <object> while parsing, before any script can add
    // fallback content, so AVPlay objects written in markup get their filler here.
    public static string FillAvplayerObjects(string html) =>
        AvplayerObject.Replace(html, match => match.Value + "<span data-tv-emulator=\"avplay\"></span>");

    // Chromium paints "plugin missing" over an object whose type it does not know.
    // Fallback content makes it skip the plugin lookup. The broadcast object is in the
    // first HTML, so the filler has to be there before the parser sees the tag.
    public static string FillBroadcastObjects(string html) =>
        BroadcastObject.Replace(html, match => match.Value + "<span data-tv-emulator=\"broadcast\"></span>");

    private JsonNode ResolveAppInfo()
    {
        if (_page is null)
            return ReadAppInfo(_appDirectory);
        lock (_appInfoGate)
            return _remoteAppInfo ??= FetchAppInfo() ?? DefaultAppInfo();
    }

    private JsonNode? FetchAppInfo()
    {
        if (_page is null)
            return null;
        try
        {
            var infoUrl = new Uri(_page, "appinfo.json");
            using var request = new HttpRequestMessage(HttpMethod.Get, infoUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", _profile?.UserAgent ?? "");
            using var response = _http.Send(request);
            if (!response.IsSuccessStatusCode)
                return null;
            var json = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            return JsonNode.Parse(json);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException)
        {
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonNode DefaultAppInfo() => new JsonObject
    {
        ["id"] = "emulator.app",
        ["title"] = "App",
        ["version"] = "1.0.0"
    };

    private void Proxy(HttpListenerContext context, TvProfile profile)
    {
        var origin = _origin ?? throw new InvalidOperationException("Brak adresu aplikacji.");
        var pathAndQuery = context.Request.Url?.PathAndQuery ?? "/";
        var target = new Uri(origin, pathAndQuery);
        using var request = new HttpRequestMessage(new HttpMethod(context.Request.HttpMethod), target);
        request.Headers.TryAddWithoutValidation("User-Agent", profile.UserAgent);
        CopyRequestHeader(context, request, "Accept");
        CopyRequestHeader(context, request, "Accept-Language");
        CopyRequestHeader(context, request, "Cookie");
        CopyRequestHeader(context, request, "Range");

        if (context.Request.HttpMethod is "POST" or "PUT" or "PATCH")
        {
            var body = new MemoryStream();
            context.Request.InputStream.CopyTo(body);
            body.Position = 0;
            var content = new StreamContent(body);
            if (!string.IsNullOrEmpty(context.Request.ContentType))
                content.Headers.TryAddWithoutValidation("Content-Type", context.Request.ContentType);
            request.Content = content;
        }

        using var response = _http.Send(request);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            CopyCookies(response, context);
            Note(context.Request.HttpMethod + " " + pathAndQuery + " " + (int)response.StatusCode);
            WriteRedirect(context, (int)response.StatusCode, RewriteLocation(response.Headers.Location, target, origin));
            return;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";
        var localBase = "http://127.0.0.1:" + Port;
        if (IsRewritable(mediaType))
        {
            var bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
            var encoding = Encoding.UTF8;
            var charset = response.Content.Headers.ContentType?.CharSet;
            if (!string.IsNullOrEmpty(charset))
            {
                try
                {
                    encoding = Encoding.GetEncoding(charset);
                }
                catch (ArgumentException)
                {
                    encoding = Encoding.UTF8;
                }
            }

            var text = ProxyRewrite.RewriteOrigin(encoding.GetString(bytes), origin, localBase);
            if (mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
                text = Inject(text, profile);
            CopyCookies(response, context);
            Note(context.Request.HttpMethod + " " + pathAndQuery + " " + (int)response.StatusCode + " " + mediaType + " " + encoding.GetByteCount(text));
            Write(context, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? mediaType, encoding.GetBytes(text));
            return;
        }

        var payload = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
        CopyCookies(response, context);
        Note(context.Request.HttpMethod + " " + pathAndQuery + " " + (int)response.StatusCode + " " + mediaType + " " + payload.Length);
        Write(context, (int)response.StatusCode, response.Content.Headers.ContentType?.ToString() ?? mediaType, payload);
    }

    private static bool IsRewritable(string mediaType) =>
        mediaType.Contains("html", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("css", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("javascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("ecmascript", StringComparison.OrdinalIgnoreCase)
        || mediaType.Contains("json", StringComparison.OrdinalIgnoreCase);

    private void CopyCookies(HttpResponseMessage response, HttpListenerContext context)
    {
        if (!response.Headers.NonValidated.TryGetValues("Set-Cookie", out var cookies))
            return;
        foreach (var cookie in cookies)
        {
            var rewritten = ProxyRewrite.RewriteCookie(cookie);
            context.Response.AppendHeader("Set-Cookie", rewritten);
            var name = rewritten.Split(';', 2)[0].Split('=', 2)[0].Trim();
            if (name.Length > 0)
                Note("set-cookie " + name);
        }
    }

    private void Note(string line)
    {
        if (_debug && !line.StartsWith("POST /__emulator/", StringComparison.Ordinal) && !line.StartsWith("GET /__emulator/", StringComparison.Ordinal))
            Log?.Invoke(line);
    }

    private string RewriteLocation(Uri? location, Uri target, Uri origin)
    {
        if (location is null)
            return "/";
        var absolute = location.IsAbsoluteUri ? location : new Uri(target, location);
        if (!absolute.Scheme.Equals(origin.Scheme, StringComparison.OrdinalIgnoreCase)
            || !absolute.Host.Equals(origin.Host, StringComparison.OrdinalIgnoreCase)
            || absolute.Port != origin.Port)
            return absolute.ToString();
        return "http://127.0.0.1:" + Port + absolute.PathAndQuery;
    }

    private static void CopyRequestHeader(HttpListenerContext context, HttpRequestMessage request, string name)
    {
        var value = context.Request.Headers[name];
        if (!string.IsNullOrEmpty(value))
            request.Headers.TryAddWithoutValidation(name, value);
    }

    private static void WriteRedirect(HttpListenerContext context, int status, string location)
    {
        context.Response.StatusCode = status;
        context.Response.Headers["Location"] = location;
        context.Response.Headers["Cache-Control"] = "no-store";
        context.Response.ContentLength64 = 0;
        context.Response.Close();
    }

    private static JsonNode ReadAppInfo(string directory)
    {
        var appInfoPath = Path.Combine(directory, "appinfo.json");
        if (File.Exists(appInfoPath))
        {
            var parsed = JsonNode.Parse(File.ReadAllText(appInfoPath));
            if (parsed is not null)
                return parsed;
        }

        var info = new JsonObject
        {
            ["id"] = "emulator.app",
            ["title"] = "App",
            ["version"] = "1.0.0"
        };
        var configPath = Path.Combine(directory, "config.xml");
        if (!File.Exists(configPath))
            return info;

        var xml = File.ReadAllText(configPath);
        var id = Regex.Match(xml, "<tizen:application\\b[^>]*\\bid\\s*=\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
        var name = Regex.Match(xml, "<name>([^<]+)</name>", RegexOptions.IgnoreCase);
        if (id.Success)
            info["id"] = id.Groups[1].Value;
        if (name.Success)
            info["title"] = name.Groups[1].Value.Trim();
        return info;
    }

    private JsonObject AcceptMedia(HttpListenerContext context)
    {
        var op = context.Request.QueryString["op"] ?? "";
        var id = context.Request.QueryString["id"] ?? "";
        var initDataType = context.Request.QueryString["initDataType"];
        var keySystem = context.Request.QueryString["keySystem"];
        var sessionType = context.Request.QueryString["sessionType"];
        using var buffer = new MemoryStream();
        context.Request.InputStream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        if (bytes.Length > 32 * 1024 * 1024)
            return new JsonObject { ["ok"] = false, ["error"] = "too-large" };

        var command = new JsonObject
        {
            ["cmd"] = "media",
            ["op"] = op,
            ["id"] = id,
            ["size"] = bytes.Length,
            ["bytes"] = Convert.ToBase64String(bytes)
        };
        if (!string.IsNullOrEmpty(initDataType))
            command["initDataType"] = initDataType;
        if (!string.IsNullOrEmpty(keySystem))
            command["keySystem"] = keySystem;
        if (!string.IsNullOrEmpty(sessionType))
            command["sessionType"] = sessionType;
        return Avplay.Submit(command)
            ? new JsonObject { ["ok"] = true }
            : new JsonObject { ["ok"] = false, ["error"] = "InvalidValuesError" };
    }

    private void HandleBoot(HttpListenerContext context)
    {
        var width = 0;
        var height = 0;
        try
        {
            using var reader = new StreamReader(context.Request.InputStream, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (text.Length > 0)
            {
                var node = JsonNode.Parse(text) as JsonObject;
                width = ReadPx(node?["w"]);
                height = ReadPx(node?["h"]);
            }
        }
        catch (JsonException)
        {
        }
        catch (InvalidOperationException)
        {
        }

        var decision = DecideBoot(width, height);
        if (decision.GiveUp)
            Log?.Invoke("boot give-up viewport=" + width + "x" + height + " expected=" + decision.Expected);
        if (decision.Release)
            StageReady();
        WriteJson(context, new JsonObject { ["url"] = decision.Url });
    }

    private readonly record struct BootDecision(string Url, bool Release, bool GiveUp, string Expected);

    private BootDecision DecideBoot(int width, int height)
    {
        var viewport = _profile?.Viewport;
        var expectedWidth = viewport?.Width ?? 0;
        var expectedHeight = viewport?.Height ?? 0;
        var matches = expectedWidth > 0 && expectedHeight > 0
            && Math.Abs(width - expectedWidth) <= BootTolerancePx
            && Math.Abs(height - expectedHeight) <= BootTolerancePx;
        var expected = expectedWidth + "x" + expectedHeight;

        lock (_bootGate)
        {
            var now = Environment.TickCount64;
            if (_bootSeenAt == 0)
                _bootSeenAt = now;

            if (_bootReleased)
                return new BootDecision(_startPath, false, false, expected);

            if (matches)
                _bootMatches++;
            else
                _bootMatches = 0;

            if (_bootMatches >= 2)
            {
                _bootReleased = true;
                return new BootDecision(_startPath, true, false, expected);
            }

            if (now - _bootSeenAt >= BootGiveUpMs)
            {
                _bootReleased = true;
                return new BootDecision(_startPath, true, true, expected);
            }

            return new BootDecision("", false, false, expected);
        }
    }

    private static string NormalizeStartPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "/";
        return path.StartsWith('/') ? path : "/" + path;
    }

    private static int ReadPx(JsonNode? node)
    {
        if (node is null)
            return 0;
        try
        {
            return (int)Math.Round(node.GetValue<double>());
        }
        catch (InvalidOperationException)
        {
            return 0;
        }
        catch (FormatException)
        {
            return 0;
        }
    }

    private static void WriteJson(HttpListenerContext context, JsonNode body) =>
        Write(context, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(body.ToJsonString()));

    private static void Write(HttpListenerContext context, int status, string contentType, byte[] body)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = contentType;
        context.Response.ContentLength64 = body.Length;
        context.Response.Headers["Cache-Control"] = "no-store";
        if (body.Length > 0)
            context.Response.OutputStream.Write(body, 0, body.Length);
        context.Response.OutputStream.Close();
        context.Response.Close();
    }
}
