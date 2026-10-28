using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using TvEmulator.Debug;
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
    private DebugServer? _debugServer;
    private CdpWatcher? _cdp;
    private readonly CancellationTokenSource _debugStop = new();
    private DispatcherTimer? _attachTimer;
    private int _sessionState;
    private bool _cursorHidden;
    private bool _hotkeyRegistered;
    private int _attachTicks;
    private bool _enginePlaced;
    private bool _releasePagesOnFit = true;
    private bool _gpuChroma;
    private bool _chromaReady;
    private bool _gpuFallbackUsed;
    private ChromaPresenter? _presenter;
    private DispatcherTimer? _chromaTimer;
    private EngineBuild? _launchEngine;
    private string? _launchExe;
    private string? _launchUserData;
    private bool _launchDebug;
    private int? _launchCpu;
    private int? _launchMemory;
    private PixelRect _stage;
    private double _settingsLeft;
    private double _settingsTop;
    private double _settingsWidth;
    private double _settingsHeight;
    private System.Windows.Media.Brush _settingsBackground = System.Windows.Media.Brushes.Transparent;
    private UserSettings _userSettings = new();

    public MainWindow()
    {
        InitializeComponent();
        _root = RepoPaths.Root();
        _profiles = Catalog.LoadProfiles(_root).ToList();
        _engines = Catalog.LoadEngines(_root).ToList();
        _userSettings = UserSettingsStore.Load();

        var index = 0;
        foreach (var screen in System.Windows.Forms.Screen.AllScreens)
        {
            index++;
            var mark = screen.Primary ? " (główny)" : "";
            MonitorBox.Items.Add(new MonitorChoice(screen, index + ". " + screen.Bounds.Width + "×" + screen.Bounds.Height + mark));
        }

        if (MonitorBox.Items.Count > 0)
            MonitorBox.SelectedIndex = 0;

        DirectoryBox.Text = UserSettingsStore.StartupApp(_userSettings, SampleApp());
        _settingsBackground = Background;
        BindProfiles();
        _ = StartDebugPanel();
    }

    private void SettingsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SettingsWindow(_root, _profiles, _engines, _userSettings) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        var previousDefault = _userSettings.DefaultApp;
        _userSettings = dialog.Result;
        UserSettingsStore.Save(_userSettings);
        DirectoryBox.Text = UserSettingsStore.NextAppField(
            DirectoryBox.Text,
            previousDefault,
            SampleApp(),
            _userSettings.DefaultApp);
        BindProfiles();
        if (ProfileBox.Items.Count > 0)
            StatusText.Text = "Zapisano ustawienia.";
    }

    private void BudgetClick(object sender, RoutedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileChoice choice)
        {
            StatusText.Text = "Wybierz profil.";
            return;
        }

        var engine = _engines.FirstOrDefault(item => item.Id == choice.Profile.Engine);
        var dialog = new BudgetWindow(choice.Profile, engine, _userSettings) { Owner = this };
        if (dialog.ShowDialog() != true)
            return;

        _userSettings = dialog.Result;
        UserSettingsStore.Save(_userSettings);
        ShowLimits(choice.Profile);
        StatusText.Text = "Zapisano moc telewizora.";
    }

    private void BindProfiles()
    {
        var selectedId = (ProfileBox.SelectedItem as ProfileChoice)?.Profile.Id;
        var choices = UserSettingsStore.VisibleProfiles(_profiles, _userSettings)
            .Select(profile => new ProfileChoice(profile, ProfileDetail(profile, _engines)))
            .ToList();
        ProfileBox.ItemsSource = choices;
        var match = choices.FindIndex(choice => choice.Profile.Id == selectedId);
        ProfileBox.SelectedIndex = match >= 0 ? match : choices.Count > 0 ? 0 : -1;
        if (choices.Count == 0)
        {
            LimitsText.Text = "";
            StatusText.Text = "Wszystkie profile są ukryte. Otwórz Ustawienia i włącz choć jeden.";
        }
    }

    private string SampleApp()
    {
        var sample = Path.Combine(_root, "samples", "tizen-hello");
        return Directory.Exists(sample) ? sample : "";
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
        _debugStop.Cancel();
        var server = _debugServer;
        _debugServer = null;
        if (server is not null)
            Task.Run(() => server.DisposeAsync().AsTask()).Wait(TimeSpan.FromSeconds(2));
        base.OnClosing(e);
    }

    private void ProfileChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (ProfileBox.SelectedItem is not ProfileChoice choice)
            return;
        ShowLimits(choice.Profile);
    }

    private void ShowLimits(TvProfile profile)
    {
        var budget = UserSettingsStore.BudgetFor(_userSettings, profile.Id);
        var cpu = budget?.CpuPercent;
        var memory = budget?.MemoryMb;
        if (cpu is null && memory is null)
        {
            LimitsText.Text = "Bez limitu procesora i pamięci. Moc telewizora ustawia je dla tego komputera.";
            return;
        }

        var cpuText = cpu is null ? "bez limitu" : cpu + "% jednego wątku";
        var memoryText = memory is null ? "bez limitu" : memory + " MB";
        LimitsText.Text = "Procesor: " + cpuText + ". Pamięć silnika: " + memoryText + ".";
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
            _releasePagesOnFit = !SessionPlanner.UsesBootGate(engine);
            var exe = Catalog.LocateEngine(_root, engine)
                ?? throw new InvalidOperationException("Brak pliku silnika dla " + engine.Id + ". Uruchom tools/fetch-engines.ps1.");

            _stage = ViewportFit.Fit16By9(monitor.Screen.Bounds, profile.Viewport.Width, profile.Viewport.Height);
            var userData = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TvEmulator",
                "userdata",
                engine.Id);
            Directory.CreateDirectory(userData);
            if (string.Equals(engine.Kind, "firefox", StringComparison.Ordinal))
                FirefoxProfile.Prepare(userData, profile.UserAgent);
            else if (!string.Equals(engine.Kind, "electron", StringComparison.Ordinal))
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
                _debugServer?.Hub.AddLog(message);
                var shown = message.Length > 300 ? message.Substring(0, 300) : message;
                Dispatcher.BeginInvoke(() => StatusText.Text = shown);
            };
            _server.DebugReported += message => _debugServer?.Hub.AddShim(message);
            _server.Start(_root, source, profile, PointerBox.IsChecked == true, DebugBox.IsChecked == true, "colorKey");

            if (profile.Player is "edge" or "avplay")
            {
                _plane = new VideoPlane(
                    Dispatcher,
                    _server.Avplay,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "TvEmulator", "userdata", "videoplane"),
                    "http://127.0.0.1:" + _server.Port + "/__emulator/videoplane.html",
                    _stage,
                    profile.Viewport,
                    DebugBox.IsChecked == true,
                    message =>
                    {
                        _sessionLog?.Write(message);
                        _debugServer?.Hub.AddLog(message);
                    });
            }

            var debug = DebugBox.IsChecked == true;
            _launchEngine = engine;
            _launchExe = exe;
            _launchUserData = userData;
            _launchDebug = debug;
            var budget = UserSettingsStore.BudgetFor(_userSettings, profile.Id);
            _launchCpu = budget?.CpuPercent;
            _launchMemory = budget?.MemoryMb;
            _gpuFallbackUsed = false;
            _debugServer?.Hub.BeginSession(profile.Id, profile.Label, profile.Engine, debug, _sessionLog?.FilePath, _plane is not null);
            if (debug && _debugServer is not null && !string.Equals(engine.Kind, "firefox", StringComparison.Ordinal))
            {
                _cdp = CdpWatcher.Start(_debugServer.Hub, _plane is not null);
                _cdp.EngineLost += () => Dispatcher.BeginInvoke(() =>
                    EndSession("Silnik przeglądarki padł. Sesja zamknięta, pasek zadań wrócił."));
            }
            _server.HoldPages();
            var gpu = _plane is not null
                && !string.Equals(engine.Kind, "firefox", StringComparison.Ordinal)
                && !string.Equals(engine.Kind, "electron", StringComparison.Ordinal);
            StartEngine(gpu);

            EnterStage(monitor);
        }
        catch (Exception ex)
        {
            _plane?.Dispose();
            _plane = null;
            _cdp?.Dispose();
            _cdp = null;
            _engine?.Dispose();
            _engine = null;
            _server?.Dispose();
            _server = null;
            _debugServer?.Hub.EndSession();
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
        if (engine.HasExited)
        {
            EndSession("Silnik zamknął się sam.");
            return;
        }

        if (_enginePlaced)
        {
            engine.Maintain(_stage);
            if (_presenter is not null)
            {
                _presenter.Source = engine.WindowHandle;
                _presenter.Place(_stage);
                ApplyPageCrop(engine);
                Stack(false);
            }
            else if (_gpuChroma && !_gpuFallbackUsed)
                TryStartPresenter();
            else
                _plane?.KeepBelow(engine.WindowHandle);
            SettleWhenFitted(engine);
            return;
        }

        if (engine.TryAttach(_stage))
        {
            _enginePlaced = true;
            if (_gpuChroma)
                TryStartPresenter();
            else
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
        if (_gpuChroma && !_chromaReady)
            return;
        // Chromium opens the app from the boot gate, after its own bars are gone.
        // Firefox and Electron still release the page once the frame fits.
        if (_releasePagesOnFit)
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
        if (_presenter is not null)
        {
            Stack(true);
            return;
        }

        if (!engine.HostManagedWindow)
            NativeMethods.Raise(engine.WindowHandle);
        else
            NativeMethods.PlaceOnTop(engine.WindowHandle, _stage.X, _stage.Y, _stage.Width, _stage.Height);
        _plane?.KeepBelow(engine.WindowHandle);
        _ = NativeMethods.SetForegroundWindow(engine.WindowHandle);
    }

    private void StartEngine(bool gpuChroma)
    {
        var engine = _launchEngine ?? throw new InvalidOperationException("Brak silnika sesji.");
        var exe = _launchExe ?? throw new InvalidOperationException("Brak pliku silnika.");
        _gpuChroma = gpuChroma;
        _chromaReady = false;
        if (_server is not null)
            _server.DeferBoot = gpuChroma;
        var profile = (ProfileBox.SelectedItem as ProfileChoice)?.Profile
            ?? throw new InvalidOperationException("Wybierz profil.");
        var arguments = SessionPlanner.Arguments(
            engine,
            profile,
            _stage,
            _server?.Port ?? 0,
            _launchUserData ?? "",
            AppSource.Parse(DirectoryBox.Text).LocalStartPath,
            Path.Combine(_root, "shell", "electron"),
            _launchDebug,
            gpuChroma);
        _engine = new EngineSession
        {
            HostManagedWindow = !string.Equals(engine.Kind, "electron", StringComparison.Ordinal),
            GpuChroma = gpuChroma,
            ColorKey = gpuChroma || _plane is null ? null : VideoPlane.KeyColor
        };
        _engine.Exited += () => Dispatcher.BeginInvoke(OnEngineExited);
        _engine.Start(exe, arguments, _launchCpu, _launchMemory);
        if (gpuChroma)
            _sessionLog?.Write("chroma gpu milestone=" + engine.Milestone);
    }

    private void OnEngineExited()
    {
        if (_gpuChroma && !_chromaReady && !_gpuFallbackUsed && _sessionState == 1)
        {
            FallbackToColorKey("Silnik GPU zakończył się przed pierwszą klatką.");
            return;
        }

        EndSession("Silnik zamknął się sam.");
    }

    private void TryStartPresenter()
    {
        var engine = _engine;
        if (engine is null || engine.WindowHandle == IntPtr.Zero || _presenter is not null)
            return;
        try
        {
            var presenter = new ChromaPresenter(_stage, message =>
            {
                _sessionLog?.Write(message);
                _debugServer?.Hub.AddLog(message);
            });
            presenter.FrameReady += () => Dispatcher.BeginInvoke(OnChromaFrame);
            presenter.Failed += reason => Dispatcher.BeginInvoke(() =>
            {
                if (!_chromaReady)
                    FallbackToColorKey(reason);
            });
            presenter.Start(engine.WindowHandle);
            _presenter = presenter;
            _chromaTimer?.Stop();
            _chromaTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _chromaTimer.Tick += (_, _) =>
            {
                _chromaTimer?.Stop();
                if (!_chromaReady)
                    FallbackToColorKey("Prezenter GPU nie dostał klatki.");
            };
            _chromaTimer.Start();
            Stack(true);
        }
        catch (Exception ex)
        {
            _presenter?.Dispose();
            _presenter = null;
            FallbackToColorKey(ex.Message);
        }
    }

    private void OnChromaFrame()
    {
        if (_chromaReady || _sessionState != 1)
            return;
        _chromaReady = true;
        _chromaTimer?.Stop();
        if (_server is not null)
            _server.DeferBoot = false;
        _sessionLog?.Write("chroma presenter ready");
        Stack(true);
        if (_engine is { } engine)
            SettleWhenFitted(engine);
    }

    private void FallbackToColorKey(string reason)
    {
        if (_gpuFallbackUsed || _sessionState != 1)
            return;
        _gpuFallbackUsed = true;
        _gpuChroma = false;
        _chromaTimer?.Stop();
        _presenter?.Dispose();
        _presenter = null;
        _sessionLog?.Write("chroma fallback " + reason);
        _debugServer?.Hub.AddLog("chroma fallback " + reason);
        _engine?.Stop();
        _engine = null;
        _enginePlaced = false;
        _attachTicks = 0;
        try
        {
            _server?.HoldPages();
            StartEngine(false);
        }
        catch (Exception ex)
        {
            EndSession(ex.Message);
        }
    }

    private void ApplyPageCrop(EngineSession engine)
    {
        if (_presenter is null || !engine.TryPageCrop(out var page, out var window))
            return;
        _presenter.SetPageCrop(page.X, page.Y, page.Width, page.Height, window.Width, window.Height);
    }

    private void Stack(bool focus)
    {
        var engine = _engine;
        var presenter = _presenter;
        if (engine is null || presenter is null || engine.WindowHandle == IntPtr.Zero)
            return;
        presenter.Source = engine.WindowHandle;
        presenter.Place(_stage);
        ApplyPageCrop(engine);
        var video = _plane?.WindowHandle ?? IntPtr.Zero;
        if (video != IntPtr.Zero)
            _plane?.KeepBelow(presenter.Handle);
        var host = new WindowInteropHelper(this).Handle;
        NativeMethods.OrderBelow(host, video != IntPtr.Zero ? video : presenter.Handle);
        if (focus)
            NativeMethods.FocusChrome(engine.WindowHandle);
    }

    private void EndSession(string? message)
    {
        if (Interlocked.CompareExchange(ref _sessionState, 2, 1) != 1)
            return;

        try
        {
            Taskbar.Restore();
            _attachTimer?.Stop();
            _chromaTimer?.Stop();
            _enginePlaced = false;
            _presenter?.Dispose();
            _presenter = null;
            if (_server is not null)
                _server.DeferBoot = false;
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
            _cdp?.Dispose();
            _cdp = null;
            _engine?.Stop();
            _engine = null;
            _server?.Dispose();
            _server = null;
            _debugServer?.Hub.EndSession();
            Taskbar.Restore();
            var logPath = _sessionLog?.FilePath;
            _sessionLog = null;
            RestoreSettings();
            if (message is not null || logPath is not null)
                StatusText.Text = (message ?? "Sesja zakończona.") + (logPath is null ? "" : " Log: " + logPath);
        }
        finally
        {
            Taskbar.Restore();
            if (_cursorHidden)
            {
                NativeMethods.ShowCursorAgain();
                _cursorHidden = false;
            }

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

    private async Task StartDebugPanel()
    {
        try
        {
            var server = await DebugServer.StartAsync(Path.Combine(_root, "debug-ui"));
            if (_debugStop.IsCancellationRequested)
            {
                await server.DisposeAsync();
                return;
            }

            _debugServer = server;
            var text = server.FormatAddresses();
            await Dispatcher.InvokeAsync(() => DebugAddressText.Text = text);
        }
        catch (Exception ex)
        {
            if (_debugStop.IsCancellationRequested)
                return;
            await Dispatcher.InvokeAsync(() => DebugAddressText.Text = "Podgląd na żywo nie wystartował: " + ex.Message);
        }
    }
}
