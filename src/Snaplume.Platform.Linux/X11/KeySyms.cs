using Avalonia.Input;
using Snaplume.Core;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// Avalonia keys as X keysyms (for XGrabKey) and as XKB keysym names (for the GlobalShortcuts portal triggers,
/// e.g. "CTRL+SHIFT+Print"). Letters use the lower-case keysym: both cases sit on the same physical key.
/// </summary>
internal static class KeySyms
{
    public const nuint Escape = 0xff1b;
    public const nuint NumLock = 0xff7f;
    public const nuint ScrollLock = 0xff14;

    private static readonly Dictionary<Key, (nuint Sym, string Name)> Named = new()
    {
        [Key.Snapshot] = (0xff61, "Print"),
        [Key.Space] = (0x20, "space"),
        [Key.Enter] = (0xff0d, "Return"),
        [Key.Tab] = (0xff09, "Tab"),
        [Key.Back] = (0xff08, "BackSpace"),
        [Key.Escape] = (0xff1b, "Escape"),
        [Key.Insert] = (0xff63, "Insert"),
        [Key.Delete] = (0xffff, "Delete"),
        [Key.Home] = (0xff50, "Home"),
        [Key.End] = (0xff57, "End"),
        [Key.PageUp] = (0xff55, "Prior"),
        [Key.PageDown] = (0xff56, "Next"),
        [Key.Left] = (0xff51, "Left"),
        [Key.Up] = (0xff52, "Up"),
        [Key.Right] = (0xff53, "Right"),
        [Key.Down] = (0xff54, "Down"),
        [Key.Pause] = (0xff13, "Pause"),
        [Key.Scroll] = (0xff14, "Scroll_Lock"),
        [Key.OemMinus] = (0x2d, "minus"),
        [Key.OemPlus] = (0x3d, "equal"),
        [Key.OemComma] = (0x2c, "comma"),
        [Key.OemPeriod] = (0x2e, "period"),
        [Key.OemSemicolon] = (0x3b, "semicolon"),
        [Key.OemQuestion] = (0x2f, "slash"),
        [Key.OemTilde] = (0x60, "grave"),
        [Key.OemOpenBrackets] = (0x5b, "bracketleft"),
        [Key.OemPipe] = (0x5c, "backslash"),
        [Key.OemCloseBrackets] = (0x5d, "bracketright"),
        [Key.OemQuotes] = (0x27, "apostrophe"),
        [Key.Multiply] = (0xffaa, "KP_Multiply"),
        [Key.Add] = (0xffab, "KP_Add"),
        [Key.Subtract] = (0xffad, "KP_Subtract"),
        [Key.Decimal] = (0xffae, "KP_Decimal"),
        [Key.Divide] = (0xffaf, "KP_Divide"),
    };

    /// <summary>Keysym and XKB name of a key; null for keys a shortcut cannot use.</summary>
    public static (nuint Sym, string Name)? Of(Key key)
    {
        if (Named.TryGetValue(key, out var named)) return named;
        if (key is >= Key.A and <= Key.Z)
        {
            char c = (char)('a' + (key - Key.A));
            return (c, c.ToString());
        }
        if (key is >= Key.D0 and <= Key.D9)
        {
            char c = (char)('0' + (key - Key.D0));
            return (c, c.ToString());
        }
        if (key is >= Key.NumPad0 and <= Key.NumPad9)
        {
            int n = key - Key.NumPad0;
            return ((nuint)(0xffb0 + n), "KP_" + n);
        }
        if (key is >= Key.F1 and <= Key.F24)
        {
            int n = key - Key.F1;
            return ((nuint)(0xffbe + n), "F" + (n + 1));
        }
        return null;
    }

    /// <summary>X modifier mask of the shortcut: Shift, Control, Mod1 (Alt), Mod4 (Super).</summary>
    public static uint ModifierMask(KeyModifiers modifiers)
    {
        uint mask = 0;
        if (modifiers.HasFlag(KeyModifiers.Shift)) mask |= Interop.Xlib.ShiftMask;
        if (modifiers.HasFlag(KeyModifiers.Control)) mask |= Interop.Xlib.ControlMask;
        if (modifiers.HasFlag(KeyModifiers.Alt)) mask |= Interop.Xlib.Mod1Mask;
        if (modifiers.HasFlag(KeyModifiers.Meta)) mask |= Interop.Xlib.Mod4Mask;
        return mask;
    }

    /// <summary>
    /// Every subset of the lock modifiers (Caps Lock, Num Lock, Scroll Lock): X delivers a grabbed key only when the
    /// modifier state matches exactly, so the shortcut is grabbed once per combination of locks.
    /// </summary>
    public static IReadOnlyList<uint> LockCombinations(uint lockMasks)
    {
        var bits = Enumerable.Range(0, 8).Select(i => 1u << i).Where(b => (lockMasks & b) != 0).ToArray();
        var result = new List<uint>(1 << bits.Length);
        for (int subset = 0; subset < 1 << bits.Length; subset++)
        {
            uint mask = 0;
            for (int i = 0; i < bits.Length; i++)
                if ((subset & (1 << i)) != 0) mask |= bits[i];
            result.Add(mask);
        }
        return result;
    }

    /// <summary>
    /// Trigger in the format of the XDG shortcuts specification used by the GlobalShortcuts portal:
    /// modifiers CTRL, ALT, SHIFT, LOGO then the keysym name, joined by "+"; null if the key has no name.
    /// </summary>
    public static string? PortalTrigger(Hotkey hotkey)
    {
        if (hotkey.IsEmpty || Of(hotkey.Key) is not { } key) return null;
        var parts = new List<string>();
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Control)) parts.Add("CTRL");
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add("ALT");
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add("SHIFT");
        if (hotkey.Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add("LOGO");
        parts.Add(key.Name);
        return string.Join("+", parts);
    }
}
