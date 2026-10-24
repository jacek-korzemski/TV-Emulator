using System.Runtime.InteropServices;

namespace TvEmulator.Services;

/// <summary>
/// Electron will not make a window taller than the work area, so a 16:9 stage that
/// covers a 1080p monitor is clipped by the taskbar. Autohide frees that strip for
/// the session and is put back afterwards.
/// </summary>
public static class Taskbar
{
    private const uint AbmGetState = 4;
    private const uint AbmSetState = 10;
    private const int AbsAutohide = 1;
    private const int AbsAlwaysOnTop = 2;

    private static int _previous;
    private static bool _occupied;

    public static void Occupy()
    {
        if (_occupied)
            return;
        _previous = GetState();
        _occupied = true;
        SetState(AbsAutohide);
    }

    public static void Restore()
    {
        if (!_occupied)
            return;
        _occupied = false;
        SetState(_previous == 0 ? AbsAlwaysOnTop : _previous);
    }

    private static int GetState()
    {
        var data = NewData();
        return (int)SHAppBarMessage(AbmGetState, ref data);
    }

    private static void SetState(int state)
    {
        var data = NewData();
        data.lParam = state;
        _ = SHAppBarMessage(AbmSetState, ref data);
    }

    private static AppBarData NewData() => new()
    {
        cbSize = Marshal.SizeOf<AppBarData>(),
        hWnd = FindWindow("Shell_TrayWnd", null)
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public Rect rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern uint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindow(string className, string? windowName);
}
