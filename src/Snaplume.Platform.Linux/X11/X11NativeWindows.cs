using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using static Snaplume.Platform.Linux.Interop.Xlib;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// Native window tweaks on the X11 window id that Avalonia gives (TopLevel.TryGetPlatformHandle). They also work
/// under Wayland, where Avalonia's windows are XWayland windows.
/// </summary>
internal sealed unsafe class X11NativeWindows : INativeWindows
{
    private readonly X11Connection? _x;

    public X11NativeWindows(X11Connection? x) => _x = x;

    /// <summary>X11 cannot keep a window out of XGetImage or a screencast: the application hides it instead.</summary>
    public void ExcludeFromCapture(IntPtr handle)
    {
    }

    /// <summary>An empty input shape: the server sends pointer events to whatever lies below.</summary>
    public void MakeClickThrough(IntPtr handle)
    {
        if (_x == null || handle == IntPtr.Zero) return;
        try
        {
            var error = _x.Checked(() =>
            {
                if (XShapeQueryExtension(_x.Display, out _, out _) == 0) return;
                XShapeCombineRectangles(_x.Display, (nuint)handle, ShapeInput, 0, 0, null, 0, ShapeSet, Unsorted);
            });
            if (error != 0) ErrorLog.Write("Fenêtre traversée par les clics", $"Erreur X11 {error}");
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Fenêtre traversée par les clics (XShape)", ex);
        }
    }

    /// <summary>
    /// Utility window type, skipped by the task bar and the pager, and no keyboard focus on click (WM_HINTS input
    /// = False), the closest X11 equivalent of WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE.
    /// </summary>
    public void MakeToolWindow(IntPtr handle)
    {
        if (_x == null || handle == IntPtr.Zero) return;
        var window = (nuint)handle;
        var stateAtom = _x.Atom("_NET_WM_STATE");
        var skipTaskbar = _x.Atom("_NET_WM_STATE_SKIP_TASKBAR");
        var skipPager = _x.Atom("_NET_WM_STATE_SKIP_PAGER");
        var typeAtom = _x.Atom("_NET_WM_WINDOW_TYPE");
        var utility = _x.Atom("_NET_WM_WINDOW_TYPE_UTILITY");
        _x.Checked(() =>
        {
            var type = utility;
            XChangeProperty(_x.Display, window, typeAtom, XA_ATOM, 32, PropModeReplace, &type, 1);
            // Read by the window manager when the window is mapped...
            var states = (X11Connection.GetLongsLocked(_x.Display, window, stateAtom, XA_ATOM) ?? Array.Empty<nuint>())
                .Union(new[] { skipTaskbar, skipPager }).ToArray();
            fixed (nuint* p = states)
                XChangeProperty(_x.Display, window, stateAtom, XA_ATOM, 32, PropModeReplace, p, states.Length);
            var hints = XGetWMHints(_x.Display, window);
            bool allocated = hints == IntPtr.Zero;
            if (allocated) hints = XAllocWMHints();
            if (hints != IntPtr.Zero)
            {
                *(nint*)((byte*)hints + XOffsets.HintsFlags) |= XOffsets.InputHint;
                *(int*)((byte*)hints + XOffsets.HintsInput) = 0;
                XSetWMHints(_x.Display, window, hints);
                XFree(hints);
            }
        });
        // ...and, for a window already shown, requested from it (EWMH: action 1 = add, source 1 = application).
        SendClientMessage(_x, window, stateAtom, 1, skipTaskbar, skipPager, 1);
    }

    /// <summary>EWMH client message to the root window, as window managers expect for state and activation requests.</summary>
    internal static void SendClientMessage(X11Connection x, nuint window, nuint messageType, nuint l0, nuint l1, nuint l2, nuint l3 = 0)
    {
        var xevent = stackalloc byte[XEventSize];
        new Span<byte>(xevent, XEventSize).Clear();
        *(int*)(xevent + XOffsets.EventType) = ClientMessage;
        *(nuint*)(xevent + XOffsets.EventWindow) = window;
        *(nuint*)(xevent + XOffsets.ClientMessageType) = messageType;
        *(int*)(xevent + XOffsets.ClientFormat) = 32;
        var data = (nuint*)(xevent + XOffsets.ClientData);
        data[0] = l0;
        data[1] = l1;
        data[2] = l2;
        data[3] = l3;
        lock (x.Gate)
        {
            XSendEvent(x.Display, x.Root, 0, SubstructureRedirectMask | SubstructureNotifyMask, xevent);
            XFlush(x.Display);
        }
    }
}
