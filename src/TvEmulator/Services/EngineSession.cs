using System.Diagnostics;

namespace TvEmulator.Services;

public sealed class EngineSession : IDisposable
{
    private Process? _process;
    private bool _stopRequested;
    private int _redraws;
    private int _fits;

    private const int MaxFits = 6;

    public IntPtr WindowHandle { get; private set; }
    public bool HostManagedWindow { get; set; }
    public int? ColorKey { get; set; }

    /// <summary>The page area covers the stage exactly, or fitting gave up.</summary>
    public bool Fitted { get; private set; }
    public event Action? Exited;

    public bool HasExited
    {
        get
        {
            var process = _process;
            if (process is null)
                return false;
            try
            {
                return process.HasExited;
            }
            catch (InvalidOperationException)
            {
                return true;
            }
        }
    }

    public void Start(string exe, IReadOnlyList<string> arguments)
    {
        var start = new ProcessStartInfo(exe)
        {
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
            UseShellExecute = false
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        _process = Process.Start(start) ?? throw new InvalidOperationException("Nie udało się uruchomić Chromium.");
        _process.EnableRaisingEvents = true;
        _process.Exited += (_, _) =>
        {
            if (!_stopRequested)
                Exited?.Invoke();
        };
    }

    public bool TryAttach(PixelRect rect)
    {
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero)
            return false;

        WindowHandle = hwnd;
        if (!HostManagedWindow)
        {
            NativeMethods.Raise(hwnd);
            NativeMethods.UnlayerCompositor(hwnd);
            Fitted = true;
            _ = NativeMethods.SetForegroundWindow(hwnd);
            return true;
        }

        NativeMethods.MakeBorderless(hwnd);
        NativeMethods.PlaceOnTop(hwnd, rect.X, rect.Y, rect.Width, rect.Height);
        ApplyColorKey(hwnd);
        _fits = 0;
        Fit(hwnd, rect);
        _ = NativeMethods.SetForegroundWindow(hwnd);
        return true;
    }

    public void Maintain(PixelRect rect)
    {
        var hwnd = FindWindow();
        if (hwnd == IntPtr.Zero)
            return;
        if (!HostManagedWindow)
        {
            WindowHandle = hwnd;
            NativeMethods.Raise(hwnd);
            NativeMethods.UnlayerCompositor(hwnd);
            Fitted = true;
            return;
        }

        var compositionReady = ColorKey is null || NativeMethods.HasColorKey(hwnd);
        if (hwnd == WindowHandle && !NativeMethods.HasCaption(hwnd) && compositionReady)
        {
            if (_redraws > 0)
            {
                _redraws--;
                NativeMethods.Redraw(hwnd);
            }

            Fit(hwnd, rect);
            return;
        }

        if (hwnd != WindowHandle)
            _fits = 0;
        WindowHandle = hwnd;
        NativeMethods.MakeBorderless(hwnd);
        NativeMethods.PlaceOnTop(hwnd, rect.X, rect.Y, rect.Width, rect.Height);
        ApplyColorKey(hwnd);
        Fit(hwnd, rect);
    }

    private void Fit(IntPtr hwnd, PixelRect stage)
    {
        // Electron's transparent window reports a smaller content box than the bounds
        // we set (Windows keeps a frame). The shell compensates. Resizing here fights it.
        if (!HostManagedWindow)
        {
            Fitted = true;
            return;
        }
        var window = NativeMethods.WindowBounds(hwnd);
        var content = NativeMethods.ContentBounds(hwnd);
        if (window is not { } outer || content is not { } page || page == stage)
        {
            Fitted = true;
            return;
        }

        if (_fits >= MaxFits)
        {
            Fitted = true;
            return;
        }

        if (page.X < outer.X || page.Y < outer.Y
            || page.X + page.Width > outer.X + outer.Width
            || page.Y + page.Height > outer.Y + outer.Height)
        {
            Fitted = false;
            return;
        }

        _fits++;
        Fitted = false;
        var target = ViewportFit.AroundContent(stage, outer, page);
        NativeMethods.PlaceOnTop(hwnd, target.X, target.Y, target.Width, target.Height);
        NativeMethods.ClipTo(hwnd, target == stage
            ? null
            : new PixelRect(stage.X - target.X, stage.Y - target.Y, stage.Width, stage.Height));
    }

    private void ApplyColorKey(IntPtr hwnd)
    {
        if (ColorKey is not int key)
            return;
        NativeMethods.ApplyColorKey(hwnd, key);
        _redraws = 3;
    }

    private IntPtr FindWindow()
    {
        var process = _process;
        if (process is null)
            return IntPtr.Zero;

        var hwnd = NativeMethods.FindChromeWindow(NativeMethods.ProcessTree(process.Id));
        if (hwnd != IntPtr.Zero)
            return hwnd;

        process.Refresh();
        return process.MainWindowHandle;
    }

    public void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null)
            return;

        _stopRequested = true;
        try
        {
            if (!process.HasExited && WindowHandle != IntPtr.Zero)
            {
                _ = NativeMethods.PostMessage(WindowHandle, NativeMethods.WmClose, IntPtr.Zero, IntPtr.Zero);
                process.WaitForExit(3000);
            }

            if (!process.HasExited)
                process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        process.Dispose();
    }

    public void Dispose() => Stop();
}
