using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;

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
    private bool _menuFromPointer;
    private WorkspaceEntry? _dropTarget;

    public WorkspaceWindow()
    {
        InitializeComponent();
        Gallery.SelectionChanged += UpdateState;
        Gallery.Message += ShowMessage;
        WorkspaceList.SelectionChanged += OnWorkspaceSelected;
        WorkspaceList.AddHandler(PointerPressedEvent, OnWorkspaceListPointerPressed, RoutingStrategies.Tunnel);
        WorkspaceMenu.Opening += OnWorkspaceContextMenuOpening;
        WorkspaceList.AddHandler(DragDrop.DragEnterEvent, OnWorkspaceDragOver);
        WorkspaceList.AddHandler(DragDrop.DragOverEvent, OnWorkspaceDragOver);
        WorkspaceList.AddHandler(DragDrop.DragLeaveEvent, OnWorkspaceDragLeave);
        WorkspaceList.AddHandler(DragDrop.DropEvent, OnWorkspaceDrop);
        if (WorkspaceMenuButton.Flyout is MenuFlyout activeMenu) activeMenu.Opening += OnActiveWorkspaceMenuOpening;
        ScrollingButton.IsVisible = Has(PlatformFeatures.ScrollingCapture);
        RefreshWorkspaceList();
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

    protected override void OnWorkspacesChanged()
    {
        UpdateState();
        // Rebuilt after the current event: the change may come from the list's own selection.
        if (_listRefreshQueued) return;
        _listRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
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

    private void OnWorkspaceSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingList || WorkspaceList.SelectedItem is not WorkspaceEntry entry || WorkspaceCommands.IsActive(entry.Name)) return;
        WorkspaceCommands.Select(this, entry.Name);
        // Back to the active one if it could not be selected.
        if (!WorkspaceCommands.IsActive(entry.Name)) OnWorkspacesChanged();
    }

    private void OnNewWorkspace(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.CreateAsync(this);

    private void OnActiveWorkspaceMenuOpening(object? sender, EventArgs e)
    {
        bool isBase = Workspaces.ActiveName.Length == 0;
        RenameActiveItem.IsEnabled = DeleteActiveItem.IsEnabled = !isBase;
    }

    private void OnRenameActive(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.RenameAsync(this, Workspaces.ActiveName);
    private void OnDeleteActive(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.DeleteAsync(this, Workspaces.ActiveName);

    /// <summary>Right click: remembers the entry under the mouse for the context menu.</summary>
    private void OnWorkspaceListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(WorkspaceList).Properties.IsRightButtonPressed) return;
        _menuEntry = EntryAt(e.Source);
        _menuFromPointer = true;
    }

    /// <summary>Commands of the entry under the mouse ("Base" can only be opened); from the keyboard, of the selected one.</summary>
    private void OnWorkspaceContextMenuOpening(object? sender, CancelEventArgs e)
    {
        if (!_menuFromPointer) _menuEntry = WorkspaceList.SelectedItem as WorkspaceEntry;
        _menuFromPointer = false;
        bool hasEntry = _menuEntry != null;
        RenameEntryItem.IsVisible = DeleteEntryItem.IsVisible = OpenEntryItem.IsVisible = EntrySeparator.IsVisible = hasEntry;
        RenameEntryItem.IsEnabled = DeleteEntryItem.IsEnabled = _menuEntry is { Name.Length: > 0 };
    }

    private void OnRenameEntry(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) _ = WorkspaceCommands.RenameAsync(this, _menuEntry.Name);
    }

    private void OnDeleteEntry(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) _ = WorkspaceCommands.DeleteAsync(this, _menuEntry.Name);
    }

    private void OnOpenEntryFolder(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null) WorkspaceCommands.OpenFolder(this, _menuEntry.Name);
    }

    private static WorkspaceEntry? EntryAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as WorkspaceEntry;

    // ---- Captures dropped on a workspace (dragged from the gallery): moved there ----

    /// <summary>Workspace that would receive the dragged captures, or null.</summary>
    private static WorkspaceEntry? DropTargetOf(DragEventArgs e) =>
        e.DataTransfer.Contains(CaptureGallery.DragFormat) && EntryAt(e.Source) is { } entry && !WorkspaceCommands.IsActive(entry.Name)
            ? entry
            : null;

    private void OnWorkspaceDragOver(object? sender, DragEventArgs e)
    {
        var target = DropTargetOf(e);
        // "Copy" is the only effect the gallery allows (so that file managers never move captures away); here it means "move".
        e.DragEffects = target != null ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(target);
        e.Handled = true;
    }

    private void OnWorkspaceDragLeave(object? sender, DragEventArgs e) => SetDropTarget(null);

    private void OnWorkspaceDrop(object? sender, DragEventArgs e)
    {
        SetDropTarget(null);
        if (DropTargetOf(e) is not { } target || e.DataTransfer.TryGetValue(CaptureGallery.DragFormat) is not { } data) return;
        e.Handled = true;
        // Only captures of the gallery's own folder (the active workspace) are moved.
        var paths = data.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.IsPathFullyQualified(p) && ImageIO.IsCapture(p) &&
                        CaptureGallery.SameFolder(Path.GetDirectoryName(p), Workspaces.ActiveFolder))
            .ToList();
        if (paths.Count == 0) return;
        // After the drag-and-drop loop has ended (it may show messages).
        Dispatcher.UIThread.Post(() => _ = Gallery.MoveToWorkspaceAsync(paths, target.Name));
    }

    private void SetDropTarget(WorkspaceEntry? entry)
    {
        if (ReferenceEquals(entry, _dropTarget)) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = entry;
        if (entry != null) entry.IsDropTarget = true;
    }

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
