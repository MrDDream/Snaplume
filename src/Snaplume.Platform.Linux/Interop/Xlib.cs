using System.Runtime.InteropServices;

namespace Snaplume.Platform.Linux.Interop;

/// <summary>
/// Xlib and its extensions, for 64-bit Linux: XID, Window, Atom, KeySym, Time and "unsigned long" are
/// <c>nuint</c>, "long" is <c>nint</c>, Bool and int are <c>int</c>, Display* is an <c>IntPtr</c>.
/// Structures are read through byte offsets (see <see cref="XOffsets"/>) rather than mirrored, because several
/// of them are unions or end with fields Snaplume never needs.
/// </summary>
internal static unsafe class Xlib
{
    private const string X11 = "libX11.so.6";
    private const string Xrandr = "libXrandr.so.2";
    private const string Xfixes = "libXfixes.so.3";
    private const string Xtst = "libXtst.so.6";
    private const string Xext = "libXext.so.6";
    private const string Xi = "libXi.so.6";

    public const int ZPixmap = 2;
    public static readonly nuint AllPlanes = nuint.MaxValue;
    public const int PropModeReplace = 0;
    public const nuint AnyPropertyType = 0;
    public const nuint XA_ATOM = 4;
    public const nuint XA_CARDINAL = 6;
    public const nuint XA_WINDOW = 33;
    public const int IsViewable = 2;
    public const int GrabModeAsync = 1;
    public const int KeyPress = 2;
    public const int ClientMessage = 33;
    public const int GenericEvent = 35;
    public const int BadAccess = 10;
    public const nint KeyPressMask = 1 << 0;
    public const nint SubstructureNotifyMask = 1 << 19;
    public const nint SubstructureRedirectMask = 1 << 20;
    public const nuint CWOverrideRedirect = 1 << 9;
    public const nuint CWBackPixel = 1 << 1;

    public const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod4Mask = 64;

    /// <summary>XEvent is a union of 24 longs.</summary>
    public const int XEventSize = 192;

    // ---- libX11 ----

    [DllImport(X11)] public static extern int XInitThreads();
    [DllImport(X11)] public static extern IntPtr XOpenDisplay(IntPtr name);
    [DllImport(X11)] public static extern int XCloseDisplay(IntPtr display);
    [DllImport(X11)] public static extern nuint XDefaultRootWindow(IntPtr display);
    [DllImport(X11)] public static extern int XDefaultScreen(IntPtr display);
    [DllImport(X11)] public static extern int XDisplayWidth(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XDisplayHeight(IntPtr display, int screen);
    [DllImport(X11)] public static extern int XConnectionNumber(IntPtr display);
    [DllImport(X11)] public static extern IntPtr XGetImage(IntPtr display, nuint drawable, int x, int y, uint width, uint height, nuint planeMask, int format);
    [DllImport(X11)] public static extern int XDestroyImage(IntPtr image);
    [DllImport(X11)] public static extern nuint XGetPixel(IntPtr image, int x, int y);
    [DllImport(X11)] public static extern int XFree(IntPtr data);
    [DllImport(X11)] public static extern int XFlush(IntPtr display);
    [DllImport(X11)] public static extern int XSync(IntPtr display, int discard);
    [DllImport(X11)] public static extern nuint XInternAtom(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, int onlyIfExists);
    [DllImport(X11)] public static extern IntPtr XGetAtomName(IntPtr display, nuint atom);

    [DllImport(X11)]
    public static extern int XGetWindowProperty(IntPtr display, nuint window, nuint property, nint offset, nint length, int delete,
        nuint requestedType, out nuint actualType, out int actualFormat, out nuint itemCount, out nuint bytesAfter, out IntPtr data);

    [DllImport(X11)] public static extern int XChangeProperty(IntPtr display, nuint window, nuint property, nuint type, int format, int mode, nuint* data, int count);
    [DllImport(X11)] public static extern int XGetWindowAttributes(IntPtr display, nuint window, byte* attributes);
    [DllImport(X11)] public static extern int XTranslateCoordinates(IntPtr display, nuint source, nuint destination, int x, int y, out int destX, out int destY, out nuint child);

    [DllImport(X11)]
    public static extern int XQueryPointer(IntPtr display, nuint window, out nuint root, out nuint child,
        out int rootX, out int rootY, out int windowX, out int windowY, out uint mask);

    [DllImport(X11)] public static extern int XWarpPointer(IntPtr display, nuint source, nuint destination, int srcX, int srcY, uint srcWidth, uint srcHeight, int destX, int destY);
    [DllImport(X11)] public static extern int XQueryKeymap(IntPtr display, byte* keys);
    [DllImport(X11)] public static extern byte XKeysymToKeycode(IntPtr display, nuint keysym);
    [DllImport(X11)] public static extern int XGrabKey(IntPtr display, int keycode, uint modifiers, nuint window, int ownerEvents, int pointerMode, int keyboardMode);
    [DllImport(X11)] public static extern int XUngrabKey(IntPtr display, int keycode, uint modifiers, nuint window);
    [DllImport(X11)] public static extern int XSelectInput(IntPtr display, nuint window, nint mask);
    [DllImport(X11)] public static extern int XPending(IntPtr display);
    [DllImport(X11)] public static extern int XNextEvent(IntPtr display, byte* xevent);
    [DllImport(X11)] public static extern int XSendEvent(IntPtr display, nuint window, int propagate, nint mask, byte* xevent);
    [DllImport(X11)] public static extern IntPtr XGetModifierMapping(IntPtr display);
    [DllImport(X11)] public static extern int XFreeModifiermap(IntPtr map);
    [DllImport(X11)] public static extern IntPtr XGetDefault(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string program, [MarshalAs(UnmanagedType.LPUTF8Str)] string option);
    [DllImport(X11)] public static extern IntPtr XSetErrorHandler(IntPtr handler);
    [DllImport(X11)] public static extern IntPtr XGetWMHints(IntPtr display, nuint window);
    [DllImport(X11)] public static extern int XSetWMHints(IntPtr display, nuint window, IntPtr hints);
    [DllImport(X11)] public static extern IntPtr XAllocWMHints();
    [DllImport(X11)] public static extern nuint XCreateSimpleWindow(IntPtr display, nuint parent, int x, int y, uint width, uint height, uint borderWidth, nuint border, nuint background);
    [DllImport(X11)] public static extern int XChangeWindowAttributes(IntPtr display, nuint window, nuint valueMask, byte* attributes);
    [DllImport(X11)] public static extern int XMapRaised(IntPtr display, nuint window);
    [DllImport(X11)] public static extern int XUnmapWindow(IntPtr display, nuint window);
    [DllImport(X11)] public static extern int XMoveWindow(IntPtr display, nuint window, int x, int y);
    [DllImport(X11)] public static extern int XRaiseWindow(IntPtr display, nuint window);
    [DllImport(X11)] public static extern int XDestroyWindow(IntPtr display, nuint window);
    [DllImport(X11)] public static extern int XQueryExtension(IntPtr display, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, out int opcode, out int firstEvent, out int firstError);
    [DllImport(X11)] public static extern int XGetEventData(IntPtr display, byte* cookie);
    [DllImport(X11)] public static extern void XFreeEventData(IntPtr display, byte* cookie);

    // ---- XRandR 1.5: monitors ----

    [DllImport(Xrandr)] public static extern IntPtr XRRGetMonitors(IntPtr display, nuint window, int getActive, out int count);
    [DllImport(Xrandr)] public static extern void XRRFreeMonitors(IntPtr monitors);

    // ---- XFixes: cursor image ----

    [DllImport(Xfixes)] public static extern int XFixesQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(Xfixes)] public static extern IntPtr XFixesGetCursorImage(IntPtr display);

    // ---- XTest: wheel ----

    [DllImport(Xtst)] public static extern int XTestQueryExtension(IntPtr display, out int eventBase, out int errorBase, out int major, out int minor);
    [DllImport(Xtst)] public static extern int XTestFakeButtonEvent(IntPtr display, uint button, int isPress, nuint delay);

    // ---- XShape (libXext): click-through windows, click highlight rings ----

    public const int ShapeBounding = 0;
    public const int ShapeInput = 2;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;

    [DllImport(Xext)] public static extern int XShapeQueryExtension(IntPtr display, out int eventBase, out int errorBase);
    [DllImport(Xext)] public static extern void XShapeCombineRectangles(IntPtr display, nuint window, int destKind, int xOffset, int yOffset, XRectangle* rectangles, int count, int operation, int ordering);

    // ---- XInput 2: raw button events (click highlight) ----

    public const int XIAllMasterDevices = 1;
    public const int XI_RawButtonPress = 15;

    [DllImport(Xi)] public static extern int XIQueryVersion(IntPtr display, ref int major, ref int minor);
    [DllImport(Xi)] public static extern int XISelectEvents(IntPtr display, nuint window, XIEventMask* masks, int count);

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle
    {
        public short X, Y;
        public ushort Width, Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct XIEventMask
    {
        public int DeviceId;
        public int MaskLength;
        public byte* Mask;
    }

    /// <summary>Name of an atom, or null.</summary>
    public static string? AtomName(IntPtr display, nuint atom)
    {
        if (atom == 0) return null;
        var name = XGetAtomName(display, atom);
        if (name == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(name);
        }
        finally
        {
            XFree(name);
        }
    }
}

/// <summary>Byte offsets of the Xlib structures read by Snaplume (LP64 layouts, checked against the Xlib headers).</summary>
internal static class XOffsets
{
    // XImage
    public const int ImageWidth = 0, ImageHeight = 4, ImageFormat = 12, ImageData = 16, ImageByteOrder = 24,
        ImageDepth = 40, ImageBytesPerLine = 44, ImageBitsPerPixel = 48, ImageRedMask = 56, ImageGreenMask = 64, ImageBlueMask = 72;

    // XWindowAttributes (136 bytes)
    public const int AttributesSize = 136;
    public const int AttrX = 0, AttrY = 4, AttrWidth = 8, AttrHeight = 12, AttrClass = 40, AttrMapState = 92, AttrOverrideRedirect = 120;

    // XRRMonitorInfo (56 bytes)
    public const int MonitorSize = 56;
    public const int MonitorName = 0, MonitorPrimary = 8, MonitorX = 20, MonitorY = 24, MonitorWidth = 28, MonitorHeight = 32;

    // XFixesCursorImage
    public const int CursorX = 0, CursorY = 2, CursorWidth = 4, CursorHeight = 6, CursorXHot = 8, CursorYHot = 10, CursorPixels = 24;

    // XEvent: XAnyEvent / XKeyEvent / XClientMessageEvent / XGenericEventCookie
    public const int EventType = 0, EventDisplay = 24, EventWindow = 32;
    public const int KeyState = 80, KeyKeycode = 84;
    public const int ClientMessageType = 40, ClientFormat = 48, ClientData = 56;
    public const int CookieExtension = 32, CookieEvType = 36, CookieData = 48;

    // XIRawEvent
    public const int RawDetail = 56;

    // XErrorEvent
    public const int ErrorDisplay = 8, ErrorCode = 32;

    // XWMHints
    public const int HintsFlags = 0, HintsInput = 8;
    public const nint InputHint = 1;

    // XModifierKeymap
    public const int ModMaxKeysPerMod = 0, ModModifierMap = 8;

    // XSetWindowAttributes (112 bytes)
    public const int SetAttributesSize = 112;
    public const int SetBackgroundPixel = 8, SetOverrideRedirect = 88;
}
