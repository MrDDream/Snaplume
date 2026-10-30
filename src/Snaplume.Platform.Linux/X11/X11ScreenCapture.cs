using System.Globalization;
using System.Runtime.InteropServices;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Interop;
using static Snaplume.Platform.Linux.Interop.Xlib;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// Screen capture under X11: XGetImage on the root window (every monitor at once, the server composes them),
/// monitors from XRandR 1.5, the pointer image from XFixes.
/// </summary>
internal sealed unsafe class X11ScreenCapture : IScreenCapture
{
    private readonly X11Connection _x;

    public X11ScreenCapture(X11Connection x) => _x = x;

    public IReadOnlyList<MonitorInfo> Monitors => ReadMonitors(_x);

    public Task<DesktopImage> CaptureDesktopAsync(bool includeCursor, CancellationToken cancel = default) => Task.Run(() =>
    {
        cancel.ThrowIfCancellationRequested();
        var (width, height) = _x.RootSize();
        var bounds = new PixelRect(0, 0, width, height);
        var image = Grab(_x, bounds);
        (int X, int Y)? cursor = null;
        lock (_x.Gate)
        {
            if (XQueryPointer(_x.Display, _x.Root, out _, out _, out int px, out int py, out _, out _, out _) != 0)
                cursor = (px, py);
        }
        if (includeCursor) DrawCursor(_x, image, bounds);
        return new DesktopImage(image, bounds, ReadMonitors(_x), cursor);
    }, cancel);

    public IScreenGrabber CreateGrabber(PixelRect area) => new X11Grabber(_x, area);

    /// <summary>Pixels of an area of the root window, opaque BGRA.</summary>
    internal static PixelBuffer Grab(X11Connection x, PixelRect area)
    {
        var buffer = new PixelBuffer(area.Width, area.Height);
        GrabInto(x, area, buffer);
        return buffer;
    }

    internal static void GrabInto(X11Connection x, PixelRect area, PixelBuffer target)
    {
        lock (x.Gate)
        {
            var image = XGetImage(x.Display, x.Root, area.X, area.Y, (uint)area.Width, (uint)area.Height, AllPlanes, ZPixmap);
            if (image == IntPtr.Zero) throw new InvalidOperationException("XGetImage failed (area outside the screen?)");
            try
            {
                var p = (byte*)image;
                var layout = new PixelConversion.ImageLayout(
                    *(int*)(p + XOffsets.ImageWidth), *(int*)(p + XOffsets.ImageHeight), *(int*)(p + XOffsets.ImageBytesPerLine),
                    *(int*)(p + XOffsets.ImageBitsPerPixel), *(int*)(p + XOffsets.ImageByteOrder) == 0,
                    *(nuint*)(p + XOffsets.ImageRedMask), *(nuint*)(p + XOffsets.ImageGreenMask), *(nuint*)(p + XOffsets.ImageBlueMask));
                var data = *(byte**)(p + XOffsets.ImageData);
                PixelConversion.Copy(new ReadOnlySpan<byte>(data, layout.BytesPerLine * layout.Height), layout, target);
            }
            finally
            {
                XDestroyImage(image);
            }
        }
    }

    private static void DrawCursor(X11Connection x, PixelBuffer image, PixelRect bounds)
    {
        try
        {
            lock (x.Gate)
            {
                if (XFixesQueryExtension(x.Display, out _, out _) == 0) return;
                var cursor = XFixesGetCursorImage(x.Display);
                if (cursor == IntPtr.Zero) return;
                try
                {
                    var p = (byte*)cursor;
                    int cx = *(short*)(p + XOffsets.CursorX), cy = *(short*)(p + XOffsets.CursorY);
                    int w = *(ushort*)(p + XOffsets.CursorWidth), h = *(ushort*)(p + XOffsets.CursorHeight);
                    int hx = *(ushort*)(p + XOffsets.CursorXHot), hy = *(ushort*)(p + XOffsets.CursorYHot);
                    var pixels = *(nuint**)(p + XOffsets.CursorPixels);
                    if (w == 0 || h == 0 || pixels == null) return;
                    // One "unsigned long" per pixel: the ARGB value is in its low 32 bits.
                    var argb = new uint[w * h];
                    for (int i = 0; i < argb.Length; i++) argb[i] = (uint)pixels[i];
                    PixelConversion.DrawCursor(image, cx - hx - bounds.X, cy - hy - bounds.Y, w, h, argb);
                }
                finally
                {
                    XFree(cursor);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Curseur X11 (XFixes)", ex);
        }
    }

    /// <summary>Monitors in root-window pixels; the whole root as one monitor when XRandR 1.5 is missing.</summary>
    internal static IReadOnlyList<MonitorInfo> ReadMonitors(X11Connection x)
    {
        var scale = DesktopScale(x);
        var result = new List<MonitorInfo>();
        try
        {
            lock (x.Gate)
            {
                var monitors = XRRGetMonitors(x.Display, x.Root, 1, out int count);
                if (monitors != IntPtr.Zero)
                {
                    try
                    {
                        for (int i = 0; i < count; i++)
                        {
                            var m = (byte*)monitors + i * XOffsets.MonitorSize;
                            var bounds = new PixelRect(*(int*)(m + XOffsets.MonitorX), *(int*)(m + XOffsets.MonitorY),
                                *(int*)(m + XOffsets.MonitorWidth), *(int*)(m + XOffsets.MonitorHeight));
                            if (bounds.IsEmpty) continue;
                            var name = AtomName(x.Display, *(nuint*)(m + XOffsets.MonitorName)) ?? $"Monitor {i + 1}";
                            result.Add(new MonitorInfo(bounds, scale, *(int*)(m + XOffsets.MonitorPrimary) != 0, name));
                        }
                    }
                    finally
                    {
                        XRRFreeMonitors(monitors);
                    }
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Moniteurs XRandR", ex);
        }
        if (result.Count == 0)
        {
            var (w, h) = x.RootSize();
            result.Add(new MonitorInfo(new PixelRect(0, 0, w, h), scale, true, "Screen"));
        }
        else if (!result.Any(m => m.IsPrimary))
        {
            result[0] = result[0] with { IsPrimary = true };
        }
        return result;
    }

    /// <summary>
    /// X11 has one scale for the whole desktop: the Xft.dpi resource that GNOME, KDE and Avalonia follow
    /// (96 = 100 %).
    /// </summary>
    private static double DesktopScale(X11Connection x)
    {
        lock (x.Gate)
        {
            var value = XGetDefault(x.Display, "Xft", "dpi");
            var text = value == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(value);
            return ParseDpiScale(text);
        }
    }

    internal static double ParseDpiScale(string? xftDpi) =>
        double.TryParse(xftDpi, NumberStyles.Float, CultureInfo.InvariantCulture, out var dpi) && dpi is >= 48 and <= 960
            ? Math.Round(dpi / 96.0, 2)
            : 1.0;

    private sealed class X11Grabber : IScreenGrabber
    {
        private readonly X11Connection _x;

        public X11Grabber(X11Connection x, PixelRect area)
        {
            _x = x;
            Area = area;
        }

        public PixelRect Area { get; }

        public Task GrabAsync(byte[] target, CancellationToken cancel = default) => Task.Run(() =>
        {
            cancel.ThrowIfCancellationRequested();
            // The buffer only wraps the caller's array: the pixels land directly in it.
            GrabInto(_x, Area, new PixelBuffer(Area.Width, Area.Height, target));
        }, cancel);

        public void Dispose()
        {
        }
    }
}
