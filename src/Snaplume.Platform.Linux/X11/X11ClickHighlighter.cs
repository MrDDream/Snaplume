using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using static Snaplume.Platform.Linux.Interop.Xlib;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// Highlights mouse clicks during an X11 recording: XInput 2 raw button events tell about every click without
/// grabbing anything, and a small ring-shaped override-redirect window is shown under the pointer for a moment.
/// ximagesrc records it like any other window. The ring is cut with XShape, so it works without a compositor,
/// and its input shape is empty, so it never takes the click.
/// </summary>
internal sealed unsafe class X11ClickHighlighter : IDisposable
{
    private const int Radius = 18;
    private const int Thickness = 4;
    private const int VisibleMs = 350;
    /// <summary>Amber, as the Windows version (opaque TrueColor pixel).</summary>
    private const uint Color = 0xFFC107;

    private readonly Thread? _thread;
    private volatile bool _stopping;
    private IntPtr _display;

    public X11ClickHighlighter()
    {
        try
        {
            X11Errors.Install();
            _display = XOpenDisplay(IntPtr.Zero);
            if (_display == IntPtr.Zero) return;
            X11Errors.Track(_display);
            _thread = new Thread(Run) { IsBackground = true, Name = "Snaplume click highlight" };
            _thread.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Mise en évidence des clics", ex);
        }
    }

    private void Run()
    {
        try
        {
            var d = _display;
            var root = XDefaultRootWindow(d);
            if (XQueryExtension(d, "XInputExtension", out int xiOpcode, out _, out _) == 0) return;
            int major = 2, minor = 0;
            if (XIQueryVersion(d, ref major, ref minor) != 0) return;
            var maskBytes = stackalloc byte[4];
            new Span<byte>(maskBytes, 4).Clear();
            maskBytes[XI_RawButtonPress >> 3] |= (byte)(1 << (XI_RawButtonPress & 7));
            var mask = new XIEventMask { DeviceId = XIAllMasterDevices, MaskLength = 4, Mask = maskBytes };
            XISelectEvents(d, root, &mask, 1);
            if (XShapeQueryExtension(d, out _, out _) == 0) return;
            var ring = CreateRing(d, root);
            XFlush(d);

            var xevent = stackalloc byte[XEventSize];
            var pollFd = new LibC.PollFd { Fd = XConnectionNumber(d), Events = LibC.POLLIN };
            long hideAt = 0;
            while (!_stopping)
            {
                while (XPending(d) > 0)
                {
                    XNextEvent(d, xevent);
                    if (*(int*)(xevent + XOffsets.EventType) != GenericEvent || *(int*)(xevent + XOffsets.CookieExtension) != xiOpcode)
                        continue;
                    if (XGetEventData(d, xevent) == 0) continue;
                    try
                    {
                        if (*(int*)(xevent + XOffsets.CookieEvType) != XI_RawButtonPress) continue;
                        var raw = *(byte**)(xevent + XOffsets.CookieData);
                        int button = *(int*)(raw + XOffsets.RawDetail);
                        if (button is < 1 or > 3) continue; // 4-7 are wheel steps
                        if (XQueryPointer(d, root, out _, out _, out int x, out int y, out _, out _, out _) == 0) continue;
                        XMoveWindow(d, ring, x - Radius, y - Radius);
                        XMapRaised(d, ring);
                        XFlush(d);
                        hideAt = Environment.TickCount64 + VisibleMs;
                    }
                    finally
                    {
                        XFreeEventData(d, xevent);
                    }
                }
                if (hideAt != 0 && Environment.TickCount64 >= hideAt)
                {
                    XUnmapWindow(d, ring);
                    XFlush(d);
                    hideAt = 0;
                }
                LibC.poll(&pollFd, 1, hideAt != 0 ? 20 : 100);
            }
            XDestroyWindow(d, ring);
            XFlush(d);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Mise en évidence des clics", ex);
        }
    }

    private static nuint CreateRing(IntPtr d, nuint root)
    {
        int size = Radius * 2;
        var window = XCreateSimpleWindow(d, root, 0, 0, (uint)size, (uint)size, 0, 0, Color);
        var attributes = stackalloc byte[XOffsets.SetAttributesSize];
        new Span<byte>(attributes, XOffsets.SetAttributesSize).Clear();
        *(int*)(attributes + XOffsets.SetOverrideRedirect) = 1;
        XChangeWindowAttributes(d, window, CWOverrideRedirect, attributes);
        var spans = RingSpans(Radius, Thickness);
        fixed (XRectangle* rects = spans)
            XShapeCombineRectangles(d, window, ShapeBounding, 0, 0, rects, spans.Length, ShapeSet, Unsorted);
        XShapeCombineRectangles(d, window, ShapeInput, 0, 0, null, 0, ShapeSet, Unsorted);
        return window;
    }

    /// <summary>The ring as horizontal one-pixel spans (XShape takes rectangles).</summary>
    internal static XRectangle[] RingSpans(int radius, int thickness)
    {
        var spans = new List<XRectangle>();
        double outer = radius - 0.5, inner = radius - thickness - 0.5;
        for (int y = 0; y < radius * 2; y++)
        {
            double dy = y + 0.5 - radius;
            int start = -1;
            for (int x = 0; x <= radius * 2; x++)
            {
                double dx = x + 0.5 - radius, distance = Math.Sqrt(dx * dx + dy * dy);
                bool inside = x < radius * 2 && distance <= outer && distance >= inner;
                if (inside && start < 0) start = x;
                else if (!inside && start >= 0)
                {
                    spans.Add(new XRectangle { X = (short)start, Y = (short)y, Width = (ushort)(x - start), Height = 1 });
                    start = -1;
                }
            }
        }
        return spans.ToArray();
    }

    public void Dispose()
    {
        _stopping = true;
        _thread?.Join(1000);
        if (_display != IntPtr.Zero)
        {
            X11Errors.Untrack(_display);
            XCloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }
}
