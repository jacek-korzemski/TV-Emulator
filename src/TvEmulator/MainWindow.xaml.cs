using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TvEmulator.Models;
using TvEmulator.Services;

namespace TvEmulator;

public partial class MainWindow : Window
{
    private readonly string _root;
    private readonly List<TvProfile> _profiles;
    private readonly List<EngineBuild> _engines;
    private AppServer? _server;
    private EngineSession? _engine;
    private VideoPlane? _plane;
    private SessionLog? _sessionLog;
    private DispatcherTimer? _attachTimer;
    private int _sessionState;
    private bool _cursorHidden;
    private bool _hotkeyRegistered;
    private int _attachTicks;
    private bool _enginePlaced;
    private PixelRect _stage;
    private double _settingsLeft;
    private double _settingsTop;
    private double _settingsWidth;
    private double _settingsHeight;
    private System.Windows.Media.Brush _settingsBackground = System.Windows.Media.Brushes.Transparent;

    public MainWindow()
    {
        InitializeComponent();
        _root = RepoPaths.Root();
        _profiles = Catalog.LoadProfiles(_root).ToList();
        _engines = Catalog.LoadEngines(_root).ToList();
        ProfileBox.ItemsSource = _profiles
            .Select(profile => new ProfileChoice(profile, ProfileDetail(profile, _engines)))
            .ToList();
        if (_profiles.Count > 0)
            ProfileBox.SelectedIndex = 0;

        var index = 0;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            index++;
            var mark = screen.Primary ? " (główny)" : "";
            MonitorBox.Items.Add(new MonitorChoice(screen, index + ". " + screen.Bounds.Width + "×" + screen.Bounds.Height + mark));
        }

        if (MonitorBox.Items.Count > 0)
            MonitorBox.SelectedIndex = 0;

        var sample = Path.Combine(_root, "samples", "tizen-hello");
        if (Directory.Exists(sample))
            DirectoryBox.Text = sample;

        _settingsBackground = Background;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        EndSession(null);
        base.OnClosing(e);
    }

    private void ProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileChoice choice)
            return;
        var profile = choice.Profile;
        var memory = profile.MemoryMb?.ToString() ?? "—";
        var cpu = profile.CpuPercent?.ToString() ?? "—";
        LimitsText.Text = "RAM: " + memory + " MB. CPU: " + cpu + "%. Limity są zapisane w profilu i nie są egzekwowane.";
    }

    private void BrowseClick(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Katalog aplikacji HTML",
            UseDescriptionForTitle = true,
            SelectedPath = Directory.Exists(DirectoryBox.Text) ? DirectoryBox.Text : _root
        };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            DirectoryBox.Text = dialog.SelectedPath;
    }

    private void StartClick(object sender, RoutedEventArgs e) => BeginSession();

    private void BeginSession()
    {
        if (Interlocked.CompareExchange(ref _sessionState, 1, 0) != 0)
            return;

        try
        {
            Taskbar.Occupy();
            if (ProfileBox.SelectedItem is not ProfileChoice choice)
                throw new InvalidOperationException("Wybierz profil.");
            var profile = choice.Profile;
            if (MonitorBox.SelectedItem is not MonitorChoice monitor)
                throw new InvalidOperationException("Wybierz monitor.");

            var source = AppSource.Parse(DirectoryBox.Text);
            if (!source.IsRemote && !File.Exists(Path.Combine(source.Directory ?? "", "index.html")))
                throw new InvalidOperationException("Podaj katalog z index.html albo adres http(s).");

            var engine = _engines.FirstOrDefault(item => item.Id == profile.Engine)
                ?? throw new InvalidOperationException("Profil wskazuje silnik, którego nie ma w engines/builds.json.");
            var exe = Catalog.LocateEngine(_root, engine)
                ?? throw new InvalidOperationException("Brak pliku silnika dla " + engine.Id + ". Uruchom tools/fetch-engines.ps1.");

            _stage = ViewportFit.Fit16By9(monitor.Screen.Bounds, profile.Viewport.Width, profile.Viewport.Height);
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TvEmulator",
                "userdata",
                engine.Id);
            Directory.CreateDirectory(userData);
            if (!string.Equals(engine.Kind, "electron", StringComparison.Ordinal))
                ChromeProfile.Prepare(userData);

            _server = new AppServer();
            if (DebugBox.IsChecked == true)
            {
                _sessionLog = new SessionLog(Path.Combine(_root, "logs"));
                _sessionLog.Write("start profile=" + profile.Id + " engine=" + profile.Engine + " target=" + DirectoryBox.Text.Trim());
            }

            _server.ExitRequested += () => Dispatcher.BeginInvoke(() => EndSession("Aplikacja zakończyła sesję."));
            _server.Log += message =>
            {
                _sessionLog?.Write(message);
                var shown = message.Length > 300 ? message.Substring(0, 300) : message;
                Dispatcher.BeginInvoke(() => StatusText.Text = shown);
            };
            _server.Start(_root, source, profile, PointerBox.IsChecked == true, DebugBox.IsChecked == true, "colorKey");

            if (profile.Platform is "tizen" or "webos")
            {
                _plane = new VideoPlane(
                    Dispatcher,
                    _server.Avplay,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvEmulator", "userdata", "videoplane"),
                    "http://127.0.0.1:" + _server.Port + "/__emulator/videoplane.html",
                    _stage,
                    profile.Viewport,
                    DebugBox.IsChecked == true,
                    message => _sessionLog?.Write(message));
            }

            var arguments = SessionPlanner.Arguments(
                engine,
                profile,
                _stage,
                _server.Port,
                userData,
                source.LocalStartPath,
                Path.Combine(_root, "shell", "electron"),
                DebugBox.IsChecked == true);
            _engine = new EngineSession
            {
                HostManagedWindow = !string.Equals(engine.Kind, "electron", StringComparison.Ordinal),
                ColorKey = _plane is null ? null : VideoPlane.KeyColor
            };
            _engine.Exited += () => Dispatcher.BeginInvoke(() => EndSession("Silnik zamknął się sam."));
            _server.HoldPages();
            _engine.Start(exe, arguments);

            EnterStage(monitor);
        }
        catch (Exception ex)
        {
            _plane?.Dispose();
            _plane = null;
            _server?.Dispose();
            _server = null;
            _engine?.Dispose();
            _engine = null;
            var failedLog = _sessionLog?.FilePath;
            _sessionLog = null;
            _sessionState = 0;
            Taskbar.Restore();
            StatusText.Text = ex.Message + (failedLog is null ? "" : " Log: " + failedLog);
        }
    }

    private void EnterStage(MonitorChoice monitor)
    {
        _settingsLeft = Left;
        _settingsTop = Top;
        _settingsWidth = Width;
        _settingsHeight = Height;
        SettingsPanel.Visibility = Visibility.Collapsed;
        Footer.Visibility = Visibility.Collapsed;
        SessionHint.Visibility = Visibility.Collapsed;
        Background = System.Windows.Media.Brushes.Black;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowState = WindowState.Normal;
        Topmost = _engine?.HostManagedWindow == true;

        var hwnd = new WindowInteropHelper(this).EnsureHandle();
        var bounds = monitor.Screen.Bounds;
        NativeMethods.Place(hwnd, bounds.X, bounds.Y, bounds.Width, bounds.Height);
        _hotkeyRegistered = NativeMethods.RegisterHotKey(
            hwnd,
            NativeMethods.HotkeyId,
            NativeMethods.ModControl | NativeMethods.ModShift | NativeMethods.ModNoRepeat,
            NativeMethods.VkQ);

        _attachTicks = 0;
        _enginePlaced = false;
        _attachTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _attachTimer.Tick += (_, _) => AttachTick();
        _attachTimer.Start();
    }

    private void AttachTick()
    {
        _attachTicks++;
        var engine = _engine;
        if (engine is null)
            return;

        if (_enginePlaced)
        {
            engine.Maintain(_stage);
            _plane?.KeepBelow(engine.WindowHandle);
            SettleWhenFitted(engine);
            return;
        }

        if (engine.TryAttach(_stage))
        {
            _enginePlaced = true;
            _plane?.KeepBelow(engine.WindowHandle);
            if (!_cursorHidden && PointerBox.IsChecked != true)
            {
                NativeMethods.HideCursor();
                _cursorHidden = true;
            }

            SettleWhenFitted(engine);
            return;
        }

        if (_attachTicks == 40)
            SessionHint.Visibility = Visibility.Visible;
        if (_attachTicks > 80)
            _attachTimer?.Stop();
    }

    private void SettleWhenFitted(EngineSession engine)
    {
        if (!engine.Fitted)
            return;
        _server?.StageReady();
        if (_attachTimer is not null)
            _attachTimer.Interval = TimeSpan.FromSeconds(1);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmActivate && _sessionState == 1 && (wParam.ToInt32() & 0xFFFF) != 0)
            RaiseEngine();

        if (msg == NativeMethods.WmHotkey && wParam.ToInt32() == NativeMethods.HotkeyId)
        {
            handled = true;
            EndSession("Powrót z sesji.");
        }

        return IntPtr.Zero;
    }

    private void RaiseEngine()
    {
        var engine = _engine;
        if (engine is null || engine.WindowHandle == IntPtr.Zero)
            return;
        if (!engine.HostManagedWindow)
            NativeMethods.Raise(engine.WindowHandle);
        else
            NativeMethods.PlaceOnTop(engine.WindowHandle, _stage.X, _stage.Y, _stage.Width, _stage.Height);
        _plane?.KeepBelow(engine.WindowHandle);
        _ = NativeMethods.SetForegroundWindow(engine.WindowHandle);
    }

    private void EndSession(string? message)
    {
        if (Interlocked.CompareExchange(ref _sessionState, 2, 1) != 1)
            return;

        try
        {
            _attachTimer?.Stop();
            _enginePlaced = false;
            var hwnd = new WindowInteropHelper(this).Handle;
            if (_hotkeyRegistered && hwnd != IntPtr.Zero)
            {
                _ = NativeMethods.UnregisterHotKey(hwnd, NativeMethods.HotkeyId);
                _hotkeyRegistered = false;
            }

            if (_cursorHidden)
            {
                NativeMethods.ShowCursorAgain();
                _cursorHidden = false;
            }

            _plane?.Dispose();
            _plane = null;
            _engine?.Stop();
            _engine = null;
            _server?.Dispose();
            _server = null;
            Taskbar.Restore();
            var logPath = _sessionLog?.FilePath;
            _sessionLog = null;
            RestoreSettings();
            if (message is not null || logPath is not null)
                StatusText.Text = (message ?? "Sesja zakończona.") + (logPath is null ? "" : " Log: " + logPath);
        }
        finally
        {
            _sessionState = 0;
        }
    }

    private void RestoreSettings()
    {
        Topmost = false;
        WindowStyle = WindowStyle.SingleBorderWindow;
        ResizeMode = ResizeMode.CanResize;
        Background = _settingsBackground;
        SettingsPanel.Visibility = Visibility.Visible;
        Footer.Visibility = Visibility.Visible;
        SessionHint.Visibility = Visibility.Collapsed;
        WindowState = WindowState.Normal;
        Left = _settingsLeft;
        Top = _settingsTop;
        Width = _settingsWidth;
        Height = _settingsHeight;
        Activate();
    }

    private static string ProfileDetail(TvProfile profile, IReadOnlyList<EngineBuild> engines)
    {
        var engine = engines.FirstOrDefault(item => item.Id == profile.Engine);
        var chromium = engine is null ? profile.Engine : "Chromium " + engine.Milestone;
        return string.IsNullOrWhiteSpace(profile.Player) ? chromium : chromium + " · " + profile.Player;
    }

    private sealed class ProfileChoice(TvProfile profile, string detail)
    {
        public TvProfile Profile { get; } = profile;
        public string Label => Profile.Label;
        public string Detail { get; } = detail;
    }

    private sealed class MonitorChoice(System.Windows.Forms.Screen screen, string label)
    {
        public System.Windows.Forms.Screen Screen { get; } = screen;
        public override string ToString() => label;
    }
}
