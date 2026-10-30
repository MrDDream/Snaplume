using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Ui;

namespace Snaplume.Preferences;

/// <summary>
/// The settings, edited on a copy (<see cref="Result"/>) that the application takes when the user saves.
/// Features the system lacks (Wayland...) are hidden or explained.
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly int[] Delays = { 1, 2, 3, 5, 10, 15 };
    private static readonly int[] Framerates = { 15, 24, 30, 60 };
    /// <summary>Webcam sizes offered, in % of the width of the recorded area.</summary>
    private static readonly (int Percent, string Key)[] WebcamSizes =
    {
        (15, "Settings.Video.WebcamSizeSmall"),
        (20, "Settings.Video.WebcamSizeMedium"),
        (30, "Settings.Video.WebcamSizeLarge"),
    };
    /// <summary>Name of the page shown last (the Tag of its navigation entry).</summary>
    private static string _lastPage = "";

    private readonly TaskCompletionSource<bool> _closed = new();
    /// <summary>The camera list is filled: until then, the saved camera is kept as is.</summary>
    private bool _webcamsLoaded;
    private bool _saved;
    private string? _updatePage;

    public AppSettings Result { get; }

    /// <summary>For the XAML designer only.</summary>
    public SettingsWindow() : this(new AppSettings())
    {
    }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;

        var languages = new List<ChoiceItem> { new("", Loc.T("Settings.LanguageAutoSystem")) };
        languages.AddRange(Loc.Languages.Select(l => new ChoiceItem(l.Code, l.Name)));
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(l => Equals(l.Value, settings.Language)) ?? languages[0];
        var themes = new List<ChoiceItem>
        {
            new(ThemeManager.Auto, Loc.T("Settings.ThemeAutoSystem")),
            new(ThemeManager.Light, Loc.T("Settings.ThemeLight")),
            new(ThemeManager.Dark, Loc.T("Settings.ThemeDark")),
        };
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedItem = themes.FirstOrDefault(t => Equals(t.Value, settings.Theme)) ?? themes[0];

        FolderBox.Text = settings.WorkspaceFolder;
        PrefixBox.Text = settings.FileNamePrefix;
        var formats = new List<ChoiceItem>
        {
            new("png", Loc.T("Settings.General.FormatPng")),
            new("jpg", Loc.T("Settings.General.FormatJpeg")),
        };
        FormatBox.ItemsSource = formats;
        FormatBox.SelectedItem = settings.ImageFormat == "jpg" ? formats[1] : formats[0];
        PrefixBox.TextChanged += (_, _) => UpdatePrefixExample();
        FormatBox.SelectionChanged += (_, _) => UpdatePrefixExample();
        EditorCheck.IsChecked = settings.OpenEditorAfterCapture;
        ClipboardCheck.IsChecked = settings.CopyToClipboardAfterCapture;
        WorkspaceCheck.IsChecked = settings.ShowPanelAfterCapture;
        StartupCheck.IsChecked = settings.StartWithSession;
        CursorCheck.IsChecked = settings.IncludeCursor;
        var delays = Delays.Select(d => new ChoiceItem(d, Loc.T(d == 1 ? "Common.Second" : "Common.Seconds", d))).ToList();
        DelayBox.ItemsSource = delays;
        DelayBox.SelectedItem = delays.FirstOrDefault(d => Equals(d.Value, settings.DelaySeconds)) ?? delays.First(d => Equals(d.Value, 3));

        SystemAudioCheck.IsChecked = settings.VideoSystemAudio;
        MicrophoneCheck.IsChecked = settings.VideoMicrophone;
        ClicksCheck.IsChecked = settings.VideoShowClicks;
        var framerates = Framerates.Select(f => new ChoiceItem(f, Loc.T("Settings.Video.FramerateValue", f))).ToList();
        FramerateBox.ItemsSource = framerates;
        FramerateBox.SelectedItem = framerates.FirstOrDefault(f => Equals(f.Value, settings.VideoFramerate)) ?? framerates.First(f => Equals(f.Value, 30));
        WebcamCheck.IsChecked = settings.VideoWebcam;
        WebcamCheck.IsCheckedChanged += (_, _) => WebcamOptions.IsEnabled = WebcamCheck.IsChecked == true;
        WebcamOptions.IsEnabled = settings.VideoWebcam;
        var corners = AppSettings.WebcamCorners.Select(c => new ChoiceItem(c, Loc.T("Settings.Video.Corner" + c))).ToList();
        WebcamCornerBox.ItemsSource = corners;
        WebcamCornerBox.SelectedItem = corners.FirstOrDefault(c => Equals(c.Value, settings.VideoWebcamCorner)) ?? corners[^1];
        var sizes = WebcamSizes.Select(s => new ChoiceItem(s.Percent, Loc.T(s.Key, s.Percent))).ToList();
        // A size typed in settings.json is offered as is.
        if (sizes.All(s => !Equals(s.Value, settings.VideoWebcamSize)))
            sizes.Add(new ChoiceItem(settings.VideoWebcamSize, Loc.T("Settings.Video.WebcamSizeCustom", settings.VideoWebcamSize)));
        WebcamSizeBox.ItemsSource = sizes;
        WebcamSizeBox.SelectedItem = sizes.First(s => Equals(s.Value, settings.VideoWebcamSize));
        _ = LoadWebcamsAsync(settings.VideoWebcamDevice);

        RegionHotkey.Hotkey = Parse(settings.HotkeyRegion);
        ScreenHotkey.Hotkey = Parse(settings.HotkeyScreen);
        WindowHotkey.Hotkey = Parse(settings.HotkeyWindow);
        RepeatHotkey.Hotkey = Parse(settings.HotkeyRepeat);
        DelayedHotkey.Hotkey = Parse(settings.HotkeyDelayed);
        VideoHotkey.Hotkey = Parse(settings.HotkeyVideo);
        TextHotkey.Hotkey = Parse(settings.HotkeyText);

        VersionText.Text = Loc.T("Settings.About.Version", typeof(App).Assembly.GetName().Version?.ToString(3));
        AutoUpdateCheck.IsChecked = settings.CheckForUpdates;
        if (settings.LastUpdateCheckUtc != default)
            UpdateStatus.Text = Loc.T("Update.LastChecked", settings.LastUpdateCheckUtc.ToLocalTime().ToString("g", Loc.Culture));

        AdaptToPlatform();
        Nav.SelectionChanged += OnNavChanged;
        ShowPage(_lastPage.Length > 0 ? _lastPage : nameof(PageGeneral));
        UpdatePrefixExample();
        Closed += (_, _) => _closed.TrySetResult(_saved);
    }

    /// <summary>Hides or explains what this system cannot do (start with the session, click highlight, global shortcuts...).</summary>
    private void AdaptToPlatform()
    {
        if (!AppServices.HasPlatform) return;
        var platform = AppServices.Platform;
        SystemSection.IsVisible = platform.Has(PlatformFeatures.StartWithSession);

        if (!platform.Has(PlatformFeatures.ClickHighlight))
        {
            ClicksRow.IsEnabled = false;
            ClicksDescription.Text = Loc.T("Settings.Video.ShowClicksUnavailable");
        }

        // Without detection (Wayland), an area is chosen by dragging only.
        if (!platform.Has(PlatformFeatures.WindowDetection))
        {
            SelectionHelpText.Text = Loc.T("Settings.Capture.SelectionHelpDragOnly");
            RegionHotkeyLabel.Text = Loc.T("Settings.Hotkeys.Area");
        }
        WindowHotkeyRow.IsVisible = platform.Has(PlatformFeatures.ActiveWindow);

        bool hotkeys = platform.Has(PlatformFeatures.GlobalHotkeys) && platform.Hotkeys.IsSupported;
        HotkeysUnsupported.IsVisible = !hotkeys;
        HotkeysCard.IsVisible = hotkeys;
        bool printScreenTaken;
        try
        {
            printScreenTaken = platform.Shell.PrintScreenTakenBySystem;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Touche Impr. écran", ex);
            printScreenTaken = false;
        }
        SnippingWarning.IsVisible = hotkeys && printScreenTaken;
        // The keyboard settings page only exists on Windows.
        KeyboardSettingsButton.IsVisible = platform.Id == "windows";
    }

    /// <summary>Shows the window (not modal: it may be opened from the tray, without owner) until it is closed.</summary>
    /// <returns>True when the user saved: <see cref="Result"/> holds the new settings.</returns>
    public Task<bool> ShowAndWaitAsync()
    {
        Show();
        Activate();
        return _closed.Task;
    }

    private static Hotkey Parse(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : default;

    /// <summary>
    /// Fills the camera list (enumerated off the UI thread: drivers can be slow to answer).
    /// A saved camera that is not connected stays selectable, so saving does not forget it.
    /// </summary>
    private async Task LoadWebcamsAsync(string saved)
    {
        var options = new List<ChoiceItem> { new("", Loc.T("Settings.Video.WebcamDefault")) };
        WebcamDeviceBox.ItemsSource = options;
        WebcamDeviceBox.SelectedItem = options[0];
        List<ChoiceItem> found;
        try
        {
            found = !AppServices.HasPlatform ? new List<ChoiceItem>() : await Task.Run(() => AppServices.Platform.Recorder.Webcams()
                .Where(c => !string.IsNullOrEmpty(c.Id))
                .Select(c => new ChoiceItem(c.Id, string.IsNullOrWhiteSpace(c.Name) ? c.Id : c.Name))
                .ToList());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
            found = new List<ChoiceItem>();
        }
        var all = new List<ChoiceItem>(options);
        all.AddRange(found);
        if (saved.Length > 0 && found.All(c => !string.Equals((string)c.Value, saved, StringComparison.OrdinalIgnoreCase)))
            all.Add(new ChoiceItem(saved, Loc.T("Settings.Video.WebcamDisconnected")));
        WebcamDeviceBox.ItemsSource = all;
        WebcamDeviceBox.SelectedItem = all.FirstOrDefault(c => string.Equals((string)c.Value, saved, StringComparison.OrdinalIgnoreCase)) ?? all[0];
        _webcamsLoaded = true;
    }

    private Control[] Pages => new Control[] { PageGeneral, PageCapture, PageVideo, PageHotkeys, PageAbout };

    /// <summary>Selects the navigation entry whose Tag is the page name.</summary>
    private void ShowPage(string page)
    {
        var entries = Nav.Items.OfType<ListBoxItem>().ToList();
        Nav.SelectedItem = entries.FirstOrDefault(i => i.Tag as string == page) ?? entries.First();
    }

    private void OnNavChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        _lastPage = page;
        foreach (var panel in Pages) panel.IsVisible = panel.Name == page;
        ContentScroller.Offset = default;
    }

    private void UpdatePrefixExample()
    {
        var extension = (FormatBox.SelectedItem as ChoiceItem)?.Value as string ?? "png";
        // Built like the real file name (CaptureService); only the sentence around it is translated.
        var fileName = $"{FileOps.SanitizeFileName(PrefixBox.Text ?? "")}{CaptureService.TimeStamp()}.{extension}";
        PrefixExample.Text = Loc.T("Settings.General.FileNameExample", fileName);
    }

    /// <summary>The folder typed, with %VARIABLES% (and ~ outside Windows) expanded and surrounding quotes removed.</summary>
    private string TypedFolder
    {
        get
        {
            var text = Environment.ExpandEnvironmentVariables((FolderBox.Text ?? "").Trim().Trim('"'));
            if (!OperatingSystem.IsWindows() && (text == "~" || text.StartsWith("~/", StringComparison.Ordinal)))
                text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile) + text[1..];
            return text;
        }
    }

    private async void OnBrowse(object? sender, RoutedEventArgs e)
    {
        try
        {
            var typed = TypedFolder;
            var start = Path.IsPathFullyQualified(typed) && Directory.Exists(typed) ? typed : AppSettings.DefaultWorkspaceFolder;
            var options = new FolderPickerOpenOptions { Title = Loc.T("Settings.General.Folder") };
            if (Directory.Exists(start)) options.SuggestedStartLocation = await StorageProvider.TryGetFolderFromPathAsync(start);
            var folders = await StorageProvider.OpenFolderPickerAsync(options);
            if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path) FolderBox.Text = path;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Choix du dossier des captures", ex);
        }
    }

    private void OnOpenFolder(object? sender, RoutedEventArgs e)
    {
        var folder = TypedFolder;
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return;
        OpenSafely(() => FileOps.OpenFolder(folder));
    }

    private void OnOpenSettingsFolder(object? sender, RoutedEventArgs e) => OpenSafely(() => FileOps.OpenFolder(AppSettings.SettingsFolder));

    private void OnOpenErrorLog(object? sender, RoutedEventArgs e)
    {
        if (File.Exists(ErrorLog.FilePath)) OpenOrWarn(ErrorLog.FilePath);
        else _ = Dialogs.ShowAsync(this, Loc.T("Settings.About.NoErrors"), "Snaplume", DialogButtons.Ok, DialogIcon.Information);
    }

    /// <summary>Asks GitHub for the latest version (the only request Snaplume sends, see <see cref="UpdateChecker"/>).</summary>
    private async void OnCheckUpdates(object? sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenUpdateButton.IsVisible = false;
        UpdateStatus.Text = Loc.T("Update.Checking");
        try
        {
            var release = await UpdateChecker.GetLatestAsync();
            // Kept by the application whether this window is validated or not.
            var settings = AppServices.Settings;
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            if (release.IsNewer)
            {
                settings.UpdateNotifiedVersion = release.DisplayVersion;
                _updatePage = release.Page;
                UpdateStatus.Text = Loc.T("Update.Available", release.DisplayVersion, UpdateChecker.CurrentVersion.ToString(3));
                OpenUpdateButton.IsVisible = true;
            }
            else
            {
                UpdateStatus.Text = Loc.T("Update.UpToDate", UpdateChecker.CurrentVersion.ToString(3));
            }
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException)
        {
            UpdateStatus.Text = Loc.T("Update.Offline");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Vérification des mises à jour", ex);
            UpdateStatus.Text = Loc.T("Update.Failed", ex.Message);
        }
        finally
        {
            CheckUpdateButton.IsEnabled = true;
        }
    }

    private void OnOpenUpdate(object? sender, RoutedEventArgs e) => OpenOrWarn(_updatePage ?? UpdateChecker.ReleasesPage);

    private void OnOpenKeyboardSettings(object? sender, RoutedEventArgs e) => OpenOrWarn("ms-settings:easeofaccess-keyboard");

    /// <summary>Opens a file or a URI with its default application; FileOps logs the reason of a failure.</summary>
    private void OpenOrWarn(string target)
    {
        if (!FileOps.OpenWithDefaultApp(target)) ShowOpenFailed();
    }

    /// <summary>Runs an "open in the file manager" action, with a message instead of a crash when it fails.</summary>
    private void OpenSafely(Action open)
    {
        try
        {
            open();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ouverture depuis les paramètres", ex);
            ShowOpenFailed();
        }
    }

    private void ShowOpenFailed() =>
        _ = Dialogs.ShowAsync(this, Loc.T("Settings.OpenFailed"), Loc.T("Settings.Title"), DialogButtons.Ok, DialogIcon.Warning);

    private void FocusFolder()
    {
        ShowPage(nameof(PageGeneral));
        FolderBox.Focus();
    }

    /// <summary>
    /// Checks the captures folder: absolute path, not too broad, created if needed, writable, and what the
    /// platform says of it (network or system location). Returns the normalized path, or null after telling
    /// the user what is wrong (or when they chose not to use it).
    /// </summary>
    private async Task<string?> ValidateFolderAsync()
    {
        var folder = TypedFolder;
        try
        {
            // A relative path would depend on the current directory of the moment.
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder))
                throw new ArgumentException(Loc.T("Settings.FolderNotAbsolute"));
            folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
            if (IsTooBroad(folder))
            {
                FocusFolder();
                await Dialogs.ShowAsync(this, Loc.T("Settings.FolderTooBroad", folder, AppSettings.DefaultWorkspaceFolder), Loc.T("Settings.Title"),
                    DialogButtons.Ok, DialogIcon.Warning);
                return null;
            }
            Directory.CreateDirectory(folder);
            // Write access: create (and delete) a temporary file.
            var probe = Path.Combine(folder, $".snaplume-{Guid.NewGuid():N}.tmp");
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
        }
        catch (Exception ex)
        {
            FocusFolder();
            await Dialogs.ShowAsync(this, Loc.T("Settings.InvalidFolder", ex.Message), Loc.T("Settings.Title"), DialogButtons.Ok, DialogIcon.Warning);
            return null;
        }

        // E.g. deleted captures do not go to the trash on a network folder: they are lost. Asked only for a new folder.
        bool changed = !string.Equals(folder, Path.TrimEndingDirectorySeparator(Result.WorkspaceFolder), App.PathComparison);
        string? warning = null;
        if (changed && AppServices.HasPlatform)
        {
            try
            {
                warning = AppServices.Platform.Shell.CheckWorkspaceFolder(folder);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Vérification du dossier des captures", ex);
            }
        }
        if (warning != null &&
            await Dialogs.ShowAsync(this, Loc.T("Settings.FolderWarningConfirm", warning), Loc.T("Settings.Title"),
                DialogButtons.OkCancel, DialogIcon.Warning, DialogResult.Cancel) != DialogResult.Ok)
        {
            FocusFolder();
            return null;
        }
        return folder;
    }

    /// <summary>
    /// A drive root, a personal folder (home, Desktop, Documents, Pictures, Videos, Music, Downloads), a folder
    /// holding one of them (C:\Users, /home) or a system folder: each sub-folder of the captures folder becomes a
    /// workspace, and deleting a workspace sends its whole folder to the trash.
    /// </summary>
    internal static bool IsTooBroad(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        // Drive root (C:\), network share root (\\server\share), or "/".
        if (Path.GetPathRoot(folder) is { Length: > 0 } root && SameFolder(root, folder)) return true;

        var personal = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
        }.Select(Environment.GetFolderPath).Append(DownloadsFolder()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        // The folder itself, or one of its parents (e.g. C:\Users or /home, or a OneDrive folder holding Documents).
        if (personal.Any(p => SameFolder(p, folder) || IsInside(p, folder))) return true;

        var system = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p)).ToList();
        if (!OperatingSystem.IsWindows())
            system.AddRange(new[] { "/bin", "/boot", "/dev", "/etc", "/lib", "/lib64", "/opt", "/proc", "/run", "/sbin", "/srv", "/sys", "/usr", "/var" });
        // System folders and everything inside them.
        return system.Any(p => SameFolder(p, folder) || IsInside(folder, p));
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            App.PathComparison);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
    private static bool IsInside(string path, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, App.PathComparison);
    }

    /// <summary>The Downloads folder (wherever the user moved it), or "" if unknown.</summary>
    private static string DownloadsFolder()
    {
        try
        {
            if (AppServices.HasPlatform && AppServices.Platform.Shell.DownloadsFolder is { Length: > 0 } downloads) return downloads;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Dossier Téléchargements", ex);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? Path.Combine(profile, "Downloads") : "";
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close();

    private async void OnOk(object? sender, RoutedEventArgs e)
    {
        var boxes = new[] { RegionHotkey, ScreenHotkey, WindowHotkey, RepeatHotkey, DelayedHotkey, VideoHotkey, TextHotkey };
        // Shortcuts saved by an older version (or typed in settings.json) may lack a modifier.
        if (boxes.FirstOrDefault(b => !HotkeyBox.IsAllowed(b.Hotkey)) is { } invalid)
        {
            ShowPage(nameof(PageHotkeys));
            invalid.Focus();
            await Dialogs.ShowAsync(this, Loc.T(OperatingSystem.IsWindows() ? "Settings.HotkeyNeedsModifier" : "Settings.HotkeyNeedsModifierSystem",
                invalid.Hotkey.DisplayName), Loc.T("Settings.Title"), DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        var hotkeys = boxes.Select(b => b.Hotkey).Where(h => !h.IsEmpty).ToList();
        if (hotkeys.Count != hotkeys.Distinct().Count())
        {
            ShowPage(nameof(PageHotkeys));
            await Dialogs.ShowAsync(this, Loc.T("Settings.DuplicateHotkey"), Loc.T("Settings.Title"), DialogButtons.Ok, DialogIcon.Warning);
            return;
        }

        if (await ValidateFolderAsync() is not { } folder) return;

        Result.WorkspaceFolder = folder;
        Result.FileNamePrefix = PrefixBox.Text ?? "";
        Result.ImageFormat = (FormatBox.SelectedItem as ChoiceItem)?.Value as string ?? "png";
        Result.OpenEditorAfterCapture = EditorCheck.IsChecked == true;
        Result.CopyToClipboardAfterCapture = ClipboardCheck.IsChecked == true;
        Result.ShowPanelAfterCapture = WorkspaceCheck.IsChecked == true;
        Result.StartWithSession = StartupCheck.IsChecked == true;
        Result.IncludeCursor = CursorCheck.IsChecked == true;
        Result.DelaySeconds = (DelayBox.SelectedItem as ChoiceItem)?.Value as int? ?? 3;
        Result.VideoSystemAudio = SystemAudioCheck.IsChecked == true;
        Result.VideoMicrophone = MicrophoneCheck.IsChecked == true;
        Result.VideoShowClicks = ClicksCheck.IsChecked == true;
        Result.VideoFramerate = (FramerateBox.SelectedItem as ChoiceItem)?.Value as int? ?? 30;
        Result.VideoWebcam = WebcamCheck.IsChecked == true;
        if (_webcamsLoaded) Result.VideoWebcamDevice = (WebcamDeviceBox.SelectedItem as ChoiceItem)?.Value as string ?? "";
        Result.VideoWebcamCorner = (WebcamCornerBox.SelectedItem as ChoiceItem)?.Value as string ?? "BottomRight";
        Result.VideoWebcamSize = (WebcamSizeBox.SelectedItem as ChoiceItem)?.Value as int? ?? 20;
        Result.HotkeyRegion = RegionHotkey.Hotkey.ToString();
        Result.HotkeyScreen = ScreenHotkey.Hotkey.ToString();
        Result.HotkeyWindow = WindowHotkey.Hotkey.ToString();
        Result.HotkeyRepeat = RepeatHotkey.Hotkey.ToString();
        Result.HotkeyDelayed = DelayedHotkey.Hotkey.ToString();
        Result.HotkeyVideo = VideoHotkey.Hotkey.ToString();
        Result.HotkeyText = TextHotkey.Hotkey.ToString();
        Result.Language = (LanguageBox.SelectedItem as ChoiceItem)?.Value as string ?? "";
        Result.Theme = (ThemeBox.SelectedItem as ChoiceItem)?.Value as string ?? ThemeManager.Auto;
        Result.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        _saved = true;
        Close();
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Escape cancels, unless it closes a drop-down list or is being recorded as a shortcut.
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}
