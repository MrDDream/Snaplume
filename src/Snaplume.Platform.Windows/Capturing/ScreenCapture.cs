using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaplume.Capturing;
using Snaplume.Imaging;
using static Snaplume.Platform.Windows.Native.NativeMethods;

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
    /// Every monitor with its scale. EnumDisplayMonitors rather than a cached list of screens: the monitors may change
    /// at any time.
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
/// GDI copy of an area of the screen into a 32-bit DIB section kept for the next copies (a scrolling
/// capture grabs the same area hundreds of times). Not thread-safe: one copy at a time.
/// </summary>
internal sealed class GdiScreenGrabber : IScreenGrabber
{
    private readonly IntPtr _memoryDc;
    private readonly IntPtr _bitmap;
    private readonly IntPtr _previousBitmap;
    /// <summary>The pixels of the DIB section: top-down BGRX rows of exactly width * 4 bytes.</summary>
    private readonly IntPtr _bits;
    private bool _disposed;

    /// <param name="area">Absolute physical screen coordinates.</param>
    /// <exception cref="Win32Exception">GDI could not create the bitmap (e.g. out of memory).</exception>
    public GdiScreenGrabber(PixelRect area)
    {
        if (area.IsEmpty) throw new ArgumentException("Empty area", nameof(area));
        Area = area;
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(), biWidth = area.Width, biHeight = -area.Height, biPlanes = 1, biBitCount = 32,
        };
        _memoryDc = CreateCompatibleDC(IntPtr.Zero);
        if (_memoryDc == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastPInvokeError());
        _bitmap = CreateDIBSection(_memoryDc, ref header, 0, out _bits, IntPtr.Zero, 0);
        if (_bitmap == IntPtr.Zero || _bits == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            DeleteDC(_memoryDc);
            throw error;
        }
        _previousBitmap = SelectObject(_memoryDc, _bitmap);
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
    /// <exception cref="Win32Exception">The screen cannot be read (e.g. while the secure desktop is shown).</exception>
    public void Grab(bool includeCursor)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (!BitBlt(_memoryDc, 0, 0, Area.Width, Area.Height, screen, Area.X, Area.Y, SRCCOPY))
                throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
        if (includeCursor) DrawCursor(_memoryDc, Area.X, Area.Y);
    }

    /// <summary>Copies the last grab as opaque BGRA pixels (stride = width * 4) into <paramref name="target"/>.</summary>
    public void CopyTo(byte[] target)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        int length = Area.Width * 4 * Area.Height;
        if (target.Length < length) throw new ArgumentException("Buffer too small", nameof(target));
        // GDI may batch drawing calls: they must be done before the pixels are read.
        GdiFlush();
        Marshal.Copy(_bits, target, 0, length);
        // The 4th byte of a screen copy is undefined (BGRX).
        foreach (ref uint pixel in MemoryMarshal.Cast<byte, uint>(target.AsSpan(0, length)))
            pixel |= 0xFF000000;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        SelectObject(_memoryDc, _previousBitmap);
        DeleteObject(_bitmap);
        DeleteDC(_memoryDc);
    }

    private static void DrawCursor(IntPtr hdc, int originX, int originY)
    {
        var info = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref info) || (info.flags & CURSOR_SHOWING) == 0) return;
        if (!GetIconInfo(info.hCursor, out var icon)) return;
        try
        {
            DrawIconEx(hdc, info.ptScreenPos.X - icon.xHotspot - originX, info.ptScreenPos.Y - icon.yHotspot - originY,
                info.hCursor, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            if (icon.hbmMask != IntPtr.Zero) DeleteObject(icon.hbmMask);
            if (icon.hbmColor != IntPtr.Zero) DeleteObject(icon.hbmColor);
        }
    }

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GdiFlush();
}
