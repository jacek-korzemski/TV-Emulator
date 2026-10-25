using System.Windows;
using System.Windows.Interop;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using TvEmulator.Services;

namespace TvEmulator;

public sealed class VideoPlaneWindow : Window
{
    private readonly WebView2 _view = new() { DefaultBackgroundColor = System.Drawing.Color.Black };

    public event Action<string>? MessageReceived;
    public event Action<string>? Failed;

    public IntPtr Handle { get; private set; }

    public VideoPlaneWindow()
    {
        Title = "TV Emulator video plane";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Background = System.Windows.Media.Brushes.Black;
        Width = 160;
        Height = 90;
        Content = _view;
        SourceInitialized += (_, _) =>
        {
            Handle = new WindowInteropHelper(this).Handle;
            NativeMethods.MakePassive(Handle);
        };
    }

    public async void Start(string userDataFolder, string url, bool debug)
    {
        try
        {
            var arguments = "--disable-web-security --autoplay-policy=no-user-gesture-required --disable-features=CalculateNativeWinOcclusion,WebContentsOcclusion";
            if (debug)
                arguments += " --remote-debugging-port=9229 --enable-precise-memory-info";
            var options = new CoreWebView2EnvironmentOptions(arguments);
            var environment = await CoreWebView2Environment.CreateAsync(null, userDataFolder, options);
            await _view.EnsureCoreWebView2Async(environment);
            var core = _view.CoreWebView2;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreDevToolsEnabled = debug;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.WebMessageReceived += (_, e) => MessageReceived?.Invoke(e.WebMessageAsJson);
            core.ProcessFailed += (_, e) => Failed?.Invoke("WebView2 " + e.ProcessFailedKind);
            core.NavigationCompleted += (_, e) =>
            {
                if (!e.IsSuccess)
                    Failed?.Invoke("WebView2 navigation " + e.WebErrorStatus);
            };
            core.Navigate(url);
        }
        catch (Exception ex)
        {
            Failed?.Invoke(ex.GetType().Name + ": " + ex.Message);
        }
    }

    public void Post(string json) => _view.CoreWebView2?.PostWebMessageAsJson(json);

    public void SetUserAgent(string userAgent)
    {
        if (_view.CoreWebView2 is { } core && userAgent.Length > 0)
            core.Settings.UserAgent = userAgent;
    }

    protected override void OnClosed(EventArgs e)
    {
        _view.Dispose();
        base.OnClosed(e);
    }
}
