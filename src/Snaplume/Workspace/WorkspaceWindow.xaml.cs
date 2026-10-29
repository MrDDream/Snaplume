using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>
/// Full-size window: the workspaces (sub-folders) on the left, all the captures of the active one on the right.
/// </summary>
public partial class WorkspaceWindow : CaptureHostWindow
{
    private bool _updatingList;
    private bool _listRefreshQueued;
    /// <summary>Entry right-clicked in the workspace list (null: empty area).</summary>
    private WorkspaceEntry? _menuEntry;
    private WorkspaceEntry? _dropTarget;

    public WorkspaceWindow()
    {
        InitializeComponent();
        Gallery.SelectionChanged += UpdateState;
        Gallery.Message += ShowMessage;
        RefreshWorkspaceList();
        UpdateState();
        OnRecordingChanged(App.Instance.Videos.IsBusy);
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
        VideoButton.ToolTip = Loc.T(recording ? "Workspace.StopRecordingTip" : "Workspace.VideoTip");
        VideoIcon.SetResourceReference(System.Windows.Shapes.Path.DataProperty, recording ? "IconStop" : "IconVideo");
        VideoIcon.SetResourceReference(Shape.StrokeProperty, recording ? "RecordBrush" : "IconBrush");
    }

    protected override void OnWorkspacesChanged()
    {
        UpdateState();
        // Rebuilt after the current event: the change may come from the list's own selection.
        if (_listRefreshQueued) return;
        _listRefreshQueued = true;
        Dispatcher.BeginInvoke(() =>
        {
            _listRefreshQueued = false;
            RefreshWorkspaceList();
        });
    }

    private void RefreshWorkspaceList()
    {
        _updatingList = true;
        try
        {
            var entries = Workspaces.List().Select(name => new WorkspaceEntry(name, Workspaces.DisplayName(name))).ToList();
            WorkspaceList.ItemsSource = entries;
            WorkspaceList.SelectedItem = entries.FirstOrDefault(e => WorkspaceCommands.IsActive(e.Name));
            _dropTarget = null;
        }
        finally
        {
            _updatingList = false;
        }
    }

    // ---- Workspace list ----

    private void OnWorkspaceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || WorkspaceList.SelectedItem is not WorkspaceEntry entry || WorkspaceCommands.IsActive(entry.Name)) return;
        WorkspaceCommands.Select(this, entry.Name);
        // Back to the active one if it could not be selected.
        if (!WorkspaceCommands.IsActive(entry.Name)) OnWorkspacesChanged();
    }

    private void OnNewWorkspace(object sender, RoutedEventArgs e) => WorkspaceCommands.Create(this);

    private void OnWorkspaceMenu(object sender, RoutedEventArgs e)
    {
        bool isBase = Workspaces.ActiveName.Length == 0;
        RenameActiveItem.IsEnabled = DeleteActiveItem.IsEnabled = !isBase;
        OpenMenu(WorkspaceMenuButton, PlacementMode.Top);
    }

    private void OnRenameActive(object sender, RoutedEventArgs e) => WorkspaceCommands.Rename(this, Workspaces.ActiveName);
    private void OnDeleteActive(object sender, RoutedEventArgs e) => WorkspaceCommands.Delete(this, Workspaces.ActiveName);

    /// <summary>Right click: commands of the entry under the mouse ("Base" can only be opened).</summary>
    private void OnWorkspaceContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        _menuEntry = EntryAt(e.OriginalSource);
        var entryVisibility = _menuEntry != null ? Visibility.Visible : Visibility.Collapsed;
        RenameEntryItem.Visibility = DeleteEntryItem.Visibility = OpenEntryItem.Visibility = EntrySeparator.Visibility = entryVisibility;
        RenameEntryItem.IsEnabled = DeleteEntryItem.IsEnabled = _menuEntry is { Name.Length: > 0 };
    }

    private void OnRenameEntry(object sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) WorkspaceCommands.Rename(this, _menuEntry.Name);
    }

    private void OnDeleteEntry(object sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) WorkspaceCommands.Delete(this, _menuEntry.Name);
    }

    private void OnOpenEntryFolder(object sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) WorkspaceCommands.OpenFolder(this, _menuEntry.Name);
    }

    private static WorkspaceEntry? EntryAt(object source) =>
        CaptureGallery.FindAncestor<ListBoxItem>(source as DependencyObject)?.DataContext as WorkspaceEntry;

    // ---- Captures dropped on a workspace (dragged from the gallery): moved there ----

    /// <summary>Workspace that would receive the dragged captures, or null.</summary>
    private static WorkspaceEntry? DropTargetOf(DragEventArgs e) =>
        e.Data.GetDataPresent(CaptureGallery.DragFormat) && EntryAt(e.OriginalSource) is { } entry && !WorkspaceCommands.IsActive(entry.Name)
            ? entry
            : null;

    private void OnWorkspaceDragOver(object sender, DragEventArgs e)
    {
        var target = DropTargetOf(e);
        // "Copy" is the only effect the gallery allows (so that Explorer never moves captures away); here it means "move".
        e.Effects = target != null ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(target);
        e.Handled = true;
    }

    private void OnWorkspaceDragLeave(object sender, DragEventArgs e) => SetDropTarget(null);

    private void OnWorkspaceDrop(object sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (DropTargetOf(e) is not { } target || e.Data.GetData(CaptureGallery.DragFormat) is not string data) return;
        e.Handled = true;
        // The format is only a convention: any application can offer it. Only captures of the gallery's
        // own folder (the active workspace) are moved.
        var paths = data.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => System.IO.Path.IsPathFullyQualified(p) && ImageIO.IsCapture(p) &&
                        CaptureGallery.SameFolder(System.IO.Path.GetDirectoryName(p), Workspaces.ActiveFolder))
            .ToList();
        if (paths.Count == 0) return;
        // After the drag-and-drop loop has ended (it may show messages).
        Dispatcher.BeginInvoke(() => Gallery.MoveToWorkspace(paths, target.Name));
    }

    private void SetDropTarget(WorkspaceEntry? entry)
    {
        if (ReferenceEquals(entry, _dropTarget)) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = entry;
        if (entry != null) entry.IsDropTarget = true;
    }

    // ---- Toolbar ----

    private void OnDelayMenu(object sender, RoutedEventArgs e) => OpenMenu(DelayButton, PlacementMode.Bottom);

    private void OnEdit(object sender, RoutedEventArgs e) => Gallery.EditSelection();
    private void OnCopy(object sender, RoutedEventArgs e) => Gallery.CopySelection();
    private void OnDelete(object sender, RoutedEventArgs e) => Gallery.DeleteSelection();

    protected override void OnClosed(EventArgs e)
    {
        Gallery.Detach();
        base.OnClosed(e);
    }
}

/// <summary>A workspace of the sidebar list.</summary>
public sealed class WorkspaceEntry : INotifyPropertyChanged
{
    private bool _isDropTarget;

    public WorkspaceEntry(string name, string displayName)
    {
        Name = name;
        DisplayName = displayName;
    }

    /// <summary>Folder name, "" for Base.</summary>
    public string Name { get; }
    public string DisplayName { get; }

    /// <summary>Captures are being dragged over this entry.</summary>
    public bool IsDropTarget
    {
        get => _isDropTarget;
        set
        {
            _isDropTarget = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
