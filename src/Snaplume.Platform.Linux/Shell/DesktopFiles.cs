using System.Globalization;
using System.Text;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>Text files of the freedesktop specifications: desktop entries and trash info. Pure, unit-tested.</summary>
internal static class DesktopFiles
{
    /// <summary>
    /// One argument of a desktop entry's Exec key: quoted when needed, with the characters the spec reserves
    /// inside quotes escaped, then the backslashes doubled again for the string-value level, where a line break or
    /// a tab is written \n, \r or \t (a raw one would end the key); "%" becomes "%%".
    /// </summary>
    public static string ExecArgument(string argument)
    {
        bool needsQuotes = argument.Length == 0 || argument.Any(c => char.IsWhiteSpace(c) || "\"'\\><~|&;$*?#()`".Contains(c));
        var escaped = argument.Replace("%", "%%");
        if (!needsQuotes) return escaped;
        var quoted = new StringBuilder("\"");
        foreach (var c in escaped)
        {
            if (c is '"' or '`' or '$' or '\\') quoted.Append('\\');
            quoted.Append(c);
        }
        quoted.Append('"');
        // Desktop entry string values interpret "\\" as one backslash: each backslash written above is doubled.
        return quoted.ToString().Replace("\\", "\\\\").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");
    }

    /// <summary>~/.config/autostart entry starting Snaplume in the notification area.</summary>
    public static string AutostartEntry(string executable, string name, string comment) =>
        "[Desktop Entry]\n" +
        "Type=Application\n" +
        $"Name={OneLine(name)}\n" +
        $"Comment={OneLine(comment)}\n" +
        $"Exec={ExecArgument(executable)} --tray\n" +
        $"Icon={Util.Xdg.AppId}\n" +
        "Terminal=false\n" +
        "X-GNOME-Autostart-enabled=true\n" +
        "NoDisplay=true\n";

    private static string OneLine(string text) => text.Replace("\r", " ").Replace("\n", " ");

    /// <summary>An autostart entry is active unless it is hidden or disabled for GNOME.</summary>
    public static bool IsAutostartActive(string content)
    {
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Equals("Hidden=true", StringComparison.OrdinalIgnoreCase)) return false;
            if (line.Equals("X-GNOME-Autostart-enabled=false", StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    /// <summary>.trashinfo content (Trash spec 1.0): the original path, percent-encoded, and the local deletion time.</summary>
    public static string TrashInfo(string originalPath, DateTime deletedLocal)
    {
        var encoded = Util.FileUris.FromPath(originalPath).Substring("file://".Length);
        return "[Trash Info]\n" +
               $"Path={encoded}\n" +
               $"DeletionDate={deletedLocal.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)}\n";
    }

    /// <summary>A name not yet used in the trash: "name", "name.2", "name.3"... before the extension.</summary>
    public static string UniqueTrashName(string name, Func<string, bool> taken)
    {
        if (!taken(name)) return name;
        var extension = Path.GetExtension(name);
        var stem = extension.Length > 0 && extension.Length < name.Length ? name[..^extension.Length] : name;
        if (extension.Length == name.Length) extension = "";
        for (int i = 2; ; i++)
        {
            var candidate = $"{stem}.{i}{extension}";
            if (!taken(candidate)) return candidate;
        }
    }

    /// <summary>
    /// A folder that must never hold the workspace (deleting a workspace sends a whole folder to the trash): "/", the
    /// system trees and the temporary folders, the folders holding home folders or drives (see
    /// <see cref="Core.PathRules.IsUnixSystemFolder"/>).
    /// </summary>
    public static bool IsSystemFolder(string folder) => Core.PathRules.IsUnixSystemFolder(folder);

    /// <summary>
    /// CUPS destinations from "lpstat -a" (C locale): "name accepting requests since ...". The indented lines
    /// that follow a printer refusing jobs give its reason ("\tRejecting Jobs"): they name no printer.
    /// </summary>
    public static IReadOnlyList<string> ParsePrinters(string output) =>
        output.Split('\n')
            .Where(l => l.Length > 0 && !char.IsWhiteSpace(l[0]))
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith("lpstat", StringComparison.Ordinal))
            .Select(l => l.Split(' ', 2)[0])
            .Distinct()
            .ToList();

    /// <summary>Default destination from "lpstat -d" (C locale): "system default destination: name".</summary>
    public static string? ParseDefaultPrinter(string output)
    {
        const string marker = "system default destination:";
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return line[marker.Length..].Trim() is { Length: > 0 } name ? name : null;
        }
        return null;
    }
}
