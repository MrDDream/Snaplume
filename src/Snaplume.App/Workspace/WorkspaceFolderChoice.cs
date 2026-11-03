using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>
/// Where a new workspace keeps its captures: a sub-folder of the captures folder (the default), or a folder chosen with
/// the system's folder picker (another drive, a network share, a synchronized folder...). Shown in the "New workspace" dialog.
/// </summary>
public sealed class WorkspaceFolderChoice : StackPanel
{
    private readonly RadioButton _default;
    private readonly RadioButton _custom;
    private readonly TextBlock _path;
    private string? _folder;

    public WorkspaceFolderChoice()
    {
        var group = "folder-" + Guid.NewGuid().ToString("N");
        Children.Add(new TextBlock { Text = Loc.T("Workspaces.New.FolderLabel"), Margin = new Thickness(0, 0, 0, 4) });
        _default = new RadioButton { Content = Loc.T("Workspaces.New.DefaultFolder"), GroupName = group, IsChecked = true };
        _custom = new RadioButton { Content = Loc.T("Workspaces.New.ChooseFolder"), GroupName = group };
        _path = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(28, 0, 0, 0), IsVisible = false };
        _path.Bind(TextBlock.ForegroundProperty, this.GetResourceObservable("MutedTextBrush"));
        Children.Add(_default);
        Children.Add(_custom);
        Children.Add(_path);
        // Click, not IsChecked: a second click on the option picks another folder.
        _custom.Click += async (_, _) => await PickFolderAsync();
        _default.IsCheckedChanged += (_, _) =>
        {
            if (_default.IsChecked == true) Folder = null;
        };
    }

    /// <summary>
    /// The system's folder picker; a folder too broad, or on a network share the user does not want, is not taken (the
    /// previous choice stays).
    /// </summary>
    private async Task PickFolderAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var title = Loc.T("Workspaces.New.ChooseFolderTitle");
        try
        {
            var picked = await WorkspaceCommands.PickFolderAsync(owner, title, _folder);
            if (picked != null && await WorkspaceCommands.AcceptFolderAsync(owner, picked, title)) Folder = picked;
        }
        catch (Exception ex)
        {
            await WorkspaceCommands.ShowErrorAsync(owner, Loc.T("Workspaces.Folder.PickFailed"), ex);
        }
        // Nothing chosen: back to the option of the folder kept.
        Folder = _folder;
    }

    /// <summary>The folder chosen, or null for the default sub-folder of the captures folder.</summary>
    public string? Folder
    {
        get => _folder;
        set
        {
            _folder = value;
            _path.Text = value ?? "";
            _path.IsVisible = value != null;
            if (value != null) _custom.IsChecked = true;
            else _default.IsChecked = true;
        }
    }
}
