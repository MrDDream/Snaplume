using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Hotkeys;

/// <summary>
/// Takes Win+Shift+S away from the Snipping Tool: a low-level keyboard hook swallows the S of that combination and
/// runs the action instead. RegisterHotKey cannot do it, the shell owns the shortcut.
/// </summary>
/// <remarks>
/// The hook runs on its own thread with its own message loop: Windows drops a hook that answers too slowly, which a
/// busy UI thread would. Keys typed in a window run as administrator never reach the hook (the Snipping Tool opens
/// there), and the hook ignores the keys that programs send (only the real keyboard counts).
/// </remarks>
internal sealed class SnippingShortcutHook : IDisposable
{
    private const int VK_S = 0x53;
    /// <summary>Asks the hook thread to keep the Start menu closed, out of the hook callback.</summary>
    private const uint WM_MASK_WIN = WM_APP + 1;

    private readonly Action _action;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _started = new();
    // Kept referenced: the native hook calls it until it is removed.
    private readonly LowLevelKeyboardProc _proc;
    private IntPtr _hook;
    private uint _threadId;
    private bool _swallowing;

    private SnippingShortcutHook(Action action)
    {
        _action = action;
        _proc = OnKey;
        _thread = new Thread(Run) { IsBackground = true, Name = "Snaplume Win+Shift+S" };
    }

    /// <summary>Installs the hook; null (and logged) if Windows refuses it.</summary>
    public static SnippingShortcutHook? Start(Action action)
    {
        var hook = new SnippingShortcutHook(action);
        hook._thread.Start();
        hook._started.Wait();
        if (hook._hook != IntPtr.Zero) return hook;
        hook._started.Dispose();
        return null;
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        _hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) ErrorLog.Write("Remplacement de Win+Maj+S", new Win32Exception(Marshal.GetLastPInvokeError()));
        _started.Set();
        if (_hook == IntPtr.Zero) return;

        while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
        {
            if (message.message == WM_MASK_WIN) MaskWinKey();
        }
        UnhookWindowsHookEx(_hook);
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (key.vkCode == VK_S && (key.flags & LLKHF_INJECTED) == 0)
            {
                int message = wParam.ToInt32();
                if (message is WM_KEYDOWN or WM_SYSKEYDOWN && (_swallowing || IsSnippingCombination()))
                {
                    // Held down, the key repeats: one capture only.
                    if (!_swallowing)
                    {
                        _swallowing = true;
                        PostThreadMessage(_threadId, WM_MASK_WIN, IntPtr.Zero, IntPtr.Zero);
                        ThreadPool.QueueUserWorkItem(_ => Run(_action));
                    }
                    return 1;
                }
                if (message is WM_KEYUP or WM_SYSKEYUP && _swallowing)
                {
                    _swallowing = false;
                    return 1;
                }
            }
        }
        return CallNextHookEx(_hook, code, wParam, lParam);
    }

    /// <summary>Win and Shift held, without Ctrl or Alt (Win+Ctrl+Shift+S stays the system's).</summary>
    private static bool IsSnippingCombination() =>
        (IsDown(VK_LWIN) || IsDown(VK_RWIN)) && IsDown(VK_SHIFT) && !IsDown(VK_CONTROL) && !IsDown(VK_MENU);

    private static bool IsDown(int key) => (GetAsyncKeyState(key) & 0x8000) != 0;

    /// <summary>
    /// The shell opens the Start menu when Win is released with no other key in between: the S it never saw does not
    /// count, so an unassigned key is pressed meanwhile.
    /// </summary>
    private static void MaskWinKey()
    {
        var inputs = new KEYBOARD_INPUT[2];
        inputs[0].type = inputs[1].type = INPUT_KEYBOARD;
        inputs[0].u.ki.wVk = inputs[1].u.ki.wVk = VK_UNASSIGNED_E8;
        inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;
        if (SendInput(2, inputs, Marshal.SizeOf<KEYBOARD_INPUT>()) != 2)
            ErrorLog.Write("Remplacement de Win+Maj+S", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Remplacement de Win+Maj+S", ex);
        }
    }

    public void Dispose()
    {
        // The thread removes the hook when its loop ends.
        PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(TimeSpan.FromSeconds(2));
        _started.Dispose();
    }
}
