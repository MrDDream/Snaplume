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

        RegionHotkey.Hotkey = Parse(settings.HotkeyRegion);
        ScreenHotkey.Hotkey = Parse(settings.HotkeyScreen);
        WindowHotkey.Hotkey = Parse(settings.HotkeyWindow);
        RepeatHotkey.Hotkey = Parse(settings.HotkeyRepeat);
        DelayedHotkey.Hotkey = Parse(settings.HotkeyDelayed);
        VideoHotkey.Hotkey = Parse(settings.HotkeyVideo);
        TextHotkey.Hotkey = Parse(settings.HotkeyText);
        SnippingWarning.Visibility = AppSettings.SnippingToolOwnsPrintScreen() ? Visibility.Visible : Visibility.Collapsed;

        VersionText.Text = Loc.T("Settings.About.Version", typeof(App).Assembly.GetName().Version?.ToString(3));
        ShowPage(_lastPage.Length > 0 ? _lastPage : PageGeneral.Name);
        UpdatePrefixExample();
    }

    private static Hotkey Parse(string text) => Hotkey.TryParse(text, out var hotkey) ? hotkey : default;

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
        var fileName = $"{FileOps.SanitizeFileName(PrefixBox.Text)}{DateTime.Now:yyyy-MM-dd_HH-mm-ss}.{extension}";
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
        Result.HotkeyRegion = RegionHotkey.Hotkey.ToString();
        Result.HotkeyScreen = ScreenHotkey.Hotkey.ToString();
        Result.HotkeyWindow = WindowHotkey.Hotkey.ToString();
        Result.HotkeyRepeat = RepeatHotkey.Hotkey.ToString();
        Result.HotkeyDelayed = DelayedHotkey.Hotkey.ToString();
        Result.HotkeyVideo = VideoHotkey.Hotkey.ToString();
        Result.HotkeyText = TextHotkey.Hotkey.ToString();
        Result.Language = (LanguageBox.SelectedItem as ChoiceOption)?.Code ?? "";
        Result.Theme = (ThemeBox.SelectedItem as ChoiceOption)?.Code ?? ThemeManager.Auto;
        DialogResult = true;
    }
}
