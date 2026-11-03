using System.IO.Compression;

namespace Snaplume.Core;

/// <summary>
/// Installs a portable update: the zip is extracted into a new folder next to the running copy ("Snaplume-1.2.0"),
/// never over it, with the "portable" marker and the user's files (settings, presets) copied from the running copy.
/// The old folder stays as it is: the user deletes it once the new version suits them.
/// </summary>
public static class PortableUpdate
{
    /// <summary>The marker that keeps the settings next to the executable (see <see cref="AppSettings.SettingsFolder"/>).</summary>
    public const string Marker = "portable";

    /// <summary>Files Snaplume writes in a portable folder that belong to the user (not errors.log).</summary>
    internal static readonly string[] UserFiles = { "settings.json", "presets.json", "document.json" };

    /// <summary>Extracts <paramref name="zip"/> into a new sibling folder of <paramref name="currentFolder"/>.</summary>
    /// <param name="version">As published (<see cref="UpdateChecker.Release.DisplayVersion"/>), part of the new folder's name.</param>
    /// <returns>The new folder.</returns>
    /// <exception cref="InvalidDataException">The zip holds an entry outside its folder, or no executable.</exception>
    /// <exception cref="IOException">The folder could not be written (e.g. a read-only location).</exception>
    public static string Install(string zip, string currentFolder, string version, string executableName)
    {
        currentFolder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(currentFolder));
        var parent = Path.GetDirectoryName(currentFolder) ?? throw new IOException($"Pas de dossier parent pour {currentFolder}");
        var target = FreeFolder(parent, "Snaplume-" + FileOps.SanitizeFileName(version));
        // Extracted under a temporary name, then renamed: a half-extracted folder never looks like a version.
        var staging = FreeFolder(parent, Path.GetFileName(target) + ".part");
        try
        {
            Extract(zip, staging);
            if (!File.Exists(Path.Combine(staging, executableName)))
                throw new InvalidDataException($"{executableName} absent de {Path.GetFileName(zip)}");
            var marker = Path.Combine(staging, Marker);
            if (!File.Exists(marker)) File.WriteAllBytes(marker, Array.Empty<byte>());
            foreach (var name in UserFiles)
            {
                var source = Path.Combine(currentFolder, name);
                if (File.Exists(source)) File.Copy(source, Path.Combine(staging, name), overwrite: true);
            }
            Directory.Move(staging, target);
            return target;
        }
        catch
        {
            TryDeleteFolder(staging);
            throw;
        }
    }

    /// <summary>
    /// Extracts every file, without the single top folder the release zip has ("Snaplume/..."). An entry that would
    /// land outside <paramref name="destination"/> ("../x", absolute paths) makes the whole zip refused.
    /// </summary>
    internal static void Extract(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (files.Count == 0) throw new InvalidDataException($"{Path.GetFileName(zip)} est vide");
        var root = CommonTopFolder(files.Select(e => e.FullName.Replace('\\', '/')));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(full);
        foreach (var entry in files)
        {
            var relative = entry.FullName.Replace('\\', '/')[root.Length..];
            var path = Path.GetFullPath(Path.Combine(full, relative.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase) || Path.IsPathRooted(relative))
                throw new InvalidDataException($"Entrée refusée dans {Path.GetFileName(zip)} : {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    /// <summary>"Snaplume/" when every entry is in that folder, else "".</summary>
    private static string CommonTopFolder(IEnumerable<string> names)
    {
        string? top = null;
        foreach (var name in names)
        {
            int slash = name.IndexOf('/');
            if (slash <= 0) return "";
            var first = name[..(slash + 1)];
            if (top == null) top = first;
            else if (top != first) return "";
        }
        return top is null or "../" or "./" ? "" : top;
    }

    /// <summary>The folder, or "name (2)", "name (3)"... when it exists.</summary>
    private static string FreeFolder(string parent, string name)
    {
        var path = Path.Combine(parent, name);
        for (int i = 2; Directory.Exists(path) || File.Exists(path); i++) path = Path.Combine(parent, $"{name} ({i})");
        return path;
    }

    private static void TryDeleteFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression d'une extraction incomplète", ex);
        }
    }
}
