using System.Globalization;
using Avalonia.Input;

namespace Snaplume.Core;

/// <summary>Keyboard shortcut, stored as "Ctrl+Shift+PrintScreen".</summary>
public readonly record struct Hotkey(KeyModifiers Modifiers, Key Key)
{
    public bool IsEmpty => Key == Key.None;

    /// <summary>Parses a stored shortcut. An empty text is a valid, disabled shortcut.</summary>
    public static bool TryParse(string? text, out Hotkey hotkey)
    {
        hotkey = default;
        if (string.IsNullOrWhiteSpace(text)) return true;

        var modifiers = KeyModifiers.None;
        var key = Key.None;
        foreach (var part in text.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            Key parsed;
            switch (part.ToLowerInvariant())
            {
                case "ctrl" or "control": modifiers |= KeyModifiers.Control; continue;
                case "alt": modifiers |= KeyModifiers.Alt; continue;
                case "shift" or "maj": modifiers |= KeyModifiers.Shift; continue;
                case "win" or "super" or "meta": modifiers |= KeyModifiers.Meta; continue;
                case "printscreen" or "prtsc": parsed = Key.Snapshot; break;
                default:
                    // Key names only: Enum.TryParse would also accept numbers ("44") and lists ("A, B").
                    if (!char.IsAsciiLetter(part[0]) || !part.All(char.IsAsciiLetterOrDigit)
                        || !Enum.TryParse(part, ignoreCase: true, out parsed) || !Enum.IsDefined(parsed))
                        return false;
                    break;
            }
            if (key != Key.None || parsed == Key.None) return false; // one key besides the modifiers
            key = parsed;
        }
        if (key == Key.None) return false;
        hotkey = new Hotkey(modifiers, key);
        return true;
    }

    public override string ToString() => Format(display: false);

    /// <summary>Name in the interface language, e.g. "Ctrl+Maj+Impr. écran" (never parsed back).</summary>
    public string DisplayName => IsEmpty ? Loc.T("Hotkey.None") : Format(display: true);

    private string Format(bool display)
    {
        if (IsEmpty) return "";
        var parts = new List<string>();
        if (Modifiers.HasFlag(KeyModifiers.Control)) parts.Add(display ? Loc.T("Hotkey.Key.Ctrl") : "Ctrl");
        if (Modifiers.HasFlag(KeyModifiers.Alt)) parts.Add(display ? Loc.T("Hotkey.Key.Alt") : "Alt");
        if (Modifiers.HasFlag(KeyModifiers.Shift)) parts.Add(display ? Loc.T("Hotkey.Key.Shift") : "Shift");
        if (Modifiers.HasFlag(KeyModifiers.Meta)) parts.Add(display ? Loc.T(OperatingSystem.IsWindows() ? "Hotkey.Key.Win" : "Hotkey.Key.Super") : "Win");
        parts.Add(KeyName(display));
        return string.Join("+", parts);
    }

    private string KeyName(bool display) => Key switch
    {
        Key.Snapshot => display ? Loc.T("Hotkey.Key.PrintScreen") : "PrintScreen",
        >= Key.D0 and <= Key.D9 when display => ((int)(Key - Key.D0)).ToString(CultureInfo.InvariantCulture),
        _ when display && DisplayKeys.TryGetValue(Key, out var name) => Loc.T(name),
        _ => Key.ToString(),
    };

    /// <summary>Keys with a translated name; the others are shown with their English name.</summary>
    private static readonly Dictionary<Key, string> DisplayKeys = new()
    {
        [Key.Space] = "Hotkey.Key.Space",
        [Key.Enter] = "Hotkey.Key.Enter",
        [Key.Tab] = "Hotkey.Key.Tab",
        [Key.Back] = "Hotkey.Key.Backspace",
        [Key.Escape] = "Hotkey.Key.Escape",
        [Key.Insert] = "Hotkey.Key.Insert",
        [Key.Delete] = "Hotkey.Key.Delete",
        [Key.Home] = "Hotkey.Key.Home",
        [Key.End] = "Hotkey.Key.End",
        [Key.PageUp] = "Hotkey.Key.PageUp",
        [Key.PageDown] = "Hotkey.Key.PageDown",
        [Key.Left] = "Hotkey.Key.Left",
        [Key.Right] = "Hotkey.Key.Right",
        [Key.Up] = "Hotkey.Key.Up",
        [Key.Down] = "Hotkey.Key.Down",
        [Key.Pause] = "Hotkey.Key.Pause",
        [Key.Scroll] = "Hotkey.Key.ScrollLock",
    };
}
