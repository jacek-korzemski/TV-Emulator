using System.Drawing;

namespace TvEmulator.Services;

public readonly record struct PixelRect(int X, int Y, int Width, int Height);

public static class ViewportFit
{
    public static PixelRect Fit16By9(Rectangle monitor, int logicalWidth, int logicalHeight)
    {
        if (logicalWidth <= 0)
            logicalWidth = 1920;
        if (logicalHeight <= 0)
            logicalHeight = 1080;

        var target = logicalWidth / (double)logicalHeight;
        var monitorWidth = (double)monitor.Width;
        var monitorHeight = (double)monitor.Height;
        double width;
        double height;
        if (monitorHeight <= 0 || monitorWidth / monitorHeight > target)
        {
            height = monitorHeight;
            width = height * target;
        }
        else
        {
            width = monitorWidth;
            height = width / target;
        }

        var pixelWidth = Math.Max(1, (int)Math.Round(width));
        var pixelHeight = Math.Max(1, (int)Math.Round(height));
        var x = monitor.X + (monitor.Width - pixelWidth) / 2;
        var y = monitor.Y + (monitor.Height - pixelHeight) / 2;
        return new PixelRect(x, y, pixelWidth, pixelHeight);
    }

    /// <summary>
    /// Window bounds that put the page area exactly on <paramref name="stage"/>, given where the
    /// page area currently sits inside the window. Chromium 85 draws its own frame inside a
    /// popup window, so the window has to be larger than the stage by that frame.
    /// </summary>
    public static PixelRect AroundContent(PixelRect stage, PixelRect window, PixelRect content)
    {
        var left = content.X - window.X;
        var top = content.Y - window.Y;
        var right = window.X + window.Width - (content.X + content.Width);
        var bottom = window.Y + window.Height - (content.Y + content.Height);
        return new PixelRect(stage.X - left, stage.Y - top, stage.Width + left + right, stage.Height + top + bottom);
    }
}
