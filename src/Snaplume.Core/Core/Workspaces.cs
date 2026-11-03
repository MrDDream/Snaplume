using System.Globalization;
using Avalonia.Threading;
using Snaplume.Editor;
using Snaplume.Imaging;

namespace Snaplume.Core;

/// <summary>
/// Workspaces = the root capture folder ("Base"), its direct sub-folders, and workspaces whose folder the user chose
/// anywhere else (<see cref="AppSettings.WorkspaceFolders"/>: another drive, a network share...). Captures and videos
/// are saved into the active one. Names are folder names for the sub-folders, free names for the others; "" is Base.
/// Methods are meant to be called on the UI thread, except the overloads of <see cref="MoveFiles(IEnumerable{string}, ResolvedWorkspace)"/>
/// and <see cref="MoveCaptures(ResolvedWorkspace, string)"/> that take a workspace resolved beforehand.
/// </summary>
public static class Workspaces
{
    /// <summary>Root capture folder (<see cref="AppSettings.WorkspaceFolder"/>).</summary>
    public static string Root => AppServices.Settings.WorkspaceFolder;

    private static Dictionary<string, string> CustomFolders => AppServices.Settings.WorkspaceFolders;

    /// <summary>
    /// Active workspace name; "" for Base, also when the chosen folder no longer exists or cannot be reached (an
    /// unplugged drive: see <see cref="UnavailableActive"/>).
    /// </summary>
    public static string ActiveName
    {
        get
        {
            var name = AppServices.Settings.ActiveWorkspace;
            return name.Length > 0 && IsPlainName(name) && !name.StartsWith('.') && IsAvailable(name) ? name : "";
        }
    }

    /// <summary>
    /// The workspace chosen whose own folder (chosen by the user) cannot be reached: captures go to Base until it is
    /// back. Null when there is none.
    /// </summary>
    public static string? UnavailableActive
    {
        get
        {
            var name = AppServices.Settings.ActiveWorkspace;
            return IsCustom(name) && !IsAvailable(name) ? name : null;
        }
    }

    /// <summary>Full path of the active workspace, where new captures and videos go.</summary>
    public static string ActiveFolder => FolderOf(ActiveName);

    /// <summary>Raised (on the UI thread) when the list or the active workspace changes.</summary>
    public static event Action? Changed;

    /// <summary>Full path of a workspace: <see cref="Root"/> for "", the folder chosen for it, else its sub-folder of the root.</summary>
    /// <exception cref="ArgumentException">The name is not a plain folder name.</exception>
    public static string FolderOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return Root;
        if (!IsPlainName(name)) throw new ArgumentException(Loc.T("Workspaces.InvalidName"), nameof(name));
        return CustomFolders.TryGetValue(name, out var folder) ? folder : DefaultFolderOf(name);
    }

    /// <summary>The folder of a workspace without a folder of its own: its sub-folder of the root.</summary>
    public static string DefaultFolderOf(string name) => Path.Combine(Root, name);

    /// <summary>The workspace has a folder chosen by the user (anywhere, not a sub-folder of the root by default).</summary>
    public static bool IsCustom(string name) => !string.IsNullOrEmpty(name) && CustomFolders.ContainsKey(name);

    /// <summary>
    /// The folder of the workspace exists (a drive may be unplugged, a network share offline). The answer for a folder
    /// of the user's choice is kept a few seconds: an offline share may take long to answer, and this is asked often.
    /// </summary>
    public static bool IsAvailable(string name)
    {
        try
        {
            var folder = FolderOf(name);
            if (!IsCustom(name)) return Directory.Exists(folder);
            lock (Availability)
            {
                if (Availability.TryGetValue(folder, out var known) && DateTime.UtcNow - known.Checked < AvailabilityLifetime) return known.Exists;
            }
            bool exists = Directory.Exists(folder);
            lock (Availability) Availability[folder] = (DateTime.UtcNow, exists);
            return exists;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Folders of the user's choice already looked for, with when.</summary>
    private static readonly Dictionary<string, (DateTime Checked, bool Exists)> Availability = new();
    private static readonly TimeSpan AvailabilityLifetime = TimeSpan.FromSeconds(3);

    /// <summary>Looks for the folders of the user's choice again at the next question (a folder was created or chosen).</summary>
    public static void ForgetAvailability()
    {
        lock (Availability) Availability.Clear();
    }

    /// <summary>Name shown to the user: "Base" (translated) for the root, else the name.</summary>
    public static string DisplayName(string name) => string.IsNullOrEmpty(name) ? Loc.T("Workspaces.Base") : name;

    /// <summary>
    /// "" (Base) first, then in alphabetical order the visible direct sub-folders of the root and the workspaces with a
    /// folder of their own (listed even when that folder is missing). A sub-folder that is also the folder of another
    /// workspace, or has the name of one, is listed once.
    /// </summary>
    public static IReadOnlyList<string> List()
    {
        var names = new List<string>(CustomFolders.Keys);
        var customPaths = CustomFolders.Values.Select(PathRules.Normalize).ToHashSet(PathRules.Comparer);
        try
        {
            var root = new DirectoryInfo(Root);
            if (root.Exists)
            {
                foreach (var folder in root.EnumerateDirectories())
                {
                    // Hidden and system folders (annotations ".snaplume", "$RECYCLE.BIN"...) are not workspaces.
                    if ((folder.Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0) continue;
                    if (folder.Name.StartsWith('.')) continue;
                    if (CustomFolders.ContainsKey(folder.Name) || customPaths.Contains(PathRules.Normalize(folder.FullName))) continue;
                    names.Add(folder.Name);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des espaces de travail", ex);
        }
        names.Sort(StringComparer.CurrentCultureIgnoreCase);
        names.Insert(0, "");
        return names;
    }

    /// <summary>Makes a workspace the active one (remembered in the settings); Base when its folder is missing.</summary>
    public static void Select(string name)
    {
        name ??= "";
        if (name.Length > 0 && !IsAvailable(name)) name = "";
        var settings = AppServices.Settings;
        if (settings.ActiveWorkspace != name)
        {
            settings.ActiveWorkspace = name;
            SaveSettings();
        }
        RaiseChanged();
    }

    /// <summary>
    /// Creates a workspace and makes it the active one: a new sub-folder of the root, or a workspace whose captures go
    /// to <paramref name="folder"/> (created if needed; an existing folder keeps its files).
    /// </summary>
    /// <returns>The final name: invalid characters replaced, " 2", " 3"... added if it already exists.</returns>
    /// <exception cref="ArgumentException">Empty or invalid name, or a folder that cannot be used (message ready to show).</exception>
    /// <exception cref="IOException">The folder could not be created.</exception>
    public static string Create(string name, string? folder = null)
    {
        var clean = Clean(name);
        var final = UniqueName(clean, ignore: null);
        if (folder == null)
        {
            Directory.CreateDirectory(DefaultFolderOf(final));
        }
        else
        {
            var full = CheckFolder(final, folder);
            Directory.CreateDirectory(full);
            CustomFolders[final] = full;
            SaveSettings();
        }
        Select(final);
        return final;
    }

    /// <summary>
    /// Renames a workspace; the active workspace follows it. A sub-folder of the root is renamed; a workspace with a
    /// folder of its own only changes its name (its folder is left as it is).
    /// </summary>
    /// <returns>The final name (see <see cref="Create"/>).</returns>
    /// <exception cref="InvalidOperationException">Base cannot be renamed.</exception>
    /// <exception cref="ArgumentException">Empty or invalid new name.</exception>
    /// <exception cref="IOException">The folder could not be renamed (e.g. a file in it is open).</exception>
    public static string Rename(string oldName, string newName)
    {
        if (string.IsNullOrEmpty(oldName)) throw new InvalidOperationException(Loc.T("Workspaces.BaseLocked"));
        var source = FolderOf(oldName);
        var clean = Clean(newName);
        if (clean == oldName) return oldName;

        var final = UniqueName(clean, ignore: oldName);
        if (CustomFolders.Remove(oldName, out var custom))
        {
            CustomFolders[final] = custom;
        }
        else
        {
            var target = DefaultFolderOf(final);
            if (string.Equals(final, oldName, StringComparison.OrdinalIgnoreCase))
            {
                // Only the case changes: go through a temporary name, back to the old one if the second move fails.
                var temp = Path.Combine(Root, "." + Guid.NewGuid().ToString("N"));
                Directory.Move(source, temp);
                try
                {
                    Directory.Move(temp, target);
                }
                catch
                {
                    try
                    {
                        Directory.Move(temp, source);
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Write($"Retour au nom {oldName} après un renommage échoué ({temp})", ex);
                    }
                    throw;
                }
            }
            else
            {
                Directory.Move(source, target);
            }
        }

        var settings = AppServices.Settings;
        if (string.Equals(settings.ActiveWorkspace, oldName, StringComparison.OrdinalIgnoreCase)) settings.ActiveWorkspace = final;
        SaveSettings();
        RaiseChanged();
        return final;
    }

    /// <summary>
    /// Deletes a workspace; Base becomes active if it was. A sub-folder of the root goes to the Recycle Bin with its
    /// captures. A workspace with a folder of its own is only removed from the list: its folder and files are never
    /// touched.
    /// </summary>
    /// <returns>False if nothing was deleted: Base, or the deletion failed or was cancelled.</returns>
    /// <exception cref="ArgumentException">
    /// The sub-folder holds the folder of a workspace of the user's choice (<see cref="NestedCustomWorkspaces"/>): it
    /// would go to the trash with it (message ready to show).
    /// </exception>
    public static bool Delete(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        if (CustomFolders.Remove(name))
        {
            SaveSettings();
        }
        else
        {
            var nested = NestedCustomWorkspaces(name);
            if (nested.Count > 0)
                throw new ArgumentException(Loc.T("Workspaces.Delete.Nested", DisplayName(name), string.Join(", ", nested)), nameof(name));
            var folder = FolderOf(name);
            if (Directory.Exists(folder) && !FileOps.MoveToTrash(new[] { folder })) return false;
        }
        if (string.Equals(AppServices.Settings.ActiveWorkspace, name, StringComparison.OrdinalIgnoreCase)) Select("");
        else RaiseChanged();
        return true;
    }

    /// <summary>
    /// The folder a workspace may take, as a full path; refused (message ready to show) when it is not an absolute path,
    /// is the root itself, already the folder of another workspace, or inside or around the folder of another one: the
    /// root may only hold it as a direct, visible sub-folder (the folder of a workspace of its own).
    /// </summary>
    /// <param name="name">The workspace that would take it (its own folder is accepted).</param>
    /// <exception cref="ArgumentException">The folder cannot be used.</exception>
    public static string CheckFolder(string name, string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder.Trim()))
            throw new ArgumentException(Loc.T("Workspaces.Folder.Invalid"), nameof(folder));
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder.Trim()));
        if (SamePath(full, Root)) throw new ArgumentException(Loc.T("Workspaces.Folder.IsBase"), nameof(folder));
        // Around the root, or deeper in it than a workspace (in another one's sub-folder, in ".snaplume"...).
        if (PathRules.IsInside(Root, full)
            || (PathRules.IsInside(full, Root) && (!SamePath(Path.GetDirectoryName(full)!, Root) || Path.GetFileName(full).StartsWith('.'))))
            throw new ArgumentException(Loc.T("Workspaces.Folder.Nested", DisplayName("")), nameof(folder));
        foreach (var other in List().Where(n => n.Length > 0 && !string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
        {
            var otherFolder = FolderOf(other);
            if (SamePath(full, otherFolder)) throw new ArgumentException(Loc.T("Workspaces.Folder.Used", other), nameof(folder));
            if (PathRules.IsInside(full, otherFolder) || PathRules.IsInside(otherFolder, full))
                throw new ArgumentException(Loc.T("Workspaces.Folder.Nested", other), nameof(folder));
        }
        return full;
    }

    /// <summary>
    /// The workspaces of the user's choice whose folder is that of <paramref name="name"/> or inside it: deleting the
    /// folder of <paramref name="name"/> would delete theirs too.
    /// </summary>
    public static IReadOnlyList<string> NestedCustomWorkspaces(string name)
    {
        var folder = FolderOf(name);
        return CustomFolders
            .Where(pair => !string.Equals(pair.Key, name, StringComparison.OrdinalIgnoreCase)
                           && (SamePath(pair.Value, folder) || PathRules.IsInside(pair.Value, folder)))
            .Select(pair => pair.Key)
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Gives a workspace another folder (<paramref name="folder"/>), or its default sub-folder of the root back (null).
    /// The captures stay where they are: <see cref="MoveCaptures"/> moves them first if wanted.
    /// </summary>
    /// <exception cref="InvalidOperationException">Base: its folder is the root, chosen in the settings.</exception>
    /// <exception cref="ArgumentException">The folder cannot be used (see <see cref="CheckFolder"/>).</exception>
    /// <exception cref="IOException">The folder could not be created.</exception>
    public static void SetFolder(string name, string? folder)
    {
        if (string.IsNullOrEmpty(name)) throw new InvalidOperationException(Loc.T("Workspaces.BaseLocked"));
        if (!IsPlainName(name)) throw new ArgumentException(Loc.T("Workspaces.InvalidName"), nameof(name));
        var full = CheckFolder(name, folder ?? DefaultFolderOf(name));
        Directory.CreateDirectory(full);
        // The sub-folder of the root under the same name is the default: no need to remember it.
        if (SamePath(full, DefaultFolderOf(name))) CustomFolders.Remove(name);
        else CustomFolders[name] = full;
        SaveSettings();
        RaiseChanged();
    }

    /// <summary>Number of captures directly in a folder (0 when it is missing).</summary>
    public static int CountCaptures(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.EnumerateFiles(folder).Count(ImageIO.IsCapture) : 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Contenu d'un espace de travail", ex);
            return 0;
        }
    }

    /// <summary>
    /// Moves the captures of a workspace's folder (and their annotations) into another folder, on another drive too,
    /// renaming them when the name is taken; to be called before <see cref="SetFolder"/>. Other files are left. Its
    /// default sub-folder of the root, left empty, is removed so that it does not come back as a workspace; a folder of
    /// the user's choice never is. Reads the settings: on another thread, use
    /// <see cref="MoveCaptures(ResolvedWorkspace, string)"/>.
    /// </summary>
    /// <returns>Captures moved, captures that could not be moved, and the first error.</returns>
    public static (int Moved, int Failed, Exception? FirstError) MoveCaptures(string name, string toFolder) =>
        MoveCaptures(Resolve(name), toFolder);

    /// <summary>
    /// <see cref="MoveCaptures(string, string)"/> for a workspace resolved beforehand (<see cref="Resolve"/>). Can run on
    /// any thread.
    /// </summary>
    public static (int Moved, int Failed, Exception? FirstError) MoveCaptures(ResolvedWorkspace from, string toFolder)
    {
        var fromFolder = from.Folder;
        if (!Directory.Exists(fromFolder) || SamePath(fromFolder, toFolder)) return (0, 0, null);
        SidecarFiles.MigrateLegacy(fromFolder);
        var result = MoveInto(Directory.EnumerateFiles(fromFolder).Where(ImageIO.IsCapture).ToList(), toFolder);
        if (!from.IsCustom) RemoveIfEmpty(fromFolder);
        return result;
    }

    /// <summary>
    /// Moves captures (and their annotations) into another workspace, renaming them when the name is taken there. Files
    /// already in that workspace are left alone. Its folder may be on another drive. Reads the settings: on another
    /// thread, use <see cref="MoveFiles(IEnumerable{string}, ResolvedWorkspace)"/>.
    /// </summary>
    /// <returns>A summary to show, e.g. "3 captures moved to « Project »".</returns>
    /// <exception cref="IOException">No file could be moved (message of the first error), or the folder of the workspace is missing.</exception>
    public static string MoveFiles(IEnumerable<string> paths, string targetName) => MoveFiles(paths, Resolve(targetName));

    /// <summary>
    /// <see cref="MoveFiles(IEnumerable{string}, string)"/> into a workspace resolved beforehand (<see cref="Resolve"/>).
    /// Can run on any thread.
    /// </summary>
    public static string MoveFiles(IEnumerable<string> paths, ResolvedWorkspace target)
    {
        var display = DisplayName(target.Name);
        if (target.IsCustom && !Directory.Exists(target.Folder))
            throw new IOException(Loc.T("Workspaces.Folder.Missing", display, target.Folder));
        var (moved, failed, firstError) = MoveInto(paths, target.Folder);
        if (moved == 0 && firstError != null) throw new IOException(firstError.Message, firstError);

        var summary = Loc.Plural(moved, "Workspaces.MovedOne", "Workspaces.MovedMany", display);
        if (failed > 0) summary += Environment.NewLine + Loc.T("Workspaces.MoveSomeFailed", failed, firstError!.Message);
        return summary;
    }

    /// <summary>
    /// A workspace with its folder as the settings say now, for work done on another thread (the settings belong to the
    /// UI thread): see <see cref="MoveCaptures(ResolvedWorkspace, string)"/> and
    /// <see cref="MoveFiles(IEnumerable{string}, ResolvedWorkspace)"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The name is not a plain folder name.</exception>
    public static ResolvedWorkspace Resolve(string name)
    {
        name ??= "";
        return new ResolvedWorkspace(name, FolderOf(name), IsCustom(name));
    }

    private static (int Moved, int Failed, Exception? FirstError) MoveInto(IEnumerable<string> paths, string target)
    {
        Directory.CreateDirectory(target);
        int moved = 0, failed = 0;
        Exception? firstError = null;
        foreach (var path in paths)
        {
            try
            {
                var source = Path.GetFullPath(path);
                if (!File.Exists(source)) continue;
                if (SamePath(Path.GetDirectoryName(source)!, target)) continue;
                var destination = FileOps.UniquePath(target, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source));
                FileOps.MoveFile(source, destination);
                moved++;
                SidecarFiles.Move(source, destination);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Déplacement vers un espace de travail", ex);
                failed++;
                firstError ??= ex;
            }
        }
        return (moved, failed, firstError);
    }

    /// <summary>
    /// Removes a folder left empty (its empty annotation folder aside). Nothing is deleted recursively: a file that
    /// appears meanwhile keeps the folder.
    /// </summary>
    private static void RemoveIfEmpty(string folder)
    {
        try
        {
            var sidecars = Path.Combine(folder, SidecarFiles.Folder);
            if (Directory.EnumerateFileSystemEntries(folder).Any(entry => !SamePath(entry, sidecars))) return;
            if (Directory.Exists(sidecars))
            {
                if (Directory.EnumerateFileSystemEntries(sidecars).Any()) return;
                Directory.Delete(sidecars, recursive: false);
            }
            Directory.Delete(folder, recursive: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression d'un dossier vide", ex);
        }
    }

    /// <summary>Called when the root folder changes (settings).</summary>
    internal static void OnRootChanged() => RaiseChanged();

    /// <summary>A single folder name: no separators, not "." or "..".</summary>
    internal static bool IsPlainName(string name) =>
        name.Length > 0 && name.Trim() == name && name is not "." and not ".."
        && !FileOps.HasInvalidNameChars(name);

    /// <inheritdoc cref="PathRules.SamePath"/>
    internal static bool SamePath(string a, string b) => PathRules.SamePath(a, b);

    /// <summary>Folder name typed by the user, made valid.</summary>
    private static string Clean(string? name)
    {
        // Windows drops trailing dots and spaces from folder names (kept the same everywhere: workspaces may be shared).
        var clean = FileOps.SanitizeFileName(name ?? "").Trim().TrimEnd('.', ' ');
        // A leading dot would make it a hidden, unlisted folder.
        if (clean.Length == 0 || clean.StartsWith('.') || !IsPlainName(clean))
            throw new ArgumentException(Loc.T("Workspaces.InvalidName"), nameof(name));
        return clean;
    }

    /// <summary>The name, or "name 2", "name 3"... if a workspace, a file or a folder of the root already has it.</summary>
    /// <param name="ignore">Existing workspace allowed to have the name (the one being renamed).</param>
    private static string UniqueName(string name, string? ignore)
    {
        var final = name;
        for (int i = 2; IsTaken(final, ignore); i++) final = name + " " + i.ToString(CultureInfo.InvariantCulture);
        return final;
    }

    private static bool IsTaken(string name, string? ignore)
    {
        var path = DefaultFolderOf(name);
        if (ignore != null && string.Equals(name, ignore, StringComparison.OrdinalIgnoreCase))
        {
            // Only the case changes. Linux tells folders apart by case: another one may have exactly the new name.
            return !OperatingSystem.IsWindows() && !string.Equals(name, ignore, StringComparison.Ordinal) && !IsCustom(ignore)
                   && (Directory.Exists(path) || File.Exists(path));
        }
        if (CustomFolders.ContainsKey(name)) return true;
        return Directory.Exists(path) || File.Exists(path);
    }

    private static void SaveSettings()
    {
        try
        {
            AppServices.Settings.Save();
        }
        catch (Exception ex)
        {
            // The choice still applies until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    private static void RaiseChanged()
    {
        ForgetAvailability();
        if (Dispatcher.UIThread.CheckAccess()) Changed?.Invoke();
        else Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}

/// <summary>A workspace and its folder, read beforehand (see <see cref="Workspaces.Resolve"/>).</summary>
/// <param name="Name">"" for Base.</param>
/// <param name="IsCustom">Its folder was chosen by the user (<see cref="Workspaces.IsCustom"/>).</param>
public sealed record ResolvedWorkspace(string Name, string Folder, bool IsCustom);
