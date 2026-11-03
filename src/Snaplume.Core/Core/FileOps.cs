namespace Snaplume.Core;

public static class FileOps
{
    /// <summary>
    /// Sends files or folders to the trash (recoverable). On Windows, when an item cannot be recycled
    /// (e.g. on a network share), the system asks before deleting it permanently.
    /// </summary>
    /// <returns>False if the operation failed or the user cancelled it.</returns>
    public static bool MoveToTrash(IEnumerable<string> paths)
    {
        var existing = paths.Select(Path.GetFullPath).Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (existing.Count == 0) return true;
        try
        {
            return AppServices.Platform.Shell.MoveToTrash(existing);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Mise à la corbeille", ex);
            return false;
        }
    }

    /// <summary>
    /// URI schemes that may be opened: web pages, and the Windows settings pages on Windows; nothing that runs a program.
    /// </summary>
    private static string[] AllowedSchemes => OperatingSystem.IsWindows()
        ? new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps, "ms-settings" }
        : new[] { Uri.UriSchemeHttp, Uri.UriSchemeHttps };

    /// <summary>
    /// Opens an existing file or folder (full path) with its default application, or a web URI ("ms-settings:" too on
    /// Windows). Any other target (other schemes, relative paths) is refused.
    /// </summary>
    /// <returns>False (and the error is logged) when the target is refused or could not be opened, e.g. no associated application.</returns>
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
            AppServices.Platform.Shell.Open(toOpen);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Ouverture de {target}", ex);
            return false;
        }
    }

    /// <summary>Shows the file selected in the file manager.</summary>
    public static void Reveal(string path) => AppServices.Platform.Shell.Reveal(Path.GetFullPath(path));

    public static void OpenFolder(string folder)
    {
        folder = Path.GetFullPath(folder);
        Directory.CreateDirectory(folder);
        AppServices.Platform.Shell.Open(folder);
    }

    /// <summary>
    /// Moves a file to a path that does not exist yet, also to another drive or a network share: when the move itself
    /// fails there, the file is copied (its dates kept: annotation files and thumbnails are matched on them), then the
    /// original deleted. If the original cannot be deleted, the copy is removed and the error thrown: never two copies.
    /// </summary>
    /// <param name="move">For the tests: the plain move tried first (by default <see cref="File.Move(string, string)"/>).</param>
    public static void MoveFile(string source, string destination, Action<string, string>? move = null)
    {
        try
        {
            (move ?? File.Move)(source, destination);
            return;
        }
        catch (IOException ex) when (ex is not FileNotFoundException and not DirectoryNotFoundException && File.Exists(source) && !File.Exists(destination))
        {
            // Across volumes on some systems: copy and delete below.
        }
        var info = new FileInfo(source);
        DateTime written = info.LastWriteTimeUtc, created = info.CreationTimeUtc;
        File.Copy(source, destination, overwrite: false);
        try
        {
            try
            {
                File.SetCreationTimeUtc(destination, created);
            }
            catch (Exception)
            {
                // Not supported everywhere.
            }
            File.SetLastWriteTimeUtc(destination, written);
            File.Delete(source);
        }
        catch
        {
            try { File.Delete(destination); } catch (Exception) { /* the original error matters more */ }
            throw;
        }
    }

    /// <summary>Returns a path that does not exist yet, adding _2, _3... if needed.</summary>
    public static string UniquePath(string folder, string baseName, string extension)
    {
        var path = Path.Combine(folder, baseName + extension);
        for (int i = 2; File.Exists(path); i++) path = Path.Combine(folder, $"{baseName}_{i}{extension}");
        return path;
    }

    /// <summary>
    /// Characters invalid in a file name on Windows, replaced everywhere: captures and workspaces may be copied
    /// between systems (Linux itself only refuses '/' and NUL).
    /// </summary>
    private static readonly char[] InvalidNameChars =
        "\"<>|:*?\\/".ToCharArray().Concat(Enumerable.Range(0, 32).Select(i => (char)i)).ToArray();

    public static string SanitizeFileName(string name) =>
        new(name.Select(c => InvalidNameChars.Contains(c) ? '_' : c).ToArray());

    /// <summary>A single folder or file name valid on every system: no separators or invalid characters.</summary>
    internal static bool HasInvalidNameChars(string name) => name.IndexOfAny(InvalidNameChars) >= 0;
}
