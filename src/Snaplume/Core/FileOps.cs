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

    /// <summary>
    /// Opens a file with its default application, or a URI such as "ms-settings:regionlanguage".
    /// </summary>
    /// <returns>False (and the error is logged) when Windows could not open it, e.g. no associated application.</returns>
    public static bool OpenWithDefaultApp(string target)
    {
        try
        {
            // URIs are passed as is; files by their full path.
            bool isUri = Uri.TryCreate(target, UriKind.Absolute, out var uri) && !uri.IsFile && !uri.IsUnc;
            Process.Start(new ProcessStartInfo(isUri ? target : Path.GetFullPath(target)) { UseShellExecute = true })?.Dispose();
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
