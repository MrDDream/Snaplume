using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Export;
using Snaplume.Imaging;
using Snaplume.Player;
using Snaplume.Ui;

namespace Snaplume.Workspace;

/// <summary>
/// Thumbnails of the captures stored in the active workspace folder, with the file commands.
/// Shared by the full workspace window and the quick panel.
/// </summary>
public partial class CaptureGallery : UserControl
{
    /// <summary>
    /// Data added to the captures dragged out of a gallery: the workspace list only accepts these. In-process only,
    /// so other applications see plain files.
    /// </summary>
    public static readonly DataFormat<string> DragFormat = DataFormat.CreateInProcessFormat<string>("Snaplume.GalleryCaptures");
    private const int MaxThumbnailRetries = 3;
    /// <summary>Distance (device-independent pixels) the mouse must move, button down, to start dragging.</summary>
    private const double DragDistance = 4;

    /// <summary>File information read in the background.</summary>
    private sealed record FileEntry(string Path, DateTime Created, DateTime CreatedUtc, DateTime ModifiedUtc, long Length);

    private readonly ObservableCollection<CaptureItem> _items = new();
    private readonly Dictionary<string, CaptureItem> _cache = new(PathComparer);
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _watcherRestartTimer;
    private FileSystemWatcher? _watcher;
    private string _folder = "";
    private CancellationTokenSource _thumbnails = new();
    private int _refreshRun;
    private bool _refreshPending = true;
    private bool _focusWhenLoaded;
    private bool _importing;
    private bool _attached;
    private string? _pendingSelection;
    private int? _pendingIndex;
    private Window? _hostWindow;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _draggingOut;
    private PointerPressedEventArgs? _pressArgs;
    private ListBoxItem? _pressedSelectedItem;

    /// <summary>Selection or item count changed.</summary>
    public event Action? SelectionChanged;
    /// <summary>Short feedback such as "Image copied".</summary>
    public event Action<string>? Message;

    public CaptureGallery()
    {
        InitializeComponent();
        CaptureList.ItemsSource = _items;
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _refreshTimer.Tick += (_, _) =>
        {
            _refreshTimer.Stop();
            Refresh();
        };
        // Debounces the restart of the folder watcher after an error (buffer overflow, network folder lost...).
        _watcherRestartTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _watcherRestartTimer.Tick += (_, _) =>
        {
            _watcherRestartTimer.Stop();
            Reload();
        };

        CaptureList.SelectionChanged += (_, _) => SelectionChanged?.Invoke();
        CaptureList.DoubleTapped += OnItemDoubleTapped;
        CaptureList.KeyDown += OnListKeyDown;
        CaptureList.AddHandler(PointerPressedEvent, OnListPointerPressed, RoutingStrategies.Tunnel);
        CaptureList.AddHandler(PointerMovedEvent, OnListPointerMoved, RoutingStrategies.Tunnel);
        CaptureList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Tunnel);
        CaptureList.AddHandler(DragDrop.DragEnterEvent, OnDragOver);
        CaptureList.AddHandler(DragDrop.DragOverEvent, OnDragOver);
        CaptureList.AddHandler(DragDrop.DropEvent, OnDrop);
        ItemMenu.Opening += OnItemMenuOpening;

        if (Avalonia.Application.Current is App { Captures: { } captures })
        {
            captures.CaptureSaved += OnCaptureSaved;
            Workspaces.Changed += OnWorkspacesChanged;
            _attached = true;
            Reload();
        }
    }

    private static StringComparer PathComparer => OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public int SelectedCount => CaptureList.SelectedItems?.Count ?? 0;

    public List<string> SelectedPaths => SelectedItems.Select(i => i.Path).ToList();

    private IEnumerable<CaptureItem> SelectedItems => CaptureList.SelectedItems?.OfType<CaptureItem>() ?? Enumerable.Empty<CaptureItem>();

    /// <summary>Selected captures in the order shown in the gallery (not the order they were clicked).</summary>
    public List<string> SelectedPathsInGalleryOrder
    {
        get
        {
            var selected = new HashSet<CaptureItem>(SelectedItems);
            return _items.Where(selected.Contains).Select(i => i.Path).ToList();
        }
    }

    /// <summary>Selected images, in gallery order (documents and printing leave the videos out).</summary>
    private List<string> SelectedImages => SelectedPathsInGalleryOrder.Where(ImageIO.IsImage).ToList();

    public string SummaryText
    {
        get
        {
            var text = Loc.Plural(_items.Count, "Gallery.Summary.One", "Gallery.Summary.Many");
            if (SelectedCount > 1) text = Loc.T("Gallery.Summary.Selected", text, SelectedCount);
            return text;
        }
    }

    private Window? OwnerWindow => TopLevel.GetTopLevel(this) as Window;

    /// <summary>The window hosting the gallery is shown: a hidden window does not read its folder, it catches up when shown.</summary>
    private bool IsShown => OwnerWindow is { IsVisible: true };

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_hostWindow != null) _hostWindow.PropertyChanged -= OnHostPropertyChanged;
        _hostWindow = OwnerWindow;
        if (_hostWindow != null) _hostWindow.PropertyChanged += OnHostPropertyChanged;
        // The window template (and so the gallery) is only attached when the window is first shown.
        if (_refreshPending && IsShown) Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_hostWindow != null) _hostWindow.PropertyChanged -= OnHostPropertyChanged;
        _hostWindow = null;
    }

    private void OnHostPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == Visual.IsVisibleProperty && _hostWindow is { IsVisible: true } && _refreshPending) Refresh();
    }

    /// <summary>Stops listening to the application (the window hosting the gallery is closed).</summary>
    public void Detach()
    {
        if (_attached && Avalonia.Application.Current is App { Captures: { } captures }) captures.CaptureSaved -= OnCaptureSaved;
        Workspaces.Changed -= OnWorkspacesChanged;
        _attached = false;
        _refreshTimer.Stop();
        _watcherRestartTimer.Stop();
        _thumbnails.Cancel();
        _watcher?.Dispose();
        _watcher = null;
    }

    /// <summary>A capture was saved (maybe on another thread).</summary>
    private void OnCaptureSaved(string path) => Dispatcher.UIThread.Post(() =>
    {
        _pendingSelection = path;
        Refresh();
    });

    private void OnWorkspacesChanged()
    {
        // Another workspace was selected, or the active one renamed.
        if (!SameFolder(Workspaces.ActiveFolder, _folder)) Reload();
    }

    /// <summary>Starts watching the (possibly new) active workspace folder.</summary>
    public void Reload()
    {
        _watcherRestartTimer.Stop();
        _watcher?.Dispose();
        _watcher = null;
        _thumbnails.Cancel();
        _thumbnails = new CancellationTokenSource();

        var folder = Workspaces.ActiveFolder;
        if (!SameFolder(folder, _folder))
        {
            _items.Clear();
            _cache.Clear();
        }
        _folder = folder;
        try
        {
            Directory.CreateDirectory(folder);
            // Not recursive: the sub-folders are the other workspaces.
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
            watcher.Created += changed;
            watcher.Changed += changed;
            watcher.Deleted += changed;
            watcher.Renamed += (_, _) => Dispatcher.UIThread.Post(ScheduleRefresh);
            watcher.Error += (sender, e) =>
            {
                var error = e.GetException();
                Dispatcher.UIThread.Post(() => OnWatcherError(sender, error));
            };
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Surveillance du dossier", ex);
        }
        Refresh();
    }

    /// <summary>The watcher stops after an error: start a new one (and read the folder again) a bit later.</summary>
    private void OnWatcherError(object? sender, Exception ex)
    {
        // Ignore a watcher already replaced (e.g. the active workspace was renamed).
        if (_watcher == null || !ReferenceEquals(sender, _watcher)) return;
        ErrorLog.Write("Surveillance du dossier", ex);
        _watcher.Dispose();
        _watcher = null;
        _watcherRestartTimer.Stop();
        _watcherRestartTimer.Start();
    }

    public void FocusList()
    {
        if (_items.Count == 0)
        {
            // Not read yet (the window has just been shown): focus once the captures are listed.
            _focusWhenLoaded = true;
            CaptureList.Focus();
            return;
        }
        if (CaptureList.SelectedIndex < 0) CaptureList.SelectedIndex = 0;
        if (CaptureList.ContainerFromIndex(Math.Max(0, CaptureList.SelectedIndex)) is { } item) item.Focus();
        else CaptureList.Focus();
    }

    private void ScheduleRefresh()
    {
        _refreshTimer.Stop();
        _refreshTimer.Start();
    }

    /// <summary>Lists the folder in the background, then updates the gallery.</summary>
    private async void Refresh()
    {
        if (!IsShown)
        {
            _refreshPending = true;
            return;
        }
        _refreshPending = false;
        int run = ++_refreshRun;
        var folder = _folder;
        List<FileEntry> files;
        bool unavailable = false;
        try
        {
            files = await Task.Run(() => ReadFolder(folder));
        }
        catch (Exception ex)
        {
            // Network folder lost, access denied...: show it empty rather than failing.
            ErrorLog.Write($"Lecture du dossier {folder}", ex);
            files = new List<FileEntry>();
            unavailable = true;
        }
        if (run != _refreshRun) return; // a newer refresh (or another folder) is on its way
        Apply(files, unavailable);
    }

    private static List<FileEntry> ReadFolder(string folder)
    {
        var files = new List<FileEntry>();
        if (!Directory.Exists(folder)) return files;
        foreach (var file in new DirectoryInfo(folder).EnumerateFiles())
        {
            // Hidden files: the Windows attribute, or a leading dot (temporary files of ImageIO.Save among them).
            if (!ImageIO.IsCapture(file.Name) || file.Attributes.HasFlag(FileAttributes.Hidden) || file.Name.StartsWith('.')) continue;
            files.Add(new FileEntry(file.FullName, file.CreationTime, file.CreationTimeUtc, file.LastWriteTimeUtc, file.Length));
        }
        // Newest first; the name keeps the order stable for captures taken in the same second.
        files.Sort((a, b) =>
        {
            int byDate = b.CreatedUtc.CompareTo(a.CreatedUtc);
            return byDate != 0 ? byDate : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
        });
        return files;
    }

    private void Apply(List<FileEntry> files, bool unavailable)
    {
        var items = new List<CaptureItem>(files.Count);
        foreach (var file in files)
        {
            if (!_cache.TryGetValue(file.Path, out var item))
            {
                item = new CaptureItem(file.Path);
                _cache[file.Path] = item;
            }
            if (item.Update(file.Created, file.ModifiedUtc, file.Length))
            {
                item.ThumbnailFailures = 0;
                LoadThumbnail(item);
            }
            items.Add(item);
        }
        if (_cache.Count > items.Count)
        {
            var present = new HashSet<CaptureItem>(items);
            foreach (var stale in _cache.Where(p => !present.Contains(p.Value)).Select(p => p.Key).ToList())
                _cache.Remove(stale);
        }

        Synchronize(items);

        if (_pendingSelection != null)
        {
            if (_cache.TryGetValue(_pendingSelection, out var created))
            {
                CaptureList.SelectedItems?.Clear();
                CaptureList.SelectedItem = created;
                CaptureList.ScrollIntoView(created);
                _pendingSelection = null;
            }
            else if (!SameFolder(Path.GetDirectoryName(_pendingSelection), _folder))
            {
                _pendingSelection = null; // saved in another workspace
            }
        }
        else if (_pendingIndex is int index && _items.Count > 0)
        {
            // After a deletion: select the capture that took the place of the deleted one.
            CaptureList.SelectedIndex = Math.Clamp(index, 0, _items.Count - 1);
            _focusWhenLoaded = true;
        }
        _pendingIndex = null;

        if (_focusWhenLoaded && _items.Count > 0)
        {
            _focusWhenLoaded = false;
            Dispatcher.UIThread.Post(FocusList, DispatcherPriority.Loaded);
        }

        EmptyHint.IsVisible = _items.Count == 0;
        if (unavailable)
        {
            EmptyTitle.Text = Loc.T("Gallery.FolderUnavailable");
            EmptyHintText.Text = _folder;
        }
        else
        {
            EmptyTitle.Text = Loc.T("Gallery.Empty");
            EmptyHintText.Text = Hotkey.TryParse(AppServices.Settings.HotkeyRegion, out var hotkey) && !hotkey.IsEmpty && HotkeysWork
                ? Loc.T("Gallery.EmptyHintHotkey", hotkey.DisplayName)
                : Loc.T("Gallery.EmptyHint");
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>The global shortcuts work here (not under some Wayland desktops): the empty gallery may mention them.</summary>
    private static bool HotkeysWork => AppServices.HasPlatform && AppServices.Platform.Has(Platform.PlatformFeatures.GlobalHotkeys);

    /// <summary>
    /// Brings the displayed list to the new one with the fewest changes (remove, move, insert),
    /// so that the scroll position, the selection and the existing thumbnails are kept.
    /// </summary>
    private void Synchronize(List<CaptureItem> wanted)
    {
        var selected = SelectedItems.ToList();
        var keep = new HashSet<CaptureItem>(wanted);
        for (int i = _items.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(_items[i])) _items.RemoveAt(i);
        }
        for (int i = 0; i < wanted.Count; i++)
        {
            if (i < _items.Count && ReferenceEquals(_items[i], wanted[i])) continue;
            int current = -1;
            for (int j = i + 1; j < _items.Count; j++)
            {
                if (ReferenceEquals(_items[j], wanted[i]))
                {
                    current = j;
                    break;
                }
            }
            if (current >= 0) _items.Move(current, i);
            else _items.Insert(i, wanted[i]);
        }
        if (CaptureList.SelectedItems is { } list)
        {
            foreach (var item in selected.Where(i => keep.Contains(i) && !list.Contains(i))) list.Add(item);
        }
    }

    /// <summary>Thumbnail from the shared cache, decoded at the displayed size.</summary>
    private async void LoadThumbnail(CaptureItem item)
    {
        var cancellation = _thumbnails.Token;
        var (width, height) = ThumbnailBox();
        var modified = item.Modified;
        var length = item.Length;
        try
        {
            var thumbnail = await ThumbnailCache.GetAsync(item.Path, modified, length, item.IsVideo, width, height, cancellation);
            if (cancellation.IsCancellationRequested || item.Modified != modified || item.Length != length) return; // outdated
            item.ThumbnailFailures = 0;
            if (thumbnail == null) return;
            item.Thumbnail = thumbnail.Image;
            if (thumbnail.Dimensions.Length > 0) item.Dimensions = thumbnail.Dimensions;
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
            // Locked by the program still writing it: try again a little later, a few times.
            if (cancellation.IsCancellationRequested || ++item.ThumbnailFailures > MaxThumbnailRetries) return;
            await Task.Delay(TimeSpan.FromSeconds(item.ThumbnailFailures * 1.5));
            if (!cancellation.IsCancellationRequested && item.Modified == modified && _cache.ContainsKey(item.Path))
                LoadThumbnail(item);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Miniature de {item.Path}", ex);
        }
    }

    /// <summary>Size of the thumbnail area in device pixels.</summary>
    private (int Width, int Height) ThumbnailBox()
    {
        double scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        double width = this.TryFindResource("GalleryItemWidth", out var w) && w is double dw ? dw : 200;
        double height = this.TryFindResource("GalleryThumbnailHeight", out var h) && h is double dh ? dh : 130;
        // Minus the frame (1 px) and the image margin (2 px) on each side.
        return ((int)Math.Ceiling((width - 6) * scale), (int)Math.Ceiling((height - 6) * scale));
    }

    // ---- Commands ----

    private void OnEdit(object? sender, RoutedEventArgs e) => EditSelection();

    private void OnItemDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true) != null) EditSelection();
    }

    public void EditSelection()
    {
        foreach (var path in SelectedPaths.Take(10)) App.Instance.OpenEditor(path);
    }

    /// <summary>Reads the text of the first selected image (OCR).</summary>
    public void ReadTextOfSelection()
    {
        if (SelectedPaths.FirstOrDefault(ImageIO.IsImage) is { } path) App.Instance.ShowOcr(path);
    }

    private void OnReadText(object? sender, RoutedEventArgs e) => ReadTextOfSelection();

    /// <summary>Decodes the QR codes of the first selected image.</summary>
    public void ReadQrOfSelection()
    {
        if (SelectedPaths.FirstOrDefault(ImageIO.IsImage) is { } path) App.Instance.ShowQr(path);
    }

    private void OnReadQr(object? sender, RoutedEventArgs e) => ReadQrOfSelection();

    /// <summary>"Create a document" dialog with the selected images.</summary>
    public void CreateDocumentFromSelection()
    {
        var images = SelectedImages;
        if (images.Count == 0) return;
        _ = DocumentWindow.ShowForAsync(OwnerWindow, images);
    }

    private void OnCreateDocument(object? sender, RoutedEventArgs e) => CreateDocumentFromSelection();

    public void PrintSelection()
    {
        var images = SelectedImages;
        if (images.Count == 0 || OwnerWindow is not { } owner) return;
        ShareService.Print(owner, images);
    }

    private void OnPrint(object? sender, RoutedEventArgs e) => PrintSelection();

    /// <summary>New message of the mail application with the selected captures attached (videos included).</summary>
    public void EmailSelection()
    {
        var paths = SelectedPathsInGalleryOrder;
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        ShareService.Email(owner, paths);
    }

    private void OnEmail(object? sender, RoutedEventArgs e) => EmailSelection();

    private void OnCopy(object? sender, RoutedEventArgs e) => CopySelection();

    public void CopySelection() => _ = CopySelectionAsync();

    private async Task CopySelectionAsync()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        try
        {
            var clipboard = AppServices.Platform.Clipboard;
            if (paths.Count == 1 && ImageIO.IsImage(paths[0]))
            {
                var path = paths[0];
                var (image, png) = await Task.Run(() =>
                {
                    var loaded = ImageIO.Load(path);
                    return (loaded, ImageIO.EncodePng(loaded));
                });
                Message?.Invoke(await clipboard.SetImageAsync(image, png) ? Loc.T("Gallery.ImageCopied") : Loc.T("Ocr.CopyFailed"));
            }
            else
            {
                // Videos and multiple files are copied as files (paste in the file manager, mail, chat...).
                bool copied = await clipboard.SetFilesAsync(paths);
                Message?.Invoke(!copied ? Loc.T("Ocr.CopyFailed")
                    : paths.Count == 1 ? Loc.T("Gallery.FileCopied") : Loc.T("Gallery.FilesCopied", paths.Count));
            }
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.T("Gallery.CopyFailed"), ex);
        }
    }

    private async void OnCopyPath(object? sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        try
        {
            bool copied = await AppServices.Platform.Clipboard.SetTextAsync(string.Join(Environment.NewLine, paths));
            Message?.Invoke(copied ? Loc.Plural(paths.Count, "Gallery.PathCopied.One", "Gallery.PathCopied.Many") : Loc.T("Ocr.CopyFailed"));
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.T("Gallery.CopyFailed"), ex);
        }
    }

    private void OnOpenDefault(object? sender, RoutedEventArgs e)
    {
        foreach (var path in SelectedPaths.Take(10))
        {
            // Fails when no application is associated with the file type, for instance (logged by FileOps).
            if (FileOps.OpenWithDefaultApp(path)) continue;
            _ = Dialogs.ShowAsync(OwnerWindow, Loc.T("Gallery.OpenFailed"), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return;
        }
    }

    private void OnShowInFolder(object? sender, RoutedEventArgs e)
    {
        if (SelectedPaths.FirstOrDefault() is not { } path) return;
        try
        {
            FileOps.Reveal(path);
        }
        catch (Exception ex)
        {
            _ = ShowErrorAsync(Loc.T("Gallery.OpenFailed"), ex);
        }
    }

    private void OnRename(object? sender, RoutedEventArgs e) => RenameSelection();

    public void RenameSelection() => _ = RenameSelectionAsync();

    private async Task RenameSelectionAsync()
    {
        if (SelectedPaths.FirstOrDefault() is not { } path || OwnerWindow is not { } owner) return;
        var title = Loc.T("Gallery.Rename.Title");
        if (IsOpen(path))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Rename.CloseViewer"), title, DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        var value = await InputDialog.AskAsync(owner, title, Loc.T("Gallery.Rename.Label"), Path.GetFileName(path));
        if (value == null) return;

        var typed = value.Trim();
        var extension = Path.GetExtension(path);
        // Windows drops trailing dots and spaces: remove them so the final name is the one shown (everywhere: captures move between systems).
        var name = FileOps.SanitizeFileName(typed).TrimEnd('.', ' ');
        // The extension tells the system how to open the file: always keep the original one.
        bool sameExtension = string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);
        if (!sameExtension) name += extension;
        if (name.Length <= extension.Length) return; // nothing left but the extension

        // Tell the user when the name is not exactly what was typed (invalid characters, other extension).
        bool typedOtherExtension = ImageIO.IsCapture(typed) &&
                                   !string.Equals(Path.GetExtension(typed), extension, StringComparison.OrdinalIgnoreCase);
        var expected = string.Equals(Path.GetExtension(typed), extension, StringComparison.OrdinalIgnoreCase) ? typed : typed + extension;
        if ((typedOtherExtension || !string.Equals(name, expected, StringComparison.Ordinal)) &&
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Rename.Adjusted", name), title, DialogButtons.OkCancel, DialogIcon.Information) != DialogResult.Ok)
            return;

        var target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (string.Equals(target, path, StringComparison.Ordinal)) return;
        // Another file with that name (only the case differs: the same file on Windows, which allows the rename).
        if (File.Exists(target) && !(OperatingSystem.IsWindows() && string.Equals(target, path, StringComparison.OrdinalIgnoreCase)))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Rename.NameTaken"), title, DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.T("Gallery.Rename.Failed"), ex);
            return;
        }
        try
        {
            SidecarFiles.Rename(path, target);
        }
        catch (Exception ex)
        {
            // The capture is renamed; only its annotations stay behind (the image itself is intact).
            ErrorLog.Write("Renommage des annotations", ex);
        }
        _pendingSelection = target;
        Refresh();
    }

    private void OnDelete(object? sender, RoutedEventArgs e) => DeleteSelection();

    public void DeleteSelection() => _ = DeleteSelectionAsync();

    /// <summary>Sends the selected captures to the trash, after a confirmation whose default answer is No.</summary>
    private async Task DeleteSelectionAsync()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        var title = Loc.T("Gallery.Delete.Title");
        if (paths.Any(IsOpen))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Delete.CloseViewer"), title, DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        var question = paths.Count == 1
            ? Loc.T("Gallery.Delete.ConfirmOne", Path.GetFileName(paths[0]))
            : Loc.T("Gallery.Delete.ConfirmMany", paths.Count);
        if (await Dialogs.ShowAsync(owner, question, title, DialogButtons.YesNo, DialogIcon.Question, DialogResult.No) != DialogResult.Yes)
            return;
        int index = CaptureList.SelectedIndex;
        bool deleted = await Task.Run(() => FileOps.MoveToTrash(paths));
        if (!deleted)
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Delete.Failed"), title, DialogButtons.Ok, DialogIcon.Warning);

        // The editable version (base image + annotations) goes to the trash with its capture.
        var sidecars = new List<string>();
        foreach (var path in paths.Where(p => !File.Exists(p)))
        {
            try
            {
                var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
                sidecars.Add(basePath);
                sidecars.Add(jsonPath);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Suppression des annotations", ex);
            }
        }
        if (sidecars.Count > 0 && !await Task.Run(() => FileOps.MoveToTrash(sidecars)))
            ErrorLog.Write("Suppression des annotations", new IOException(string.Join(", ", sidecars.Where(File.Exists))));

        _pendingIndex = index;
        Refresh();
    }

    /// <summary>Moves captures (and their annotations) to another workspace.</summary>
    public async Task MoveToWorkspaceAsync(IReadOnlyList<string> paths, string workspace)
    {
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        if (paths.Any(IsOpen))
        {
            await Dialogs.ShowAsync(owner, Loc.T("Gallery.Move.CloseViewer"), Loc.T("Gallery.Move.Title"), DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        try
        {
            var summary = Workspaces.MoveFiles(paths, workspace);
            if (!string.IsNullOrEmpty(summary)) Message?.Invoke(summary);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync(Loc.T("Gallery.Move.Failed"), ex);
        }
        Refresh();
    }

    /// <summary>Fills "Move to" with the other workspaces, and enables the commands that apply to the selection.</summary>
    private void OnItemMenuOpening(object? sender, CancelEventArgs e)
    {
        MoveToMenu.Items.Clear();
        var active = Workspaces.ActiveName;
        foreach (var name in Workspaces.List().Where(n => !string.Equals(n, active, StringComparison.OrdinalIgnoreCase)))
        {
            // A TextBlock, so that an underscore in the name is not taken for an access key.
            var item = new MenuItem { Header = new TextBlock { Text = Workspaces.DisplayName(name) } };
            var target = name;
            item.Click += (_, _) => _ = MoveToWorkspaceAsync(SelectedPaths, target);
            MoveToMenu.Items.Add(item);
        }
        if (MoveToMenu.Items.Count == 0)
            MoveToMenu.Items.Add(new MenuItem { Header = Loc.T("Gallery.Menu.NoOtherWorkspace"), IsEnabled = false });
        MoveToMenu.IsEnabled = SelectedCount > 0;

        bool hasImage = SelectedPaths.Any(ImageIO.IsImage);
        ReadTextMenu.IsEnabled = ReadQrMenu.IsEnabled = CreateDocumentMenu.IsEnabled = PrintMenu.IsEnabled = hasImage;
        EmailMenu.IsEnabled = SelectedCount > 0;
    }

    /// <summary>True when the capture is open in an editor or in the video player.</summary>
    internal static bool IsOpen(string path) =>
        App.Instance.OpenWindows.OfType<EditorWindow>().Any(w => App.IsSamePath(w.FilePath, path)) ||
        App.Instance.OpenWindows.OfType<PlayerWindow>().Any(w => App.IsSamePath(w.FilePath, path));

    /// <summary>True when a capture of this folder is open in an editor or in the video player.</summary>
    internal static bool IsAnyOpenIn(string folder) =>
        App.Instance.OpenWindows.OfType<EditorWindow>().Select(w => w.FilePath)
            .Concat(App.Instance.OpenWindows.OfType<PlayerWindow>().Select(w => w.FilePath))
            .Any(path => SameFolder(Path.GetDirectoryName(path), folder));

    /// <summary>Compares two folders whatever their spelling (relative parts, trailing separator; case on Windows).</summary>
    internal static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), App.PathComparison);
        }
        catch (Exception)
        {
            return false; // not a valid path
        }
    }

    private Task ShowErrorAsync(string message, Exception ex) => WorkspaceCommands.ShowErrorAsync(OwnerWindow, message, ex);

    private void OnListKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: EditSelection(); break;
            case Key.Delete: DeleteSelection(); break;
            case Key.F2: RenameSelection(); break;
            case Key.C when e.KeyModifiers == KeyModifiers.Control: CopySelection(); break;
            case Key.P when e.KeyModifiers == KeyModifiers.Control: PrintSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- Drag and drop ----

    private void OnListPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragArmed = false;
        _pressedSelectedItem = null;
        if (!e.GetCurrentPoint(CaptureList).Properties.IsLeftButtonPressed) return;
        _dragStart = e.GetPosition(CaptureList);
        var container = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        _dragArmed = container != null;
        _pressArgs = e;
        // Pressing one of several selected captures keeps them all selected, so that they can be
        // dragged together; it becomes a single selection on release if no drag started.
        if (container is { IsSelected: true } && e.ClickCount == 1 && e.KeyModifiers == KeyModifiers.None && SelectedCount > 1)
        {
            _pressedSelectedItem = container;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        _dragArmed = false;
        _pressArgs = null;
        if (_pressedSelectedItem is not { } container) return;
        _pressedSelectedItem = null;
        if (container.DataContext is not CaptureItem item) return;
        CaptureList.SelectedItems?.Clear();
        CaptureList.SelectedItem = item;
    }

    /// <summary>Drag captures out to other applications (mail, chat, documents...) or to another workspace.</summary>
    private void OnListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (!_dragArmed || _pressArgs is not { } press) return;
        if (!e.GetCurrentPoint(CaptureList).Properties.IsLeftButtonPressed)
        {
            _dragArmed = false;
            return;
        }
        var delta = e.GetPosition(CaptureList) - _dragStart;
        if (Math.Abs(delta.X) < DragDistance && Math.Abs(delta.Y) < DragDistance) return;

        _dragArmed = false;
        _pressedSelectedItem = null;
        _pressArgs = null;
        _ = DragOutAsync(press);
    }

    private async Task DragOutAsync(PointerPressedEventArgs press)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0 || TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;
        _draggingOut = true;
        try
        {
            var data = new DataTransfer();
            foreach (var path in paths)
            {
                if (await storage.TryGetFileFromPathAsync(path) is { } file) data.Add(DataTransferItem.CreateFile(file));
            }
            data.Add(DataTransferItem.Create(DragFormat, string.Join("\n", paths)));
            // Copy only: other applications (file managers...) must never move captures out of the workspace.
            await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Glisser-déposer vers une autre application", ex);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    /// <summary>Paths of a drag-and-drop that can be imported: captures that are not already in this workspace.</summary>
    private List<string> ImportableFiles(DragEventArgs e) =>
        (e.DataTransfer.TryGetFiles() ?? Array.Empty<IStorageItem>())
        .Select(item => item.TryGetLocalPath())
        .OfType<string>()
        .Where(f => ImageIO.IsCapture(f) && !SameFolder(Path.GetDirectoryName(f), _folder))
        .ToList();

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = !_draggingOut && !_importing && ImportableFiles(e).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop image files in to add them to the workspace (copied in the background).</summary>
    private async void OnDrop(object? sender, DragEventArgs e)
    {
        if (_draggingOut || _importing) return;
        var files = ImportableFiles(e);
        if (files.Count == 0) return;
        e.Handled = true;
        _importing = true;
        var folder = _folder;
        Message?.Invoke(Loc.Plural(files.Count, "Gallery.Importing.One", "Gallery.Importing.Many"));
        try
        {
            var (last, failed) = await Task.Run(() => Import(files, folder));
            _pendingSelection = last;
            if (failed > 0)
                await Dialogs.ShowAsync(OwnerWindow, Loc.T("Gallery.ImportFailed", failed, files.Count), "Snaplume", DialogButtons.Ok, DialogIcon.Warning);
        }
        finally
        {
            _importing = false;
        }
        Refresh();
    }

    private static (string? Last, int Failed) Import(List<string> files, string folder)
    {
        string? last = null;
        int failed = 0;
        foreach (var file in files)
        {
            try
            {
                if (!File.Exists(file)) continue; // a folder, or gone meanwhile
                var target = FileOps.UniquePath(folder, Path.GetFileNameWithoutExtension(file), Path.GetExtension(file));
                File.Copy(file, target);
                try
                {
                    // Shown first, like a new capture (a copy is already new where the creation time cannot be set).
                    File.SetCreationTime(target, DateTime.Now);
                }
                catch (Exception)
                {
                    // Not supported by this file system.
                }
                last = target;
            }
            catch (Exception ex)
            {
                failed++;
                ErrorLog.Write("Import par glisser-déposer", ex);
            }
        }
        return (last, failed);
    }
}
