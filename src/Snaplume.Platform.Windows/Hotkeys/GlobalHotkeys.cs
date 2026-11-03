using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Input;
using Avalonia.Threading;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;
using WinForms = System.Windows.Forms;

namespace Snaplume.Platform.Windows.Hotkeys;

/// <summary>System-wide shortcuts (RegisterHotKey) received by a hidden message-only window.</summary>
/// <remarks>
/// The window belongs to the thread that creates this object, which must run a message loop: the Avalonia UI
/// thread. Actions are posted to the Avalonia dispatcher rather than run inside the window procedure, so that
/// their exceptions reach the application's handler instead of WinForms' own error dialog.
/// </remarks>
public sealed class GlobalHotkeys : IGlobalHotkeys
{
    private readonly MessageWindow _window;
    private readonly Dictionary<int, Action> _actions = new();
    private int _nextId = 1;
    private SnippingShortcutHook? _snippingHook;

    public GlobalHotkeys()
    {
        _window = new MessageWindow(this);
    }

    public bool IsSupported => true;

    /// <returns>False if the shortcut is already taken by another application (or its key has no Windows code).</returns>
    public bool Register(string id, string description, Hotkey hotkey, Action action)
    {
        if (hotkey.IsEmpty) return true;
        if (KeyMapping.ToVirtualKey(hotkey.Key) is not uint vk)
        {
            ErrorLog.Write($"Raccourci {hotkey}", "Touche sans code Windows");
            return false;
        }

        uint modifiers = MOD_NOREPEAT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) modifiers |= MOD_ALT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) modifiers |= MOD_CONTROL;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) modifiers |= MOD_SHIFT;
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) modifiers |= MOD_WIN;

        int number = _nextId++;
        if (!RegisterHotKey(_window.Handle, number, modifiers, vk))
        {
            // Usually ERROR_HOTKEY_ALREADY_REGISTERED (1409): another application owns it.
            ErrorLog.Write($"Raccourci {hotkey}", new Win32Exception(Marshal.GetLastPInvokeError()));
            return false;
        }
        _actions[number] = action;
        return true;
    }

    /// <summary>Also gives Win+Shift+S back to the system.</summary>
    public void UnregisterAll()
    {
        foreach (var id in _actions.Keys) UnregisterHotKey(_window.Handle, id);
        _actions.Clear();
        // Ids are only unique among the registered shortcuts: start again (they would otherwise grow at every settings change).
        _nextId = 1;
        ReplaceSystemCapture(null);
    }

    /// <summary>Win+Shift+S runs <paramref name="action"/> instead of opening the Snipping Tool.</summary>
    public bool ReplaceSystemCapture(Action? action)
    {
        _snippingHook?.Dispose();
        _snippingHook = null;
        if (action == null) return true;
        _snippingHook = SnippingShortcutHook.Start(action);
        return _snippingHook != null;
    }

    private void OnHotkey(int id)
    {
        if (_actions.TryGetValue(id, out var action)) Dispatcher.UIThread.Post(action);
    }

    public void Dispose()
    {
        UnregisterAll();
        _window.DestroyHandle();
    }

    /// <summary>Message-only window (HWND_MESSAGE): never shown, not enumerated, only receives WM_HOTKEY.</summary>
    private sealed class MessageWindow : WinForms.NativeWindow
    {
        private readonly GlobalHotkeys _owner;

        public MessageWindow(GlobalHotkeys owner)
        {
            _owner = owner;
            CreateHandle(new WinForms.CreateParams { Caption = "SnaplumeHotkeys", Parent = HWND_MESSAGE });
        }

        protected override void WndProc(ref WinForms.Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                _owner.OnHotkey(m.WParam.ToInt32());
                m.Result = IntPtr.Zero;
                return;
            }
            base.WndProc(ref m);
        }
    }
}

/// <summary>Avalonia keys to Windows virtual-key codes, for the keys a global shortcut may use.</summary>
public static class KeyMapping
{
    public static uint? ToVirtualKey(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (uint)(0x41 + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (uint)(0x30 + (key - Key.D0)),
        >= Key.NumPad0 and <= Key.NumPad9 => (uint)(0x60 + (key - Key.NumPad0)),
        >= Key.F1 and <= Key.F24 => (uint)(0x70 + (key - Key.F1)),
        Key.Snapshot => 0x2C, // VK_SNAPSHOT, the Print Screen key
        Key.Back => 0x08,
        Key.Tab => 0x09,
        Key.Clear => 0x0C,
        Key.Enter => 0x0D,
        Key.Pause => 0x13,
        Key.CapsLock => 0x14,
        Key.Escape => 0x1B,
        Key.Space => 0x20,
        Key.PageUp => 0x21,
        Key.PageDown => 0x22,
        Key.End => 0x23,
        Key.Home => 0x24,
        Key.Left => 0x25,
        Key.Up => 0x26,
        Key.Right => 0x27,
        Key.Down => 0x28,
        Key.Insert => 0x2D,
        Key.Delete => 0x2E,
        Key.Help => 0x2F,
        Key.Apps => 0x5D,
        Key.Sleep => 0x5F,
        Key.Multiply => 0x6A,
        Key.Add => 0x6B,
        Key.Separator => 0x6C,
        Key.Subtract => 0x6D,
        Key.Decimal => 0x6E,
        Key.Divide => 0x6F,
        Key.NumLock => 0x90,
        Key.Scroll => 0x91,
        Key.BrowserBack => 0xA6,
        Key.BrowserForward => 0xA7,
        Key.BrowserRefresh => 0xA8,
        Key.BrowserStop => 0xA9,
        Key.BrowserSearch => 0xAA,
        Key.BrowserFavorites => 0xAB,
        Key.BrowserHome => 0xAC,
        Key.VolumeMute => 0xAD,
        Key.VolumeDown => 0xAE,
        Key.VolumeUp => 0xAF,
        Key.MediaNextTrack => 0xB0,
        Key.MediaPreviousTrack => 0xB1,
        Key.MediaStop => 0xB2,
        Key.MediaPlayPause => 0xB3,
        Key.LaunchMail => 0xB4,
        Key.SelectMedia => 0xB5,
        Key.LaunchApplication1 => 0xB6,
        Key.LaunchApplication2 => 0xB7,
        Key.OemSemicolon => 0xBA,
        Key.OemPlus => 0xBB,
        Key.OemComma => 0xBC,
        Key.OemMinus => 0xBD,
        Key.OemPeriod => 0xBE,
        Key.OemQuestion => 0xBF,
        Key.OemTilde => 0xC0,
        Key.OemOpenBrackets => 0xDB,
        Key.OemPipe => 0xDC,
        Key.OemCloseBrackets => 0xDD,
        Key.OemQuotes => 0xDE,
        Key.Oem8 => 0xDF,
        Key.OemBackslash => 0xE2,
        _ => null,
    };
}
