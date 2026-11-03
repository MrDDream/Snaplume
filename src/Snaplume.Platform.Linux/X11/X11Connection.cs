using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Snaplume.Core;
using static Snaplume.Platform.Linux.Interop.Xlib;
using Snaplume.Platform.Linux.Interop;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// A private connection to the X server (Avalonia keeps its own). Every Xlib call on it goes through
/// <see cref="Gate"/>; the hotkey and click-highlight threads open their own connection instead of sharing this one.
/// </summary>
internal sealed unsafe class X11Connection : IDisposable
{
    private readonly ConcurrentDictionary<string, nuint> _atoms = new();

    private X11Connection(IntPtr display)
    {
        Display = display;
        Root = XDefaultRootWindow(display);
        Screen = XDefaultScreen(display);
    }

    public IntPtr Display { get; private set; }
    public nuint Root { get; }
    public int Screen { get; }
    public object Gate { get; } = new();

    /// <summary>Opens a connection to $DISPLAY; null when there is no X server or no libX11.</summary>
    public static X11Connection? TryOpen()
    {
        try
        {
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))) return null;
            // Avalonia already called it; repeating it is harmless and protects a connection opened first.
            XInitThreads();
            var display = X11Errors.OpenDisplay();
            return display == IntPtr.Zero ? null : new X11Connection(display);
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Connexion X11", ex);
            return null;
        }
    }

    public nuint Atom(string name) => _atoms.GetOrAdd(name, n =>
    {
        lock (Gate) return XInternAtom(Display, n, 0);
    });

    public (int Width, int Height) RootSize()
    {
        lock (Gate) return (XDisplayWidth(Display, Screen), XDisplayHeight(Display, Screen));
    }

    /// <summary>
    /// A property made of 32-bit items (CARDINAL, WINDOW, ATOM). Xlib returns them as C longs, 8 bytes each on
    /// 64-bit systems, whatever the "32" format says.
    /// </summary>
    public nuint[]? GetLongs(nuint window, nuint property, nuint type = AnyPropertyType, int max = 4096)
    {
        lock (Gate) return GetLongsLocked(Display, window, property, type, max);
    }

    internal static nuint[]? GetLongsLocked(IntPtr display, nuint window, nuint property, nuint type = AnyPropertyType, int max = 4096)
    {
        if (XGetWindowProperty(display, window, property, 0, max, 0, type, out _, out int format, out nuint count, out _, out IntPtr data) != 0)
            return null;
        try
        {
            if (data == IntPtr.Zero || format != 32) return null;
            var result = new nuint[(int)count];
            for (int i = 0; i < result.Length; i++) result[i] = ((nuint*)data)[i];
            return result;
        }
        finally
        {
            if (data != IntPtr.Zero) XFree(data);
        }
    }

    /// <summary>Runs Xlib calls and reports whether the server answered one of them with an error.</summary>
    public int Checked(Action calls)
    {
        lock (Gate)
        {
            XSync(Display, 0);
            X11Errors.TakeLastError(Display);
            calls();
            XSync(Display, 0);
            return X11Errors.TakeLastError(Display);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            if (Display == IntPtr.Zero) return;
            X11Errors.CloseDisplay(Display);
            Display = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Xlib's default error handler ends the process, and a window can vanish between two calls (BadWindow is
/// routine when listing windows). Errors on Snaplume's connections are recorded instead; errors on other
/// connections (Avalonia's) go to the handler that was installed before.
/// </summary>
internal static unsafe class X11Errors
{
    private static readonly ConcurrentDictionary<IntPtr, int> LastError = new();
    private static IntPtr _previous;
    private static int _installed;

    private static void Install()
    {
        if (Interlocked.Exchange(ref _installed, 1) == 1) return;
        delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int> handler = &OnError;
        _previous = XSetErrorHandler((IntPtr)handler);
    }

    /// <summary>A new connection to $DISPLAY whose errors are recorded (<see cref="TakeLastError"/>); zero when it fails.</summary>
    /// <exception cref="DllNotFoundException">libX11 is not installed.</exception>
    public static IntPtr OpenDisplay()
    {
        Install();
        var display = XOpenDisplay(IntPtr.Zero);
        if (display != IntPtr.Zero) LastError[display] = 0;
        return display;
    }

    /// <summary>Closes a connection of <see cref="OpenDisplay"/>.</summary>
    public static void CloseDisplay(IntPtr display)
    {
        LastError.TryRemove(display, out _);
        XCloseDisplay(display);
    }

    /// <summary>Last error code since the previous call (0 = none), and clears it.</summary>
    public static int TakeLastError(IntPtr display) => LastError.TryGetValue(display, out var code) && LastError.TryUpdate(display, 0, code) ? code : 0;

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnError(IntPtr display, IntPtr errorEvent)
    {
        try
        {
            if (LastError.ContainsKey(display))
            {
                LastError[display] = ((byte*)errorEvent)[XOffsets.ErrorCode];
                return 0;
            }
            if (_previous != IntPtr.Zero)
                return ((delegate* unmanaged[Cdecl]<IntPtr, IntPtr, int>)_previous)(display, errorEvent);
        }
        catch
        {
            // Never let an exception cross into Xlib.
        }
        return 0;
    }
}
