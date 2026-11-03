using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using Avalonia.Threading;
using SkiaSharp;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Imaging;
using Snaplume.Ocr;
using Snaplume.Platform;
using Snaplume.Preferences;
using Snaplume.Qr;
using Snaplume.Recording;
using Snaplume.Ui;
using Snaplume.Workspace;

namespace Snaplume;

/// <summary>
/// The application: startup, single instance, command line, tray icon, global shortcuts and the windows it opens.
/// It lives in the notification area (explicit shutdown): closing a window only hides it.
/// </summary>
public partial class App : Application
{
    private WorkspaceWindow? _workspace;
    private QuickPanel? _quickPanel;
    private SettingsWindow? _settingsWindow;
    private bool _showingError;
    private bool _restartOnExit;
    /// <summary>Starts an update just before quitting (see <see cref="QuitForUpdate"/>).</summary>
    private Func<bool>? _launchOnExit;
    private bool _started;

    public static App Instance => (App)Current!;

    public AppSettings Settings => AppServices.Settings;
    public IPlatform Platform => AppServices.Platform;
    public CaptureService Captures { get; private set; } = null!;
    public VideoService Videos { get; private set; } = null!;
    /// <summary>Conversions of the recordings that follow them (Settings › Video › Video format).</summary>
    public RecordingConverter Conversions { get; private set; } = null!;
    public ITray? Tray { get; private set; }
    public bool IsExiting { get; private set; }

    /// <summary>New settings were saved from the settings window (<see cref="Settings"/> is a new object).</summary>
    public event Action? SettingsApplied;

    private IClassicDesktopStyleApplicationLifetime? Desktop => ApplicationLifetime as IClassicDesktopStyleApplicationLifetime;

    /// <summary>The open windows of the application (editors, players, dialogs...).</summary>
    public IReadOnlyList<Window> OpenWindows => Desktop?.Windows ?? (IReadOnlyList<Window>)Array.Empty<Window>();

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        // Without the desktop lifetime (designer, headless tests), only the resources and styles are used.
        if (Desktop is not { } desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        desktop.Exit += OnExit;
        desktop.ShutdownRequested += OnShutdownRequested;
        Dispatcher.UIThread.UnhandledException += OnUnhandledException;
        _ = StartAsync(desktop.Args ?? Array.Empty<string>());
    }

    private async Task StartAsync(string[] args)
    {
        try
        {
            await Start(args);
        }
        catch (Exception ex)
        {
            // Never leave an invisible process holding the single instance.
            ErrorLog.Write("Démarrage", ex);
            IsExiting = true;
            try
            {
                if (!_started) Loc.Initialize(AppServices.Settings.Language);
                await Dialogs.ShowAsync(null, Loc.T("App.StartupFailed", ex.Message, ErrorLog.FilePath), "Snaplume",
                    DialogButtons.Ok, DialogIcon.Error);
            }
            catch (Exception)
            {
                // Quit anyway.
            }
            Desktop?.Shutdown(1);
        }
    }

    private async Task Start(string[] args)
    {
        // The platform first: the settings ask it whether the start with the session is registered.
        if (!AppServices.HasPlatform) AppServices.Platform = CreatePlatform();
        var instance = Platform.SingleInstance;
        bool first = instance.TryAcquire();
        if (!first && args.Contains("--restarted"))
        {
            // Restart after a language change: wait for the previous instance to quit.
            for (int i = 0; i < 40 && !first; i++)
            {
                await Task.Delay(250);
                first = instance.TryAcquire();
            }
        }
        if (!first)
        {
            // Already running: it runs this command line (or just shows its quick panel).
            IsExiting = true;
            try
            {
                if (!await instance.ForwardAsync(CommandLine.WithFullPaths(args)))
                    ErrorLog.Write("Transmission de la ligne de commande", "Instance déjà lancée injoignable");
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Transmission de la ligne de commande", ex);
            }
            Desktop?.Shutdown();
            return;
        }

        AppServices.Settings = AppSettings.Load();
        Loc.Initialize(Settings.Language);
        _started = true;
        ThemeManager.Apply(Settings.Theme);
        if (Loc.IsRightToLeft)
        {
            // Arabic: every window is laid out right to left, unless it sets its own direction (the selection
            // overlay); images and drawings opt out explicitly. A style, so that a local value always wins.
            Styles.Add(new Style(x => x.Is<Window>())
            {
                Setters = { new Setter(Visual.FlowDirectionProperty, Avalonia.Media.FlowDirection.RightToLeft) },
            });
        }
        var unavailableFolder = EnsureWorkspaceFolder();
        Captures = new CaptureService(this);
        Videos = new VideoService(this);
        Conversions = new RecordingConverter(this);
        CreateTray();
        HotkeyBox.FocusChanged += OnHotkeyBoxFocusChanged;
        RegisterHotkeys(showWarnings: true);
        if (unavailableFolder != null)
        {
            Tray?.ShowWarning(Loc.T("App.FolderUnavailableTitle"),
                Loc.T("App.FolderUnavailableText", unavailableFolder, Settings.WorkspaceFolder), OnUi(ShowSettings));
        }
        if (Settings.LoadFailed) ShowSettingsResetWarning();

        // Parsed on the listener's thread; only the result goes to the UI thread.
        instance.Listen(received =>
        {
            CommandLine parsed;
            try
            {
                parsed = CommandLine.Parse(received);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Ligne de commande transmise", ex);
                return;
            }
            Dispatcher.UIThread.Post(() => HandleArgs(parsed, startup: false));
        });
        _ = Task.Run(VideoService.DeleteStaleTempFiles);

        HandleArgs(CommandLine.Parse(args), startup: true);
        StartUpdateChecks();
    }

    /// <summary>
    /// settings.json could not be read: the defaults are used. Says so, with the copy of the unreadable file (a click
    /// shows it in its folder).
    /// </summary>
    private void ShowSettingsResetWarning()
    {
        var backup = Path.Combine(AppSettings.SettingsFolder, "settings.json.bak");
        bool hasBackup = File.Exists(backup);
        Tray?.ShowWarning(Loc.T("App.SettingsResetTitle"),
            hasBackup ? Loc.T("App.SettingsResetTextBackup", backup) : Loc.T("App.SettingsResetText"),
            OnUi(() =>
            {
                try
                {
                    if (hasBackup) FileOps.Reveal(backup);
                    else FileOps.OpenFolder(AppSettings.SettingsFolder);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Ouverture du dossier des paramètres", ex);
                }
            }));
    }

    private static IPlatform CreatePlatform()
    {
#if SNAPLUME_WINDOWS
        return Snaplume.Platform.Windows.WindowsPlatform.Create();
#else
        return Snaplume.Platform.Linux.LinuxPlatform.Create();
#endif
    }

    // ---- Updates ----

    private DispatcherTimer? _updateTimer;
    private bool _checkingUpdates;

    /// <summary>
    /// Automatic update check, when the user turned it on: a minute after startup (not to slow it down),
    /// then every hour, each time doing nothing until a day has passed since the last check.
    /// </summary>
    private void StartUpdateChecks()
    {
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(1) };
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(1);
            await CheckForUpdatesInBackgroundAsync();
        };
        _updateTimer.Start();
    }

    private async Task CheckForUpdatesInBackgroundAsync()
    {
        if (!Settings.CheckForUpdates || _checkingUpdates || IsExiting) return;
        if (DateTime.UtcNow - Settings.LastUpdateCheckUtc < TimeSpan.FromDays(1)) return;
        _checkingUpdates = true;
        try
        {
            var release = await UpdateChecker.GetLatestAsync();
            Settings.LastUpdateCheckUtc = DateTime.UtcNow;
            if (release.IsNewer && release.DisplayVersion != Settings.UpdateNotifiedVersion)
            {
                Settings.UpdateNotifiedVersion = release.DisplayVersion;
                // A click offers to install it when this copy can be updated from here, else opens the release page.
                if (UpdateWindow.Plan(release) != null)
                    Tray?.ShowInfo(Loc.T("Update.AvailableTitle"), Loc.T("Update.AvailableTextInstall", release.DisplayVersion),
                        OnUi(() => UpdateWindow.Show(null, release, offer: true)));
                else
                    Tray?.ShowInfo(Loc.T("Update.AvailableTitle"), Loc.T("Update.AvailableText", release.DisplayVersion),
                        OnUi(() => FileOps.OpenWithDefaultApp(release.Page)));
            }
            Settings.Save();
        }
        catch (Exception ex)
        {
            // Offline: tried again in an hour, without filling the error log.
            if (ex is not (System.Net.Http.HttpRequestException or TaskCanceledException))
                ErrorLog.Write("Vérification des mises à jour", ex);
        }
        finally
        {
            _checkingUpdates = false;
        }
    }

    /// <summary>
    /// Creates the capture folder. If that fails (USB drive or network share not connected),
    /// the default folder is used for this session.
    /// </summary>
    /// <returns>The unavailable folder, or null if it is fine.</returns>
    private string? EnsureWorkspaceFolder()
    {
        try
        {
            Directory.CreateDirectory(Settings.WorkspaceFolder);
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Dossier des captures", ex);
            var unavailable = Settings.WorkspaceFolder;
            Settings.UseFallbackFolder(AppSettings.DefaultWorkspaceFolder);
            Directory.CreateDirectory(Settings.WorkspaceFolder);
            return unavailable;
        }
    }

    // ---- Command line ----

    /// <summary>Runs a command line, at startup or forwarded by a second instance.</summary>
    /// <param name="startup">False when forwarded: "--exit" then never quits the instance already running.</param>
    private void HandleArgs(CommandLine args, bool startup)
    {
        if (IsExiting) return;
        if (args.Workspace != null) SelectWorkspace(args.Workspace);
        bool exit = args.Exit && startup;

        if (args.Capture is CaptureKind mode)
        {
            _ = RunCommandLineCaptureAsync(mode, args, exit);
        }
        else if (args.Text)
        {
            _ = RunCommandLineWindowAsync(Captures.CaptureTextAsync(settleMs: 100), exit);
        }
        else if (args.Qr)
        {
            _ = RunCommandLineWindowAsync(Captures.CaptureQrAsync(settleMs: 100), exit);
        }
        else if (args.Record != null)
        {
            _ = RunCommandLineRecordingAsync(args, exit);
        }
        else if (args.EditPath != null)
        {
            OpenEditor(args.EditPath);
        }
        else if (args.Settings)
        {
            ShowSettings();
        }
        else if (args.Window)
        {
            ShowWorkspace();
        }
        else if (!args.Tray)
        {
            ShowQuickPanel();
        }
    }

    /// <summary>"--workspace name": the existing workspace with that name, else a new one.</summary>
    private static void SelectWorkspace(string name)
    {
        try
        {
            var existing = Workspaces.List().FirstOrDefault(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) Workspaces.Select(existing);
            else Workspaces.Create(name);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Ligne de commande : --workspace {name}", ex);
        }
    }

    /// <summary>"--capture screen --exit" allows scripting captures.</summary>
    private async Task RunCommandLineCaptureAsync(CaptureKind mode, CommandLine args, bool exit)
    {
        try
        {
            await Captures.CaptureAsync(mode, args.DelaySeconds, settleMs: 100, quiet: args.Exit);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ligne de commande : capture", ex);
        }
        if (exit) ExitApp();
    }

    /// <summary>"--capture text|qr --exit": quits once the result window is closed (or the selection cancelled).</summary>
    private async Task RunCommandLineWindowAsync(Task<Window?> capture, bool exit)
    {
        Window? window = null;
        try
        {
            window = await capture;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ligne de commande : capture de texte ou de code", ex);
        }
        if (!exit) return;
        if (window == null) ExitApp();
        else window.Closed += (_, _) => ExitApp();
    }

    /// <summary>"--record screen --duration 10 --exit" records a video without interaction.</summary>
    private async Task RunCommandLineRecordingAsync(CommandLine args, bool exit)
    {
        try
        {
            await Videos.ToggleAsync(fullScreen: args.Record == "screen", settleMs: 100, durationSeconds: args.DurationSeconds);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ligne de commande : enregistrement vidéo", ex);
        }
        // The file of a recording converted afterwards is only there once converted.
        await Conversions.WhenIdleAsync();
        if (exit) ExitApp();
    }

    // ---- Global shortcuts ----

    /// <summary>Freed while a shortcut field of the settings has the focus (<see cref="OnHotkeyBoxFocusChanged"/>).</summary>
    private bool _hotkeysSuspended;

    /// <summary>Registers the global shortcuts of the settings; tells which ones are taken if asked.</summary>
    public void RegisterHotkeys(bool showWarnings)
    {
        _hotkeysSuspended = false;
        if (IsExiting || !AppServices.HasPlatform) return;
        var hotkeys = Platform.Hotkeys;
        try
        {
            hotkeys.UnregisterAll();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Raccourcis globaux", ex);
        }
        // Not available on this desktop (some Wayland compositors): the settings explain how to bind a system shortcut.
        if (!Platform.Has(PlatformFeatures.GlobalHotkeys) || !hotkeys.IsSupported) return;

        var failed = new List<string>();
        void Register(string id, string descriptionKey, string text, Action action)
        {
            if (!Hotkey.TryParse(text, out var hotkey) || hotkey.IsEmpty) return;
            bool registered;
            try
            {
                // The system may call back on another thread.
                registered = hotkeys.Register(id, Loc.T(descriptionKey), hotkey, () => Dispatcher.UIThread.Post(action));
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Raccourci global {id}", ex);
                registered = false;
            }
            if (!registered) failed.Add(hotkey.DisplayName);
        }

        Register("region", "Settings.Hotkeys.Region", Settings.HotkeyRegion, () => _ = Captures.CaptureAsync(CaptureKind.Region));
        Register("screen", "Settings.Hotkeys.Screen", Settings.HotkeyScreen, () => _ = Captures.CaptureAsync(CaptureKind.Screen));
        if (Platform.Has(PlatformFeatures.ActiveWindow))
            Register("window", "Settings.Hotkeys.Window", Settings.HotkeyWindow, () => _ = Captures.CaptureAsync(CaptureKind.ActiveWindow));
        Register("repeat", "Settings.Hotkeys.Repeat", Settings.HotkeyRepeat, () => _ = Captures.CaptureAsync(CaptureKind.RepeatLast));
        Register("delayed", "Settings.Hotkeys.Delayed", Settings.HotkeyDelayed, () => _ = Captures.CaptureAsync(CaptureKind.Region, Settings.DelaySeconds));
        Register("video", "Settings.Hotkeys.Video", Settings.HotkeyVideo, () => _ = Videos.ToggleAsync());
        Register("text", "Ocr.CaptureText", Settings.HotkeyText, () => _ = Captures.CaptureTextAsync());
        if (Settings.ReplaceSnippingShortcut && Platform.Has(PlatformFeatures.ReplaceSystemCapture))
        {
            bool replaced;
            try
            {
                replaced = hotkeys.ReplaceSystemCapture(() => Dispatcher.UIThread.Post(() => _ = Captures.CaptureAsync(CaptureKind.Region)));
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Remplacement de Win+Maj+S", ex);
                replaced = false;
            }
            if (!replaced) failed.Add(new Hotkey(KeyModifiers.Meta | KeyModifiers.Shift, Key.S).DisplayName);
        }

        if (!showWarnings) return;
        if (failed.Count > 0)
        {
            Tray?.ShowWarning(Loc.T("App.HotkeysUnavailableTitle"),
                Loc.T("App.HotkeysUnavailableText", string.Join(", ", failed)),
                OnUi(ShowSettings));
        }
        else if (PrintScreenTakenBySystem() && Settings.HotkeyRegion.Contains("PrintScreen", StringComparison.OrdinalIgnoreCase))
        {
            Tray?.ShowWarning(Loc.T("App.PrintScreenTakenTitle"), Loc.T("App.PrintScreenTakenText"), OnUi(ShowSettings));
        }
    }

    private bool PrintScreenTakenBySystem()
    {
        try
        {
            return Platform.Shell.PrintScreenTakenBySystem;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Touche Impr. écran", ex);
            return false;
        }
    }

    // ---- Windows ----

    /// <summary>Compact panel near the notification area (like the tray flyout of Screenpresso).</summary>
    public void ShowQuickPanel()
    {
        if (IsExiting) return;
        _quickPanel ??= new QuickPanel();
        _quickPanel.ShowDocked();
    }

    public void ToggleQuickPanel()
    {
        // Clicking the tray icon deactivates the panel first, so only visibility counts.
        if (_quickPanel is { IsVisible: true }) _quickPanel.Hide();
        else ShowQuickPanel();
    }

    public void ShowWorkspace()
    {
        if (IsExiting) return;
        _workspace ??= new WorkspaceWindow();
        _workspace.Show();
        BringToFront(_workspace);
    }

    /// <summary>True for a GIF of several frames (exported from a video): the editor would only keep the first one.</summary>
    private static bool IsAnimatedGif(string path)
    {
        if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var codec = SKCodec.Create(path);
            return codec?.FrameCount > 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Opens an image in the editor (an animated GIF or WebP opens in the default application, a video in the player).</summary>
    public void OpenEditor(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (ImageIO.IsVideo(path))
            {
                OpenPlayer(path);
                return;
            }
            if (IsAnimatedGif(path) || ImageIO.IsAnimation(path))
            {
                if (!FileOps.OpenWithDefaultApp(path)) ShowError(Loc.T("App.OpenImageFailed", path));
                return;
            }
            if (OpenWindows.OfType<EditorWindow>().FirstOrDefault(w => IsSamePath(w.FilePath, path)) is { } existing)
            {
                BringToFront(existing);
                return;
            }
            var editor = new EditorWindow(path);
            editor.Show();
            editor.Activate();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ouverture de l'éditeur", ex);
            ShowError(Loc.T("App.OpenImageFailed", ex.Message));
        }
    }

    /// <summary>Opens a video in the built-in player.</summary>
    public void OpenPlayer(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (OpenWindows.OfType<Player.PlayerWindow>().FirstOrDefault(w => IsSamePath(w.FilePath, path)) is { } existing)
            {
                BringToFront(existing);
                return;
            }
            var player = new Player.PlayerWindow(path);
            player.Show();
            player.Activate();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ouverture du lecteur", ex);
            ShowError(Loc.T("App.OpenVideoFailed", ex.Message));
        }
    }

    /// <summary>Same file, whatever the form of the paths (relative, ".."; case ignored on Windows only).</summary>
    internal static bool IsSamePath(string? path, string fullPath) => !string.IsNullOrEmpty(path) && PathRules.SamePath(path, fullPath);

    internal static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Recognizes the text of an image file and shows it.</summary>
    public void ShowOcr(string path)
    {
        try
        {
            ShowOcr(ImageIO.Load(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OCR", ex);
            ShowError(ex.Message);
        }
    }

    /// <summary>Recognizes the text of an image (a capture area, the image being edited) and shows it.</summary>
    public Window? ShowOcr(PixelBuffer image)
    {
        if (IsExiting) return null;
        var window = new OcrWindow(image);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Reads the QR codes and barcodes of an image file and shows them.</summary>
    public void ShowQr(string path)
    {
        try
        {
            ShowQr(ImageIO.Load(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("QR code", ex);
            ShowError(ex.Message);
        }
    }

    /// <summary>Reads the QR codes and barcodes of an image and shows them.</summary>
    public Window? ShowQr(PixelBuffer image)
    {
        if (IsExiting) return null;
        var window = new QrWindow(image);
        window.Show();
        window.Activate();
        return window;
    }

    public void ShowSettings() => _ = ShowSettingsAsync(null);

    /// <param name="page">Page to show ("PageVideo"...), the one shown last when null.</param>
    public void ShowSettings(string? page) => _ = ShowSettingsAsync(page);

    private async Task ShowSettingsAsync(string? page)
    {
        if (IsExiting) return;
        if (_settingsWindow != null)
        {
            if (page != null) _settingsWindow.ShowPage(page);
            BringToFront(_settingsWindow);
            return;
        }
        try
        {
            var window = new SettingsWindow(Settings.Clone(), page);
            _settingsWindow = window;
            if (await window.ShowAndWaitAsync()) await ApplySettingsAsync(window);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Paramètres", ex);
        }
        finally
        {
            _settingsWindow = null;
            RegisterHotkeys(showWarnings: true);
        }
    }

    /// <summary>
    /// A shortcut field of the settings got or lost the keyboard focus: the global shortcuts are freed while one has
    /// it, so that they can be typed there, and registered again once none has (not between two fields).
    /// </summary>
    private void OnHotkeyBoxFocusChanged(bool focused)
    {
        if (focused)
        {
            if (_hotkeysSuspended) return;
            _hotkeysSuspended = true;
            try
            {
                Platform.Hotkeys.UnregisterAll();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Raccourcis globaux", ex);
            }
            return;
        }
        Dispatcher.UIThread.Post(() =>
        {
            if (_hotkeysSuspended && !HotkeyBox.IsAnyFocused) RegisterHotkeys(showWarnings: false);
        }, DispatcherPriority.Background);
    }

    /// <summary>The settings saved: the values edited in the window go onto a copy of the current settings.</summary>
    private async Task ApplySettingsAsync(SettingsWindow window)
    {
        var current = Settings;
        // Only what the window edits: the active workspace changed from the tray menu meanwhile, the OCR language of a
        // text window, the update state of the background check, the panels and bars... stay as they are now.
        var result = current.Clone();
        window.MergeInto(result);
        result.Sanitize();
        // A new format or compression for the recordings: the export dialog opens with it too.
        var recordingFormat = Player.RecordingFormatChoice.From(result);
        var previousFormat = Player.RecordingFormatChoice.From(current);
        if (recordingFormat with { KeepOriginal = false } != previousFormat with { KeepOriginal = false })
            Player.RecordingFormats.ApplyToExportDialog(recordingFormat, result);
        bool folderChanged = !PathRules.SamePath(current.WorkspaceFolder, result.WorkspaceFolder);
        bool languageChanged = current.Language != result.Language;
        // Another root folder: a workspace with a folder of its own stays active; a sub-folder of the root only if it
        // exists in the new root too.
        if (folderChanged && result.ActiveWorkspace.Length > 0 && !result.WorkspaceFolders.ContainsKey(result.ActiveWorkspace)
            && !Directory.Exists(Path.Combine(result.WorkspaceFolder, result.ActiveWorkspace)))
        {
            result.ActiveWorkspace = "";
        }
        AppServices.Settings = result;
        ThemeManager.Apply(result.Theme);
        try
        {
            result.Save();
        }
        catch (Exception ex)
        {
            // The new settings still apply until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
            await Dialogs.ShowAsync(null, Loc.T("Settings.SaveFailed", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Warning);
        }
        try
        {
            result.ApplyStartWithSession();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Démarrage avec la session", ex);
        }
        if (folderChanged)
        {
            _workspace?.Reload();
            _quickPanel?.Reload();
            Workspaces.OnRootChanged();
        }
        UpdateTrayTooltip();
        SettingsApplied?.Invoke();
        if (languageChanged)
        {
            // Shown in the newly chosen language, so the user understands it.
            Loc.Initialize(result.Language);
            if (await Dialogs.ShowAsync(null, Loc.T("App.RestartForLanguage"), "Snaplume", DialogButtons.YesNo, DialogIcon.Question) == DialogResult.Yes)
                Restart();
        }
    }

    /// <summary>A message box without owner, for errors of actions started from the tray or a shortcut.</summary>
    private static void ShowError(string message) =>
        _ = Dialogs.ShowAsync(Dialogs.ActiveWindow, message, "Snaplume", DialogButtons.Ok, DialogIcon.Error);

    // ---- Exit ----

    public void ExitApp() => ExitApp(restart: false);

    /// <summary>Quits and starts again (the interface language is applied at startup).</summary>
    public void Restart() => ExitApp(restart: true);

    /// <summary>
    /// Quits to let an update replace Snaplume. <paramref name="launch"/> starts the installer (or the new portable
    /// copy) once the video and capture in progress are finished and every editor agreed to close, just before
    /// quitting; Snaplume keeps running if an editor stays open or <paramref name="launch"/> returns false.
    /// </summary>
    /// <param name="postponed">
    /// Called when an editor keeps its unsaved changes: nothing was started, the update waits (the user is told).
    /// </param>
    public void QuitForUpdate(Func<bool> launch, Action postponed)
    {
        if (IsExiting) return;
        // The editors are asked first, before a recording or a capture is stopped for nothing.
        foreach (var editor in OpenWindows.OfType<EditorWindow>().ToList())
        {
            editor.Close();
            if (!editor.IsVisible) continue;
            postponed();
            return;
        }
        _launchOnExit = launch;
        ExitApp(restart: false);
    }

    private void ExitApp(bool restart)
    {
        if (IsExiting) return;
        if (restart) _restartOnExit = true;
        if (Videos is { IsBusy: true })
        {
            // Save the video in progress first, then quit (StopThen runs once the recorder is idle).
            Videos.StopThen(() => ExitApp(restart));
            return;
        }
        if (Conversions is { IsBusy: true })
        {
            // A recording being converted: its original goes to the workspace, then quit.
            Conversions.CancelThen(() => ExitApp(restart));
            return;
        }
        if (Captures is { IsBusy: true })
        {
            // Let the capture in progress stop cleanly first: countdown or selection cancelled, scrolling
            // stopped with the mouse put back and the image saved; then quit.
            Captures.StopThen(() => ExitApp(restart));
            return;
        }
        // A video being exported by a player would be lost: asked first, like the unsaved changes of the editors (and
        // before them, so that answering No closes nothing).
        if (OpenWindows.OfType<Player.PlayerWindow>().FirstOrDefault(p => p.IsExporting) is { } exporting)
        {
            _ = ConfirmQuitWhileExportingAsync(exporting);
            return;
        }
        // Let editors ask about unsaved changes first: one still open after Close() keeps Snaplume running.
        foreach (var editor in OpenWindows.OfType<EditorWindow>().ToList())
        {
            editor.Close();
            if (editor.IsVisible)
            {
                _restartOnExit = false;
                _launchOnExit = null;
                return;
            }
        }
        if (_launchOnExit is { } launch)
        {
            _launchOnExit = null;
            if (!launch()) return;
        }
        IsExiting = true;
        if (_restartOnExit && Environment.ProcessPath is { } exe)
        {
            try
            {
                // The new instance waits for this one to release the single instance.
                System.Diagnostics.Process.Start(exe, "--restarted")?.Dispose();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Redémarrage", ex);
            }
        }
        Desktop?.Shutdown();

        // Yes: the exports are cancelled (their players closed, their partial files deleted) and quitting goes on.
        async Task ConfirmQuitWhileExportingAsync(Player.PlayerWindow player)
        {
            player.Activate();
            var answer = await Dialogs.ShowAsync(player, Loc.T("App.QuitWhileExporting"), "Snaplume", DialogButtons.YesNo, DialogIcon.Warning,
                DialogResult.No);
            if (answer != DialogResult.Yes)
            {
                _restartOnExit = false;
                _launchOnExit = null;
                return;
            }
            foreach (var other in OpenWindows.OfType<Player.PlayerWindow>().Where(p => p.IsExporting).ToList()) other.Close();
            ExitApp(restart);
        }
    }

    /// <summary>The session is ending (log off, shutdown): finish the video file while there is time.</summary>
    private void OnShutdownRequested(object? sender, ShutdownRequestedEventArgs e)
    {
        try
        {
            if (Videos is { IsBusy: true }) Videos.Stop();
            // The originals of the recordings being converted are kept (in the temporary folder if time runs out).
            if (Conversions is { IsBusy: true }) Conversions.CancelAll();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Fin de session", ex);
        }
    }

    private void OnExit(object? sender, ControlledApplicationLifetimeExitEventArgs e)
    {
        IsExiting = true;
        _updateTimer?.Stop();
        WorkspaceListing.Updated -= UpdateTrayTooltip;
        HotkeyBox.FocusChanged -= OnHotkeyBoxFocusChanged;
        if (!AppServices.HasPlatform) return;
        Dispose(() => Platform.Hotkeys.Dispose(), "Raccourcis globaux");
        Dispose(() => Tray?.Dispose(), "Icône de notification");
        Dispose(() => Platform.SingleInstance.Dispose(), "Instance unique");
    }

    private static void Dispose(Action dispose, string context)
    {
        try
        {
            dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Fermeture : {context}", ex);
        }
    }

    // ---- Errors ----

    private void OnUnhandledException(object? sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Erreur non gérée", e.Exception);
        e.Handled = true;
        // An error raised while the message is shown (e.g. by a timer) is only logged.
        if (_showingError || IsExiting) return;
        _showingError = true;
        _ = ShowUnexpectedErrorAsync(e.Exception);
    }

    private async Task ShowUnexpectedErrorAsync(Exception ex)
    {
        try
        {
            await Dialogs.ShowAsync(null, Loc.T("App.UnexpectedError", ex.Message, ErrorLog.FilePath), "Snaplume",
                DialogButtons.Ok, DialogIcon.Error);
        }
        catch (Exception)
        {
            // Already logged.
        }
        finally
        {
            _showingError = false;
        }
    }
}
