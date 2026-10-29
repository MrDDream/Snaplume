using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;
using Snaplume.Workspace;

namespace Snaplume.Export;

/// <summary>
/// "Create a document": a PDF, Word or HTML file made of the selected captures, in the order of the
/// list. The document is written in the background; the dialog can cancel it.
/// </summary>
public partial class DocumentWindow : Window
{
    /// <summary>One capture of the list.</summary>
    public sealed class Entry : INotifyPropertyChanged
    {
        private BitmapSource? _thumbnail;
        private int _position;

        public Entry(string path)
        {
            Path = path;
            try
            {
                Details = File.GetCreationTime(path).ToString("g", Loc.Culture);
            }
            catch (Exception)
            {
                Details = "";
            }
        }

        public string Path { get; }
        public string Name => System.IO.Path.GetFileName(Path);
        public string Details { get; }

        public int Position
        {
            get => _position;
            set
            {
                if (_position == value) return;
                _position = value;
                OnPropertyChanged();
            }
        }

        public BitmapSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                _thumbnail = value;
                OnPropertyChanged();
            }
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>Item of a drop-down list.</summary>
    public sealed record Choice<T>(T Value, string Label);

    /// <summary>Choices remembered from one document to the next (document.json in the settings folder).</summary>
    private sealed class Remembered
    {
        public DocumentKind Kind { get; set; } = DocumentKind.Pdf;
        public bool OnePerPage { get; set; } = true;
        public DocumentPageSize? PageSize { get; set; }
        public DocumentOrientation Orientation { get; set; } = DocumentOrientation.Auto;
        public bool IncludeFileName { get; set; } = true;
        public bool IncludeDate { get; set; }
        public bool OpenWhenDone { get; set; } = true;
    }

    private static string RememberedFile => Path.Combine(AppSettings.SettingsFolder, "document.json");

    private readonly ObservableCollection<Entry> _entries = new();
    private readonly CancellationTokenSource _closing = new();
    private CancellationTokenSource? _generation;
    /// <summary>The document being written, and the temporary file it is written to.</summary>
    private Task? _generationTask;
    private string? _generationTemp;
    private bool _closeWhenStopped;
    private bool _closed;
    /// <summary>Longest wait for a cancelled generation when the window closes (or Snaplume quits).</summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(3);

    /// <param name="paths">Captures in gallery order; videos and other files are left out.</param>
    public DocumentWindow(Window? owner, IReadOnlyList<string> paths)
    {
        InitializeComponent();
        if (owner != null) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        foreach (var path in paths.Where(ImageIO.IsImage)) _entries.Add(new Entry(path));
        EntryList.ItemsSource = _entries;
        Renumber();

        FormatBox.ItemsSource = new[]
        {
            new Choice<DocumentKind>(DocumentKind.Pdf, Loc.T("Export.Format.Pdf")),
            new Choice<DocumentKind>(DocumentKind.Docx, Loc.T("Export.Format.Docx")),
            new Choice<DocumentKind>(DocumentKind.Html, Loc.T("Export.Format.Html")),
        };
        LayoutBox.ItemsSource = new[]
        {
            new Choice<bool>(true, Loc.T("Export.Layout.OnePerPage")),
            new Choice<bool>(false, Loc.T("Export.Layout.Flowing")),
        };
        PageSizeBox.ItemsSource = new[]
        {
            new Choice<DocumentPageSize>(DocumentPageSize.A4, Loc.T("Export.PageSize.A4")),
            new Choice<DocumentPageSize>(DocumentPageSize.Letter, Loc.T("Export.PageSize.Letter")),
        };
        OrientationBox.ItemsSource = new[]
        {
            new Choice<DocumentOrientation>(DocumentOrientation.Auto, Loc.T("Export.Orientation.Auto")),
            new Choice<DocumentOrientation>(DocumentOrientation.Portrait, Loc.T("Export.Orientation.Portrait")),
            new Choice<DocumentOrientation>(DocumentOrientation.Landscape, Loc.T("Export.Orientation.Landscape")),
        };

        var remembered = LoadRemembered();
        Select(FormatBox, remembered.Kind);
        Select(LayoutBox, remembered.OnePerPage);
        // Letter where the metric system is not used (United States...), A4 elsewhere.
        Select(PageSizeBox, remembered.PageSize ?? (RegionInfo.CurrentRegion.IsMetric ? DocumentPageSize.A4 : DocumentPageSize.Letter));
        Select(OrientationBox, remembered.Orientation);
        FileNameCheck.IsChecked = remembered.IncludeFileName;
        DateCheck.IsChecked = remembered.IncludeDate;
        OpenWhenDoneCheck.IsChecked = remembered.OpenWhenDone;

        var workspace = Workspaces.ActiveName;
        TitleBox.Text = workspace.Length == 0 ? Loc.T("Export.DefaultTitle") : Workspaces.DisplayName(workspace);

        UpdateState();
        Loaded += (_, _) =>
        {
            LoadThumbnails();
            TitleBox.Focus();
            TitleBox.SelectAll();
        };
        Closing += OnClosing;
        Closed += OnClosed;
    }

    private bool IsGenerating => _generation != null;

    private static void Select<T>(ComboBox box, T value)
    {
        box.SelectedItem = box.Items.Cast<Choice<T>>().FirstOrDefault(c => EqualityComparer<T>.Default.Equals(c.Value, value))
                           ?? box.Items[0];
    }

    private static T Selected<T>(ComboBox box) => ((Choice<T>)box.SelectedItem).Value;

    private DocumentKind SelectedKind => Selected<DocumentKind>(FormatBox);

    // ---- Capture list ----

    private async void LoadThumbnails()
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        int width = (int)Math.Ceiling(70 * dpi.DpiScaleX), height = (int)Math.Ceiling(46 * dpi.DpiScaleY);
        foreach (var entry in _entries.ToList())
        {
            if (_closing.IsCancellationRequested) return;
            try
            {
                var file = new FileInfo(entry.Path);
                if (!file.Exists) continue;
                var thumbnail = await ThumbnailCache.GetAsync(entry.Path, file.LastWriteTimeUtc, file.Length, false, width, height, _closing.Token);
                entry.Thumbnail = thumbnail?.Image;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // The list still works without the picture.
                ErrorLog.Write($"Miniature de {entry.Path}", ex);
            }
        }
    }

    private void Renumber()
    {
        for (int i = 0; i < _entries.Count; i++) _entries[i].Position = i + 1;
        CountText.Text = Loc.Plural(_entries.Count, "Export.Count.One", "Export.Count.Many");
    }

    private List<Entry> SelectedEntries() =>
        _entries.Where(e => EntryList.SelectedItems.Contains(e)).ToList();

    private void OnMoveUp(object sender, RoutedEventArgs e) => MoveSelection(-1);

    private void OnMoveDown(object sender, RoutedEventArgs e) => MoveSelection(1);

    /// <summary>Moves the selected captures one row up or down, keeping their selection.</summary>
    private void MoveSelection(int direction)
    {
        var selected = SelectedEntries();
        if (selected.Count == 0 || IsGenerating) return;
        var indexes = selected.Select(_entries.IndexOf).ToList();
        if (direction < 0 && indexes.Min() == 0) return;
        if (direction > 0 && indexes.Max() == _entries.Count - 1) return;

        // Up: from the top; down: from the bottom, so that moved items never cross each other.
        foreach (var index in direction < 0 ? indexes.OrderBy(i => i) : indexes.OrderByDescending(i => i))
            _entries.Move(index, index + direction);
        Renumber();
        foreach (var entry in selected.Where(e => !EntryList.SelectedItems.Contains(e))) EntryList.SelectedItems.Add(entry);
        EntryList.ScrollIntoView(direction < 0 ? selected[0] : selected[^1]);
        UpdateState();
    }

    private void OnRemove(object sender, RoutedEventArgs e) => RemoveSelection();

    private void RemoveSelection()
    {
        var selected = SelectedEntries();
        // Same rule as the button: at least one capture stays in the list (Ctrl+A then Del included).
        if (selected.Count == 0 || selected.Count >= _entries.Count || IsGenerating) return;
        int index = _entries.IndexOf(selected[0]);
        foreach (var entry in selected) _entries.Remove(entry);
        Renumber();
        if (_entries.Count > 0) EntryList.SelectedIndex = Math.Min(index, _entries.Count - 1);
        UpdateState();
    }

    private void OnEntrySelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void OnEntryKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete) RemoveSelection();
        else if (e.Key == Key.Up && Keyboard.Modifiers == ModifierKeys.Control) MoveSelection(-1);
        else if (e.Key == Key.Down && Keyboard.Modifiers == ModifierKeys.Control) MoveSelection(1);
        else return;
        e.Handled = true;
    }

    private void OnFormatChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FormatBox.SelectedItem == null) return;
        // HTML: the page size and orientation only apply when the page is printed.
        HtmlNote.Visibility = SelectedKind == DocumentKind.Html ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateState()
    {
        bool idle = !IsGenerating;
        var indexes = SelectedEntries().Select(_entries.IndexOf).ToList();
        UpButton.IsEnabled = idle && indexes.Count > 0 && indexes.Min() > 0;
        DownButton.IsEnabled = idle && indexes.Count > 0 && indexes.Max() < _entries.Count - 1;
        RemoveButton.IsEnabled = idle && indexes.Count > 0 && indexes.Count < _entries.Count;
        CreateButton.IsEnabled = idle && _entries.Count > 0;
        OptionsPanel.IsEnabled = idle;
        EntryList.IsEnabled = idle;
        CloseButton.Content = Loc.T(idle ? "Export.Close" : "Export.Cancel");
        if (idle && _entries.Count == 0) StatusText.Text = Loc.T("Export.NoImage");
    }

    // ---- Creation ----

    private DocumentOptions ReadOptions() => new()
    {
        Kind = SelectedKind,
        Title = TitleBox.Text.Trim(),
        OnePerPage = Selected<bool>(LayoutBox),
        PageSize = Selected<DocumentPageSize>(PageSizeBox),
        Orientation = Selected<DocumentOrientation>(OrientationBox),
        IncludeFileName = FileNameCheck.IsChecked == true,
        IncludeDate = DateCheck.IsChecked == true,
        Culture = Loc.Culture,
        Language = Loc.Code,
    };

    private async void OnCreate(object sender, RoutedEventArgs e)
    {
        if (IsGenerating || _entries.Count == 0) return;
        var options = ReadOptions();
        if (options.Kind == DocumentKind.Docx && DocxTooLarge()) return;
        var path = AskOutputPath(options);
        if (path == null) return;
        SaveRemembered(options);

        var paths = _entries.Select(entry => entry.Path).ToList();
        _generation = new CancellationTokenSource();
        var cancellation = _generation.Token;
        Progress.Maximum = paths.Count;
        Progress.Value = 0;
        Progress.Visibility = Visibility.Visible;
        StatusText.Text = Loc.T("Export.Creating", 0, paths.Count);
        UpdateState();
        var progress = new Progress<int>(done =>
        {
            if (!IsGenerating) return;
            Progress.Value = done;
            StatusText.Text = Loc.T("Export.Creating", done, paths.Count);
        });

        DocumentResult? result = null;
        Exception? error = null;
        try
        {
            _generationTemp = DocumentGenerator.TempPathOf(path);
            var task = Task.Run(() => DocumentGenerator.Generate(paths, options, path, progress, cancellation), cancellation);
            _generationTask = task;
            result = await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            error = ex;
        }
        finally
        {
            _generation.Dispose();
            _generation = null;
            _generationTask = null;
            _generationTemp = null;
            Progress.Visibility = Visibility.Collapsed;
            UpdateState();
        }

        // Already closed (the generation was stopped while the window closed, or Snaplume quit).
        if (_closed) return;
        if (_closeWhenStopped)
        {
            Close();
            return;
        }
        if (error != null)
        {
            ErrorLog.Write("Création d'un document", error);
            StatusText.Text = "";
            var message = error switch
            {
                InvalidDataException => Loc.T("Export.NoReadableImage"),
                DocumentTooLargeException tooLarge => DocxTooLargeMessage(tooLarge.Bytes),
                _ => Loc.T("Export.Failed", error.Message),
            };
            MessageBox.Show(this, message, Title, MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        if (result == null)
        {
            StatusText.Text = Loc.T("Export.Cancelled");
            return;
        }

        if (result.Skipped.Count > 0)
        {
            var names = string.Join("\n", result.Skipped.Take(8).Select(Path.GetFileName));
            if (result.Skipped.Count > 8) names += "\n…";
            MessageBox.Show(this, Loc.T("Export.SomeSkipped", result.Skipped.Count, names), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        ShowResult(result.Path, OpenWhenDoneCheck.IsChecked == true);
        Close();
    }

    /// <summary>
    /// A Word document is built in memory: beyond <see cref="DocumentGenerator.MaxDocxImageBytes"/> of images,
    /// explains it (PDF and HTML have no such limit) instead of asking where to save it.
    /// </summary>
    private bool DocxTooLarge()
    {
        long total = 0;
        foreach (var entry in _entries)
        {
            try
            {
                total += new FileInfo(entry.Path).Length;
            }
            catch (Exception)
            {
                // Missing or unreadable: left out of the document anyway.
            }
        }
        if (total <= DocumentGenerator.MaxDocxImageBytes) return false;
        MessageBox.Show(this, DocxTooLargeMessage(total), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
        return true;
    }

    private static string DocxTooLargeMessage(long bytes) =>
        Loc.T("Export.DocxTooLarge", Math.Ceiling(bytes / 1048576.0), DocumentGenerator.MaxDocxImageBytes / 1048576);

    /// <summary>Opens the document with its application, or shows it in Explorer (also when no application can open it).</summary>
    private static void ShowResult(string path, bool open)
    {
        if (open && FileOps.OpenWithDefaultApp(path)) return;
        try
        {
            FileOps.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans l'Explorateur", ex);
        }
    }

    /// <summary>Save dialog in the active workspace, named after the title. Null if cancelled.</summary>
    private string? AskOutputPath(DocumentOptions options)
    {
        var extension = DocumentOptions.ExtensionOf(options.Kind);
        var name = FileOps.SanitizeFileName(options.Title).Trim().TrimEnd('.', ' ');
        if (name.Length == 0) name = FileOps.SanitizeFileName(Loc.T("Export.DefaultTitle"));
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Export.SaveTitle"),
            Filter = options.Kind switch
            {
                DocumentKind.Docx => $"{Loc.T("Export.Format.Docx")}|*.docx",
                DocumentKind.Html => $"{Loc.T("Export.Format.Html")}|*.html;*.htm",
                _ => $"{Loc.T("Export.Format.Pdf")}|*.pdf",
            },
            DefaultExt = extension,
            AddExtension = true,
            OverwritePrompt = true,
            FileName = name + extension,
        };
        try
        {
            var folder = Workspaces.ActiveFolder;
            if (Directory.Exists(folder)) dialog.InitialDirectory = folder;
        }
        catch (Exception)
        {
            // Settings not available: the dialog opens in its last folder.
        }
        if (dialog.ShowDialog(this) != true) return null;

        var path = dialog.FileName;
        var typed = Path.GetExtension(path);
        bool valid = string.Equals(typed, extension, StringComparison.OrdinalIgnoreCase) ||
                     (options.Kind == DocumentKind.Html && string.Equals(typed, ".htm", StringComparison.OrdinalIgnoreCase));
        if (!valid)
        {
            // Another extension typed by hand: the document keeps its real format.
            path += extension;
            if (File.Exists(path) && MessageBox.Show(this, Loc.T("Export.Overwrite", Path.GetFileName(path)), Title,
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return null;
        }
        if (_entries.Any(entry => string.Equals(Path.GetFullPath(entry.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase)))
        {
            MessageBox.Show(this, Loc.T("Export.SameAsCapture"), Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }
        return path;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        // Escape on an open drop-down list only closes the list.
        if (e.Key != Key.Escape || e.Handled || FormatBox.IsDropDownOpen || LayoutBox.IsDropDownOpen ||
            PageSizeBox.IsDropDownOpen || OrientationBox.IsDropDownOpen) return;
        e.Handled = true;
        OnCloseOrCancel(this, e);
    }

    private void OnCloseOrCancel(object sender, RoutedEventArgs e)
    {
        if (IsGenerating)
        {
            _generation!.Cancel();
            StatusText.Text = Loc.T("Export.Cancelling");
            return;
        }
        Close();
    }

    /// <summary>
    /// Closing while the document is written: cancel it and wait a little for it to stop (it then deletes its
    /// temporary file). Still running after that: the window stays open until it stops, except when Snaplume
    /// quits (WPF then ignores e.Cancel): see <see cref="OnClosed"/>.
    /// </summary>
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (!IsGenerating) return;
        _generation!.Cancel();
        if (WaitForGeneration()) return;
        e.Cancel = true;
        _closeWhenStopped = true;
        StatusText.Text = Loc.T("Export.Cancelling");
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _closed = true;
        _closing.Cancel();
        if (!IsGenerating) return;
        // Closed anyway while writing (Snaplume is quitting): the background thread would die with the process
        // and leave the half-written ".tmp" file behind.
        _generation!.Cancel();
        WaitForGeneration();
        DeleteTemp(_generationTemp);
    }

    /// <summary>Waits (blocking, briefly) for the cancelled generation. True if it has stopped.</summary>
    private bool WaitForGeneration()
    {
        if (_generationTask is not { } task) return true;
        try
        {
            return task.Wait(StopTimeout);
        }
        catch (AggregateException)
        {
            return true; // stopped: cancelled or failed (reported by OnCreate)
        }
    }

    private static void DeleteTemp(string? temp)
    {
        if (temp == null) return;
        try
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        catch (Exception ex)
        {
            // Still open by the generation: nothing more can be done while quitting.
            ErrorLog.Write("Suppression du fichier temporaire d'un document", ex);
        }
    }

    // ---- Remembered choices ----

    private static Remembered LoadRemembered()
    {
        try
        {
            if (File.Exists(RememberedFile))
                return JsonSerializer.Deserialize<Remembered>(File.ReadAllText(RememberedFile)) ?? new Remembered();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecture des options de document", ex);
        }
        return new Remembered();
    }

    private void SaveRemembered(DocumentOptions options)
    {
        try
        {
            var remembered = new Remembered
            {
                Kind = options.Kind,
                OnePerPage = options.OnePerPage,
                PageSize = options.PageSize,
                Orientation = options.Orientation,
                IncludeFileName = options.IncludeFileName,
                IncludeDate = options.IncludeDate,
                OpenWhenDone = OpenWhenDoneCheck.IsChecked == true,
            };
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            File.WriteAllText(RememberedFile, JsonSerializer.Serialize(remembered, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            // Only the remembered choices are lost.
            ErrorLog.Write("Enregistrement des options de document", ex);
        }
    }
}
