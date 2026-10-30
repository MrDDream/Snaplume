using System.Globalization;
using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Win32;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Preferences;
using Snaplume.Recording;
using Snaplume.Workspace;

namespace Snaplume;

public partial class App : Application
{
    /// <summary>Names are per user: two accounts in the same session each get their own instance.</summary>
    /// <remarks>
    /// The Local\ names live in the session's namespace: another user logged on to the same session
    /// (e.g. "run as" another account) could create this mutex or event first and keep Snaplume from
    /// starting for this user. That is only a local denial of service; the command line pipe, which
    /// carries data, is protected by its owner and access rules (see <see cref="CreatePipeServer"/>).
    /// </remarks>
    private static readonly SecurityIdentifier? User = CurrentUser();
    private static readonly string UserSid = User?.Value ?? Environment.UserName;
    private static readonly string MutexName = $@"Local\Snaplume.SingleInstance.{UserSid}";
    private static readonly string ShowEventName = $@"Local\Snaplume.Show.{UserSid}";
    private static readonly string PipeName = $"Snaplume.CommandLine.{UserSid}";

    /// <summary>Largest command line accepted from another instance.</summary>
    private const int MaxForwardedBytes = 64 * 1024;

    private static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    private Mutex? _mutex;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private CancellationTokenSource? _pipeCancel;
    /// <summary>Pipe instance waiting for a client: closed on exit, so that a restarted instance can create its own.</summary>
    private NamedPipeServerStream? _pipeServer;
    private HotkeyManager? _hotkeys;
    private WorkspaceWindow? _workspace;
    private QuickPanel? _quickPanel;
    private SettingsWindow? _settingsWindow;
    private bool _showingError;
    private bool _restartOnExit;

    public static App Instance => (App)Current;

    public AppSettings Settings { get; private set; } = new();
    public CaptureService Captures { get; private set; } = null!;
    public VideoService Videos { get; private set; } = null!;
    public TrayIcon? Tray { get; private set; }
    public bool IsExiting { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        DispatcherUnhandledException += OnUnhandledException;
        try
        {
            Start(e.Args);
        }
        catch (Exception ex)
        {
            // Never leave an invisible process holding the single-instance mutex.
            ErrorLog.Write("Démarrage", ex);
            IsExiting = true;
            try
            {
                MessageBox.Show(Loc.T("App.StartupFailed", ex.Message, ErrorLog.FilePath), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            catch (Exception)
            {
                // Quit anyway.
            }
            Shutdown(1);
        }
    }

    private void Start(string[] args)
    {
        _mutex = new Mutex(true, MutexName, out bool firstInstance);
        if (!firstInstance && args.Contains("--restarted"))
        {
            // Restart after a language change: wait for the previous instance to quit.
            try
            {
                firstInstance = _mutex.WaitOne(TimeSpan.FromSeconds(10));
            }
            catch (AbandonedMutexException)
            {
                firstInstance = true;
            }
        }
        if (!firstInstance)
        {
            // Already running: it runs this command line (or just shows its quick panel).
            _mutex.Dispose();
            _mutex = null;
            if (!ForwardToRunningInstance(args)) SignalRunningInstance();
            IsExiting = true;
            Shutdown();
            return;
        }

        Settings = AppSettings.Load();
        Loc.Initialize(Settings.Language);
        ThemeManager.Initialize(Settings.Theme);
        // Arabic: every window is laid out right to left, unless it sets its own direction
        // (the selection overlay); images and drawings opt out explicitly.
        if (Loc.IsRightToLeft)
        {
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
            {
                if (sender is Window window && window.ReadLocalValue(FrameworkElement.FlowDirectionProperty) == DependencyProperty.UnsetValue)
                    window.FlowDirection = FlowDirection.RightToLeft;
            }));
        }
        var unavailableFolder = EnsureWorkspaceFolder();
        Captures = new CaptureService(this);
        Videos = new VideoService(this);
        Tray = new TrayIcon(this);
        _hotkeys = new HotkeyManager();
        RegisterHotkeys(showWarnings: true);
        if (unavailableFolder != null)
        {
            Tray.ShowWarning(Loc.T("App.FolderUnavailableTitle"),
                Loc.T("App.FolderUnavailableText", unavailableFolder, Settings.WorkspaceFolder), ShowSettings);
        }

        SystemEvents.SessionEnding += OnSystemSessionEnding;
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => Dispatcher.BeginInvoke(ShowQuickPanel), null, -1, false);
        _pipeCancel = new CancellationTokenSource();
        _ = ListenForCommandLinesAsync(_pipeCancel.Token);
        _ = Task.Run(VideoService.DeleteStaleTempFiles);

        HandleArgs(CommandLine.Parse(args), startup: true);
        StartUpdateChecks();
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
                Tray?.ShowInfo(Loc.T("Update.AvailableTitle"), Loc.T("Update.AvailableText", release.DisplayVersion),
                    () => FileOps.OpenWithDefaultApp(release.Page));
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
        await Captures.CaptureAsync(mode, args.DelaySeconds, settleMs: 100, quiet: args.Exit);
        if (exit) ExitApp();
    }

    /// <summary>"--capture text|qr --exit": quits once the result window is closed (or the selection cancelled).</summary>
    private async Task RunCommandLineWindowAsync(Task<Window?> capture, bool exit)
    {
        var window = await capture;
        if (!exit) return;
        if (window == null) ExitApp();
        else window.Closed += (_, _) => ExitApp();
    }

    /// <summary>"--record screen --duration 10 --exit" records a video without interaction.</summary>
    private async Task RunCommandLineRecordingAsync(CommandLine args, bool exit)
    {
        await Videos.ToggleAsync(fullScreen: args.Record == "screen", settleMs: 100, durationSeconds: args.DurationSeconds);
        if (exit) ExitApp();
    }

    /// <summary>Sends the command line to the running instance.</summary>
    /// <returns>False if it could not be reached (it is then only asked to show itself).</returns>
    private static bool ForwardToRunningInstance(string[] args)
    {
        try
        {
            // CurrentUserOnly: the pipe must belong to this user, not to another session's impostor.
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            client.Write(JsonSerializer.SerializeToUtf8Bytes(CommandLine.WithFullPaths(args)));
            client.Flush();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Transmission de la ligne de commande", ex);
            return false;
        }
    }

    private static void SignalRunningInstance()
    {
        try
        {
            using var show = EventWaitHandle.OpenExisting(ShowEventName);
            show.Set();
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Affichage de l'instance existante", ex);
        }
    }

    /// <summary>Receives the command lines of the instances started while this one runs.</summary>
    private async Task ListenForCommandLinesAsync(CancellationToken token)
    {
        int failures = 0;
        while (!token.IsCancellationRequested)
        {
            NamedPipeServerStream? server = null;
            try
            {
                server = CreatePipeServer();
                _pipeServer = server;
                await server.WaitForConnectionAsync(token);
                failures = 0;
            }
            catch (Exception ex)
            {
                ClosePipeServer(server);
                if (token.IsCancellationRequested) return;
                ErrorLog.Write("Réception d'une ligne de commande", ex);
                // The pipe cannot be created (name taken...): the Show event still works.
                if (++failures >= 3) return;
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            try
            {
                var args = await ReadForwardedArgumentsAsync(server, token);
                if (args != null) _ = Dispatcher.BeginInvoke(() => HandleArgs(CommandLine.Parse(args), startup: false));
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // One bad client (malformed or too long data, too slow, gone before the end): it is
                // ignored and the next one is awaited; the pipe itself works.
                ErrorLog.Write("Réception d'une ligne de commande", ex);
            }
            catch (Exception)
            {
                return;
            }
            finally
            {
                ClosePipeServer(server);
            }
        }
    }

    private void ClosePipeServer(NamedPipeServerStream? server)
    {
        if (server == null) return;
        if (_pipeServer == server) _pipeServer = null;
        server.Dispose();
    }

    /// <summary>Pipe only this user can open; FirstPipeInstance fails if another process already took the name.</summary>
    private static NamedPipeServerStream CreatePipeServer()
    {
        var user = User ?? throw new InvalidOperationException("No user SID");
        var security = new PipeSecurity();
        security.SetOwner(user);
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.FirstPipeInstance, 0, 0, security);
    }

    /// <returns>The arguments, or null if there are none or they are not a list of texts (e.g. "null", [null]).</returns>
    /// <exception cref="Exception">Invalid JSON, too much data, or the client too slow (5 s).</exception>
    private static async Task<string[]?> ReadForwardedArgumentsAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var data = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            data.Write(chunk, 0, read);
            if (data.Length > MaxForwardedBytes) throw new InvalidDataException("Forwarded command line too long");
        }
        if (data.Length == 0) return null;
        var args = JsonSerializer.Deserialize<string?[]>(data.ToArray());
        if (args == null || args.Any(a => a == null))
        {
            ErrorLog.Write("Réception d'une ligne de commande", "Arguments invalides, ignorés");
            return null;
        }
        return Array.ConvertAll(args, a => a!);
    }

    public void RegisterHotkeys(bool showWarnings)
    {
        if (_hotkeys == null || IsExiting) return;
        _hotkeys.UnregisterAll();

        var failed = new List<string>();
        void Register(string text, Action action)
        {
            if (!Hotkey.TryParse(text, out var hotkey) || hotkey.IsEmpty) return;
            if (!_hotkeys.Register(hotkey, action)) failed.Add(hotkey.DisplayName);
        }

        Register(Settings.HotkeyRegion, () => _ = Captures.CaptureAsync(CaptureKind.Region));
        Register(Settings.HotkeyScreen, () => _ = Captures.CaptureAsync(CaptureKind.Screen));
        Register(Settings.HotkeyWindow, () => _ = Captures.CaptureAsync(CaptureKind.ActiveWindow));
        Register(Settings.HotkeyRepeat, () => _ = Captures.CaptureAsync(CaptureKind.RepeatLast));
        Register(Settings.HotkeyDelayed, () => _ = Captures.CaptureAsync(CaptureKind.Region, Settings.DelaySeconds));
        Register(Settings.HotkeyVideo, () => _ = Videos.ToggleAsync());
        Register(Settings.HotkeyText, () => _ = Captures.CaptureTextAsync());

        if (!showWarnings) return;
        if (failed.Count > 0)
        {
            Tray?.ShowWarning(Loc.T("App.HotkeysUnavailableTitle"),
                Loc.T("App.HotkeysUnavailableText", string.Join(", ", failed)),
                ShowSettings);
        }
        else if (AppSettings.SnippingToolOwnsPrintScreen() && Settings.HotkeyRegion.Contains("PrintScreen", StringComparison.OrdinalIgnoreCase))
        {
            Tray?.ShowWarning(Loc.T("App.PrintScreenTakenTitle"), Loc.T("App.PrintScreenTakenText"), ShowSettings);
        }
    }

    /// <summary>Compact panel above the notification area (like the tray flyout of Screenpresso).</summary>
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
        if (_workspace.WindowState == WindowState.Minimized) _workspace.WindowState = WindowState.Normal;
        _workspace.Activate();
    }

    private static bool IsAnimatedGif(string path)
    {
        if (!path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            using var stream = File.OpenRead(path);
            return new System.Windows.Media.Imaging.GifBitmapDecoder(stream, System.Windows.Media.Imaging.BitmapCreateOptions.DelayCreation,
                System.Windows.Media.Imaging.BitmapCacheOption.None).Frames.Count > 1;
        }
        catch (Exception)
        {
            return false;
        }
    }

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
            // The editor keeps a single frame: animated GIFs (exported from videos) open in the default viewer.
            if (IsAnimatedGif(path))
            {
                if (!FileOps.OpenWithDefaultApp(path)) MessageBox.Show(Loc.T("App.OpenImageFailed", path), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }
            if (Windows.OfType<EditorWindow>().FirstOrDefault(w => IsSamePath(w.FilePath, path)) is { } existing)
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
            MessageBox.Show(Loc.T("App.OpenImageFailed", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Built-in video player (mpv-like).</summary>
    public void OpenPlayer(string path)
    {
        try
        {
            path = Path.GetFullPath(path);
            if (Windows.OfType<Player.PlayerWindow>().FirstOrDefault(w => IsSamePath(w.FilePath, path)) is { } existing)
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
            MessageBox.Show(Loc.T("App.OpenVideoFailed", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Same file, whatever the form of the paths (relative, "..", case).</summary>
    private static bool IsSamePath(string? path, string fullPath)
    {
        if (string.IsNullOrEmpty(path)) return false;
        try
        {
            return string.Equals(Path.GetFullPath(path), fullPath, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void BringToFront(Window window)
    {
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    /// <summary>Window with the text read in the image (OCR).</summary>
    public Ocr.OcrWindow ShowOcr(System.Windows.Media.Imaging.BitmapSource image)
    {
        var window = new Ocr.OcrWindow(image);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Text in an image file (gallery, editor).</summary>
    public void ShowOcr(string path)
    {
        try
        {
            ShowOcr(ImageIO.Load(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OCR", ex);
            MessageBox.Show(ex.Message, "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>QR codes and barcodes in an image file (gallery, editor).</summary>
    public void ShowQr(string path) => Qr.QrService.ShowForFile(path);

    public void ShowSettings()
    {
        if (IsExiting) return;
        if (_settingsWindow != null)
        {
            _settingsWindow.Activate();
            return;
        }
        // Free the shortcuts so that they can be typed in the shortcut fields.
        _hotkeys?.UnregisterAll();
        _settingsWindow = new SettingsWindow(Settings.Clone());
        try
        {
            if (_settingsWindow.ShowDialog() == true)
            {
                var result = _settingsWindow.Result;
                // The active workspace may have been changed from the tray menu meanwhile,
                // the OCR language from a text window, the update state by the background check: the dialog does not edit them.
                result.ActiveWorkspace = Settings.ActiveWorkspace;
                result.OcrLanguage = Settings.OcrLanguage;
                result.LastUpdateCheckUtc = Settings.LastUpdateCheckUtc;
                result.UpdateNotifiedVersion = Settings.UpdateNotifiedVersion;
                result.Sanitize();
                bool folderChanged = !string.Equals(Settings.WorkspaceFolder, result.WorkspaceFolder, StringComparison.OrdinalIgnoreCase);
                bool languageChanged = Settings.Language != result.Language;
                // Another root folder: keep the active workspace only if it exists there too.
                if (folderChanged && result.ActiveWorkspace.Length > 0
                    && !Directory.Exists(Path.Combine(result.WorkspaceFolder, result.ActiveWorkspace)))
                {
                    result.ActiveWorkspace = "";
                }
                Settings = result;
                ThemeManager.SetMode(Settings.Theme);
                try
                {
                    Settings.Save();
                }
                catch (Exception ex)
                {
                    // The new settings still apply until Snaplume quits.
                    ErrorLog.Write("Enregistrement des paramètres", ex);
                    MessageBox.Show(Loc.T("Settings.SaveFailed", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Warning);
                }
                try
                {
                    Settings.ApplyStartWithWindows();
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Démarrage avec Windows", ex);
                }
                if (folderChanged)
                {
                    _workspace?.Reload();
                    _quickPanel?.Reload();
                    Workspaces.OnRootChanged();
                }
                if (languageChanged)
                {
                    // Shown in the newly chosen language, so the user understands it.
                    Loc.Initialize(Settings.Language);
                    if (MessageBox.Show(Loc.T("App.RestartForLanguage"), "Snaplume", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
                        Restart();
                }
            }
        }
        finally
        {
            _settingsWindow = null;
            RegisterHotkeys(showWarnings: true);
        }
    }

    public void ExitApp() => ExitApp(restart: false);

    /// <summary>Quits and starts again (the interface language is applied at startup).</summary>
    public void Restart() => ExitApp(restart: true);

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
        if (Captures is { IsBusy: true })
        {
            // Let the capture in progress stop cleanly first: countdown or selection cancelled, scrolling
            // stopped with the mouse put back and the image saved; then quit.
            Captures.StopThen(() => ExitApp(restart));
            return;
        }
        // Let editors ask about unsaved changes first.
        foreach (var editor in Windows.OfType<EditorWindow>().ToList())
        {
            editor.Close();
            if (editor.IsVisible)
            {
                _restartOnExit = false;
                return;
            }
        }
        IsExiting = true;
        if (_restartOnExit && Environment.ProcessPath is { } exe)
        {
            try
            {
                // The new instance waits for this one to release the single-instance mutex.
                System.Diagnostics.Process.Start(exe, "--restarted")?.Dispose();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Redémarrage", ex);
            }
        }
        Shutdown();
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        StopRecordingForSessionEnd();
    }

    private void OnSystemSessionEnding(object? sender, SessionEndingEventArgs e)
    {
        if (Dispatcher.CheckAccess()) StopRecordingForSessionEnd();
        else Dispatcher.BeginInvoke(StopRecordingForSessionEnd);
    }

    /// <summary>Windows is logging off or shutting down: finish the video file while there is time.</summary>
    private void StopRecordingForSessionEnd()
    {
        if (Videos is { IsBusy: true }) Videos.Stop();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        IsExiting = true;
        SystemEvents.SessionEnding -= OnSystemSessionEnding;
        _pipeCancel?.Cancel();
        // Closed now, not when the listener wakes up (it may never run again): a restarted instance
        // must be able to create the pipe while this process ends.
        ClosePipeServer(_pipeServer);
        _showWait?.Unregister(null);
        _hotkeys?.Dispose();
        Tray?.Dispose();
        _showEvent?.Dispose();
        if (_mutex != null)
        {
            try
            {
                _mutex.ReleaseMutex();
            }
            catch (ApplicationException)
            {
                // Not owned (startup failed before acquiring it).
            }
            _mutex.Dispose();
        }
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ErrorLog.Write("Erreur non gérée", e.Exception);
        e.Handled = true;
        // An error raised while the message is shown (e.g. by a timer) is only logged.
        if (_showingError) return;
        _showingError = true;
        try
        {
            MessageBox.Show(Loc.T("App.UnexpectedError", e.Exception.Message, ErrorLog.FilePath),
                "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _showingError = false;
        }
    }

    /// <summary>Exception on another thread: the process ends, at least keep a trace.</summary>
    private static void OnFatalException(object sender, UnhandledExceptionEventArgs e) =>
        ErrorLog.Write("Erreur fatale", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.Write("Tâche en échec non observée", e.Exception);
        e.SetObserved();
    }
}

/// <summary>
/// Command line: [--tray] [--capture region|screen|all|window|repeat|scrolling|text|qr] [--delay N]
/// [--record region|screen] [--duration N] [--workspace name] [--exit] [--settings] [--window] [image to edit or video to play]
/// </summary>
/// <remarks>When Snaplume is already running, the command line is run by that instance ("--exit" is then ignored).</remarks>
public sealed record CommandLine(bool Tray, CaptureKind? Capture, int DelaySeconds, bool Exit, string? EditPath,
    string? Record, int DurationSeconds, bool Settings, bool Text, string? Workspace, bool Window = false, bool Qr = false)
{
    /// <summary>Options followed by a value.</summary>
    private static readonly HashSet<string> ValueOptions = new(StringComparer.OrdinalIgnoreCase)
    {
        "--delay", "--capture", "--record", "--duration", "--workspace",
    };

    public static CommandLine Parse(string[] args)
    {
        bool tray = false, exit = false, settings = false, text = false, window = false, qr = false;
        CaptureKind? capture = null;
        int delay = 0, duration = 0;
        string? editPath = null, record = null, workspace = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--tray": tray = true; break;
                case "--exit": exit = true; break;
                case "--delay" when i + 1 < args.Length && TryParseInt(args[i + 1], out var d):
                    delay = Math.Clamp(d, 0, 60);
                    i++;
                    break;
                case "--capture" when i + 1 < args.Length:
                    var kind = args[++i].ToLowerInvariant();
                    switch (kind)
                    {
                        case "text": text = true; break;
                        case "qr": qr = true; break;
                        case "scrolling" or "scroll": capture = CaptureKind.Scrolling; break;
                        case "region": capture = CaptureKind.Region; break;
                        case "screen": capture = CaptureKind.Screen; break;
                        case "all": capture = CaptureKind.AllScreens; break;
                        case "window": capture = CaptureKind.ActiveWindow; break;
                        case "repeat": capture = CaptureKind.RepeatLast; break;
                        default: ErrorLog.Write("Ligne de commande", $"--capture {kind} : mode inconnu, ignoré"); break;
                    }
                    break;
                case "--record" when i + 1 < args.Length:
                    var area = args[++i].ToLowerInvariant();
                    if (area is "screen" or "region") record = area;
                    else ErrorLog.Write("Ligne de commande", $"--record {area} : mode inconnu, ignoré");
                    break;
                case "--duration" when i + 1 < args.Length && TryParseInt(args[i + 1], out var s):
                    duration = Math.Clamp(s, 1, 24 * 3600);
                    i++;
                    break;
                case "--workspace" when i + 1 < args.Length:
                    workspace = args[++i].Trim();
                    break;
                case "--settings": settings = true; break;
                case "--window": window = true; break;
                case "--restarted": break;
                default:
                    if (File.Exists(args[i]) && ImageIO.IsCapture(args[i])) editPath = Path.GetFullPath(args[i]);
                    else ErrorLog.Write("Ligne de commande", $"{args[i]} : argument ignoré");
                    break;
            }
        }
        return new CommandLine(tray, capture, delay, exit, editPath, record, duration, settings, text, workspace, window, qr);
    }

    /// <summary>The arguments with file paths made absolute, for the instance already running (other current folder).</summary>
    public static string[] WithFullPaths(string[] args)
    {
        var result = (string[])args.Clone();
        for (int i = 0; i < result.Length; i++)
        {
            if (ValueOptions.Contains(result[i]))
            {
                i++;
                continue;
            }
            if (result[i].StartsWith("--", StringComparison.Ordinal)) continue;
            try
            {
                result[i] = Path.GetFullPath(result[i]);
            }
            catch (Exception)
            {
                // Not a path: sent as is, the running instance ignores it.
            }
        }
        return result;
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
