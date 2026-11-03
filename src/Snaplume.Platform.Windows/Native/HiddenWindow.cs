using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Snaplume.Platform.Windows.Native;

/// <summary>
/// A window that is never shown, only there to receive messages (hotkeys, notification icon, clipboard ownership).
/// </summary>
/// <remarks>
/// The window belongs to the thread that creates it, which must run a message loop: the Avalonia UI thread, whose
/// dispatcher pumps every Win32 message of the thread. Messages the handler leaves (null result) go to DefWindowProc.
/// </remarks>
internal sealed class HiddenWindow : IDisposable
{
    private const int WS_POPUP = unchecked((int)0x80000000);
    private static int s_classCount;

    private readonly WndProc _procedure;
    private readonly Func<uint, IntPtr, IntPtr, IntPtr?> _handler;
    private readonly string _className;

    /// <param name="name">Window title, to recognise it in Spy++.</param>
    /// <param name="handler">Called with (message, wParam, lParam); returns the result, or null for the default handling.</param>
    /// <param name="messageOnly">
    /// HWND_MESSAGE child: not enumerated and gets no broadcast message. A notification icon needs a top-level window
    /// instead (TaskbarCreated is a broadcast, and its menu is shown with the window in the foreground).
    /// </param>
    /// <exception cref="Win32Exception">The window could not be created.</exception>
    public HiddenWindow(string name, Func<uint, IntPtr, IntPtr, IntPtr?> handler, bool messageOnly)
    {
        _handler = handler;
        // Kept in a field: the native side calls it for the window's whole life.
        _procedure = Procedure;
        _className = $"Snaplume.{name}.{Environment.ProcessId}.{Interlocked.Increment(ref s_classCount)}";
        var instance = NativeMethods.GetModuleHandle(null);
        var windowClass = new WNDCLASSEX
        {
            cbSize = Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procedure),
            hInstance = instance,
            lpszClassName = _className,
        };
        if (RegisterClassEx(ref windowClass) == 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        Handle = CreateWindowEx(messageOnly ? 0 : NativeMethods.WS_EX_TOOLWINDOW, _className, name, messageOnly ? 0 : WS_POPUP,
            0, 0, 0, 0, messageOnly ? NativeMethods.HWND_MESSAGE : IntPtr.Zero, IntPtr.Zero, instance, IntPtr.Zero);
        if (Handle == IntPtr.Zero)
        {
            var error = new Win32Exception(Marshal.GetLastPInvokeError());
            UnregisterClass(_className, instance);
            throw error;
        }
    }

    public IntPtr Handle { get; private set; }

    private IntPtr Procedure(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam)
    {
        IntPtr? result = null;
        try
        {
            result = _handler(message, wParam, lParam);
        }
        catch (Exception ex)
        {
            // An exception must not unwind through the native window procedure.
            Snaplume.Core.ErrorLog.Write("Fenêtre cachée", ex);
        }
        return result ?? DefWindowProc(hWnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (Handle == IntPtr.Zero) return;
        DestroyWindow(Handle);
        Handle = IntPtr.Zero;
        UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
        GC.KeepAlive(_procedure);
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public int cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterClass(string className, IntPtr instance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className, string windowName, int style,
        int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);
}
