using System.Runtime.InteropServices;
using Snaplume.Capturing;
using Snaplume.Imaging;
using static Snaplume.Platform.Windows.Native.NativeMethods;
using Gdi = System.Drawing;
using GdiImaging = System.Drawing.Imaging;

namespace Snaplume.Platform.Windows.Capturing;

/// <summary>Monitors and GDI copies of the screen.</summary>
/// <remarks>
/// Coordinates are physical pixels because the application is per-monitor DPI aware (app.manifest): the
/// system then reports every monitor at its real size, whatever its scale.
/// </remarks>
public sealed class ScreenCapture : IScreenCapture
{
    public IReadOnlyList<MonitorInfo> Monitors => EnumerateMonitors();

    public Task<DesktopImage> CaptureDesktopAsync(bool includeCursor, CancellationToken cancel = default)
    {
        // The copy itself takes tens of milliseconds for big desktops: off the UI thread.
        return Task.Run(() =>
        {
            cancel.ThrowIfCancellationRequested();
            var bounds = VirtualScreen();
            var monitors = EnumerateMonitors();
            (int X, int Y)? cursor = GetCursorPos(out var p) ? (p.X, p.Y) : null;
            using var grabber = new GdiScreenGrabber(bounds);
            var image = new PixelBuffer(bounds.Width, bounds.Height);
            grabber.Grab(includeCursor);
            grabber.CopyTo(image.Pixels);
            return new DesktopImage(image, bounds, monitors, cursor);
        }, cancel);
    }

    public IScreenGrabber CreateGrabber(PixelRect area) => new GdiScreenGrabber(area);

    /// <summary>
    /// Every monitor with its scale. EnumDisplayMonitors rather than WinForms' Screen: Screen caches its list
    /// until a WM_DISPLAYCHANGE reaches a WinForms window, which may never happen under Avalonia.
    /// </summary>
    internal static IReadOnlyList<MonitorInfo> EnumerateMonitors()
    {
        var list = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr _, ref RECT _, IntPtr _) =>
        {
            var info = new MONITORINFOEX { cbSize = Marshal.SizeOf<MONITORINFOEX>(), szDevice = "" };
            if (!GetMonitorInfo(monitor, ref info)) return true;
            double scale = 1;
            try
            {
                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0) scale = dpiX / 96.0;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                // Before Windows 8.1: 96 DPI is assumed.
            }
            var r = info.rcMonitor;
            list.Add(new MonitorInfo(PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom), scale,
                (info.dwFlags & MONITORINFOF_PRIMARY) != 0, info.szDevice ?? ""));
            return true;
        }, IntPtr.Zero);
        if (list.Count == 0) list.Add(new MonitorInfo(VirtualScreen(), 1, true, ""));
        return list;
    }
}

/// <summary>
/// GDI copy of an area of the screen into a 32-bit bitmap kept for the next copies (a scrolling
/// capture grabs the same area hundreds of times). Not thread-safe: one copy at a time.
/// </summary>
internal sealed class GdiScreenGrabber : IScreenGrabber
{
    private readonly Gdi.Bitmap _bitmap;

    /// <param name="area">Absolute physical screen coordinates.</param>
    public GdiScreenGrabber(PixelRect area)
    {
        if (area.IsEmpty) throw new ArgumentException("Empty area", nameof(area));
        Area = area;
        _bitmap = new Gdi.Bitmap(area.Width, area.Height, GdiImaging.PixelFormat.Format32bppRgb);
    }

    public PixelRect Area { get; }

    /// <summary>
    /// Synchronous: a GDI copy of the area is quick, and the scrolling capture's timings were tuned with copies made
    /// on the calling thread.
    /// </summary>
    public Task GrabAsync(byte[] target, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        Grab(includeCursor: false);
        CopyTo(target);
        return Task.CompletedTask;
    }

    /// <summary>Copies the screen into the bitmap, with the mouse cursor drawn on it if asked.</summary>
    public void Grab(bool includeCursor)
    {
        using var g = Gdi.Graphics.FromImage(_bitmap);
        g.CopyFromScreen(Area.X, Area.Y, 0, 0, new Gdi.Size(Area.Width, Area.Height), Gdi.CopyPixelOperation.SourceCopy);
        if (includeCursor) DrawCursor(g, Area.X, Area.Y);
    }

    /// <summary>Copies the last grab as opaque BGRA pixels (stride = width * 4) into <paramref name="target"/>.</summary>
    public void CopyTo(byte[] target)
    {
        int stride = Area.Width * 4;
        if (target.Length < (long)stride * Area.Height) throw new ArgumentException("Buffer too small", nameof(target));
        var data = _bitmap.LockBits(new Gdi.Rectangle(0, 0, Area.Width, Area.Height), GdiImaging.ImageLockMode.ReadOnly,
            GdiImaging.PixelFormat.Format32bppRgb);
        try
        {
            if (data.Stride == stride)
            {
                Marshal.Copy(data.Scan0, target, 0, stride * Area.Height);
            }
            else
            {
                for (int y = 0; y < Area.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, target, y * stride, stride);
            }
        }
        finally
        {
            _bitmap.UnlockBits(data);
        }
        // The 4th byte of a screen copy is undefined (BGRX).
        foreach (ref uint pixel in MemoryMarshal.Cast<byte, uint>(target.AsSpan(0, stride * Area.Height)))
            pixel |= 0xFF000000;
    }

    public void Dispose() => _bitmap.Dispose();

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
