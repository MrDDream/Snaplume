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

    /// <summary>Folders whose legacy sidecar folder was already looked for in this session.</summary>
    private static readonly HashSet<string> MigratedFolders = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Deletes the sidecar files whose capture no longer exists (deleted or renamed outside Snaplume).</summary>
    public static void CleanOrphans(string dir)
    {
        try
        {
            var folder = Path.Combine(dir, Folder);
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
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

    /// <summary>The annotations follow a capture moved to another folder; a failure only loses them, not the capture.</summary>
    public static void Move(string oldFile, string newFile)
    {
        try
        {
            var (oldBase, oldJson) = PathsOf(oldFile);
            if (!File.Exists(oldBase) && !File.Exists(oldJson)) return;
            var (newBase, newJson) = PathsOf(newFile);
            EnsureFolder(Path.GetDirectoryName(newJson)!);
            if (File.Exists(oldBase)) File.Move(oldBase, newBase, overwrite: true);
            if (File.Exists(oldJson)) File.Move(oldJson, newJson, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Déplacement des annotations", ex);
        }
    }
}
