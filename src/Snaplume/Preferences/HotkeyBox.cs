using System.Media;
using System.Windows.Controls;
using System.Windows.Input;
using Snaplume.Core;

namespace Snaplume.Preferences;

/// <summary>Text box recording a key combination when pressed.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey;

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Arrow;
        Padding = new System.Windows.Thickness(4, 3, 4, 3);
        ToolTip = Loc.T("Hotkey.BoxHint");
    }

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value.DisplayName;
        }
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key == Key.Tab && Keyboard.Modifiers is ModifierKeys.None or ModifierKeys.Shift) return; // keep focus navigation
        e.Handled = true;
        Record(key);
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        // Windows only sends "key up" for Print Screen.
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key != Key.Snapshot) return;
        e.Handled = true;
        Record(key);
    }

    private void Record(Key key)
    {
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None:
                return;
            case Key.Back or Key.Delete or Key.Escape when Keyboard.Modifiers == ModifierKeys.None:
                Hotkey = default;
                return;
        }
        var hotkey = new Hotkey(Keyboard.Modifiers, key);
        if (!IsAllowed(hotkey))
        {
            // A plain letter (or Shift+letter) would be stolen from every application: keep the previous shortcut.
            SystemSounds.Beep.Play();
            return;
        }
        Hotkey = hotkey;
    }

    /// <summary>
    /// A global shortcut needs Ctrl, Alt or Win, except for keys that type nothing:
    /// Print Screen, Pause, Scroll Lock and the function keys.
    /// </summary>
    public static bool IsAllowed(Hotkey hotkey) =>
        hotkey.IsEmpty ||
        (hotkey.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt | ModifierKeys.Windows)) != 0 ||
        hotkey.Key is Key.Snapshot or Key.Pause or Key.Scroll or (>= Key.F1 and <= Key.F24);
}
