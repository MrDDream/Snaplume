using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snaplume.Core;

namespace Snaplume.Preferences;

/// <summary>Text box recording a key combination when pressed.</summary>
public sealed class HotkeyBox : TextBox
{
    private Hotkey _hotkey;

    public HotkeyBox()
    {
        Classes.Add("hotkey");
        IsReadOnly = true;
        CaretBrush = Brushes.Transparent;
        Cursor = new Cursor(StandardCursorType.Arrow);
        ToolTip.SetTip(this, Loc.T(OperatingSystem.IsWindows() ? "Hotkey.BoxHint" : "Hotkey.BoxHintSystem"));
        // Before the text box: it would otherwise handle navigation and editing keys itself.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
    }

    /// <summary>Drawn as a standard text box.</summary>
    protected override Type StyleKeyOverride => typeof(TextBox);

    public Hotkey Hotkey
    {
        get => _hotkey;
        set
        {
            _hotkey = value;
            Text = value.DisplayName;
        }
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Tab && e.KeyModifiers is KeyModifiers.None or KeyModifiers.Shift) return; // keep focus navigation
        e.Handled = true;
        Record(e.Key, e.KeyModifiers);
    }

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e)
    {
        // Windows only sends "key up" for Print Screen.
        if (e.Key != Key.Snapshot) return;
        e.Handled = true;
        Record(e.Key, e.KeyModifiers);
    }

    private void Record(Key key, KeyModifiers modifiers)
    {
        switch (key)
        {
            case Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin or Key.ImeProcessed or Key.DeadCharProcessed or Key.None:
                return;
            case Key.Back or Key.Delete or Key.Escape when modifiers == KeyModifiers.None:
                Hotkey = default;
                return;
        }
        var hotkey = new Hotkey(modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Shift | KeyModifiers.Meta), key);
        // A plain letter (or Shift+letter) would be stolen from every application: keep the previous shortcut.
        if (IsAllowed(hotkey)) Hotkey = hotkey;
    }

    /// <summary>
    /// A global shortcut needs Ctrl, Alt or Win / Super, except for keys no application relies on:
    /// Print Screen, Pause, Scroll Lock and F13 to F24. F1 to F12 alone would be taken from every
    /// application (F1 help, F2 rename, F5 refresh...).
    /// </summary>
    public static bool IsAllowed(Hotkey hotkey) =>
        hotkey.IsEmpty ||
        (hotkey.Modifiers & (KeyModifiers.Control | KeyModifiers.Alt | KeyModifiers.Meta)) != 0 ||
        hotkey.Key is Key.Snapshot or Key.Pause or Key.Scroll or (>= Key.F13 and <= Key.F24);
}
