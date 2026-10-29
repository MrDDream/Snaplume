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
        if (MessageBox.Show(owner, Loc.T("Workspaces.Delete.Confirm", Workspaces.DisplayName(name)), title,
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
