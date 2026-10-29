using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Snaplume.Native.NativeMethods;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;
using WinForms = System.Windows.Forms;

namespace Snaplume.Capturing;

/// <summary>Frozen image of the whole virtual screen, with the window layout at that moment.</summary>
public sealed class ScreenSnapshot
{
    /// <summary>Image of the virtual screen, 1 image pixel = 1 physical pixel.</summary>
    public required BitmapSource Image { get; init; }

    /// <summary>Virtual screen bounds, absolute physical coordinates.</summary>
    public required PixelRect Bounds { get; init; }

    /// <summary>Monitor bounds, relative to <see cref="Bounds"/>.</summary>
    public required IReadOnlyList<PixelRect> Monitors { get; init; }

    private Lazy<WindowFinder> _windows = null!;

    /// <summary>
    /// Windows and controls under the mouse, listed on first use: only the interactive selection
    /// needs them, and it reads them before showing itself (see <see cref="RegionSelectorWindow"/>).
    /// </summary>
    public WindowFinder Windows => _windows.Value;

    public static ScreenSnapshot Take(bool includeCursor)
    {
        var bounds = new PixelRect(
            GetSystemMetrics(SM_XVIRTUALSCREEN), GetSystemMetrics(SM_YVIRTUALSCREEN),
            GetSystemMetrics(SM_CXVIRTUALSCREEN), GetSystemMetrics(SM_CYVIRTUALSCREEN));

        var monitors = WinForms.Screen.AllScreens
            .Select(s => PixelRect.From(s.Bounds).Offset(-bounds.X, -bounds.Y))
            .ToList();

        var image = CaptureScreen(bounds, includeCursor);
        return new ScreenSnapshot
        {
            Image = image,
            Bounds = bounds,
            Monitors = monitors,
            _windows = new Lazy<WindowFinder>(() => WindowFinder.Create(bounds, monitors)),
        };
    }

    /// <summary>
    /// Monitor containing the point (relative coordinates). In the dead zones between monitors
    /// of different sizes, the nearest one.
    /// </summary>
    public PixelRect MonitorAt(int x, int y)
    {
        if (Monitors.Count == 0) return new PixelRect(0, 0, Bounds.Width, Bounds.Height);
        var best = Monitors[0];
        long bestDistance = long.MaxValue;
        foreach (var monitor in Monitors)
        {
            long distance = monitor.DistanceSquared(x, y);
            if (distance == 0) return monitor;
            if (distance < bestDistance)
            {
                best = monitor;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>Monitor under the mouse cursor (relative coordinates).</summary>
    public PixelRect MonitorUnderCursor()
    {
        var (x, y) = CursorPosition();
        return MonitorAt(x, y);
    }

    public (int X, int Y) CursorPosition()
    {
        GetCursorPos(out var p);
        return (p.X - Bounds.X, p.Y - Bounds.Y);
    }

    private static BitmapSource CaptureScreen(PixelRect bounds, bool includeCursor)
    {
        using var bmp = new Gdi.Bitmap(bounds.Width, bounds.Height, GdiImaging.PixelFormat.Format32bppRgb);
        using (var g = Gdi.Graphics.FromImage(bmp))
        {
            g.CopyFromScreen(bounds.X, bounds.Y, 0, 0, new Gdi.Size(bounds.Width, bounds.Height), Gdi.CopyPixelOperation.SourceCopy);
            if (includeCursor) DrawCursor(g, bounds.X, bounds.Y);
        }

        var data = bmp.LockBits(new Gdi.Rectangle(0, 0, bmp.Width, bmp.Height), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppRgb);
        try
        {
            var source = BitmapSource.Create(bmp.Width, bmp.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * bmp.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    private static void DrawCursor(Gdi.Graphics g, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0) return;
        if (!GetIconInfo(info.hCursor, out var icon)) return;
        try
        {
            var hdc = g.GetHdc();
            try
            {
                DrawIconEx(hdc, info.ptScreenPos.X - icon.xHotspot - originX, info.ptScreenPos.Y - icon.yHotspot - originY,
                    info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }
}
