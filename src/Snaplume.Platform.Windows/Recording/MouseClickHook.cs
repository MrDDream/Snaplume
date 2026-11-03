using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Recording;

/// <summary>
/// Reports the left and right mouse clicks of the whole desktop during a recording (zoom on clicks), with a
/// low-level mouse hook. It sees every click, however short, where polling GetAsyncKeyState 30 times a second would
/// miss the quick ones, and it only watches: the click always goes on to its window.
/// </summary>
/// <remarks>
/// The hook runs on its own thread with its own message loop, like <see cref="Hotkeys.SnippingShortcutHook"/>:
/// Windows drops a hook that answers too slowly, which a busy UI thread would. The callback only stores the click.
/// </remarks>
internal sealed class MouseClickHook : IDisposable
{
    private readonly Action<int, int> _clicked;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    // Kept referenced: the native hook calls it until it is removed.
    private readonly LowLevelMouseProc _proc;
    private IntPtr _hook;
    private uint _threadId;

    private MouseClickHook(Action<int, int> clicked)
    {
        _clicked = clicked;
        _proc = OnMouse;
        _thread = new Thread(Run) { IsBackground = true, Name = "Snaplume click zoom" };
    }

    /// <summary>Installs the hook; null (and logged) if Windows refuses it.</summary>
    /// <param name="clicked">Called on the hook thread with the physical screen coordinates of each click; keep it short.</param>
    public static MouseClickHook? Start(Action<int, int> clicked)
    {
        var hook = new MouseClickHook(clicked);
        hook._thread.Start();
        hook._started.Wait();
        if (hook._hook != IntPtr.Zero) return hook;
        hook._started.Dispose();
        return null;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) ErrorLog.Write("Zoom sur les clics", new Win32Exception(Marshal.GetLastPInvokeError()));
        _started.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
        {
        }
        UnhookWindowsHookEx(_hook);
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam.ToInt32() is WM_LBUTTONDOWN or WM_RBUTTONDOWN)
        {
            var mouse = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            try
            {
                _clicked(mouse.pt.X, mouse.pt.Y);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Zoom sur les clics", ex);
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    public void Dispose()
    {
        // The thread removes the hook when its loop ends.
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }
}
