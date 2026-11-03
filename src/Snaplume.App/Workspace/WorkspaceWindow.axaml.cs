using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Workspace;

/// <summary>
/// Full-size window: the workspaces (sub-folders) on the left, all the captures of the active one on the right.
/// </summary>
public partial class WorkspaceWindow : CaptureHostWindow
{
    /// <summary>
    /// Folder of the active workspace, read when the workspaces change: the status line is updated at each selection
    /// change, and the folder may be on a slow network share.
    /// </summary>
    private string _activeFolder;

    public WorkspaceWindow()
    {
        InitializeComponent();
        _activeFolder = Workspaces.ActiveFolder;
        Host(Gallery, WorkspaceList);
        if (WorkspaceMenuButton.Flyout is MenuFlyout activeMenu) activeMenu.Opening += OnActiveWorkspaceMenuOpening;
        ScrollingButton.IsVisible = Has(PlatformFeatures.ScrollingCapture);
        UpdateState();
        if (Avalonia.Application.Current is App { Videos: { } videos }) OnRecordingChanged(videos.IsBusy);
    }

    protected override TextBlock StatusLine => StatusText;

    protected override void UpdateState()
    {
        EditButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = Gallery.SelectedCount > 0;
        if (!IsShowingMessage) StatusText.Text = $"{Gallery.SummaryText}   •   {_activeFolder}";
    }

    protected override void OnRecordingChanged(bool recording)
    {
        VideoButtonText.Text = Loc.T(recording ? "Workspace.StopRecording" : "Workspace.Video");
        ToolTip.SetTip(VideoButton, Loc.T(recording ? "Workspace.StopRecordingTip" : "Workspace.VideoTip"));
        if (this.TryFindResource(recording ? "IconStop" : "IconVideo", out var data) && data is Avalonia.Media.Geometry geometry)
            VideoIcon.Data = geometry;
        if (recording) VideoIcon.Bind(Avalonia.Controls.Shapes.Shape.StrokeProperty, this.GetResourceObservable("RecordBrush"));
        else VideoIcon.ClearValue(Avalonia.Controls.Shapes.Shape.StrokeProperty);
    }

    // The workspace list follows the changes itself (WorkspaceList).
    protected override void OnWorkspacesChanged()
    {
        _activeFolder = Workspaces.ActiveFolder;
        UpdateState();
    }

    // ---- Workspace commands of the sidebar ----

    private void OnActiveWorkspaceMenuOpening(object? sender, EventArgs e)
    {
        var active = Workspaces.ActiveName;
        bool isBase = active.Length == 0, custom = Workspaces.IsCustom(active);
        RenameActiveItem.IsEnabled = DeleteActiveItem.IsEnabled = ChangeFolderActiveItem.IsEnabled = !isBase;
        DeleteActiveItem.Header = Loc.T(custom ? "Workspaces.Menu.Remove" : "Workspaces.Menu.Delete");
        DefaultFolderActiveItem.IsVisible = custom;
    }

    private void OnRenameActive(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.RenameAsync(this, Workspaces.ActiveName);
    private void OnDeleteActive(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.DeleteAsync(this, Workspaces.ActiveName);
    private void OnChangeFolderActive(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.ChangeFolderAsync(this, Workspaces.ActiveName);

    private void OnDefaultFolderActive(object? sender, RoutedEventArgs e) =>
        _ = WorkspaceCommands.ChangeFolderAsync(this, Workspaces.ActiveName, toDefault: true);

    // ---- Toolbar ----

    private void OnDelete(object? sender, RoutedEventArgs e) => Gallery.DeleteSelection();
}
