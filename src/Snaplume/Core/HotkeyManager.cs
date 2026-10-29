using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Core;

/// <summary>System-wide shortcuts registered on a hidden message-only window.</summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;

    public HotkeyManager()
    {
        _source = new HwndSource(new HwndSourceParameters("SnaplumeHotkeys")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,
            ParentWindow = HWND_MESSAGE,
        });
        _source.AddHook(WndProc);
    }

    /// <returns>False if the shortcut is already taken by another application.</returns>
    public bool Register(Hotkey hotkey, Action action)
    {
        if (hotkey.IsEmpty) return true;

        uint modifiers = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Alt)) modifiers |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Control)) modifiers |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Shift)) modifiers |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(ModifierKeys.Windows)) modifiers |= MOD_WIN;

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers, (uint)KeyInterop.VirtualKeyFromKey(hotkey.Key)))
        {
            // Usually ERROR_HOTKEY_ALREADY_REGISTERED (1409): another application owns it.
            ErrorLog.Write($"Raccourci {hotkey}", new Win32Exception(Marshal.GetLastPInvokeError()));
            return false;
        }
        _actions[id] = action;
        return true;
    }

    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_source.Handle, id);
        _actions.Clear();
        // Ids are only unique among the registered shortcuts: start again (they would otherwise grow at every settings change).
        _nextId = 1;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            action();
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source.Dispose();
    }
}
