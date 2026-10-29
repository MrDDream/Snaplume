using System.Globalization;
using System.Windows;
using Snaplume.Editor;

namespace Snaplume.Core;

/// <summary>
/// Workspaces = the root capture folder ("Base") and its direct sub-folders. Captures and videos are saved into the active one.
/// Names are folder names; "" is Base. Methods are meant to be called on the UI thread.
/// </summary>
public static class Workspaces
{
    /// <summary>Root capture folder (<see cref="AppSettings.WorkspaceFolder"/>).</summary>
    public static string Root => App.Instance.Settings.WorkspaceFolder;

    /// <summary>Active workspace name; "" for Base, also when the chosen sub-folder no longer exists.</summary>
    public static string ActiveName
    {
        get
        {
            var name = App.Instance.Settings.ActiveWorkspace;
            return name.Length > 0 && IsPlainName(name) && Directory.Exists(Path.Combine(Root, name)) ? name : "";
        }
    }

    /// <summary>Full path of the active workspace, where new captures and videos go.</summary>
    public static string ActiveFolder => FolderOf(ActiveName);

    /// <summary>Raised (on the UI thread) when the list or the active workspace changes.</summary>
    public static event Action? Changed;

    /// <summary>Full path of a workspace ("" → <see cref="Root"/>).</summary>
    /// <exception cref="ArgumentException">The name is not a plain folder name.</exception>
    public static string FolderOf(string name)
    {
        if (string.IsNullOrEmpty(name)) return Root;
        if (!IsPlainName(name)) throw new ArgumentException(Loc.T("Workspaces.InvalidName"), nameof(name));
        return Path.Combine(Root, name);
    }

    /// <summary>Name shown to the user: "Base" (translated) for the root, else the folder name.</summary>
    public static string DisplayName(string name) => string.IsNullOrEmpty(name) ? Loc.T("Workspaces.Base") : name;

    /// <summary>"" (Base) first, then the visible direct sub-folders in alphabetical order.</summary>
    public static IReadOnlyList<string> List()
    {
        var names = new List<string>();
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

    /// <summary>Makes a workspace the active one (remembered in the settings).</summary>
    public static void Select(string name)
    {
        name ??= "";
        if (name.Length > 0 && !Directory.Exists(FolderOf(name))) name = "";
        var settings = App.Instance.Settings;
        if (settings.ActiveWorkspace != name)
        {
            settings.ActiveWorkspace = name;
            SaveSettings();
        }
        RaiseChanged();
    }

    /// <summary>Creates a sub-folder and makes it the active workspace.</summary>
    /// <returns>The final name: invalid characters replaced, " 2", " 3"... added if it already exists.</returns>
    /// <exception cref="ArgumentException">Empty or invalid name (message ready to show).</exception>
    /// <exception cref="IOException">The folder could not be created.</exception>
    public static string Create(string name)
    {
        var clean = Clean(name);
        var final = UniqueName(clean, ignore: null);
        Directory.CreateDirectory(Path.Combine(Root, final));
        Select(final);
        return final;
    }

    /// <summary>Renames a workspace folder; the active workspace follows it.</summary>
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
        var target = Path.Combine(Root, final);
        if (string.Equals(final, oldName, StringComparison.OrdinalIgnoreCase))
        {
            // Only the case changes: go through a temporary name.
            var temp = Path.Combine(Root, "." + Guid.NewGuid().ToString("N"));
            Directory.Move(source, temp);
            Directory.Move(temp, target);
        }
        else
        {
            Directory.Move(source, target);
        }

        var settings = App.Instance.Settings;
        if (string.Equals(settings.ActiveWorkspace, oldName, StringComparison.OrdinalIgnoreCase))
        {
            settings.ActiveWorkspace = final;
            SaveSettings();
        }
        RaiseChanged();
        return final;
    }

    /// <summary>Sends a workspace folder and its captures to the Recycle Bin; Base becomes active if it was.</summary>
    /// <returns>False if nothing was deleted: Base, or the deletion failed or was cancelled.</returns>
    public static bool Delete(string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        var folder = FolderOf(name);
        if (Directory.Exists(folder) && !FileOps.MoveToRecycleBin(new[] { folder })) return false;
        if (string.Equals(App.Instance.Settings.ActiveWorkspace, name, StringComparison.OrdinalIgnoreCase)) Select("");
        else RaiseChanged();
        return true;
    }

    /// <summary>
    /// Moves captures (and their annotations) into another workspace, renaming them when the
    /// name is taken there. Files already in that workspace are left alone.
    /// </summary>
    /// <returns>A summary to show, e.g. "3 captures moved to « Project »".</returns>
    /// <exception cref="IOException">No file could be moved (message of the first error).</exception>
    public static string MoveFiles(IEnumerable<string> paths, string targetName)
    {
        var target = FolderOf(targetName);
        Directory.CreateDirectory(target);
        int moved = 0, failed = 0;
        Exception? firstError = null;
        foreach (var path in paths)
        {
            try
            {
                var source = Path.GetFullPath(path);
                if (!File.Exists(source)) continue;
                if (string.Equals(Path.GetDirectoryName(source), Path.TrimEndingDirectorySeparator(Path.GetFullPath(target)), StringComparison.OrdinalIgnoreCase))
                    continue;
                var destination = FileOps.UniquePath(target, Path.GetFileNameWithoutExtension(source), Path.GetExtension(source));
                File.Move(source, destination);
                moved++;
                MoveSidecar(source, destination);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Déplacement vers un espace de travail", ex);
                failed++;
                firstError ??= ex;
            }
        }
        if (moved == 0 && firstError != null) throw new IOException(firstError.Message, firstError);

        var display = DisplayName(targetName);
        var summary = Loc.T(moved == 1 || (moved == 0 && Loc.Code == "fr") ? "Workspaces.MovedOne" : "Workspaces.MovedMany", moved, display);
        if (failed > 0) summary += Environment.NewLine + Loc.T("Workspaces.MoveSomeFailed", failed, firstError!.Message);
        return summary;
    }

    /// <summary>The annotations (".snaplume" folder next to the file) follow the capture.</summary>
    private static void MoveSidecar(string oldFile, string newFile)
    {
        try
        {
            var (oldBase, oldJson) = EditorDocument.SidecarPaths(oldFile);
            if (!File.Exists(oldBase) && !File.Exists(oldJson)) return;
            var (newBase, newJson) = EditorDocument.SidecarPaths(newFile);
            var folder = Directory.CreateDirectory(Path.GetDirectoryName(newJson)!);
            folder.Attributes |= FileAttributes.Hidden;
            if (File.Exists(oldBase)) File.Move(oldBase, newBase, overwrite: true);
            if (File.Exists(oldJson)) File.Move(oldJson, newJson, overwrite: true);
        }
        catch (Exception ex)
        {
            // The capture itself was moved: only its editable annotations are lost.
            ErrorLog.Write("Déplacement des annotations", ex);
        }
    }

    /// <summary>Called when the root folder changes (settings).</summary>
    internal static void OnRootChanged() => RaiseChanged();

    /// <summary>A single folder name: no separators, not "." or "..".</summary>
    internal static bool IsPlainName(string name) =>
        name.Length > 0 && name.Trim() == name && name is not "." and not ".."
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    /// <summary>Folder name typed by the user, made valid.</summary>
    private static string Clean(string? name)
    {
        // Windows drops trailing dots and spaces from folder names.
        var clean = FileOps.SanitizeFileName(name ?? "").Trim().TrimEnd('.', ' ');
        // A leading dot would make it a hidden, unlisted folder.
        if (clean.Length == 0 || clean.StartsWith('.') || !IsPlainName(clean))
            throw new ArgumentException(Loc.T("Workspaces.InvalidName"), nameof(name));
        return clean;
    }

    /// <summary>The name, or "name 2", "name 3"... if a file or folder already has it.</summary>
    /// <param name="ignore">Existing workspace allowed to have the name (the one being renamed).</param>
    private static string UniqueName(string name, string? ignore)
    {
        var final = name;
        for (int i = 2; IsTaken(final, ignore); i++) final = name + " " + i.ToString(CultureInfo.InvariantCulture);
        return final;
    }

    private static bool IsTaken(string name, string? ignore)
    {
        if (ignore != null && string.Equals(name, ignore, StringComparison.OrdinalIgnoreCase)) return false;
        var path = Path.Combine(Root, name);
        return Directory.Exists(path) || File.Exists(path);
    }

    private static void SaveSettings()
    {
        try
        {
            App.Instance.Settings.Save();
        }
        catch (Exception ex)
        {
            // The choice still applies until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    private static void RaiseChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) Changed?.Invoke();
        else dispatcher.BeginInvoke(() => Changed?.Invoke());
    }
}
