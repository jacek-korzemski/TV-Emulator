using System.Numerics;
using System.Runtime.InteropServices;
using Vortice.Direct2D1;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DirectComposition;
using Vortice.DXGI;
using Vortice.Mathematics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;
using D2DPixelFormat = Vortice.DCommon.PixelFormat;
using D2DAlpha = Vortice.DCommon.AlphaMode;

namespace TvEmulator.Services;

/// <summary>
/// Shows a Chromium window with the key color punched out, while that window stays
/// covered. The page is captured on the GPU and keyed on a composition surface above
/// the Edge video plane. The video window itself is unchanged, so DRM stays where it is.
/// </summary>
public sealed class ChromaPresenter : IDisposable
{
    const int WsPopup = unchecked((int)0x80000000);
    const int WsVisible = 0x10000000;
    const int WsExNoRedirectionBitmap = 0x00200000;
    const int WsExTopmost = 0x00000008;
    const int WsExToolWindow = 0x00000080;
    const int WsExNoActivate = 0x08000000;
    const int WmMouseActivate = 0x0021;
    const int WmMouseMove = 0x0200;
    const int WmLButtonDown = 0x0201;
    const int WmLButtonUp = 0x0202;
    const int WmRButtonDown = 0x0204;
    const int WmRButtonUp = 0x0205;
    const int WmMButtonDown = 0x0207;
    const int WmMButtonUp = 0x0208;
    const int WmMouseWheel = 0x020A;
    const int WmKeyDown = 0x0100;
    const int WmKeyUp = 0x0101;
    const int WmChar = 0x0102;
    const int WmSysKeyDown = 0x0104;
    const int WmSysKeyUp = 0x0105;
    const int MaNoActivate = 3;

    static readonly Guid GraphicsCaptureItemIid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");
    static readonly Guid TextureIid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
    static readonly Dictionary<IntPtr, ChromaPresenter> Windows = new();
    static readonly WndProcDelegate SharedProc = StaticProc;
    static bool _classRegistered;

    readonly object _gate = new();
    readonly SynchronizationContext? _ui = SynchronizationContext.Current;
    readonly Action<string> _log;
    readonly int _keyR = 1;
    readonly int _keyG = 0;
    readonly int _keyB = 1;

    IntPtr _hwnd;
    ID3D11Device? _device;
    ID3D11DeviceContext? _context;
    IDirect3DDevice? _winrtDevice;
    IDXGISwapChain1? _swapChain;
    IDCompositionDevice? _dcomp;
    IDCompositionTarget? _compTarget;
    IDCompositionVisual? _visual;
    ID2D1Factory1? _d2dFactory;
    ID2D1Device? _d2dDevice;
    ID2D1DeviceContext? _d2dContext;
    ID2D1Bitmap1? _target;
    ID2D1Effect? _effect;
    GraphicsCaptureItem? _item;
    Direct3D11CaptureFramePool? _pool;
    GraphicsCaptureSession? _session;
    int _width;
    int _height;
    int _pendingWidth;
    int _pendingHeight;
    int _cropX;
    int _cropY;
    int _cropWidth;
    int _cropHeight;
    int _cropWindowWidth;
    int _cropWindowHeight;
    bool _closed;
    bool _signaled;
    int _failed;

    public ChromaPresenter(PixelRect stage, Action<string> log)
    {
        _log = log;
        _pendingWidth = Math.Max(1, stage.Width);
        _pendingHeight = Math.Max(1, stage.Height);
        _hwnd = CreateWindow(stage);
        Windows[_hwnd] = this;
        CreateDevice();
    }

    public IntPtr Handle => _hwnd;

    /// <summary>Chromium top-level window that should receive pointer and keys.</summary>
    public IntPtr Source { get; set; }

    /// <summary>
    /// Page rectangle inside the captured window. Chromium keeps a frame above the
    /// render widget; scaling the whole window would show that frame and squash the page.
    /// </summary>
    public void SetPageCrop(int x, int y, int width, int height, int windowWidth, int windowHeight)
    {
        lock (_gate)
        {
            _cropX = x;
            _cropY = y;
            _cropWidth = width;
            _cropHeight = height;
            _cropWindowWidth = windowWidth;
            _cropWindowHeight = windowHeight;
        }
    }

    public readonly record struct CaptureCrop(float X, float Y, float Width, float Height);

    public static CaptureCrop MapPage(int pageX, int pageY, int pageWidth, int pageHeight, int windowWidth, int windowHeight, int captureWidth, int captureHeight)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || captureWidth <= 0 || captureHeight <= 0)
            return new CaptureCrop(0, 0, Math.Max(0, captureWidth), Math.Max(0, captureHeight));

        var scaleX = windowWidth > 0 ? captureWidth / (float)windowWidth : 1f;
        var scaleY = windowHeight > 0 ? captureHeight / (float)windowHeight : 1f;
        var x = Math.Clamp(pageX * scaleX, 0, captureWidth);
        var y = Math.Clamp(pageY * scaleY, 0, captureHeight);
        var width = Math.Min(pageWidth * scaleX, captureWidth - x);
        var height = Math.Min(pageHeight * scaleY, captureHeight - y);
        if (width < 1 || height < 1)
            return new CaptureCrop(0, 0, captureWidth, captureHeight);
        return new CaptureCrop(x, y, width, height);
    }

    public event Action? FrameReady;
    public event Action<string>? Failed;

    public void Start(IntPtr source)
    {
        Source = source;
        lock (_gate)
        {
            if (_closed)
                throw new ObjectDisposedException(nameof(ChromaPresenter));
            _item = CreateItem(source);
            var size = _item.Size;
            if (size.Width < 1 || size.Height < 1)
                size = new Windows.Graphics.SizeInt32(_pendingWidth, _pendingHeight);
            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _winrtDevice,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                size);
            _session = _pool.CreateCaptureSession(_item);
            _session.IsCursorCaptureEnabled = false;
            try
            {
                _session.IsBorderRequired = false;
            }
            catch (Exception)
            {
                // Older projections omit the border switch. The captured window is covered anyway.
            }

            _pool.FrameArrived += (_, _) => OnFrame();
            _session.StartCapture();
        }

        _log("chroma capture " + _pendingWidth + "x" + _pendingHeight);
    }

    public void Place(PixelRect stage)
    {
        var width = Math.Max(1, stage.Width);
        var height = Math.Max(1, stage.Height);
        _pendingWidth = width;
        _pendingHeight = height;
        if (_hwnd != IntPtr.Zero)
            SetWindowPos(_hwnd, NativeMethods.HwndTopMost, stage.X, stage.Y, width, height, 0x0010 | 0x0040);
    }

    public void Dispose()
    {
        IntPtr hwnd;
        lock (_gate)
        {
            if (_closed)
                return;
            _closed = true;
            hwnd = _hwnd;
            _hwnd = IntPtr.Zero;
            _session?.Dispose();
            _pool?.Dispose();
            _item = null;
            _effect?.Dispose();
            _target?.Dispose();
            _d2dContext?.Dispose();
            _d2dDevice?.Dispose();
            _d2dFactory?.Dispose();
            _visual?.Dispose();
            _compTarget?.Dispose();
            _dcomp?.Dispose();
            _swapChain?.Dispose();
            _context?.Dispose();
            _device?.Dispose();
            _winrtDevice?.Dispose();
            _session = null;
            _pool = null;
            _item = null;
        }

        if (hwnd != IntPtr.Zero)
        {
            Windows.Remove(hwnd);
            DestroyWindow(hwnd);
        }
    }

    void OnFrame()
    {
        if (_failed != 0)
            return;
        try
        {
            lock (_gate)
            {
                if (_closed || _pool is null || _d2dContext is null || _effect is null || _swapChain is null || _dcomp is null)
                    return;
                using var frame = _pool.TryGetNextFrame();
                if (frame is null)
                    return;
                EnsureTarget(_pendingWidth, _pendingHeight);
                Draw(frame);
                if (!_signaled)
                {
                    _signaled = true;
                    Post(() => FrameReady?.Invoke());
                }
            }
        }
        catch (Exception ex)
        {
            if (System.Threading.Interlocked.Exchange(ref _failed, 1) == 1)
                return;
            var reason = ex.GetType().Name + ": " + ex.Message;
            Post(() => Failed?.Invoke(reason));
        }
    }

    void Draw(Direct3D11CaptureFrame frame)
    {
        var access = frame.Surface.As<IDirect3DDxgiInterfaceAccess>();
        var iid = TextureIid;
        var texturePtr = access.GetInterface(ref iid);
        using var texture = new ID3D11Texture2D(texturePtr);
        using var surface = texture.QueryInterface<IDXGISurface>();
        using var source = _d2dContext!.CreateBitmapFromDxgiSurface(surface, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Ignore)));
        var content = frame.ContentSize;
        var crop = MapPage(_cropX, _cropY, _cropWidth, _cropHeight, _cropWindowWidth, _cropWindowHeight, content.Width, content.Height);
        var scaleX = crop.Width > 0 ? _width / crop.Width : 1f;
        var scaleY = crop.Height > 0 ? _height / crop.Height : 1f;
        _d2dContext.BeginDraw();
        _d2dContext.Transform = Matrix3x2.Identity;
        _d2dContext.Clear(new Color4(0, 0, 0, 0));
        _d2dContext.Transform = Matrix3x2.CreateScale(scaleX, scaleY);
        _effect!.SetInput(0, source, true);
        using var keyed = _effect.Output;
        _d2dContext.DrawImage(
            keyed,
            Vector2.Zero,
            new Vortice.RawRectF(crop.X, crop.Y, crop.X + crop.Width, crop.Y + crop.Height),
            Vortice.Direct2D1.InterpolationMode.Linear,
            Vortice.Direct2D1.CompositeMode.SourceOver);
        _d2dContext.EndDraw();
        _swapChain!.Present(1, PresentFlags.None);
        _dcomp!.Commit();
    }

    void EnsureTarget(int width, int height)
    {
        if (_target is not null && width == _width && height == _height)
            return;
        _d2dContext!.Target = null;
        _target?.Dispose();
        _target = null;
        _swapChain!.ResizeBuffers(0, (uint)width, (uint)height, Format.Unknown, SwapChainFlags.None);
        using var buffer = _swapChain.GetBuffer<IDXGISurface>(0);
        _target = _d2dContext.CreateBitmapFromDxgiSurface(buffer, new BitmapProperties1(
            new D2DPixelFormat(Format.B8G8R8A8_UNorm, D2DAlpha.Premultiplied),
            96, 96, BitmapOptions.Target | BitmapOptions.CannotDraw));
        _d2dContext.Target = _target;
        _width = width;
        _height = height;
    }

    void CreateDevice()
    {
        Vortice.Direct3D.FeatureLevel[] levels = [Vortice.Direct3D.FeatureLevel.Level_11_0];
        D3D11.D3D11CreateDevice(
            IntPtr.Zero,
            DriverType.Hardware,
            DeviceCreationFlags.BgraSupport,
            levels,
            out ID3D11Device device,
            out _,
            out ID3D11DeviceContext context).CheckError();
        _device = device;
        _context = context;
        using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
        var hr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out var inspectable);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        _winrtDevice = MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);

        using var factory = DXGI.CreateDXGIFactory2<IDXGIFactory2>(false);
        var desc = new SwapChainDescription1
        {
            Width = (uint)_pendingWidth,
            Height = (uint)_pendingHeight,
            Format = Format.B8G8R8A8_UNorm,
            SampleDescription = new SampleDescription(1, 0),
            BufferUsage = Usage.RenderTargetOutput,
            BufferCount = 2,
            SwapEffect = SwapEffect.FlipSequential,
            Scaling = Scaling.Stretch,
            AlphaMode = Vortice.DXGI.AlphaMode.Premultiplied
        };
        _swapChain = factory.CreateSwapChainForComposition(_device, desc, null);
        _dcomp = DComp.DCompositionCreateDevice<IDCompositionDevice>(dxgiDevice);
        _dcomp.CreateTargetForHwnd(_hwnd, true, out _compTarget!);
        _dcomp.CreateVisual(out _visual!);
        _visual.SetContent(_swapChain);
        _compTarget.SetRoot(_visual);
        _dcomp.Commit();

        _d2dFactory = D2D1.D2D1CreateFactory<ID2D1Factory1>(FactoryType.MultiThreaded);
        _d2dDevice = _d2dFactory.CreateDevice(dxgiDevice);
        _d2dContext = _d2dDevice.CreateDeviceContext(DeviceContextOptions.None);
        _d2dContext.UnitMode = UnitMode.Pixels;
        _effect = new ID2D1Effect(_d2dContext.CreateEffect(EffectGuids.ChromaKey));
        SetChroma(_effect);
        EnsureTarget(_pendingWidth, _pendingHeight);
        _d2dContext.BeginDraw();
        _d2dContext.Clear(new Color4(0, 0, 0, 0));
        _d2dContext.EndDraw();
        _swapChain.Present(1, PresentFlags.None);
        _dcomp.Commit();
    }

    unsafe void SetChroma(ID2D1Effect effect)
    {
        var color = new Vector3(_keyR / 255f, _keyG / 255f, _keyB / 255f);
        var tolerance = 1f / 255f;
        var off = 0;
        effect.SetValue(0, PropertyType.Vector3, &color, (uint)sizeof(Vector3));
        effect.SetValue(1, PropertyType.Float, &tolerance, sizeof(float));
        effect.SetValue(2, PropertyType.Bool, &off, sizeof(int));
        effect.SetValue(3, PropertyType.Bool, &off, sizeof(int));
    }

    void Post(Action action)
    {
        if (_ui is null)
            action();
        else
            _ui.Post(_ => action(), null);
    }

    static GraphicsCaptureItem CreateItem(IntPtr hwnd)
    {
        var factory = ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");
        var unknown = ((IObjectReference)factory).ThisPtr;
        var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
        var hr = Marshal.QueryInterface(unknown, ref interopId, out var interopPtr);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(interopPtr);
        Marshal.Release(interopPtr);
        var itemId = GraphicsCaptureItemIid;
        hr = interop.CreateForWindow(hwnd, ref itemId, out var pointer);
        if (hr < 0)
            Marshal.ThrowExceptionForHR(hr);
        return MarshalInterface<GraphicsCaptureItem>.FromAbi(pointer);
    }

    IntPtr CreateWindow(PixelRect stage)
    {
        EnsureClass();
        var hwnd = CreateWindowEx(
            WsExNoRedirectionBitmap | WsExTopmost | WsExToolWindow | WsExNoActivate,
            "TvEmulatorChromaPresenter",
            "TV Emulator chroma",
            WsPopup | WsVisible,
            stage.X,
            stage.Y,
            Math.Max(1, stage.Width),
            Math.Max(1, stage.Height),
            IntPtr.Zero,
            IntPtr.Zero,
            GetModuleHandle(null),
            IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Nie udało się otworzyć okna prezentera. " + Marshal.GetLastWin32Error());
        return hwnd;
    }

    static void EnsureClass()
    {
        if (_classRegistered)
            return;
        var wc = new WndClass
        {
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(SharedProc),
            hInstance = GetModuleHandle(null),
            lpszClassName = "TvEmulatorChromaPresenter",
            hCursor = LoadCursor(IntPtr.Zero, new IntPtr(32512))
        };
        if (RegisterClass(ref wc) == 0 && Marshal.GetLastWin32Error() != 1410)
            throw new InvalidOperationException("Nie udało się zarejestrować okna prezentera. " + Marshal.GetLastWin32Error());
        _classRegistered = true;
    }

    static IntPtr StaticProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (Windows.TryGetValue(hwnd, out var presenter))
            return presenter.OnMessage(msg, wParam, lParam);
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    IntPtr OnMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == WmMouseActivate)
            return new IntPtr(MaNoActivate);
        if (msg is WmMouseMove or WmLButtonDown or WmLButtonUp or WmRButtonDown or WmRButtonUp or WmMButtonDown or WmMButtonUp or WmMouseWheel)
        {
            ForwardPointer((int)msg, wParam, lParam);
            return IntPtr.Zero;
        }

        if (msg is WmKeyDown or WmKeyUp or WmChar or WmSysKeyDown or WmSysKeyUp)
        {
            var source = Source;
            if (source != IntPtr.Zero)
                NativeMethods.PostMessage(source, (int)msg, wParam, lParam);
            return IntPtr.Zero;
        }

        return DefWindowProc(_hwnd, msg, wParam, lParam);
    }

    void ForwardPointer(int msg, IntPtr wParam, IntPtr lParam)
    {
        var source = Source;
        if (source == IntPtr.Zero)
            return;
        var widget = NativeMethods.FindRenderWidget(source);
        var target = widget != IntPtr.Zero ? widget : source;
        var packed = lParam.ToInt64();
        var point = new NativeMethods.WinPoint
        {
            X = (short)(packed & 0xFFFF),
            Y = (short)((packed >> 16) & 0xFFFF)
        };
        NativeMethods.ClientToScreen(_hwnd, ref point);
        NativeMethods.ScreenToClient(target, ref point);
        var translated = (IntPtr)(((point.Y & 0xFFFF) << 16) | (point.X & 0xFFFF));
        if (msg is WmLButtonDown or WmRButtonDown or WmMButtonDown)
            _ = NativeMethods.SetForegroundWindow(source);
        NativeMethods.PostMessage(target, msg, wParam, translated);
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid iid, out IntPtr result);

        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid iid, out IntPtr result);
    }

    [ComImport]
    [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IDirect3DDxgiInterfaceAccess
    {
        IntPtr GetInterface(ref Guid iid);
    }

    delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WndClass
    {
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
    }

    [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern ushort RegisterClass(ref WndClass wc);

    [DllImport("user32.dll")]
    static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandle(string? module);

    [DllImport("user32.dll")]
    static extern IntPtr LoadCursor(IntPtr instance, IntPtr cursor);
}
