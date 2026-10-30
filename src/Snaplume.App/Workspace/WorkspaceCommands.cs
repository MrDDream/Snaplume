using Avalonia.Controls;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Ui;

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
            _ = ShowErrorAsync(owner, Loc.T("Workspaces.SelectFailed"), ex);
        }
    }

    /// <summary>Asks for a name, then creates the workspace and makes it the active one.</summary>
    public static async Task CreateAsync(Window owner)
    {
        var title = Loc.T("Workspaces.New.Title");
        var value = await InputDialog.AskAsync(owner, title, Loc.T("Workspaces.New.Label"), "", isFileName: false);
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            Workspaces.Create(value.Trim());
        }
        catch (ArgumentException ex)
        {
            // Invalid or already used name.
            await Dialogs.ShowAsync(owner, ex.Message, title, DialogButtons.Ok, DialogIcon.Warning);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.New.Failed"), ex);
        }
    }

    public static async Task RenameAsync(Window owner, string name)
    {
        if (name.Length == 0) return; // "Base" is the root folder itself
        var title = Loc.T("Workspaces.Rename.Title");
        if (CaptureGallery.IsAnyOpenIn(Workspaces.FolderOf(name)))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Workspaces.CloseViewers"), title, DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        var value = await InputDialog.AskAsync(owner, title, Loc.T("Workspaces.Rename.Label"), name, isFileName: false);
        if (string.IsNullOrWhiteSpace(value)) return;
        var newName = value.Trim();
        if (string.Equals(newName, name, StringComparison.Ordinal)) return;
        try
        {
            Workspaces.Rename(name, newName);
        }
        catch (ArgumentException ex)
        {
            await Dialogs.ShowAsync(owner, ex.Message, title, DialogButtons.Ok, DialogIcon.Warning);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.Rename.Failed"), ex);
        }
    }

    /// <summary>Sends the workspace and its captures to the trash, after confirmation (default answer: No).</summary>
    public static async Task DeleteAsync(Window owner, string name)
    {
        if (name.Length == 0) return;
        var title = Loc.T("Workspaces.Delete.Title");
        if (CaptureGallery.IsAnyOpenIn(Workspaces.FolderOf(name)))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Workspaces.CloseViewers"), title, DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        // The whole folder goes to the trash: say so when it also holds files that are not captures.
        var folder = Workspaces.FolderOf(name);
        var (others, more) = await Task.Run(() => (CountOtherFiles(folder, out bool m), m));
        var confirm = others == 0
            ? Loc.T("Workspaces.Delete.Confirm", Workspaces.DisplayName(name))
            : Loc.T("Workspaces.Delete.ConfirmOtherFiles", Workspaces.DisplayName(name),
                others.ToString("N0", Loc.Culture) + (more ? "+" : ""));
        if (await Dialogs.ShowAsync(owner, confirm, title, DialogButtons.YesNo, DialogIcon.Warning, DialogResult.No) != DialogResult.Yes)
            return;
        try
        {
            if (!Workspaces.Delete(name))
                await Dialogs.ShowAsync(owner, Loc.T("Workspaces.Delete.Failed"), title, DialogButtons.Ok, DialogIcon.Warning);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.Delete.Failed"), ex);
        }
    }

    /// <summary>Most files counted before giving up (a huge folder must not delay the confirmation).</summary>
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
                if (ImageIO.IsCapture(path) || IsSnaplumeOrSystemFile(folder, path)) continue;
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

    private static bool IsSnaplumeOrSystemFile(string folder, string path)
    {
        var name = Path.GetFileName(path);
        if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase) || name.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
            || name.Equals(".directory", StringComparison.Ordinal) || name.Equals(".DS_Store", StringComparison.Ordinal))
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
            _ = ShowErrorAsync(owner, Loc.T("Workspace.OpenFolderFailed"), ex);
        }
    }

    public static Task ShowErrorAsync(Window? owner, string message, Exception ex)
    {
        ErrorLog.Write(message, ex);
        return Dialogs.ShowAsync(owner, Loc.T("Gallery.Error", message, ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
    }
}
