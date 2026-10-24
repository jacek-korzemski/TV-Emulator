using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows.Threading;
using TvEmulator.Models;

namespace TvEmulator.Services;

/// <summary>
/// Native video layer of a Tizen TV. It sits directly below the Chromium window,
/// which lets it show through wherever the page paints <see cref="KeyColor"/>.
/// </summary>
public sealed class VideoPlane : IDisposable
{
    public const int KeyColor = 0x010001;

    private readonly Dispatcher _dispatcher;
    private readonly AvplayBridge _bridge;
    private readonly string _userDataFolder;
    private readonly string _pageUrl;
    private readonly PixelRect _stage;
    private readonly Viewport _viewport;
    private readonly bool _debug;
    private readonly Action<string> _log;
    private readonly List<JsonObject> _pending = new();
    private JsonObject? _shade;
    private VideoPlaneWindow? _window;
    private bool _ready;
    private bool _failed;
    private bool _visible;
    private bool _disposed;
    private string? _userAgent;
    private PixelRect _rect;
    private IntPtr _above;
    private bool _topmost = true;

    public void SetTopmost(bool value)
    {
        _topmost = value;
        if (_window is not null)
            _window.Topmost = value;
    }

    public VideoPlane(
        Dispatcher dispatcher,
        AvplayBridge bridge,
        string userDataFolder,
        string pageUrl,
        PixelRect stage,
        Viewport viewport,
        bool debug,
        Action<string> log)
    {
        _dispatcher = dispatcher;
        _bridge = bridge;
        _userDataFolder = userDataFolder;
        _pageUrl = pageUrl;
        _stage = stage;
        _viewport = viewport;
        _debug = debug;
        _log = log;
        _rect = stage;
        _bridge.Command += OnCommand;
    }

    public static PixelRect MapRect(PixelRect stage, Viewport viewport, double x, double y, double width, double height)
    {
        var scaleX = stage.Width / (double)Math.Max(1, viewport.Width);
        var scaleY = stage.Height / (double)Math.Max(1, viewport.Height);
        return new PixelRect(
            stage.X + (int)Math.Round(x * scaleX),
            stage.Y + (int)Math.Round(y * scaleY),
            Math.Max(1, (int)Math.Round(width * scaleX)),
            Math.Max(1, (int)Math.Round(height * scaleY)));
    }

    public void KeepBelow(IntPtr engineWindow)
    {
        _above = engineWindow;
        Place();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        _bridge.Command -= OnCommand;
        _window?.Close();
        _window = null;
    }

    private void OnCommand(JsonObject command) => _dispatcher.BeginInvoke(() => Handle(command));

    private void Handle(JsonObject command)
    {
        if (_disposed)
            return;

        var name = (string?)command["cmd"];
        switch (name)
        {
            case "display":
                _rect = MapRect(_stage, _viewport, Number(command["x"]), Number(command["y"]), Number(command["w"]), Number(command["h"]));
                Place();
                break;
            case "shade":
                // Keep only the newest animation frame. Queuing every frame while
                // WebView2 starts would replay a stale fade after the page is ready.
                _shade = (JsonObject)command.DeepClone();
                break;
            case "property" when (string?)command["name"] == "USER_AGENT":
                _userAgent = (string?)command["value"];
                if (_ready)
                    _window?.SetUserAgent(_userAgent ?? "");
                break;
            case "open":
                _visible = true;
                break;
            case "media" when (string?)command["op"] is "open":
                _visible = true;
                break;
            case "close":
            case "reset":
            case "media" when (string?)command["op"] is "close":
                _visible = false;
                _shade = null;
                break;
        }

        EnsureWindow();
        if (_failed)
        {
            if (name == "prepare")
                _bridge.Publish(new JsonObject { ["type"] = "prepareError", ["gen"] = command["gen"]?.DeepClone(), ["code"] = "PLAYER_ERROR_NOT_SUPPORTED_FILE", ["message"] = "Płaszczyzna wideo (WebView2) nie wystartowała." });
            if (name == "media" && (string?)command["op"] is "open")
                _bridge.Publish(new JsonObject { ["type"] = "mse-error", ["message"] = "Płaszczyzna wideo (WebView2) nie wystartowała." });
            return;
        }

        if (name == "shade")
        {
            if (_ready)
                _window?.Post(command.ToJsonString());
        }
        else if (_ready)
            _window?.Post(command.ToJsonString());
        else
            _pending.Add(command);
        Place();
    }

    private void EnsureWindow()
    {
        if (_window is not null || _failed)
            return;

        _window = new VideoPlaneWindow { Topmost = _topmost };
        _window.MessageReceived += OnMessage;
        _window.Failed += OnFailed;
        _window.Show();
        Place();
        _window.Start(_userDataFolder, _pageUrl, _debug);
    }

    private void Place()
    {
        var handle = _window?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero)
            return;
        if (!_visible || _above == IntPtr.Zero)
        {
            NativeMethods.Hide(handle);
            return;
        }

        NativeMethods.MakePassive(handle);
        NativeMethods.PlaceBelow(handle, _above, _rect.X, _rect.Y, _rect.Width, _rect.Height);
    }

    private void OnMessage(string json)
    {
        if (_disposed)
            return;

        JsonObject? message;
        try
        {
            message = JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return;
        }

        if (message is null)
            return;

        switch ((string?)message["type"])
        {
            case "ready":
                _ready = true;
                _log("videoplane ready shaka=" + (string?)message["shaka"]);
                if (_userAgent is not null)
                    _window?.SetUserAgent(_userAgent);
                foreach (var command in _pending)
                    _window?.Post(command.ToJsonString());
                _pending.Clear();
                if (_shade is not null)
                    _window?.Post(_shade.ToJsonString());
                break;
            case "log":
                _log("videoplane " + (string?)message["text"]);
                break;
            default:
                _bridge.Publish(message);
                break;
        }
    }

    private void OnFailed(string reason)
    {
        _log("videoplane failed " + reason);
        if (_ready)
        {
            _bridge.Publish(new JsonObject { ["type"] = "error", ["code"] = "PLAYER_ERROR_GENEREIC", ["message"] = reason });
            return;
        }

        _failed = true;
        foreach (var command in _pending)
        {
            if ((string?)command["cmd"] == "prepare")
                _bridge.Publish(new JsonObject { ["type"] = "prepareError", ["gen"] = command["gen"]?.DeepClone(), ["code"] = "PLAYER_ERROR_NOT_SUPPORTED_FILE", ["message"] = reason });
        }

        _pending.Clear();
    }

    private static double Number(JsonNode? node)
    {
        if (node is null)
            return 0;
        return node.GetValueKind() switch
        {
            JsonValueKind.Number => node.GetValue<double>(),
            JsonValueKind.String when double.TryParse((string?)node, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => 0
        };
    }
}
