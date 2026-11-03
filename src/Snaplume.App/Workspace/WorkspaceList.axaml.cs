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

namespace Snaplume.Workspace;

/// <summary>
/// The list of the workspaces, the active one selected; selecting another one makes it active (<see cref="WorkspaceCommands"/>).
/// Follows <see cref="Workspaces.Changed"/> while it is shown in a window.
/// </summary>
public partial class WorkspaceList : UserControl
{
    private bool _updating;
    private bool _refreshQueued;
    /// <summary>Entry right-clicked (null: empty area).</summary>
    private WorkspaceEntry? _menuEntry;
    private bool _menuFromPointer;
    private WorkspaceEntry? _dropTarget;

    public WorkspaceList()
    {
        InitializeComponent();
        EntryList.SelectionChanged += OnSelected;
        EntryList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        EntryMenu.Opening += OnMenuOpening;
        EntryList.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        EntryList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        EntryList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        EntryList.AddHandler(DragDrop.DropEvent, OnDrop);
        Refresh();
    }

    /// <summary>
    /// Captures of the active workspace dropped on another one: their paths and the name of that workspace. The host
    /// moves them (<see cref="CaptureGallery.MoveToWorkspaceAsync"/>).
    /// </summary>
    public event Action<IReadOnlyList<string>, string>? CapturesDropped;

    /// <summary>The entries shown, in their order (Base first).</summary>
    public IReadOnlyList<WorkspaceEntry> Entries => EntryList.ItemsSource as IReadOnlyList<WorkspaceEntry> ?? Array.Empty<WorkspaceEntry>();

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Workspaces.Changed += OnWorkspacesChanged;
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        Workspaces.Changed -= OnWorkspacesChanged;
        base.OnDetachedFromVisualTree(e);
    }

    /// <summary>Rebuilt after the current event: the change may come from the list's own selection.</summary>
    private void OnWorkspacesChanged()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Refresh();
        });
    }

    /// <summary>Reads the workspaces again and selects the active one.</summary>
    public void Refresh()
    {
        _updating = true;
        try
        {
            var entries = Workspaces.List().Select(WorkspaceEntry.Of).ToList();
            EntryList.ItemsSource = entries;
            EntryList.SelectedItem = entries.FirstOrDefault(e => WorkspaceCommands.IsActive(e.Name));
            _dropTarget = null;
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating || EntryList.SelectedItem is not WorkspaceEntry entry || WorkspaceCommands.IsActive(entry.Name) || Owner is not { } owner) return;
        WorkspaceCommands.Select(owner, entry.Name);
        // Back to the active one if it could not be selected.
        if (!WorkspaceCommands.IsActive(entry.Name)) OnWorkspacesChanged();
    }

    private void OnNewWorkspace(object? sender, RoutedEventArgs e)
    {
        if (Owner is { } owner) _ = WorkspaceCommands.CreateAsync(owner);
    }

    // ---- Context menu ----

    /// <summary>Right click: remembers the entry under the mouse for the context menu.</summary>
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(EntryList).Properties.IsRightButtonPressed) return;
        _menuEntry = EntryAt(e.Source);
        _menuFromPointer = true;
    }

    /// <summary>Commands of the entry under the mouse ("Base" can only be opened); from the keyboard, of the selected one.</summary>
    private void OnMenuOpening(object? sender, CancelEventArgs e)
    {
        if (!_menuFromPointer) _menuEntry = EntryList.SelectedItem as WorkspaceEntry;
        _menuFromPointer = false;
        bool hasEntry = _menuEntry != null;
        RenameEntryItem.IsVisible = DeleteEntryItem.IsVisible = OpenEntryItem.IsVisible = ChangeFolderItem.IsVisible = EntrySeparator.IsVisible = hasEntry;
        RenameEntryItem.IsEnabled = DeleteEntryItem.IsEnabled = ChangeFolderItem.IsEnabled = _menuEntry is { Name.Length: > 0 };
        // A folder of the user's choice: taken out of the list only (never deleted), and its default folder can come back.
        bool custom = _menuEntry is { IsCustom: true };
        DeleteEntryItem.Header = Loc.T(custom ? "Workspaces.Menu.Remove" : "Workspaces.Menu.Delete");
        DefaultFolderItem.IsVisible = custom;
        OpenEntryItem.IsEnabled = _menuEntry is { IsMissing: false };
    }

    private void OnChangeFolder(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null && Owner is { } owner) _ = WorkspaceCommands.ChangeFolderAsync(owner, _menuEntry.Name);
    }

    private void OnDefaultFolder(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null && Owner is { } owner) _ = WorkspaceCommands.ChangeFolderAsync(owner, _menuEntry.Name, toDefault: true);
    }

    private void OnRenameEntry(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null && Owner is { } owner) _ = WorkspaceCommands.RenameAsync(owner, _menuEntry.Name);
    }

    private void OnDeleteEntry(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null && Owner is { } owner) _ = WorkspaceCommands.DeleteAsync(owner, _menuEntry.Name);
    }

    private void OnOpenEntryFolder(object? sender, RoutedEventArgs e)
    {
        if (_menuEntry != null && Owner is { } owner) WorkspaceCommands.OpenFolder(owner, _menuEntry.Name);
    }

    private static WorkspaceEntry? EntryAt(object? source) =>
        (source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as WorkspaceEntry;

    // ---- Captures dropped on a workspace (dragged from the gallery): moved there ----

    /// <summary>Workspace that would receive the dragged captures, or null.</summary>
    private static WorkspaceEntry? DropTargetOf(DragEventArgs e) =>
        e.DataTransfer.Contains(CaptureGallery.DragFormat) && EntryAt(e.Source) is { } entry && !WorkspaceCommands.IsActive(entry.Name)
            ? entry
            : null;

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var target = DropTargetOf(e);
        // "Copy" is the only effect the gallery allows (so that file managers never move captures away); here it means "move".
        e.DragEffects = target != null ? DragDropEffects.Copy : DragDropEffects.None;
        SetDropTarget(target);
        e.Handled = true;
    }

    private void OnDragLeave(object? sender, DragEventArgs e) => SetDropTarget(null);

    private void OnDrop(object? sender, DragEventArgs e)
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
        Dispatcher.UIThread.Post(() => CapturesDropped?.Invoke(paths, target.Name));
    }

    private void SetDropTarget(WorkspaceEntry? entry)
    {
        if (ReferenceEquals(entry, _dropTarget)) return;
        if (_dropTarget != null) _dropTarget.IsDropTarget = false;
        _dropTarget = entry;
        if (entry != null) entry.IsDropTarget = true;
    }
}

/// <summary>A workspace of the list.</summary>
public sealed class WorkspaceEntry : INotifyPropertyChanged
{
    private bool _isDropTarget;

    public WorkspaceEntry(string name, string displayName, string folder = "", bool isCustom = false, bool isMissing = false)
    {
        Name = name;
        DisplayName = displayName;
        Folder = folder;
        IsCustom = isCustom;
        IsMissing = isMissing;
    }

    /// <summary>The entry of a workspace, as it is now (folder, reachable or not).</summary>
    public static WorkspaceEntry Of(string name)
    {
        bool custom = Workspaces.IsCustom(name);
        string folder;
        try
        {
            folder = Workspaces.FolderOf(name);
        }
        catch (ArgumentException)
        {
            folder = "";
        }
        return new WorkspaceEntry(name, Workspaces.DisplayName(name), folder, custom, custom && !Workspaces.IsAvailable(name));
    }

    /// <summary>Name of the workspace, "" for Base.</summary>
    public string Name { get; }
    public string DisplayName { get; }
    /// <summary>Full path of its folder.</summary>
    public string Folder { get; }
    /// <summary>Its folder was chosen by the user (anywhere).</summary>
    public bool IsCustom { get; }
    /// <summary>Its folder cannot be reached (unplugged drive, offline share): greyed, cannot be selected.</summary>
    public bool IsMissing { get; }

    public double Opacity => IsMissing ? 0.5 : 1;
    public string Tip => IsMissing ? Loc.T("Workspaces.Folder.MissingTip", Folder) : Folder;

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
