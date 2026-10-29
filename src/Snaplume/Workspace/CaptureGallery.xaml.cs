using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Export;
using Snaplume.Player;

namespace Snaplume.Workspace;

/// <summary>
/// Thumbnails of the captures stored in the active workspace folder, with the file commands.
/// Shared by the full workspace window and the quick panel.
/// </summary>
public partial class CaptureGallery : UserControl
{
    /// <summary>Data format added to the captures dragged out of a gallery (the workspace list only accepts these).</summary>
    public const string DragFormat = "Snaplume.GalleryCaptures";
    private const int MaxThumbnailRetries = 3;

    /// <summary>File information read in the background.</summary>
    private sealed record FileEntry(string Path, DateTime Created, DateTime CreatedUtc, DateTime ModifiedUtc, long Length);

    private readonly ObservableCollection<CaptureItem> _items = new();
    private readonly Dictionary<string, CaptureItem> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _refreshTimer;
    private readonly DispatcherTimer _watcherRestartTimer;
    private FileSystemWatcher? _watcher;
    private string _folder = "";
    private CancellationTokenSource _thumbnails = new();
    private int _refreshRun;
    private bool _refreshPending = true;
    private bool _focusWhenLoaded;
    private bool _importing;
    private string? _pendingSelection;
    private int? _pendingIndex;
    private Point _dragStart;
    private bool _dragArmed;
    private bool _draggingOut;
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
        // A hidden window does not read its folder: it catches up when shown.
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible && _refreshPending) Refresh();
        };
        App.Instance.Captures.CaptureSaved += OnCaptureSaved;
        Workspaces.Changed += OnWorkspacesChanged;
        Reload();
    }

    public int SelectedCount => CaptureList.SelectedItems.Count;
    public List<string> SelectedPaths => CaptureList.SelectedItems.Cast<CaptureItem>().Select(i => i.Path).ToList();

    /// <summary>Reads the text of the first selected image (OCR).</summary>
    public void ReadTextOfSelection()
    {
        if (SelectedPaths.FirstOrDefault(ImageIO.IsImage) is { } path) App.Instance.ShowOcr(path);
    }

    private void OnReadText(object sender, RoutedEventArgs e) => ReadTextOfSelection();

    /// <summary>Selected captures in the order shown in the gallery (not the order they were clicked).</summary>
    public List<string> SelectedPathsInGalleryOrder
    {
        get
        {
            var selected = new HashSet<CaptureItem>(CaptureList.SelectedItems.Cast<CaptureItem>());
            return _items.Where(selected.Contains).Select(i => i.Path).ToList();
        }
    }

    /// <summary>Selected images, in gallery order (documents and printing leave the videos out).</summary>
    private List<string> SelectedImages => SelectedPathsInGalleryOrder.Where(ImageIO.IsImage).ToList();

    /// <summary>Decodes the QR code of the first selected image.</summary>
    public void ReadQrOfSelection()
    {
        if (SelectedPaths.FirstOrDefault(ImageIO.IsImage) is { } path) App.Instance.ShowQr(path);
    }

    private void OnReadQr(object sender, RoutedEventArgs e) => ReadQrOfSelection();

    /// <summary>"Create a document" dialog with the selected images.</summary>
    public void CreateDocumentFromSelection()
    {
        var images = SelectedImages;
        if (images.Count == 0) return;
        new DocumentWindow(OwnerWindow, images).ShowDialog();
    }

    private void OnCreateDocument(object sender, RoutedEventArgs e) => CreateDocumentFromSelection();

    public void PrintSelection()
    {
        var images = SelectedImages;
        if (images.Count == 0 || OwnerWindow is not { } owner) return;
        ShareService.Print(owner, images);
    }

    private void OnPrint(object sender, RoutedEventArgs e) => PrintSelection();

    /// <summary>New message of the mail client with the selected captures attached (videos included).</summary>
    public void EmailSelection()
    {
        var paths = SelectedPathsInGalleryOrder;
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        ShareService.Email(owner, paths);
    }

    private void OnEmail(object sender, RoutedEventArgs e) => EmailSelection();

    public string SummaryText
    {
        get
        {
            var text = Loc.Plural(_items.Count, "Gallery.Summary.One", "Gallery.Summary.Many");
            if (SelectedCount > 1) text = Loc.T("Gallery.Summary.Selected", text, SelectedCount);
            return text;
        }
    }

    private Window? OwnerWindow => Window.GetWindow(this);

    /// <summary>Stops listening to the application (the window hosting the gallery is closed).</summary>
    public void Detach()
    {
        App.Instance.Captures.CaptureSaved -= OnCaptureSaved;
        Workspaces.Changed -= OnWorkspacesChanged;
        _refreshTimer.Stop();
        _watcherRestartTimer.Stop();
        _thumbnails.Cancel();
        _watcher?.Dispose();
        _watcher = null;
    }

    private void OnCaptureSaved(string path)
    {
        _pendingSelection = path;
        Refresh();
    }

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
            _watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler changed = (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
            _watcher.Created += changed;
            _watcher.Changed += changed;
            _watcher.Deleted += changed;
            _watcher.Renamed += (_, _) => Dispatcher.BeginInvoke(ScheduleRefresh);
            _watcher.Error += (sender, e) => Dispatcher.BeginInvoke(() => OnWatcherError(sender, e.GetException()));
            _watcher.EnableRaisingEvents = true;
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
        if (CaptureList.ItemContainerGenerator.ContainerFromIndex(Math.Max(0, CaptureList.SelectedIndex)) is ListBoxItem item) item.Focus();
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
        if (!IsVisible)
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
            if (!ImageIO.IsCapture(file.Name) || file.Attributes.HasFlag(FileAttributes.Hidden)) continue;
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
                CaptureList.SelectedItems.Clear();
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
            Dispatcher.BeginInvoke(FocusList, DispatcherPriority.Loaded);
        }

        EmptyHint.Visibility = _items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (unavailable)
        {
            EmptyTitle.Text = Loc.T("Gallery.FolderUnavailable");
            EmptyHintText.Text = _folder;
        }
        else
        {
            EmptyTitle.Text = Loc.T("Gallery.Empty");
            EmptyHintText.Text = Hotkey.TryParse(App.Instance.Settings.HotkeyRegion, out var hotkey) && !hotkey.IsEmpty
                ? Loc.T("Gallery.EmptyHintHotkey", hotkey.DisplayName)
                : Loc.T("Gallery.EmptyHint");
        }
        SelectionChanged?.Invoke();
    }

    /// <summary>
    /// Brings the displayed list to the new one with the fewest changes (remove, move, insert),
    /// so that the scroll position, the selection and the existing thumbnails are kept.
    /// </summary>
    private void Synchronize(List<CaptureItem> wanted)
    {
        var selected = CaptureList.SelectedItems.Cast<CaptureItem>().ToList();
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
        foreach (var item in selected.Where(i => keep.Contains(i) && !CaptureList.SelectedItems.Contains(i)))
            CaptureList.SelectedItems.Add(item);
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
        var dpi = VisualTreeHelper.GetDpi(this);
        double width = TryFindResource("GalleryItemWidth") is double w ? w : 200;
        double height = TryFindResource("GalleryThumbnailHeight") is double h ? h : 130;
        // Minus the frame (1 px) and the image margin (2 px) on each side.
        return ((int)Math.Ceiling((width - 6) * dpi.DpiScaleX), (int)Math.Ceiling((height - 6) * dpi.DpiScaleY));
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e) => SelectionChanged?.Invoke();

    // ---- Commands ----

    private void OnEdit(object sender, RoutedEventArgs e) => EditSelection();

    private void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject) != null) EditSelection();
    }

    public void EditSelection()
    {
        foreach (var path in SelectedPaths.Take(10)) App.Instance.OpenEditor(path);
    }

    private void OnCopy(object sender, RoutedEventArgs e) => CopySelection();

    public void CopySelection()
    {
        var paths = SelectedPaths;
        try
        {
            if (paths.Count == 0) return;
            if (paths.Count == 1 && ImageIO.IsImage(paths[0]))
            {
                ClipboardHelper.SetImage(ImageIO.Load(paths[0]));
                Message?.Invoke(Loc.T("Gallery.ImageCopied"));
            }
            else
            {
                // Videos and multiple files are copied as files (paste in Explorer, mail, chat...).
                ClipboardHelper.SetFiles(paths);
                Message?.Invoke(paths.Count == 1 ? Loc.T("Gallery.FileCopied") : Loc.T("Gallery.FilesCopied", paths.Count));
            }
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("Gallery.CopyFailed"), ex);
        }
    }

    private void OnCopyPath(object sender, RoutedEventArgs e)
    {
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        try
        {
            ClipboardHelper.SetText(string.Join(Environment.NewLine, paths));
            Message?.Invoke(Loc.Plural(paths.Count, "Gallery.PathCopied.One", "Gallery.PathCopied.Many"));
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("Gallery.CopyFailed"), ex);
        }
    }

    private void OnOpenDefault(object sender, RoutedEventArgs e)
    {
        foreach (var path in SelectedPaths.Take(10))
        {
            // Fails when no application is associated with the file type, for instance (logged by FileOps).
            if (FileOps.OpenWithDefaultApp(path)) continue;
            if (OwnerWindow is { } owner)
                MessageBox.Show(owner, Loc.T("Gallery.OpenFailed"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
    }

    private void OnShowInExplorer(object sender, RoutedEventArgs e)
    {
        if (SelectedPaths.FirstOrDefault() is not { } path) return;
        try
        {
            FileOps.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("Gallery.OpenFailed"), ex);
        }
    }

    private void OnRename(object sender, RoutedEventArgs e) => RenameSelection();

    public void RenameSelection()
    {
        if (SelectedPaths.FirstOrDefault() is not { } path || OwnerWindow is not { } owner) return;
        var title = Loc.T("Gallery.Rename.Title");
        if (IsOpen(path))
        {
            MessageBox.Show(owner, Loc.T("Gallery.Rename.CloseViewer"), title, MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dialog = new InputDialog(owner, title, Loc.T("Gallery.Rename.Label"), Path.GetFileName(path));
        if (dialog.ShowDialog() != true) return;

        var typed = dialog.Value.Trim();
        var extension = Path.GetExtension(path);
        // Windows drops trailing dots and spaces: remove them so the final name is the one shown.
        var name = FileOps.SanitizeFileName(typed).TrimEnd('.', ' ');
        // The extension tells Windows how to open the file: always keep the original one.
        bool sameExtension = string.Equals(Path.GetExtension(name), extension, StringComparison.OrdinalIgnoreCase);
        if (!sameExtension) name += extension;
        if (name.Length <= extension.Length) return; // nothing left but the extension

        // Tell the user when the name is not exactly what was typed (invalid characters, other extension).
        bool typedOtherExtension = ImageIO.IsCapture(typed) &&
                                   !string.Equals(Path.GetExtension(typed), extension, StringComparison.OrdinalIgnoreCase);
        var expected = string.Equals(Path.GetExtension(typed), extension, StringComparison.OrdinalIgnoreCase) ? typed : typed + extension;
        if ((typedOtherExtension || !string.Equals(name, expected, StringComparison.Ordinal)) &&
            MessageBox.Show(owner, Loc.T("Gallery.Rename.Adjusted", name), title, MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
            return;

        var target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (string.Equals(target, path, StringComparison.Ordinal)) return;
        if (File.Exists(target) && !string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            MessageBox.Show(owner, Loc.T("Gallery.Rename.NameTaken"), title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            File.Move(path, target);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("Gallery.Rename.Failed"), ex);
            return;
        }
        try
        {
            EditorDocument.RenameSidecar(path, target);
        }
        catch (Exception ex)
        {
            // The capture is renamed; only its annotations stay behind (the image itself is intact).
            ErrorLog.Write("Renommage des annotations", ex);
        }
        _pendingSelection = target;
        Refresh();
    }

    private void OnDelete(object sender, RoutedEventArgs e) => DeleteSelection();

    public void DeleteSelection()
    {
        var paths = SelectedPaths;
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        if (paths.Any(IsOpen))
        {
            MessageBox.Show(owner, Loc.T("Gallery.Delete.CloseViewer"), Loc.T("Gallery.Delete.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        int index = CaptureList.SelectedIndex;
        if (!FileOps.MoveToRecycleBin(paths))
            MessageBox.Show(owner, Loc.T("Gallery.Delete.Failed"), Loc.T("Gallery.Delete.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);

        // The editable version (base image + annotations) goes to the Recycle Bin with its capture.
        var sidecars = new List<string>();
        foreach (var path in paths.Where(p => !File.Exists(p)))
        {
            try
            {
                var (basePath, jsonPath) = EditorDocument.SidecarPaths(path);
                sidecars.Add(basePath);
                sidecars.Add(jsonPath);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Suppression des annotations", ex);
            }
        }
        if (!FileOps.MoveToRecycleBin(sidecars))
            ErrorLog.Write("Suppression des annotations", new IOException(string.Join(", ", sidecars.Where(File.Exists))));

        _pendingIndex = index;
        Refresh();
    }

    /// <summary>Moves captures (and their annotations) to another workspace.</summary>
    public void MoveToWorkspace(IReadOnlyList<string> paths, string workspace)
    {
        if (paths.Count == 0 || OwnerWindow is not { } owner) return;
        if (paths.Any(IsOpen))
        {
            MessageBox.Show(owner, Loc.T("Gallery.Move.CloseViewer"), Loc.T("Gallery.Move.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            var summary = Workspaces.MoveFiles(paths, workspace);
            if (!string.IsNullOrEmpty(summary)) Message?.Invoke(summary);
        }
        catch (Exception ex)
        {
            ShowError(Loc.T("Gallery.Move.Failed"), ex);
        }
        Refresh();
    }

    private void OnMoveTo(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string workspace }) MoveToWorkspace(SelectedPaths, workspace);
    }

    /// <summary>Fills "Move to" with the other workspaces.</summary>
    private void OnListContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        MoveToMenu.Items.Clear();
        var active = Workspaces.ActiveName;
        foreach (var name in Workspaces.List().Where(n => !string.Equals(n, active, StringComparison.OrdinalIgnoreCase)))
        {
            // A TextBlock, so that an underscore in the name is not taken for an access key.
            var item = new MenuItem { Header = new TextBlock { Text = Workspaces.DisplayName(name) }, Tag = name };
            item.Click += OnMoveTo;
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
        Application.Current.Windows.OfType<EditorWindow>().Any(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase)) ||
        Application.Current.Windows.OfType<PlayerWindow>().Any(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when a capture of this folder is open in an editor or in the video player.</summary>
    internal static bool IsAnyOpenIn(string folder) =>
        Application.Current.Windows.OfType<EditorWindow>().Select(w => w.FilePath)
            .Concat(Application.Current.Windows.OfType<PlayerWindow>().Select(w => w.FilePath))
            .Any(path => SameFolder(Path.GetDirectoryName(path), folder));

    /// <summary>Compares two folders whatever their spelling (relative parts, trailing separator, case).</summary>
    internal static bool SameFolder(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false; // not a valid path
        }
    }

    private void ShowError(string message, Exception ex)
    {
        ErrorLog.Write(message, ex);
        if (OwnerWindow is { } owner) MessageBox.Show(owner, Loc.T("Gallery.Error", message, ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter: EditSelection(); break;
            case Key.Delete: DeleteSelection(); break;
            case Key.F2: RenameSelection(); break;
            case Key.C when Keyboard.Modifiers == ModifierKeys.Control: CopySelection(); break;
            case Key.P when Keyboard.Modifiers == ModifierKeys.Control: PrintSelection(); break;
            default: return;
        }
        e.Handled = true;
    }

    // ---- Drag and drop ----

    private void OnListPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(null);
        var container = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        _dragArmed = container != null;
        _pressedSelectedItem = null;
        // Pressing one of several selected captures keeps them all selected, so that they can be
        // dragged together; it becomes a single selection on release if no drag started.
        if (container is { IsSelected: true } && e.ClickCount == 1 && Keyboard.Modifiers == ModifierKeys.None && SelectedCount > 1)
        {
            _pressedSelectedItem = container;
            container.Focus();
            e.Handled = true;
        }
    }

    private void OnListPreviewMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragArmed = false;
        if (_pressedSelectedItem is not { } container) return;
        _pressedSelectedItem = null;
        var item = CaptureList.ItemContainerGenerator.ItemFromContainer(container);
        if (item == DependencyProperty.UnsetValue) return;
        CaptureList.SelectedItems.Clear();
        CaptureList.SelectedItem = item;
    }

    /// <summary>Drag captures out to other applications (mail, chat, documents...) or to another workspace.</summary>
    private void OnListPreviewMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(null) - _dragStart;
        if (Math.Abs(delta.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(delta.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _dragArmed = false;
        _pressedSelectedItem = null;
        var paths = SelectedPaths;
        if (paths.Count == 0) return;
        var data = new DataObject(DataFormats.FileDrop, paths.ToArray());
        data.SetData(DragFormat, string.Join("\n", paths));
        _draggingOut = true;
        try
        {
            // Copy only: other applications (Explorer...) must never move captures out of the workspace.
            DragDrop.DoDragDrop(CaptureList, data, DragDropEffects.Copy);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    /// <summary>Paths of a drag-and-drop that can be imported: captures that are not already in this workspace.</summary>
    private List<string> ImportableFiles(IDataObject data) =>
        data.GetData(DataFormats.FileDrop) is string[] files
            ? files.Where(f => ImageIO.IsCapture(f) && !SameFolder(Path.GetDirectoryName(f), _folder)).ToList()
            : new List<string>();

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && !_importing && ImportableFiles(e.Data).Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>Drop image files in to add them to the workspace (copied in the background).</summary>
    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (_draggingOut || _importing) return;
        var files = ImportableFiles(e.Data);
        if (files.Count == 0) return;
        e.Handled = true;
        _importing = true;
        var folder = _folder;
        Message?.Invoke(Loc.Plural(files.Count, "Gallery.Importing.One", "Gallery.Importing.Many"));
        try
        {
            var (last, failed) = await Task.Run(() => Import(files, folder));
            _pendingSelection = last;
            if (failed > 0 && OwnerWindow is { } owner)
                MessageBox.Show(owner, Loc.T("Gallery.ImportFailed", failed, files.Count), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                // Shown first, like a new capture.
                File.SetCreationTime(target, DateTime.Now);
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

    internal static T? FindAncestor<T>(DependencyObject? element) where T : DependencyObject
    {
        while (element != null && element is not T)
        {
            element = element is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(element)
                : LogicalTreeHelper.GetParent(element);
        }
        return element as T;
    }
}
