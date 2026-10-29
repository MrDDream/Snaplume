using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Snaplume.Native.NativeMethods;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace Snaplume.Capturing;

/// <summary>
/// GDI copy of an area of the screen into a 32-bit bitmap kept for the next copies (a scrolling
/// capture grabs the same area hundreds of times). Not thread-safe: one copy at a time.
/// </summary>
internal sealed class ScreenGrabber : IDisposable
{
    private readonly Gdi.Bitmap _bitmap;

    /// <param name="area">Absolute physical screen coordinates.</param>
    public ScreenGrabber(PixelRect area)
    {
        Area = area;
        _bitmap = new Gdi.Bitmap(area.Width, area.Height, GdiImaging.PixelFormat.Format32bppRgb);
    }

    public PixelRect Area { get; }

    /// <summary>Copies the screen into the bitmap, with the mouse cursor drawn on it if asked.</summary>
    public void Grab(bool includeCursor = false)
    {
        using var g = Gdi.Graphics.FromImage(_bitmap);
        g.CopyFromScreen(Area.X, Area.Y, 0, 0, new Gdi.Size(Area.Width, Area.Height), Gdi.CopyPixelOperation.SourceCopy);
        if (includeCursor) DrawCursor(g, Area.X, Area.Y);
    }

    /// <summary>Copies the last grab as BGRX pixels (stride = width * 4) into <paramref name="target"/>.</summary>
    public void CopyTo(byte[] target)
    {
        int stride = Area.Width * 4;
        if (target.Length < (long)stride * Area.Height) throw new ArgumentException("Buffer too small", nameof(target));
        var data = Lock();
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, target, 0, stride * Area.Height);
                return;
            }
            for (int y = 0; y < Area.Height; y++)
                Marshal.Copy(data.Scan0 + y * data.Stride, target, y * stride, stride);
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    /// <summary>The last grab as a frozen Bgr32 image, 96 DPI (1 image pixel = 1 physical pixel).</summary>
    public BitmapSource ToBitmapSource()
    {
        var data = Lock();
        try
        {
            var source = BitmapSource.Create(Area.Width, Area.Height, 96, 96, PixelFormats.Bgr32, null,
                data.Scan0, data.Stride * Area.Height, data.Stride);
            source.Freeze();
            return source;
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
    }

    public void Dispose() => _bitmap.Dispose();

    private GdiImaging.BitmapData Lock() =>
        _bitmap.LockBits(new Gdi.Rectangle(0, 0, Area.Width, Area.Height), GdiImaging.ImageLockMode.ReadOnly, GdiImaging.PixelFormat.Format32bppRgb);

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
