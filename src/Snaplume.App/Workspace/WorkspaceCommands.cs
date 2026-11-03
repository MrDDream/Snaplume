using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Ui;

namespace Snaplume.Workspace;

/// <summary>Workspace (sub-folder) commands shared by the workspace window, the quick panel and the tray menu.</summary>
public static class WorkspaceCommands
{
    public static bool IsActive(string name) => string.Equals(name, Workspaces.ActiveName, StringComparison.OrdinalIgnoreCase);

    public static void Select(Window owner, string name)
    {
        if (IsActive(name)) return;
        if (Workspaces.IsCustom(name) && !Workspaces.IsAvailable(name))
        {
            // Its folder is on a drive or a share that cannot be reached: say so instead of switching to Base.
            _ = Dialogs.ShowAsync(owner, MissingFolderMessage(name), Loc.T("Workspaces.Folder.MissingTitle"), DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        try
        {
            Workspaces.Select(name);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync(owner, Loc.T("Workspaces.SelectFailed"), ex);
        }
    }

    /// <summary>"The folder of « X » cannot be found: path...".</summary>
    public static string MissingFolderMessage(string name) =>
        Loc.T("Workspaces.Folder.Missing", Workspaces.DisplayName(name), Workspaces.FolderOf(name));

    /// <summary>
    /// Asks for a name and where the captures go (a sub-folder of the captures folder, or a folder chosen anywhere),
    /// then creates the workspace and makes it the active one.
    /// </summary>
    /// <param name="owner">Window asking; null from the tray menu (the dialog is then centered on the screen).</param>
    public static async Task CreateAsync(Window? owner)
    {
        var title = Loc.T("Workspaces.New.Title");
        var choice = new WorkspaceFolderChoice();
        var value = await InputDialog.AskAsync(owner, title, Loc.T("Workspaces.New.Label"), "", isFileName: false, extra: choice);
        if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            Workspaces.Create(value.Trim(), choice.Folder);
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
        if (await IsBusyAsync(owner, Workspaces.FolderOf(name), title)) return;
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
        if (Workspaces.IsCustom(name))
        {
            // A folder of the user's choice: only taken out of the list, its files stay where they are.
            if (await Dialogs.ShowAsync(owner, Loc.T("Workspaces.Delete.ConfirmCustom", Workspaces.DisplayName(name), Workspaces.FolderOf(name)),
                    Loc.T("Workspaces.Menu.Remove"), DialogButtons.YesNo, DialogIcon.Question, DialogResult.No) != DialogResult.Yes)
                return;
            await DeleteConfirmedAsync(owner, name, title);
            return;
        }
        if (await IsBusyAsync(owner, Workspaces.FolderOf(name), title)) return;
        // The whole folder goes to the trash: say so when it also holds files that are not captures.
        var folder = Workspaces.FolderOf(name);
        var (others, more) = await Task.Run(() => (CountOtherFiles(folder, out bool m), m));
        var confirm = others == 0
            ? Loc.T("Workspaces.Delete.Confirm", Workspaces.DisplayName(name))
            : Loc.T("Workspaces.Delete.ConfirmOtherFiles", Workspaces.DisplayName(name),
                others.ToString("N0", Loc.Culture) + (more ? "+" : ""));
        if (await Dialogs.ShowAsync(owner, confirm, title, DialogButtons.YesNo, DialogIcon.Warning, DialogResult.No) != DialogResult.Yes)
            return;
        await DeleteConfirmedAsync(owner, name, title);
    }

    private static async Task DeleteConfirmedAsync(Window owner, string name, string title)
    {
        try
        {
            if (!Workspaces.Delete(name))
                await Dialogs.ShowAsync(owner, Loc.T("Workspaces.Delete.Failed"), title, DialogButtons.Ok, DialogIcon.Warning);
        }
        catch (ArgumentException ex)
        {
            // Its folder holds the folder of another workspace (message ready to show): nothing was deleted.
            await Dialogs.ShowAsync(owner, ex.Message, title, DialogButtons.Ok, DialogIcon.Warning);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.Delete.Failed"), ex);
        }
    }

    /// <summary>
    /// Tells the user when captures of the folder are open in an editor or a player, or a recording is being converted
    /// into it: renaming, deleting or moving the workspace must wait.
    /// </summary>
    private static async Task<bool> IsBusyAsync(Window owner, string folder, string title)
    {
        if (!CaptureGallery.IsAnyOpenIn(folder)) return false;
        var message = CaptureGallery.IsConvertingInto(folder) ? Loc.T("Workspaces.ConversionRunning") : Loc.T("Workspaces.CloseViewers");
        await Dialogs.ShowAsync(owner, message, title, DialogButtons.Ok, DialogIcon.Information);
        return true;
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

    /// <summary>
    /// "Change the folder...": picks another folder for the workspace, or (<paramref name="toDefault"/>) gives it back its
    /// sub-folder of the captures folder; asks whether its captures move there or stay where they are.
    /// </summary>
    public static async Task ChangeFolderAsync(Window owner, string name, bool toDefault = false)
    {
        if (name.Length == 0) return; // Base: its folder is chosen in the settings
        var title = Loc.T("Workspaces.Folder.Title", Workspaces.DisplayName(name));
        var current = Workspaces.FolderOf(name);
        if (await IsBusyAsync(owner, current, title)) return;
        string? target;
        try
        {
            target = toDefault ? Workspaces.DefaultFolderOf(name) : await PickFolderAsync(owner, title, current);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.Folder.PickFailed"), ex);
            return;
        }
        if (target == null || PathRules.SamePath(target, current)) return;
        try
        {
            target = Workspaces.CheckFolder(name, target);
        }
        catch (ArgumentException ex)
        {
            await Dialogs.ShowAsync(owner, ex.Message, title, DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        if (!toDefault && !await AcceptFolderAsync(owner, target, title)) return;

        // Its captures: moved along, or left where they are (the workspace then shows the new folder's files).
        var from = Workspaces.Resolve(name);
        int count = await Task.Run(() => Workspaces.CountCaptures(from.Folder));
        bool move = false;
        if (count > 0)
        {
            var answer = await Dialogs.ShowAsync(owner, Loc.T("Workspaces.Folder.MoveQuestion", count, Workspaces.DisplayName(name), current, target),
                title, DialogButtons.YesNoCancel, DialogIcon.Question, DialogResult.Yes);
            if (answer is not (DialogResult.Yes or DialogResult.No)) return;
            move = answer == DialogResult.Yes;
        }
        try
        {
            if (move)
            {
                // The workspace resolved here, on the UI thread: the settings are not read from the background.
                var folder = target;
                var (_, failed, firstError) = await Task.Run(() =>
                {
                    Directory.CreateDirectory(folder);
                    return Workspaces.MoveCaptures(from, folder);
                });
                if (failed > 0)
                    await Dialogs.ShowAsync(owner, Loc.T("Workspaces.MoveSomeFailed", failed, firstError!.Message), title, DialogButtons.Ok, DialogIcon.Warning);
            }
            Workspaces.SetFolder(name, toDefault ? null : target);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(owner, Loc.T("Workspaces.Folder.Failed"), ex);
        }
    }

    /// <summary>The folder picker of the system, opened on <paramref name="start"/> when it exists; null if cancelled.</summary>
    public static async Task<string?> PickFolderAsync(Window owner, string title, string? start)
    {
        IStorageFolder? location = null;
        try
        {
            if (start != null && Directory.Exists(start)) location = await owner.StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetFullPath(start)));
        }
        catch (Exception)
        {
            // Opened at the system's default place.
        }
        var folders = await owner.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = location,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    /// <summary>
    /// A folder chosen for a workspace: refused when it is too broad (a drive, a personal or system folder), and on a
    /// network share only once the user agreed (deleted captures do not go to the trash there).
    /// </summary>
    /// <returns>True when the folder may be used.</returns>
    public static async Task<bool> AcceptFolderAsync(Window? owner, string folder, string title)
    {
        if (PathRules.IsTooBroad(folder))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Workspaces.Folder.TooBroad", folder), title, DialogButtons.Ok, DialogIcon.Warning);
            return false;
        }
        bool network = false;
        try
        {
            // May have to ask a slow network: not on the UI thread.
            if (AppServices.HasPlatform) network = await Task.Run(() => AppServices.Platform.Shell.IsNetworkFolder(folder));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Vérification du dossier d'un espace de travail", ex);
        }
        return !network || await Dialogs.ShowAsync(owner, Loc.T("Settings.NetworkFolderWarning", folder), title,
            DialogButtons.OkCancel, DialogIcon.Warning, DialogResult.Cancel) == DialogResult.Ok;
    }

    /// <summary>Opens the folder of a workspace in the file manager.</summary>
    /// <param name="owner">Window asking; null from the tray menu.</param>
    public static void OpenFolder(Window? owner, string name)
    {
        if (Workspaces.IsCustom(name) && !Workspaces.IsAvailable(name))
        {
            _ = Dialogs.ShowAsync(owner, MissingFolderMessage(name), Loc.T("Workspaces.Folder.MissingTitle"), DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
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
