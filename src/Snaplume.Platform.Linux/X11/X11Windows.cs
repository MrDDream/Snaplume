using Snaplume.Capturing;
using Snaplume.Platform.Linux.Interop;
using static Snaplume.Platform.Linux.Interop.Xlib;

namespace Snaplume.Platform.Linux.X11;

/// <summary>A top-level window as the user sees it (decorations included, client-side shadows excluded).</summary>
internal readonly record struct X11Window(nuint Id, PixelRect Bounds);

/// <summary>
/// The windows of an EWMH window manager (every current desktop does it): the stacking list gives the order,
/// _NET_FRAME_EXTENTS the server-side title bar and borders, _GTK_FRAME_EXTENTS the invisible shadow margin of
/// windows that draw their own decorations (GTK, libadwaita).
/// </summary>
internal sealed unsafe class X11WindowLocator : IWindowLocator
{
    private readonly X11Connection _x;

    public X11WindowLocator(X11Connection x) => _x = x;

    public IWindowSnapshot Snapshot(PixelRect virtualBounds, IReadOnlyList<PixelRect> monitors)
    {
        var windows = List(_x, virtualBounds);
        return new WindowSnapshot(windows);
    }

    public PixelRect? ForegroundWindowBounds()
    {
        var active = _x.GetLongs(_x.Root, _x.Atom("_NET_ACTIVE_WINDOW"), XA_WINDOW);
        if (active is not { Length: > 0 } || active[0] == 0) return null;
        var (w, h) = _x.RootSize();
        lock (_x.Gate)
            return Bounds(_x, active[0], new PixelRect(0, 0, w, h)) is { } bounds ? bounds : null;
    }

    /// <summary>Visible windows of the current desktop, top-most first; Snaplume's own windows are left out.</summary>
    internal static List<X11Window> List(X11Connection x, PixelRect virtualBounds)
    {
        var result = new List<X11Window>();
        var stacking = x.GetLongs(x.Root, x.Atom("_NET_CLIENT_LIST_STACKING"), XA_WINDOW)
                       ?? x.GetLongs(x.Root, x.Atom("_NET_CLIENT_LIST"), XA_WINDOW);
        if (stacking == null) return result;
        var currentDesktop = x.GetLongs(x.Root, x.Atom("_NET_CURRENT_DESKTOP"), XA_CARDINAL) is { Length: > 0 } cd ? cd[0] : (nuint?)null;
        var ownPid = (nuint)Environment.ProcessId;
        lock (x.Gate)
        {
            // Bottom-to-top in the property: reversed for "top-most first".
            for (int i = stacking.Length - 1; i >= 0; i--)
            {
                var window = stacking[i];
                if (IsHiddenOrForeign(x, window, currentDesktop, ownPid)) continue;
                if (Bounds(x, window, virtualBounds) is { } bounds) result.Add(new X11Window(window, bounds));
            }
        }
        return result;
    }

    /// <summary>Minimized, on another desktop, a desktop background, or one of Snaplume's windows.</summary>
    private static bool IsHiddenOrForeign(X11Connection x, nuint window, nuint? currentDesktop, nuint ownPid)
    {
        var d = x.Display;
        var state = X11Connection.GetLongsLocked(d, window, x.Atom("_NET_WM_STATE"), XA_ATOM);
        if (state != null && state.Contains(x.Atom("_NET_WM_STATE_HIDDEN"))) return true;
        var type = X11Connection.GetLongsLocked(d, window, x.Atom("_NET_WM_WINDOW_TYPE"), XA_ATOM);
        if (type != null && type.Contains(x.Atom("_NET_WM_WINDOW_TYPE_DESKTOP"))) return true;
        if (currentDesktop is { } current
            && X11Connection.GetLongsLocked(d, window, x.Atom("_NET_WM_DESKTOP"), XA_CARDINAL) is { Length: > 0 } desktop
            && desktop[0] != current && desktop[0] != unchecked((nuint)0xFFFFFFFF))
            return true;
        var pid = X11Connection.GetLongsLocked(d, window, x.Atom("_NET_WM_PID"), XA_CARDINAL);
        return pid is { Length: > 0 } && pid[0] == ownPid;
    }

    /// <summary>Visible bounds of a top-level window, clipped to the desktop; null if unmapped or off-screen.</summary>
    /// <remarks>The caller holds the connection lock.</remarks>
    internal static PixelRect? Bounds(X11Connection x, nuint window, PixelRect virtualBounds)
    {
        var attributes = stackalloc byte[XOffsets.AttributesSize + 64];
        if (XGetWindowAttributes(x.Display, window, attributes) == 0) return null;
        if (*(int*)(attributes + XOffsets.AttrMapState) != IsViewable) return null;
        int width = *(int*)(attributes + XOffsets.AttrWidth), height = *(int*)(attributes + XOffsets.AttrHeight);
        if (XTranslateCoordinates(x.Display, window, x.Root, 0, 0, out int left, out int top, out _) == 0) return null;
        var net = Extents(X11Connection.GetLongsLocked(x.Display, window, x.Atom("_NET_FRAME_EXTENTS"), XA_CARDINAL));
        var gtk = Extents(X11Connection.GetLongsLocked(x.Display, window, x.Atom("_GTK_FRAME_EXTENTS"), XA_CARDINAL));
        var bounds = FrameBounds(new PixelRect(left, top, width, height), net, gtk).Intersect(virtualBounds);
        return bounds.IsEmpty ? null : bounds;
    }

    internal readonly record struct FrameExtents(int Left, int Right, int Top, int Bottom);

    private static FrameExtents? Extents(nuint[]? values) =>
        values is { Length: >= 4 } ? new FrameExtents((int)values[0], (int)values[1], (int)values[2], (int)values[3]) : null;

    /// <summary>
    /// Window as seen on screen: the client area grown by the window manager's frame, shrunk by the shadow
    /// margin that client-side decorated windows include in their own size.
    /// </summary>
    internal static PixelRect FrameBounds(PixelRect client, FrameExtents? net, FrameExtents? gtk)
    {
        var r = client;
        if (gtk is { } g)
            r = new PixelRect(r.X + g.Left, r.Y + g.Top, r.Width - g.Left - g.Right, r.Height - g.Top - g.Bottom);
        if (net is { } n)
            r = new PixelRect(r.X - n.Left, r.Y - n.Top, r.Width + n.Left + n.Right, r.Height + n.Top + n.Bottom);
        return r;
    }

    private sealed class WindowSnapshot : IWindowSnapshot
    {
        private readonly List<X11Window> _windows;

        public WindowSnapshot(List<X11Window> windows) => _windows = windows;

        /// <summary>X11 exposes no controls inside windows: the answer is the top-most window under the point.</summary>
        public IReadOnlyList<PixelRect> FindAt(int x, int y)
        {
            foreach (var window in _windows)
                if (window.Bounds.Contains(x, y)) return new[] { window.Bounds };
            return Array.Empty<PixelRect>();
        }
    }
}

/// <summary>Pointer and keyboard for the scrolling capture: XQueryPointer, XWarpPointer, XTest wheel, XQueryKeymap.</summary>
internal sealed unsafe class X11Pointer : IPointerControl
{
    private readonly X11Connection _x;
    private byte _escapeKeycode;

    public X11Pointer(X11Connection x) => _x = x;

    public Task<bool> PrepareAsync(CancellationToken cancel = default)
    {
        lock (_x.Gate)
        {
            try
            {
                return Task.FromResult(XTestQueryExtension(_x.Display, out _, out _, out _, out _) != 0);
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
            {
                Core.ErrorLog.Write("Extension XTest", ex);
                return Task.FromResult(false);
            }
        }
    }

    public bool TryGetPosition(out int x, out int y)
    {
        lock (_x.Gate)
            return XQueryPointer(_x.Display, _x.Root, out _, out _, out x, out y, out _, out _, out _) != 0;
    }

    public bool SetPosition(int x, int y)
    {
        lock (_x.Gate)
        {
            XWarpPointer(_x.Display, 0, _x.Root, 0, 0, 0, 0, x, y);
            XFlush(_x.Display);
        }
        return true;
    }

    /// <summary>Wheel notches as presses of buttons 4 (up) and 5 (down), the X11 convention.</summary>
    public bool Scroll(int notches)
    {
        if (notches == 0) return true;
        uint button = notches > 0 ? 4u : 5u;
        try
        {
            lock (_x.Gate)
            {
                for (int i = 0; i < Math.Abs(notches); i++)
                {
                    XTestFakeButtonEvent(_x.Display, button, 1, 0);
                    XTestFakeButtonEvent(_x.Display, button, 0, 0);
                }
                XFlush(_x.Display);
            }
            return true;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            Core.ErrorLog.Write("Molette XTest", ex);
            return false;
        }
    }

    public bool EscapePressed()
    {
        var keys = stackalloc byte[32];
        lock (_x.Gate)
        {
            if (_escapeKeycode == 0) _escapeKeycode = XKeysymToKeycode(_x.Display, KeySyms.Escape);
            if (_escapeKeycode == 0) return false;
            XQueryKeymap(_x.Display, keys);
        }
        return (keys[_escapeKeycode / 8] & (1 << (_escapeKeycode % 8))) != 0;
    }

    /// <summary>Asks the window manager to activate the window under the point (_NET_ACTIVE_WINDOW, as a pager would).</summary>
    public void ActivateWindowAt(int x, int y)
    {
        var (w, h) = _x.RootSize();
        var target = X11WindowLocator.List(_x, new PixelRect(0, 0, w, h)).FirstOrDefault(win => win.Bounds.Contains(x, y));
        if (target.Id == 0) return;
        X11NativeWindows.SendClientMessage(_x, target.Id, _x.Atom("_NET_ACTIVE_WINDOW"), 2, 0, 0);
    }

    public void Release()
    {
    }
}
