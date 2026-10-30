using System.Globalization;
using System.Text;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>Text files of the freedesktop specifications: desktop entries and trash info. Pure, unit-tested.</summary>
internal static class DesktopFiles
{
    /// <summary>
    /// One argument of a desktop entry's Exec key: quoted when needed, with the characters the spec reserves
    /// inside quotes escaped, then the backslashes doubled again for the string-value level; "%" becomes "%%".
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
        return quoted.ToString().Replace("\\", "\\\\");
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
    /// Folders that must never hold the workspace: the file system root and the system trees. Deleting a
    /// workspace sends a whole folder to the trash, so a system folder is refused outright.
    /// </summary>
    private static readonly string[] SystemFolders =
    {
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/opt", "/proc", "/run",
        "/sbin", "/snap", "/sys", "/usr", "/var",
    };

    /// <returns>True when the folder is "/", "/home" itself, or lies in a system tree.</returns>
    public static bool IsSystemFolder(string folder)
    {
        var full = Path.TrimEndingDirectorySeparator(folder.Replace('\\', '/'));
        if (full.Length == 0 || full == "/" || full == "/home") return true;
        // /var/home holds the home folders on Fedora Silverblue and other image-based systems.
        if (full == "/var/home" || full.StartsWith("/var/home/", StringComparison.Ordinal)) return false;
        return SystemFolders.Any(s => full == s || full.StartsWith(s + "/", StringComparison.Ordinal));
    }

    /// <summary>CUPS destinations from "lpstat -a" (C locale): "name accepting requests since ...".</summary>
    public static IReadOnlyList<string> ParsePrinters(string output) =>
        output.Split('\n')
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
