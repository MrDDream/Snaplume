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
/// The list of the workspaces, the active one selected. A click (or Enter) makes another one active
/// (<see cref="WorkspaceCommands"/>); the arrow keys move the highlight, and the workspace highlighted becomes active
/// once they rest a moment. Shows <see cref="WorkspaceListing"/>, read off the UI thread, while it is in a window.
/// </summary>
public partial class WorkspaceList : UserControl
{
    /// <summary>Rest after the arrow keys before the highlighted workspace becomes the active one.</summary>
    internal static readonly TimeSpan KeyboardCommitDelay = TimeSpan.FromMilliseconds(300);

    private readonly DispatcherTimer _keyboardCommit;
    private bool _updating;
    /// <summary>The selection is being moved by an arrow key (not a click).</summary>
    private bool _movingWithKeys;
    /// <summary>The active workspace of the listing shown.</summary>
    private string _active = "";
    /// <summary>Entry right-clicked (null: empty area).</summary>
    private WorkspaceEntry? _menuEntry;
    private bool _menuFromPointer;
    private WorkspaceEntry? _dropTarget;

    public WorkspaceList()
    {
        InitializeComponent();
        _keyboardCommit = new DispatcherTimer { Interval = KeyboardCommitDelay };
        _keyboardCommit.Tick += (_, _) =>
        {
            _keyboardCommit.Stop();
            Commit(fromKeyboard: true);
        };
        EntryList.SelectionChanged += OnSelected;
        EntryList.AddHandler(KeyDownEvent, OnListKeyDown, RoutingStrategies.Tunnel);
        // After the list has moved its selection: the next selection change may come from the mouse.
        EntryList.AddHandler(KeyDownEvent, (_, _) => _movingWithKeys = false, RoutingStrategies.Bubble, handledEventsToo: true);
        EntryList.AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Tunnel);
        EntryMenu.Opening += OnMenuOpening;
        EntryList.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        EntryList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        EntryList.AddHandler(DragDrop.DragLeaveEvent, OnDragLeave);
        EntryList.AddHandler(DragDrop.DropEvent, OnDrop);
        Show(WorkspaceListing.Current);
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
        WorkspaceListing.Updated += OnListingUpdated;
        Show(WorkspaceListing.Current);
        WorkspaceListing.Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        WorkspaceListing.Updated -= OnListingUpdated;
        _keyboardCommit.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnListingUpdated() => Show(WorkspaceListing.Current);

    /// <summary>Reads the workspaces again (in the background); the list follows.</summary>
    public void Refresh() => WorkspaceListing.Refresh();

    /// <summary>Shows a listing, the active workspace selected.</summary>
    private void Show(WorkspaceSnapshot listing)
    {
        _active = listing.ActiveName;
        var shown = Entries;
        if (shown.Count == listing.Workspaces.Count && shown.Zip(listing.Workspaces).All(pair => pair.First.Shows(pair.Second)))
        {
            // The same workspaces: the entries (and the keyboard focus) stay; a highlight moved with the arrow keys too.
            if (!_keyboardCommit.IsEnabled) SelectActive();
            return;
        }
        _keyboardCommit.Stop();
        bool hadFocus = EntryList.IsKeyboardFocusWithin;
        _updating = true;
        try
        {
            var entries = listing.Workspaces.Select(w => new WorkspaceEntry(w.Name, Workspaces.DisplayName(w.Name), w.Folder, w.IsCustom, w.IsMissing))
                .ToList();
            EntryList.ItemsSource = entries;
            EntryList.SelectedItem = entries.FirstOrDefault(e => IsActive(e.Name));
            _dropTarget = null;
        }
        finally
        {
            _updating = false;
        }
        // The focused entry was replaced: the focus goes to the new selected one, once laid out.
        if (hadFocus)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (EntryList.SelectedItem is { } selected && EntryList.ContainerFromItem(selected) is { } container) container.Focus();
                else EntryList.Focus();
            }, DispatcherPriority.Loaded);
        }
    }

    private bool IsActive(string name) => string.Equals(name, _active, StringComparison.OrdinalIgnoreCase);

    /// <summary>Back to the active workspace (one that could not be selected was highlighted).</summary>
    private void SelectActive()
    {
        _updating = true;
        try
        {
            EntryList.SelectedItem = Entries.FirstOrDefault(e => IsActive(e.Name));
        }
        finally
        {
            _updating = false;
        }
    }

    private void OnSelected(object? sender, SelectionChangedEventArgs e)
    {
        if (_updating) return;
        _keyboardCommit.Stop();
        // The arrow keys only move the highlight: the workspace becomes active once they rest (or with Enter).
        if (_movingWithKeys) _keyboardCommit.Start();
        else Commit(fromKeyboard: false);
    }

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Up or Key.Down or Key.Home or Key.End or Key.PageUp or Key.PageDown:
                _movingWithKeys = true;
                break;
            case Key.Enter:
                _keyboardCommit.Stop();
                Commit(fromKeyboard: false);
                e.Handled = true;
                break;
        }
    }

    /// <summary>Makes the selected workspace the active one.</summary>
    /// <param name="fromKeyboard">
    /// Reached with the arrow keys: one whose folder cannot be reached is passed over in silence (no message for each
    /// key), and stays highlighted.
    /// </param>
    private void Commit(bool fromKeyboard)
    {
        if (EntryList.SelectedItem is not WorkspaceEntry entry || Owner is not { } owner) return;
        if (fromKeyboard && entry.IsMissing) return;
        WorkspaceCommands.Select(owner, entry.Name);
        if (WorkspaceCommands.IsActive(entry.Name)) _active = entry.Name;
        else SelectActive();
    }

    private void OnNewWorkspace(object? sender, RoutedEventArgs e) => _ = WorkspaceCommands.CreateAsync(Owner);

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
    private WorkspaceEntry? DropTargetOf(DragEventArgs e) =>
        e.DataTransfer.Contains(CaptureGallery.DragFormat) && EntryAt(e.Source) is { } entry && !IsActive(entry.Name)
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
        var activeFolder = Workspaces.ActiveFolder;
        var paths = data.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Where(p => Path.IsPathFullyQualified(p) && ImageIO.IsCapture(p) &&
                        Path.GetDirectoryName(p) is { } folder && PathRules.SamePath(folder, activeFolder))
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

    /// <summary>Name of the workspace, "" for Base.</summary>
    public string Name { get; }
    public string DisplayName { get; }
    /// <summary>Full path of its folder.</summary>
    public string Folder { get; }
    /// <summary>Its folder was chosen by the user (anywhere).</summary>
    public bool IsCustom { get; }
    /// <summary>Its folder cannot be reached (unplugged drive, offline share): greyed, cannot be selected.</summary>
    public bool IsMissing { get; }

    /// <summary>This entry shows the workspace as it was read (nothing to rebuild).</summary>
    internal bool Shows(WorkspaceInfo workspace) =>
        Name == workspace.Name && Folder == workspace.Folder && IsCustom == workspace.IsCustom && IsMissing == workspace.IsMissing;

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
