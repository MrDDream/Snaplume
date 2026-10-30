using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Snaplume.Core;

namespace Snaplume.Preferences;

public partial class SettingsWindow : Window
{
    /// <summary>Choice of a combo box: stored code and displayed name (languages, themes).</summary>
    private sealed record ChoiceOption(string Code, string Name);
    private sealed record NumberOption(int Value, string Name);

    private static readonly int[] Delays = { 1, 2, 3, 5, 10, 15 };
    private static readonly int[] Framerates = { 15, 24, 30, 60 };
    /// <summary>Webcam sizes offered, in % of the width of the recorded area.</summary>
    private static readonly (int Percent, string Key)[] WebcamSizes =
    {
        (15, "Settings.Video.WebcamSizeSmall"),
        (20, "Settings.Video.WebcamSizeMedium"),
        (30, "Settings.Video.WebcamSizeLarge"),
    };
    /// <summary>The camera list is filled: until then, the saved camera is kept as is.</summary>
    private bool _webcamsLoaded;
    /// <summary>Name of the page shown last (the Tag of its navigation entry).</summary>
    private static string _lastPage = "";

    public AppSettings Result { get; }

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        Result = settings;
        MaxHeight = SystemParameters.WorkArea.Height;
        if (Height > MaxHeight) Height = MaxHeight;

        var languages = new List<ChoiceOption> { new("", Loc.T("Settings.LanguageAuto")) };
        languages.AddRange(Loc.Languages.Select(l => new ChoiceOption(l.Code, l.Name)));
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = languages.FirstOrDefault(l => l.Code == settings.Language) ?? languages[0];
        var themes = new List<ChoiceOption>
        {
            new(ThemeManager.Auto, Loc.T("Settings.ThemeAuto")),
            new(ThemeManager.Light, Loc.T("Settings.ThemeLight")),
            new(ThemeManager.Dark, Loc.T("Settings.ThemeDark")),
        };
        ThemeBox.ItemsSource = themes;
        ThemeBox.SelectedItem = themes.FirstOrDefault(t => t.Code == settings.Theme) ?? themes[0];

        FolderBox.Text = settings.WorkspaceFolder;
        PrefixBox.Text = settings.FileNamePrefix;
        FormatBox.SelectedIndex = settings.ImageFormat == "jpg" ? 1 : 0;
        EditorCheck.IsChecked = settings.OpenEditorAfterCapture;
        ClipboardCheck.IsChecked = settings.CopyToClipboardAfterCapture;
        WorkspaceCheck.IsChecked = settings.ShowPanelAfterCapture;
        StartupCheck.IsChecked = settings.StartWithWindows;
        CursorCheck.IsChecked = settings.IncludeCursor;
        var delays = Delays.Select(d => new NumberOption(d, Loc.T(d == 1 ? "Common.Second" : "Common.Seconds", d))).ToList();
        DelayBox.ItemsSource = delays;
        DelayBox.SelectedItem = delays.FirstOrDefault(d => d.Value == settings.DelaySeconds) ?? delays.First(d => d.Value == 3);

        SystemAudioCheck.IsChecked = settings.VideoSystemAudio;
        MicrophoneCheck.IsChecked = settings.VideoMicrophone;
        ClicksCheck.IsChecked = settings.VideoShowClicks;
        var framerates = Framerates.Select(f => new NumberOption(f, Loc.T("Settings.Video.FramerateValue", f))).ToList();
        FramerateBox.ItemsSource = framerates;
        FramerateBox.SelectedItem = framerates.FirstOrDefault(f => f.Value == settings.VideoFramerate) ?? framerates.First(f => f.Value == 30);
        WebcamCheck.IsChecked = settings.VideoWebcam;
        var corners = AppSettings.WebcamCorners.Select(c => new ChoiceOption(c, Loc.T("Settings.Video.Corner" + c))).ToList();
        WebcamCornerBox.ItemsSource = corners;
        WebcamCornerBox.SelectedItem = corners.FirstOrDefault(c => c.Code == settings.VideoWebcamCorner) ?? corners[^1];
        var sizes = WebcamSizes.Select(s => new NumberOption(s.Percent, Loc.T(s.Key, s.Percent))).ToList();
        WebcamSizeBox.ItemsSource = sizes;
        // A size typed in settings.json is offered as is.
        if (sizes.All(s => s.Value != settings.VideoWebcamSize))
            sizes.Add(new NumberOption(settings.VideoWebcamSize, Loc.T("Settings.Video.WebcamSizeCustom", settings.VideoWebcamSize)));
        WebcamSizeBox.SelectedItem = sizes.First(s => s.Value == settings.VideoWebcamSize);
        _ = LoadWebcamsAsync(settings.VideoWebcamDevice);

        RegionHotkey.Hotkey = Parse(settings.HotkeyRegion);
        ScreenHotkey.Hotkey = Parse(settings.HotkeyScreen);
        WindowHotkey.Hotkey = Parse(settings.HotkeyWindow);
        RepeatHotkey.Hotkey = Parse(settings.HotkeyRepeat);
        DelayedHotkey.Hotkey = Parse(settings.HotkeyDelayed);
        VideoHotkey.Hotkey = Parse(settings.HotkeyVideo);
        TextHotkey.Hotkey = Parse(settings.HotkeyText);
        SnippingWarning.Visibility = AppSettings.SnippingToolOwnsPrintScreen() ? Visibility.Visible : Visibility.Collapsed;

        VersionText.Text = Loc.T("Settings.About.Version", typeof(App).Assembly.GetName().Version?.ToString(3));
        AutoUpdateCheck.IsChecked = settings.CheckForUpdates;
        if (settings.LastUpdateCheckUtc != default)
            UpdateStatus.Text = Loc.T("Update.LastChecked", settings.LastUpdateCheckUtc.ToLocalTime().ToString("g", Loc.Culture));
        ShowPage(_lastPage.Length > 0 ? _lastPage : PageGeneral.Name);
        UpdatePrefixExample();
    }

    private static Hotkey Parse(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : default;

    /// <summary>
    /// Fills the camera list (enumerated off the UI thread: drivers can be slow to answer).
    /// A saved camera that is not connected stays selectable, so saving does not forget it.
    /// </summary>
    private async Task LoadWebcamsAsync(string saved)
    {
        var options = new List<ChoiceOption> { new("", Loc.T("Settings.Video.WebcamDefault")) };
        WebcamDeviceBox.ItemsSource = options;
        WebcamDeviceBox.SelectedItem = options[0];
        List<ChoiceOption> found;
        try
        {
            found = await Task.Run(() => ScreenRecorderLib.Recorder.GetSystemVideoCaptureDevices()
                .Where(c => !string.IsNullOrEmpty(c.DeviceName))
                .Select(c => new ChoiceOption(c.DeviceName, string.IsNullOrWhiteSpace(c.FriendlyName) ? c.DeviceName : c.FriendlyName))
                .ToList());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
            found = new List<ChoiceOption>();
        }
        options.AddRange(found);
        if (saved.Length > 0 && found.All(c => !string.Equals(c.Code, saved, StringComparison.OrdinalIgnoreCase)))
            options.Add(new ChoiceOption(saved, Loc.T("Settings.Video.WebcamDisconnected")));
        WebcamDeviceBox.ItemsSource = null;
        WebcamDeviceBox.ItemsSource = options;
        WebcamDeviceBox.SelectedItem = options.FirstOrDefault(c => string.Equals(c.Code, saved, StringComparison.OrdinalIgnoreCase)) ?? options[0];
        _webcamsLoaded = true;
    }

    private FrameworkElement[] Pages => new FrameworkElement[] { PageGeneral, PageCapture, PageVideo, PageHotkeys, PageAbout };

    /// <summary>Selects the navigation entry whose Tag is the page name.</summary>
    private void ShowPage(string page)
    {
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == page)
                           ?? Nav.Items.OfType<ListBoxItem>().First();
    }

    private void ShowPage(FrameworkElement page) => ShowPage(page.Name);

    private void OnNavChanged(object sender, SelectionChangedEventArgs e)
    {
        if (Nav.SelectedItem is not ListBoxItem { Tag: string page }) return;
        _lastPage = page;
        foreach (var panel in Pages)
            panel.Visibility = panel.Name == page ? Visibility.Visible : Visibility.Collapsed;
        ContentScroller.ScrollToTop();
    }

    private void OnPrefixChanged(object sender, RoutedEventArgs e) => UpdatePrefixExample();

    private void UpdatePrefixExample()
    {
        if (PrefixExample == null || FormatBox == null) return;
        var extension = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "png";
        // Built like the real file name (CaptureService); only the sentence around it is translated.
        var fileName = $"{FileOps.SanitizeFileName(PrefixBox.Text)}{CaptureService.TimeStamp()}.{extension}";
        PrefixExample.Text = Loc.T("Settings.General.FileNameExample", fileName);
    }

    /// <summary>The folder typed, with %VARIABLES% expanded and surrounding quotes removed.</summary>
    private string TypedFolder => Environment.ExpandEnvironmentVariables(FolderBox.Text.Trim().Trim('"'));

    private void OnBrowse(object sender, RoutedEventArgs e)
    {
        var typed = TypedFolder;
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Loc.T("Settings.General.Folder"),
            InitialDirectory = Path.IsPathFullyQualified(typed) && Directory.Exists(typed) ? typed : AppSettings.DefaultWorkspaceFolder,
        };
        if (dialog.ShowDialog(this) == true) FolderBox.Text = dialog.FolderName;
    }

    private void OnOpenFolder(object sender, RoutedEventArgs e)
    {
        var folder = TypedFolder;
        if (!Path.IsPathFullyQualified(folder) || !Directory.Exists(folder)) return;
        OpenSafely(() => FileOps.OpenFolder(folder));
    }

    private void OnOpenSettingsFolder(object sender, RoutedEventArgs e) => OpenSafely(() => FileOps.OpenFolder(AppSettings.SettingsFolder));

    private void OnOpenErrorLog(object sender, RoutedEventArgs e)
    {
        if (File.Exists(ErrorLog.FilePath)) OpenOrWarn(ErrorLog.FilePath);
        else MessageBox.Show(this, Loc.T("Settings.About.NoErrors"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private string? _updatePage;

    /// <summary>Asks GitHub for the latest version (the only request Snaplume sends, see <see cref="UpdateChecker"/>).</summary>
    private async void OnCheckUpdates(object sender, RoutedEventArgs e)
    {
        CheckUpdateButton.IsEnabled = false;
        OpenUpdateButton.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = Loc.T("Update.Checking");
        try
        {
            var release = await UpdateChecker.GetLatestAsync();
            // Kept by the application whether this window is validated or not.
            var settings = App.Instance.Settings;
            settings.LastUpdateCheckUtc = DateTime.UtcNow;
            if (release.IsNewer)
            {
                settings.UpdateNotifiedVersion = release.DisplayVersion;
                _updatePage = release.Page;
                UpdateStatus.Text = Loc.T("Update.Available", release.DisplayVersion, UpdateChecker.CurrentVersion.ToString(3));
                OpenUpdateButton.Visibility = Visibility.Visible;
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

    private void OnOpenUpdate(object sender, RoutedEventArgs e) => OpenOrWarn(_updatePage ?? UpdateChecker.ReleasesPage);

    private void OnOpenWindowsKeyboardSettings(object sender, RoutedEventArgs e) => OpenOrWarn("ms-settings:easeofaccess-keyboard");

    /// <summary>Opens a file or a URI with its default application; FileOps logs the reason of a failure.</summary>
    private void OpenOrWarn(string target)
    {
        if (!FileOps.OpenWithDefaultApp(target)) ShowOpenFailed();
    }

    /// <summary>Runs an "open with Explorer" action, with a message instead of a crash when it fails.</summary>
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
        MessageBox.Show(this, Loc.T("Settings.OpenFailed"), Loc.T("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);

    /// <summary>
    /// Checks the captures folder: absolute path, created if needed, writable.
    /// Returns the normalized path, or null after telling the user what is wrong.
    /// </summary>
    private string? ValidateFolder()
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
                ShowPage(PageGeneral);
                FolderBox.Focus();
                MessageBox.Show(this, Loc.T("Settings.FolderTooBroad", folder, AppSettings.DefaultWorkspaceFolder), Loc.T("Settings.Title"),
                    MessageBoxButton.OK, MessageBoxImage.Warning);
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
            ShowPage(PageGeneral);
            FolderBox.Focus();
            MessageBox.Show(this, Loc.T("Settings.InvalidFolder", ex.Message), Loc.T("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return null;
        }

        // Deleted captures do not go to the Recycle Bin on a network folder: they are lost.
        bool changed = !string.Equals(folder, Path.TrimEndingDirectorySeparator(Result.WorkspaceFolder), StringComparison.OrdinalIgnoreCase);
        if (changed && IsNetworkPath(folder) &&
            MessageBox.Show(this, Loc.T("Settings.NetworkFolderWarning", folder), Loc.T("Settings.Title"),
                MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
        {
            ShowPage(PageGeneral);
            FolderBox.Focus();
            return null;
        }
        return folder;
    }

    /// <summary>
    /// A drive root, a personal folder (profile, Desktop, Documents, Pictures, Videos, Music, Downloads), a
    /// folder holding one of them (C:\Users) or a system folder: each sub-folder of the captures folder
    /// becomes a workspace, and deleting a workspace sends its whole folder to the Recycle Bin.
    /// </summary>
    internal static bool IsTooBroad(string folder)
    {
        folder = Path.TrimEndingDirectorySeparator(folder);
        // Drive root (C:\) or network share root (\\server\share).
        if (Path.GetPathRoot(folder) is { } root && SameFolder(root, folder)) return true;

        var personal = new[]
        {
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolder.Desktop, Environment.SpecialFolder.DesktopDirectory,
            Environment.SpecialFolder.MyDocuments, Environment.SpecialFolder.MyPictures, Environment.SpecialFolder.MyVideos,
            Environment.SpecialFolder.MyMusic, Environment.SpecialFolder.CommonDesktopDirectory,
            Environment.SpecialFolder.CommonDocuments, Environment.SpecialFolder.CommonPictures, Environment.SpecialFolder.CommonVideos,
        }.Select(Environment.GetFolderPath).Append(DownloadsFolder()).Where(p => !string.IsNullOrEmpty(p)).ToList();
        // The folder itself, or one of its parents (e.g. C:\Users, or a OneDrive folder holding Documents).
        if (personal.Any(p => SameFolder(p, folder) || IsInside(p, folder))) return true;

        var system = new[]
        {
            Environment.SpecialFolder.Windows, Environment.SpecialFolder.ProgramFiles, Environment.SpecialFolder.ProgramFilesX86,
            Environment.SpecialFolder.CommonApplicationData,
        }.Select(Environment.GetFolderPath).Where(p => !string.IsNullOrEmpty(p));
        // System folders and everything inside them.
        return system.Any(p => SameFolder(p, folder) || IsInside(folder, p));
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>True when <paramref name="path"/> is strictly inside <paramref name="folder"/>.</summary>
    private static bool IsInside(string path, string folder)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)) + Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(parent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>The Downloads folder (wherever the user moved it), or "" if unknown.</summary>
    private static string DownloadsFolder()
    {
        try
        {
            if (SHGetKnownFolderPath(new Guid("374DE290-123F-4565-9164-39C4925E467B"), 0, IntPtr.Zero, out var pointer) == 0)
            {
                try
                {
                    return Marshal.PtrToStringUni(pointer) ?? "";
                }
                finally
                {
                    Marshal.FreeCoTaskMem(pointer);
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Dossier Téléchargements", ex);
        }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return profile.Length > 0 ? Path.Combine(profile, "Downloads") : "";
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern int SHGetKnownFolderPath([MarshalAs(UnmanagedType.LPStruct)] Guid folderId, uint flags, IntPtr token, out IntPtr path);

    private static bool IsNetworkPath(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return true; // UNC path (\\server\share)
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        var boxes = new[] { RegionHotkey, ScreenHotkey, WindowHotkey, RepeatHotkey, DelayedHotkey, VideoHotkey, TextHotkey };
        // Shortcuts saved by an older version (or typed in settings.json) may lack a modifier.
        if (boxes.FirstOrDefault(b => !HotkeyBox.IsAllowed(b.Hotkey)) is { } invalid)
        {
            ShowPage(PageHotkeys);
            invalid.Focus();
            MessageBox.Show(this, Loc.T("Settings.HotkeyNeedsModifier", invalid.Hotkey.DisplayName), Loc.T("Settings.Title"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var hotkeys = boxes.Select(b => b.Hotkey).Where(h => !h.IsEmpty).ToList();
        if (hotkeys.Count != hotkeys.Distinct().Count())
        {
            ShowPage(PageHotkeys);
            MessageBox.Show(this, Loc.T("Settings.DuplicateHotkey"), Loc.T("Settings.Title"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (ValidateFolder() is not { } folder) return;

        Result.WorkspaceFolder = folder;
        Result.FileNamePrefix = PrefixBox.Text;
        Result.ImageFormat = (FormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "png";
        Result.OpenEditorAfterCapture = EditorCheck.IsChecked == true;
        Result.CopyToClipboardAfterCapture = ClipboardCheck.IsChecked == true;
        Result.ShowPanelAfterCapture = WorkspaceCheck.IsChecked == true;
        Result.StartWithWindows = StartupCheck.IsChecked == true;
        Result.IncludeCursor = CursorCheck.IsChecked == true;
        Result.DelaySeconds = (DelayBox.SelectedItem as NumberOption)?.Value ?? 3;
        Result.VideoSystemAudio = SystemAudioCheck.IsChecked == true;
        Result.VideoMicrophone = MicrophoneCheck.IsChecked == true;
        Result.VideoShowClicks = ClicksCheck.IsChecked == true;
        Result.VideoFramerate = (FramerateBox.SelectedItem as NumberOption)?.Value ?? 30;
        Result.VideoWebcam = WebcamCheck.IsChecked == true;
        if (_webcamsLoaded) Result.VideoWebcamDevice = (WebcamDeviceBox.SelectedItem as ChoiceOption)?.Code ?? "";
        Result.VideoWebcamCorner = (WebcamCornerBox.SelectedItem as ChoiceOption)?.Code ?? "BottomRight";
        Result.VideoWebcamSize = (WebcamSizeBox.SelectedItem as NumberOption)?.Value ?? 20;
        Result.HotkeyRegion = RegionHotkey.Hotkey.ToString();
        Result.HotkeyScreen = ScreenHotkey.Hotkey.ToString();
        Result.HotkeyWindow = WindowHotkey.Hotkey.ToString();
        Result.HotkeyRepeat = RepeatHotkey.Hotkey.ToString();
        Result.HotkeyDelayed = DelayedHotkey.Hotkey.ToString();
        Result.HotkeyVideo = VideoHotkey.Hotkey.ToString();
        Result.HotkeyText = TextHotkey.Hotkey.ToString();
        Result.Language = (LanguageBox.SelectedItem as ChoiceOption)?.Code ?? "";
        Result.Theme = (ThemeBox.SelectedItem as ChoiceOption)?.Code ?? ThemeManager.Auto;
        Result.CheckForUpdates = AutoUpdateCheck.IsChecked == true;
        DialogResult = true;
    }
}
