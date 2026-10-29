using System.Windows;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>Workspace (sub-folder) commands shared by the workspace window and the quick panel.</summary>
public static class WorkspaceCommands
{
    public static bool IsActive(string name) => string.Equals(name, Workspaces.ActiveName, StringComparison.OrdinalIgnoreCase);

    public static void Select(Window owner, string name)
    {
        if (IsActive(name)) return;
        try
        {
            Workspaces.Select(name);
        }
        catch (Exception ex)
        {
            ShowError(owner, Loc.T("Workspaces.SelectFailed"), ex);
        }
    }

    /// <summary>Asks for a name, then creates the workspace and makes it the active one.</summary>
    public static void Create(Window owner)
    {
        var title = Loc.T("Workspaces.New.Title");
        var dialog = new InputDialog(owner, title, Loc.T("Workspaces.New.Label"), "", isFileName: false);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        try
        {
            Workspaces.Create(dialog.Value.Trim());
        }
        catch (ArgumentException ex)
        {
            // Invalid or already used name.
            MessageBox.Show(owner, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError(owner, Loc.T("Workspaces.New.Failed"), ex);
        }
    }

    public static void Rename(Window owner, string name)
    {
        if (name.Length == 0) return; // "Base" is the root folder itself
        var title = Loc.T("Workspaces.Rename.Title");
        if (CaptureGallery.IsAnyOpenIn(Workspaces.FolderOf(name)))
        {
            MessageBox.Show(owner, Loc.T("Workspaces.CloseViewers"), title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new InputDialog(owner, title, Loc.T("Workspaces.Rename.Label"), name, isFileName: false);
        if (dialog.ShowDialog() != true || string.IsNullOrWhiteSpace(dialog.Value)) return;
        var newName = dialog.Value.Trim();
        if (string.Equals(newName, name, StringComparison.Ordinal)) return;
        try
        {
            Workspaces.Rename(name, newName);
        }
        catch (ArgumentException ex)
        {
            MessageBox.Show(owner, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError(owner, Loc.T("Workspaces.Rename.Failed"), ex);
        }
    }

    /// <summary>Sends the workspace and its captures to the Recycle Bin, after confirmation.</summary>
    public static void Delete(Window owner, string name)
    {
        if (name.Length == 0) return;
        var title = Loc.T("Workspaces.Delete.Title");
        if (CaptureGallery.IsAnyOpenIn(Workspaces.FolderOf(name)))
        {
            MessageBox.Show(owner, Loc.T("Workspaces.CloseViewers"), title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        // The whole folder goes to the Recycle Bin: say so when it also holds files that are not captures.
        int others = CountOtherFiles(Workspaces.FolderOf(name), out bool more);
        var confirm = others == 0
            ? Loc.T("Workspaces.Delete.Confirm", Workspaces.DisplayName(name))
            : Loc.T("Workspaces.Delete.ConfirmOtherFiles", Workspaces.DisplayName(name),
                others.ToString("N0", Loc.Culture) + (more ? "+" : ""));
        if (MessageBox.Show(owner, confirm, title,
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) != MessageBoxResult.Yes)
            return;
        try
        {
            if (!Workspaces.Delete(name))
                MessageBox.Show(owner, Loc.T("Workspaces.Delete.Failed"), title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            ShowError(owner, Loc.T("Workspaces.Delete.Failed"), ex);
        }
    }

    /// <summary>Most files counted before giving up (a huge folder must not freeze the confirmation).</summary>
    private const int MaxCounted = 10_000;

    /// <summary>
    /// Files of the folder and its sub-folders that are not captures (documents, archives...), leaving out
    /// Snaplume's own annotation folders and the files Windows adds (desktop.ini, Thumbs.db).
    /// </summary>
    /// <param name="more">The count stopped at <see cref="MaxCounted"/>.</param>
    private static int CountOtherFiles(string folder, out bool more)
    {
        more = false;
        int count = 0;
        try
        {
            var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = 0 };
            foreach (var path in Directory.EnumerateFiles(folder, "*", options))
            {
                if (ImageIO.IsCapture(path) || IsSnaplumeOrWindowsFile(folder, path)) continue;
                if (++count < MaxCounted) continue;
                more = true;
                break;
            }
        }
        catch (Exception ex)
        {
            // Not readable: the usual confirmation is shown.
            ErrorLog.Write("Contenu d'un espace de travail", ex);
        }
        return count;
    }

    private static bool IsSnaplumeOrWindowsFile(string folder, string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase))
            return true;
        // Annotations kept next to the captures (".snaplume", or ".librecapture" from older versions).
        var relative = Path.GetRelativePath(folder, path);
        return relative.Split(Path.DirectorySeparatorChar)[..^1]
            .Any(part => part.Equals(".snaplume", StringComparison.OrdinalIgnoreCase) || part.Equals(".librecapture", StringComparison.OrdinalIgnoreCase));
    }

    public static void OpenFolder(Window owner, string name)
    {
        try
        {
            FileOps.OpenFolder(Workspaces.FolderOf(name));
        }
        catch (Exception ex)
        {
            ShowError(owner, Loc.T("Workspace.OpenFolderFailed"), ex);
        }
    }

    public static void ShowError(Window owner, string message, Exception ex)
    {
        ErrorLog.Write(message, ex);
        MessageBox.Show(owner, Loc.T("Gallery.Error", message, ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
