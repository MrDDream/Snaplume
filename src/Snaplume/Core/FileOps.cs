using System.Diagnostics;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Core;

public static class FileOps
{
    /// <summary>
    /// Sends files or folders to the Recycle Bin (recoverable). When an item cannot be recycled
    /// (e.g. on a network share), Windows asks before deleting it permanently.
    /// </summary>
    /// <returns>False if the operation failed or the user cancelled it.</returns>
    public static bool MoveToRecycleBin(IEnumerable<string> paths)
    {
        var existing = paths.Select(Path.GetFullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', existing) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    /// <summary>URI schemes that may be opened: web pages and the Windows settings pages, nothing that runs a program.</summary>
    private static readonly string[] AllowedSchemes = { Uri.UriSchemeHttp, Uri.UriSchemeHttps, "ms-settings" };

    /// <summary>
    /// Opens an existing file or folder (full path) with its default application, or a web or
    /// "ms-settings:" URI. Any other target (other schemes, relative paths) is refused.
    /// </summary>
    /// <returns>False (and the error is logged) when the target is refused or Windows could not open it, e.g. no associated application.</returns>
    public static bool OpenWithDefaultApp(string target)
    {
        try
        {
            string? toOpen = null;
            if (Path.IsPathFullyQualified(target))
            {
                var full = Path.GetFullPath(target);
                if (File.Exists(full) || Directory.Exists(full)) toOpen = full;
            }
            else if (Uri.TryCreate(target, UriKind.Absolute, out var uri)
                     && AllowedSchemes.Contains(uri.Scheme, StringComparer.OrdinalIgnoreCase))
            {
                toOpen = target;
            }
            if (toOpen == null)
            {
                ErrorLog.Write("Ouverture refusée", target);
                return false;
            }
            Process.Start(new ProcessStartInfo(toOpen) { UseShellExecute = true })?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Ouverture de {target}", ex);
            return false;
        }
    }

    public static void ShowInExplorer(string path) =>
        Process.Start(ExplorerPath, $"/select,\"{Path.GetFullPath(path)}\"")?.Dispose();

    public static void OpenFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        Process.Start(ExplorerPath, $"\"{folder}\"")?.Dispose();
    }

    /// <summary>Explorer by its full path, never an explorer.exe found in the current folder.</summary>
    private static string ExplorerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Returns a path that does not exist yet, adding _2, _3... if needed.</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}_{i}{extension}");
        return path;
    }

    public static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
