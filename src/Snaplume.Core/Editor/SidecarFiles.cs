using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>
/// Files keeping a capture editable, in a hidden ".snaplume" folder next to it: the base image (pixelated
/// areas burnt in) and the annotations. They follow the capture when it is renamed, moved or deleted.
/// </summary>
public static class SidecarFiles
{
    public const string Folder = ".snaplume";
    private const string LegacyFolder = ".librecapture";
    public const string BaseSuffix = ".base.png";
    public const string JsonSuffix = ".json";

    /// <summary>Ending of the files being written (renamed into place once complete).</summary>
    public const string TempSuffix = ".tmp";

    /// <summary>A temporary file older than this was left by an interrupted save: <see cref="CleanOrphans"/> deletes it.</summary>
    private static readonly TimeSpan StaleTempAge = TimeSpan.FromHours(1);

    /// <summary>Folders whose legacy sidecar folder was already looked for in this session.</summary>
    private static readonly HashSet<string> MigratedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders already cleaned in this session (see <see cref="CleanOrphans"/>).</summary>
    private static readonly HashSet<string> CleanedFolders = new(StringComparer.OrdinalIgnoreCase);

    public static (string BasePath, string JsonPath) PathsOf(string file)
    {
        var folder = Path.Combine(Path.GetDirectoryName(file)!, Folder);
        var name = Path.GetFileName(file);
        return (Path.Combine(folder, name + BaseSuffix), Path.Combine(folder, name + JsonSuffix));
    }

    /// <summary>Creates the sidecar folder of a capture folder, hidden on Windows (the leading dot hides it elsewhere).</summary>
    public static void EnsureFolder(string sidecarFolder)
    {
        var info = Directory.CreateDirectory(sidecarFolder);
        if (OperatingSystem.IsWindows()) info.Attributes |= FileAttributes.Hidden;
    }

    /// <summary>Renames the sidecar folder of older versions (".librecapture"), once per folder and session.</summary>
    public static void MigrateLegacy(string dir)
    {
        lock (MigratedFolders)
        {
            if (!MigratedFolders.Add(Path.GetFullPath(dir))) return;
        }
        var folder = Path.Combine(dir, Folder);
        var legacy = Path.Combine(dir, LegacyFolder);
        if (Directory.Exists(folder) || !Directory.Exists(legacy)) return;
        try { Directory.Move(legacy, folder); }
        catch (Exception ex) { ErrorLog.Write("Migration des annotations", ex); }
    }

    /// <summary>
    /// Deletes the sidecar files whose capture no longer exists (deleted or renamed outside Snaplume), and the
    /// temporary files of saves interrupted more than an hour ago; once per folder and session.
    /// </summary>
    public static void CleanOrphans(string dir)
    {
        lock (CleanedFolders)
        {
            if (!CleanedFolders.Add(Path.GetFullPath(dir))) return;
        }
        try
        {
            var folder = Path.Combine(dir, Folder);
            if (!Directory.Exists(folder)) return;
            var staleBefore = DateTime.UtcNow - StaleTempAge;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(TempSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    // A save in progress (another window) is younger than that.
                    if (File.GetLastWriteTimeUtc(file) < staleBefore) File.Delete(file);
                    continue;
                }
                string? capture = name.EndsWith(BaseSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^BaseSuffix.Length]
                    : name.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^JsonSuffix.Length]
                    : null;
                if (string.IsNullOrEmpty(capture) || File.Exists(Path.Combine(dir, capture))) continue;
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Nettoyage des annotations orphelines", ex);
        }
    }

    /// <summary>Deletes a file left by a failed write; a failure is only logged (the write's own error matters more).</summary>
    internal static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression d'un fichier temporaire", ex);
        }
    }

    public static void Delete(string file)
    {
        MigrateLegacy(Path.GetDirectoryName(file)!);
        var (basePath, jsonPath) = PathsOf(file);
        if (File.Exists(basePath)) File.Delete(basePath);
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
    }

    public static void Rename(string oldFile, string newFile)
    {
        MigrateLegacy(Path.GetDirectoryName(oldFile)!);
        MigrateLegacy(Path.GetDirectoryName(newFile)!);
        var (oldBase, oldJson) = PathsOf(oldFile);
        var (newBase, newJson) = PathsOf(newFile);
        if (File.Exists(oldBase)) File.Move(oldBase, newBase, overwrite: true);
        if (File.Exists(oldJson)) File.Move(oldJson, newJson, overwrite: true);
    }

    /// <summary>
    /// The annotations follow a capture moved to another folder, on another drive too (the capture keeps its dates
    /// there, which the annotation file checks); a failure only loses them, not the capture.
    /// </summary>
    public static void Move(string oldFile, string newFile)
    {
        try
        {
            var (oldBase, oldJson) = PathsOf(oldFile);
            if (!File.Exists(oldBase) && !File.Exists(oldJson)) return;
            var (newBase, newJson) = PathsOf(newFile);
            EnsureFolder(Path.GetDirectoryName(newJson)!);
            foreach (var (from, to) in new[] { (oldBase, newBase), (oldJson, newJson) })
            {
                if (!File.Exists(from)) continue;
                // Left by a capture deleted outside Snaplume: replaced.
                if (File.Exists(to)) File.Delete(to);
                FileOps.MoveFile(from, to);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Déplacement des annotations", ex);
        }
    }
}
