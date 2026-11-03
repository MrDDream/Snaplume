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
    public WorkspaceWindow()
    {
        InitializeComponent();
        Gallery.SelectionChanged += UpdateState;
        Gallery.Message += ShowMessage;
        WorkspaceList.CapturesDropped += (paths, target) => _ = Gallery.MoveToWorkspaceAsync(paths, target);
        if (WorkspaceMenuButton.Flyout is MenuFlyout activeMenu) activeMenu.Opening += OnActiveWorkspaceMenuOpening;
        ScrollingButton.IsVisible = Has(PlatformFeatures.ScrollingCapture);
        UpdateState();
        if (Avalonia.Application.Current is App { Videos: { } videos }) OnRecordingChanged(videos.IsBusy);
    }

    public void Reload() => Gallery.Reload();

    protected override TextBlock StatusLine => StatusText;

    protected override void UpdateState()
    {
        EditButton.IsEnabled = CopyButton.IsEnabled = DeleteButton.IsEnabled = Gallery.SelectedCount > 0;
        if (!IsShowingMessage) StatusText.Text = $"{Gallery.SummaryText}   •   {Workspaces.ActiveFolder}";
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
    protected override void OnWorkspacesChanged() => UpdateState();

    // ---- Workspace commands of the sidebar ----

    private void OnNewWorkspace(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.CreateAsync(this);

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

    private void OnEdit(object? sender, RoutedEventArgs e) => Gallery.EditSelection();
    private void OnCopy(object? sender, RoutedEventArgs e) => Gallery.CopySelection();
    private void OnDelete(object? sender, RoutedEventArgs e) => Gallery.DeleteSelection();

    protected override void OnClosed(EventArgs e)
    {
        Gallery.Detach();
        base.OnClosed(e);
    }
}
