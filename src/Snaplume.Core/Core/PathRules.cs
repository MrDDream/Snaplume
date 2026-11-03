namespace Snaplume.Core;

/// <summary>
/// Comparisons of folders (case ignored on Windows only, separators at the end ignored) and the folders that must
/// never hold the captures: each sub-folder of the captures folder becomes a workspace, and deleting a workspace sends
/// its whole folder to the trash.
/// </summary>
public static class PathRules
{
    public static StringComparison Comparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    public static StringComparer Comparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    /// <summary>Full path without a separator at the end; the text as it is when it is not a valid path.</summary>
    public static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception)
        {
            return path;
        }
    }

    /// <summary>Same folder, separators at the end and (on Windows) case aside.</summary>
    public static bool SamePath(string a, string b) => string.Equals(Normalize(a), Normalize(b), Comparison);

    /// <summary>True when <paramref name="child"/> is strictly inside <paramref name="parent"/> (at any depth).</summary>
    public static bool IsInside(string child, string parent)
    {
        var folder = Normalize(parent);
        // A root ("C:\", "/") keeps its separator.
        if (!Path.EndsInDirectorySeparator(folder)) folder += Path.DirectorySeparatorChar;
        var path = Normalize(child);
        return path.Length > folder.Length && path.StartsWith(folder, Comparison);
    }

    /// <summary>
    /// A folder too broad to hold the captures: a drive or file system root; a personal folder (home, Desktop,
    /// Documents, Pictures, Videos, Music, Downloads, OneDrive) or a folder holding one of them (C:\Users, /home);
    /// a folder of programs, settings or temporary files, or anything inside one (Windows, Program Files, AppData,
    /// Snaplume's own folder and settings, /usr, /tmp...).
    /// </summary>
    /// <param name="downloadsFolder">The Downloads folder; by default the platform's, when there is one.</param>
    public static bool IsTooBroad(string folder, string? downloadsFolder = null)
    {
        folder = Normalize(folder);
        // Drive root (C:\), network share root (\\server\share), or "/".
        if (Path.GetPathRoot(folder) is { Length: > 0 } root && SamePath(root, folder)) return true;
        if (!OperatingSystem.IsWindows() && IsUnixSystemFolder(folder)) return true;
        // The folder itself, or one of its parents (e.g. C:\Users or /home, or a OneDrive folder holding Documents).
        if (PersonalFolders(downloadsFolder).Any(p => SamePath(p, folder) || IsInside(p, folder))) return true;
        // These folders and everything inside them.
        return ProtectedFolders().Any(p => SamePath(p, folder) || IsInside(folder, p));
    }

    /// <summary>Folders refused, with every folder holding them.</summary>
    private static IEnumerable<string> PersonalFolders(string? downloadsFolder)
    {
        var folders = new List<string?>
        {
            downloadsFolder ?? PlatformDownloads(),
            // A portable copy: its updates are unzipped next to it ("Snaplume-1.2.0"), each would become a workspace.
            Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory)),
        };
        folders.AddRange(new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
        }.Select(Environment.GetFolderPath));
        // OneDrive roots: personal, and the work or school ones.
        folders.AddRange(new[] { "OneDrive", "OneDriveConsumer", "OneDriveCommercial" }.Select(Environment.GetEnvironmentVariable));
        return Usable(folders);
    }

    /// <summary>Folders refused, with everything inside them.</summary>
    private static IEnumerable<string> ProtectedFolders()
    {
        var folders = new List<string?>
        {
            // Snaplume itself (a portable copy keeps its settings there) and its settings.
            AppContext.BaseDirectory,
            AppSettings.SettingsFolder,
        };
        folders.AddRange(new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData, Environment.SpecialFolder.ApplicationData,
            Environment.SpecialFolder.LocalApplicationData,
        }.Select(Environment.GetFolderPath));
        // C:\Users\me\AppData (Roaming, Local and LocalLow); on Linux the parent of ~/.config is the home folder.
        if (OperatingSystem.IsWindows())
            folders.Add(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))));
        return Usable(folders);
    }

    private static IEnumerable<string> Usable(IEnumerable<string?> folders) =>
        folders.Where(p => !string.IsNullOrEmpty(p) && Path.IsPathFullyQualified(p)).Select(p => p!);

    private static string PlatformDownloads()
    {
        try
        {
            return AppServices.HasPlatform ? AppServices.Platform.Shell.DownloadsFolder : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>System trees of Linux, refused with everything inside them (the temporary folders too).</summary>
    private static readonly string[] UnixSystemTrees =
    {
        "/bin", "/boot", "/dev", "/etc", "/lib", "/lib32", "/lib64", "/libx32", "/opt", "/proc", "/run",
        "/sbin", "/snap", "/srv", "/sys", "/tmp", "/usr", "/var", "/var/tmp",
    };

    /// <summary>
    /// Folders holding the home folders (/var/home on Fedora Silverblue and other image-based systems) or the drives:
    /// refused themselves, folders inside them accepted.
    /// </summary>
    private static readonly string[] UnixMountParents = { "/home", "/var/home", "/mnt", "/media", "/run/media" };

    /// <summary>
    /// The Linux rules of <see cref="IsTooBroad"/> on the text of an absolute path ('/' separators), whatever system
    /// runs the code: "/", the system trees and what they hold, the folders holding home folders or drives, and the
    /// folder of a user's drives (/media/me, /run/media/me).
    /// </summary>
    public static bool IsUnixSystemFolder(string folder)
    {
        var full = folder.Replace('\\', '/');
        while (full.Length > 1 && full.EndsWith('/')) full = full[..^1];
        if (!full.StartsWith('/') || full == "/" || full.Split('/').Any(part => part is "." or "..")) return true;
        if (UnixMountParents.Contains(full, StringComparer.Ordinal)) return true;
        if (full[..full.LastIndexOf('/')] is "/media" or "/run/media") return true;
        // Home folders and drives are accepted below these, although /var and /run are system trees.
        if (full.StartsWith("/var/home/", StringComparison.Ordinal) || full.StartsWith("/run/media/", StringComparison.Ordinal)) return false;
        return UnixSystemTrees.Any(s => full == s || full.StartsWith(s + "/", StringComparison.Ordinal));
    }
}
