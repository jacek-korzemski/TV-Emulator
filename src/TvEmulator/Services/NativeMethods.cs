using System.Runtime.InteropServices;
using System.Text;

namespace TvEmulator.Services;

public static class NativeMethods
{
    public const int GwlStyle = -16;
    public const int GwlExStyle = -20;
    public const int GwlpHwndParent = -8;
    public const int WsExTransparent = 0x00000020;
    public const int WsExToolWindow = 0x00000080;
    public const int WsExLayered = 0x00080000;
    public const int WsExNoActivate = 0x08000000;
    public const uint LwaColorKey = 0x00000001;
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpHideWindow = 0x0080;
    public const uint RdwInvalidate = 0x0001;
    public const uint RdwAllChildren = 0x0080;
    public const uint RdwUpdateNow = 0x0100;
    public const uint RdwFrame = 0x0400;
    public const int WsCaption = 0x00C00000;
    public const int WsThickFrame = 0x00040000;
    public const int WsMinimizeBox = 0x00020000;
    public const int WsMaximizeBox = 0x00010000;
    public const int WsSysMenu = 0x00080000;
    public const int WsPopup = unchecked((int)0x80000000);
    public const uint SwpFrameChanged = 0x0020;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpNoZOrder = 0x0004;
    public const int WmActivate = 0x0006;
    public const int WmClose = 0x0010;
    public const int WmHotkey = 0x0312;
    public static readonly IntPtr HwndTopMost = new(-1);
    public const uint ModControl = 0x0002;
    public const uint ModShift = 0x0004;
    public const uint ModNoRepeat = 0x4000;
    public const uint VkQ = 0x51;
    public const int HotkeyId = 0x5476;

    private const uint Th32CsSnapProcess = 0x00000002;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    public static void MakeBorderless(IntPtr hwnd)
    {
        var style = GetStyle(hwnd);
        style &= ~(WsCaption | WsThickFrame | WsMinimizeBox | WsMaximizeBox | WsSysMenu);
        style |= WsPopup;
        SetStyle(hwnd, style);
    }

    public static bool HasCaption(IntPtr hwnd) => (GetStyle(hwnd) & WsCaption) != 0;

    public static void SetOwner(IntPtr hwnd, IntPtr owner) => SetLong(hwnd, GwlpHwndParent, owner);

    public static void Place(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpFrameChanged | SwpShowWindow | SwpNoZOrder);
    }

    public static void PlaceOnTop(IntPtr hwnd, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, HwndTopMost, x, y, width, height, SwpFrameChanged | SwpShowWindow);
    }

    /// <summary>
    /// Bring a window to the top without resizing it. Resizing an Electron transparent
    /// window with SWP_FRAMECHANGED drops its per-pixel alpha and the page turns black.
    /// </summary>
    public static void RaiseTopMost(IntPtr hwnd)
    {
        SetWindowPos(hwnd, HwndTopMost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    public static void Raise(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate | SwpShowWindow);
    }

    /// <summary>
    /// Electron's compositor child is a layered window. Its transparent pixels then
    /// show white instead of the video plane. The plane stays visible once that
    /// layered bit is gone; the page keeps its own alpha.
    /// </summary>
    public static void UnlayerCompositor(IntPtr hwnd)
    {
        void Visit(IntPtr parent)
        {
            EnumChildWindows(parent, (child, _) =>
            {
                var className = new StringBuilder(80);
                _ = GetClassName(child, className, className.Capacity);
                if (className.ToString().Contains("D3D", StringComparison.Ordinal))
                {
                    var style = GetExStyle(child);
                    if ((style & WsExLayered) != 0)
                        SetExStyle(child, style & ~WsExLayered);
                }
                Visit(child);
                return true;
            }, IntPtr.Zero);
        }

        Visit(hwnd);
    }

    public static void PlaceBelow(IntPtr hwnd, IntPtr above, int x, int y, int width, int height)
    {
        SetWindowPos(hwnd, above, x, y, width, height, SwpShowWindow | SwpNoActivate);
    }

    public static void Hide(IntPtr hwnd)
    {
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0, SwpHideWindow | SwpNoActivate | SwpNoZOrder | SwpNoSize | SwpNoMove);
    }

    /// <summary>
    /// Pixels of exactly <paramref name="rgb"/> become holes that show the window below.
    /// Chromium keeps painting through GDI here because every engine runs with --disable-gpu.
    /// </summary>
    public static void ApplyColorKey(IntPtr hwnd, int rgb)
    {
        SetExStyle(hwnd, GetExStyle(hwnd) | WsExLayered);
        var colorRef = (uint)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        _ = SetLayeredWindowAttributes(hwnd, colorRef, 0, LwaColorKey);
        Redraw(hwnd);
    }

    public static bool HasColorKey(IntPtr hwnd)
    {
        if ((GetExStyle(hwnd) & WsExLayered) == 0)
            return false;
        return GetLayeredWindowAttributes(hwnd, out _, out _, out var flags) && (flags & LwaColorKey) != 0;
    }

    public static void Redraw(IntPtr hwnd)
    {
        _ = RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwUpdateNow | RdwFrame);
    }

    public static PixelRect? WindowBounds(IntPtr hwnd)
    {
        if (!GetWindowRect(hwnd, out var rect))
            return null;
        return new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
    }

    /// <summary>Screen bounds of the page area: Chromium's render widget child window.</summary>
    public static PixelRect? ContentBounds(IntPtr hwnd)
    {
        PixelRect? found = null;
        EnumChildWindows(hwnd, (child, _) =>
        {
            var className = new StringBuilder(64);
            _ = GetClassName(child, className, className.Capacity);
            if (className.ToString() != "Chrome_RenderWidgetHostHWND" || !GetWindowRect(child, out var rect))
                return true;
            var bounds = new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
            if (bounds.Width < 50 || bounds.Height < 50)
                return true;
            if (found is not { } best || bounds.Width * bounds.Height > best.Width * best.Height)
                found = bounds;
            return true;
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>Limits what is drawn of the window to <paramref name="visible"/> (window coordinates); null shows all of it.</summary>
    public static void ClipTo(IntPtr hwnd, PixelRect? visible)
    {
        if (visible is not { } rect)
        {
            _ = SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        var region = CreateRectRgn(rect.X, rect.Y, rect.X + rect.Width, rect.Y + rect.Height);
        if (SetWindowRgn(hwnd, region, true) == 0)
            _ = DeleteObject(region);
    }

    public static void MakePassive(IntPtr hwnd)
    {
        // Layered or click-through styles make PlayReady present a blank (white) frame.
        // The Chromium window above this one already owns the color key and the clicks.
        var style = GetExStyle(hwnd);
        style |= WsExNoActivate | WsExToolWindow;
        style &= ~WsExLayered;
        style &= ~WsExTransparent;
        SetExStyle(hwnd, style);
    }

    public static IntPtr FindChromeWindow(IReadOnlyCollection<int> processIds)
    {
        IntPtr found = IntPtr.Zero;
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
                return true;
            GetWindowThreadProcessId(hwnd, out var pid);
            if (!processIds.Contains((int)pid))
                return true;

            var className = new StringBuilder(256);
            _ = GetClassName(hwnd, className, className.Capacity);
            var cls = className.ToString();
            if (!cls.StartsWith("Chrome_WidgetWin", StringComparison.Ordinal)
                && !cls.StartsWith("MozillaWindowClass", StringComparison.Ordinal))
                return true;

            if (!GetWindowRect(hwnd, out var rect))
                return true;
            if (rect.Right - rect.Left < 200 || rect.Bottom - rect.Top < 150)
                return true;

            found = hwnd;
            return false;
        }, IntPtr.Zero);
        return found;
    }

    public static List<int> ProcessTree(int rootPid)
    {
        var rows = new List<(int Pid, int Parent)>();
        var snapshot = CreateToolhelp32Snapshot(Th32CsSnapProcess, 0);
        if (snapshot == IntPtr.Zero || snapshot == new IntPtr(-1))
            return [rootPid];

        try
        {
            var entry = new ProcessEntry32 { dwSize = (uint)Marshal.SizeOf<ProcessEntry32>() };
            if (Process32First(snapshot, ref entry))
            {
                do
                {
                    rows.Add(((int)entry.th32ProcessID, (int)entry.th32ParentProcessID));
                    entry.dwSize = (uint)Marshal.SizeOf<ProcessEntry32>();
                }
                while (Process32Next(snapshot, ref entry));
            }
        }
        finally
        {
            _ = CloseHandle(snapshot);
        }

        var result = new List<int> { rootPid };
        var grew = true;
        while (grew)
        {
            grew = false;
            foreach (var row in rows)
            {
                if (result.Contains(row.Parent) && !result.Contains(row.Pid))
                {
                    result.Add(row.Pid);
                    grew = true;
                }
            }
        }

        return result;
    }

    public static void HideCursor()
    {
        while (ShowCursor(false) >= 0)
        {
        }
    }

    public static void ShowCursorAgain()
    {
        while (ShowCursor(true) < 0)
        {
        }
    }

    private static int GetStyle(IntPtr hwnd)
    {
        if (IntPtr.Size == 8)
            return unchecked((int)GetWindowLongPtr(hwnd, GwlStyle).ToInt64());
        return GetWindowLong32(hwnd, GwlStyle);
    }

    private static void SetStyle(IntPtr hwnd, int style) => SetLong(hwnd, GwlStyle, new IntPtr(style));

    private static int GetExStyle(IntPtr hwnd)
    {
        if (IntPtr.Size == 8)
            return unchecked((int)GetWindowLongPtr(hwnd, GwlExStyle).ToInt64());
        return GetWindowLong32(hwnd, GwlExStyle);
    }

    private static void SetExStyle(IntPtr hwnd, int style) => SetLong(hwnd, GwlExStyle, new IntPtr(style));

    private static void SetLong(IntPtr hwnd, int index, IntPtr value)
    {
        if (IntPtr.Size == 8)
            _ = SetWindowLongPtr(hwnd, index, value);
        else
            _ = SetWindowLong32(hwnd, index, value.ToInt32());
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out Rect lpRect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
    private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
    private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetLayeredWindowAttributes(IntPtr hwnd, out uint crKey, out byte bAlpha, out uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int ShowCursor(bool bShow);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32First(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref ProcessEntry32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);
}
