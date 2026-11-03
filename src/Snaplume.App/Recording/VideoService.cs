using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>
/// Screen recording to MP4 through the platform's recorder (ScreenRecorderLib under Windows, GStreamer under Linux).
/// One recording at a time; the file goes to the active workspace when finished. The format and compression of the
/// settings are made by the recorder when it can (H.264, HEVC, quality presets); otherwise the recording is handed to
/// <see cref="RecordingConverter"/> once it stops (<see cref="RecordingPlanner"/>).
/// </summary>
/// <remarks>
/// States: idle → starting (area selection, video options until Record is pressed, countdown, system permission) →
/// recording → finishing (file written and moved) → idle. <see cref="Finish"/> is the only way back to idle and resets
/// everything. While the options are shown, the area can still be moved (grip of the frame) or given a standard size.
/// Where the system chooses what is recorded (Wayland portal, no <see cref="PlatformFeatures.RecordAnyArea"/>),
/// Snaplume shows no selection: the portal asks the user.
/// </remarks>
public sealed class VideoService
{
    private const int CountdownSeconds = 3;
    private const string TempPrefix = "Snaplume_";
    /// <summary>Smallest area worth recording (a click instead of a drag selects less).</summary>
    private const int MinSize = 16;
    /// <summary>
    /// A failure this soon after the start, with the webcam, is taken for a camera that could not
    /// be opened (busy, unplugged): the recording starts again without it.
    /// </summary>
    private static readonly TimeSpan WebcamStartupWindow = TimeSpan.FromSeconds(4);
    /// <summary>
    /// A recorder that has not reported the end of its file this long after being stopped is given up: the file is kept
    /// as it is, and Snaplume can record again.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(30);

    private readonly App _app;
    private readonly ScreenOperation _operation = new();
    private readonly Stopwatch _elapsed = new();
    private IScreenRecording? _recording;
    private RecordingControlBar? _bar;
    private RecordingFrame? _frame;
    private DispatcherTimer? _timer;
    private TaskCompletionSource<string?>? _completion;
    /// <summary>Cancels a start waiting for the system (permission dialog of the portal).</summary>
    private CancellationTokenSource? _starting;
    /// <summary>Cancels the countdown before the recording (Stop, Cancel).</summary>
    private CancellationTokenSource? _countdown;
    /// <summary>The options are shown: true when Record is pressed, false when cancelled.</summary>
    private TaskCompletionSource<bool>? _armed;
    private string? _tempPath;
    private PixelRect _area;
    /// <summary>The current recording inlays the webcam.</summary>
    private bool _webcamActive;
    private Action? _afterStop;
    private bool _cancelled;
    private bool _stopping;
    private bool _paused;
    /// <summary>This video's area selection is on screen (only that one is closed by <see cref="Stop"/>).</summary>
    private bool _selecting;
    /// <summary>The end of the recording is being handled (<see cref="OnCompleteAsync"/>): once only.</summary>
    private bool _completing;
    /// <summary>Format and compression of the current recording (Settings › Video), chosen when it starts.</summary>
    private RecordingFormatChoice _choice = new(WebFormat.Mp4H264, RecordingCompression.None);
    /// <summary>What the recorder is asked for, and the conversion afterwards (<see cref="RecordingPlanner"/>).</summary>
    private RecordingPlan? _plan;

    public VideoService(App app)
    {
        _app = app;
        // Options changed in the settings window while the video options are shown.
        app.SettingsApplied += () => _bar?.RefreshOptions();
    }

    /// <summary>True from the start of the area selection until the video is saved (or abandoned).</summary>
    public bool IsBusy => _operation.IsBusy;

    /// <summary>
    /// Raised on the UI thread at every change: start, recording begun, paused or resumed,
    /// finishing, back to idle (to update menus and buttons).
    /// </summary>
    public event Action? StateChanged;

    private AppSettings Settings => _app.Settings;
    private IPlatform Platform => _app.Platform;

    /// <summary>Starts a recording (area selection first, unless <paramref name="fullScreen"/>), or stops the one in progress.</summary>
    /// <param name="fullScreen">Record the monitor under the cursor instead of selecting an area.</param>
    /// <param name="hide">Window hidden during the recording (e.g. the quick panel).</param>
    /// <param name="durationSeconds">
    /// Stops automatically after this time (command line); the recording then starts without waiting for Record.
    /// </param>
    /// <returns>
    /// Path of the saved video, or null if cancelled or failed, or when it is converted afterwards (Settings › Video ›
    /// Video format: <see cref="RecordingConverter"/> announces the file once converted).
    /// </returns>
    public async Task<string?> ToggleAsync(bool fullScreen = false, Window? hide = null, int settleMs = 0, int durationSeconds = 0)
    {
        if (IsBusy)
        {
            Stop();
            return null;
        }
        // Not while a capture is interactive (choosing its area, scrolling...): see ScreenOperation.
        if (!_operation.TryBegin(hide)) return null;

        // Continuations run later: Finish() must complete before the caller goes on (e.g. quits).
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _completion = completion;
        _cancelled = false;
        _stopping = false;
        _paused = false;
        _countdown = new CancellationTokenSource();
        StateChanged?.Invoke();
        try
        {
            await _operation.SettleAsync(settleMs);

            var platform = Platform;
            // Without RecordAnyArea the system asks which screen or window to record when the recording starts.
            bool systemChooses = !platform.Has(PlatformFeatures.RecordAnyArea);
            PixelRect? selected = null;
            if (!_cancelled)
            {
                if (systemChooses || fullScreen) selected = MonitorUnderCursor(platform);
                else selected = await SelectAreaAsync(platform);
            }
            if (_cancelled || selected is not PixelRect chosen || chosen.Width < MinSize || chosen.Height < MinSize)
            {
                Finish(null);
                return await completion.Task;
            }

            // The area is chosen: screenshots may be taken during the countdown and the recording.
            _operation.EndInteractive();

            var area = CaptureGeometry.EvenSize(chosen);
            _area = area;
            bool movable = !fullScreen && !systemChooses;
            bool showOptions = durationSeconds <= 0;
            ShowControls(area, showFrame: movable, showOptions, movable ? platform.Screen.Monitors.Select(m => m.Bounds).ToList() : null);
            if (showOptions)
            {
                // Until Record is pressed (or the cross, Escape, the shortcut again...).
                var armed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _armed = armed;
                bool record = await armed.Task;
                _armed = null;
                if (!record || _cancelled)
                {
                    Finish(null);
                    return await completion.Task;
                }
                // Moved or resized meanwhile; it stays where it is from now on.
                area = _area;
                _frame?.EndMoving();
                // The bar took the focus for its options: the recorded window gets it back (the frame lets the point through).
                if (!systemChooses) platform.Pointer.ActivateWindowAt(area.X + area.Width / 2, area.Y + area.Height / 2);
            }
            if (!systemChooses)
            {
                // With the portal, its dialog comes first: a countdown before it would mean nothing.
                var countdown = _countdown?.Token ?? CancellationToken.None;
                for (int remaining = CountdownSeconds; remaining > 0 && !_cancelled; remaining--)
                {
                    _bar?.ShowCountdown(remaining);
                    await Task.Delay(1000, countdown);
                }
            }
            if (_cancelled)
            {
                Finish(null);
                return await completion.Task;
            }

            await BeginAsync(area);
            if (_completion != completion) return await completion.Task;
            StateChanged?.Invoke();

            if (durationSeconds > 0)
            {
                await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(durationSeconds)));
                if (!completion.Task.IsCompleted) Stop();
            }
        }
        catch (OperationCanceledException)
        {
            // Countdown cancelled, or refused or cancelled in the system's permission dialog (Wayland portal): nothing to report.
            DeleteTempFile(_tempPath);
            if (_completion == completion) Finish(null);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo", ex);
            DeleteTempFile(_tempPath);
            if (_completion == completion) Finish(null);
            _app.Tray?.ShowError(Loc.T("Video.Failed"), ex.Message);
        }
        return await completion.Task;
    }

    /// <summary>Stops and saves the recording (or aborts the selection / countdown / permission).</summary>
    public void Stop()
    {
        if (!IsBusy) return;
        if (_recording == null)
        {
            _cancelled = true;
            _armed?.TrySetResult(false);
            _countdown?.Cancel();
            _starting?.Cancel();
            // Only this video's selection: another one on screen belongs to a capture.
            if (_selecting) RegionSelectorWindow.CancelCurrent();
            return;
        }
        if (_stopping) return;
        _stopping = true;
        // The bar says "finishing" until the file is written: the clock no longer turns it back into the recording bar.
        _timer?.Stop();
        _elapsed.Stop();
        _bar?.SetFinishing();
        try
        {
            _recording.Stop();
            _ = GiveUpIfNoAnswerAsync(_completion);
        }
        catch (Exception ex)
        {
            // The recorder is gone: what it wrote is looked for as if it had completed.
            ErrorLog.Write("Arrêt de l'enregistrement vidéo", ex);
            _ = OnCompleteAsync();
        }
        StateChanged?.Invoke();
    }

    /// <summary>
    /// The recorder was stopped but never reported the end of its file (<see cref="StopTimeout"/>): its file is kept where
    /// it is (the user is told), and the service goes back to idle.
    /// </summary>
    private async Task GiveUpIfNoAnswerAsync(TaskCompletionSource<string?>? owner)
    {
        await Task.Delay(StopTimeout);
        if (owner == null || _completion != owner || _completing) return;
        ErrorLog.Write("Arrêt de l'enregistrement vidéo", $"Pas de fin d'enregistrement {StopTimeout.TotalSeconds} s après l'arrêt");
        var temp = _tempPath;
        bool keep = !_cancelled;
        // Renamed so that the cleanup at startup leaves it (refused while the recorder still holds it: kept as it is).
        string? kept = keep && temp != null && File.Exists(temp) ? KeepTempFile(temp) : null;
        if (!keep) DeleteTempFile(temp);
        Finish(null);
        if (kept != null) _app.Tray?.ShowError(Loc.T("Video.SaveFailed"), Loc.T("Video.KeptInTemp", Loc.T("Video.StopTimeout"), kept));
        else if (keep) _app.Tray?.ShowError(Loc.T("Video.Failed"), Loc.T("Video.StopTimeout"));
    }

    /// <summary>Stops without keeping the video.</summary>
    private void Cancel()
    {
        _cancelled = true;
        Stop();
    }

    /// <summary>
    /// Stops the recording and runs the action once the file is written and the service is idle
    /// again (immediately, through the dispatcher, if nothing is being recorded).
    /// </summary>
    public void StopThen(Action action)
    {
        if (!IsBusy)
        {
            Dispatcher.UIThread.Post(action);
            return;
        }
        _afterStop += action;
        Stop();
    }

    private void TogglePause()
    {
        if (_recording is not { CanPause: true } recording || _stopping) return;
        try
        {
            if (_paused)
            {
                recording.Resume();
                _paused = false;
                _elapsed.Start();
            }
            else
            {
                recording.Pause();
                _paused = true;
                _elapsed.Stop();
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Pause de l'enregistrement vidéo", ex);
            return;
        }
        _bar?.SetPaused(_paused);
        _bar?.SetRecording(_elapsed.Elapsed);
        StateChanged?.Invoke();
    }

    /// <summary>
    /// Where the recordings are written until they reach the workspace (or wait for their conversion): a folder of this
    /// user only, never the shared temporary folder. %LOCALAPPDATA%\Snaplume\recordings on Windows,
    /// $XDG_CACHE_HOME/snaplume/recordings (~/.cache by default) elsewhere, created if needed.
    /// </summary>
    /// <exception cref="IOException">The folder is a link, or other users may write to it.</exception>
    internal static string RecordingsFolder() => OperatingSystem.IsWindows()
        ? UpdateDownloader.PrivateFolderIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snaplume"), "recordings")
        : UpdateDownloader.PrivateFolder("recordings");

    /// <summary>
    /// Removes the temporary videos left by a crash (older than a day, so never one being recorded), in the folder of
    /// the recordings and in the temporary folder used by older versions. The videos kept on purpose ("Snaplume video …")
    /// are left alone.
    /// </summary>
    public static void DeleteStaleTempFiles()
    {
        var limit = DateTime.UtcNow.AddDays(-1);
        foreach (var folder in new Func<string>[] { RecordingsFolder, Path.GetTempPath })
        {
            try
            {
                foreach (var file in new DirectoryInfo(folder()).EnumerateFiles(TempPrefix + "*.mp4"))
                {
                    if (file.LastWriteTimeUtc >= limit) continue;
                    try
                    {
                        file.Delete();
                    }
                    catch (Exception ex)
                    {
                        ErrorLog.Write("Nettoyage des vidéos temporaires", ex);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Nettoyage des vidéos temporaires", ex);
            }
        }
    }

    /// <summary>Monitor under the mouse, absolute physical coordinates (the primary one if the mouse is unknown).</summary>
    private static PixelRect MonitorUnderCursor(IPlatform platform)
    {
        (int X, int Y)? cursor = platform.Pointer.TryGetPosition(out int x, out int y) ? (x, y) : null;
        return CaptureGeometry.MonitorUnderCursor(platform.Screen.Monitors, cursor)?.Bounds ?? throw new InvalidOperationException("No monitor");
    }

    /// <summary>The area the user selects on a still of the desktop, absolute physical coordinates; null if cancelled.</summary>
    private async Task<PixelRect?> SelectAreaAsync(IPlatform platform)
    {
        var desktop = await platform.Screen.CaptureDesktopAsync(includeCursor: false);
        if (_cancelled) return null;
        _selecting = true;
        try
        {
            return await RegionSelectorWindow.PickAsync(desktop, Loc.T("Video.SelectTitle")) is PixelRect relative
                ? CaptureGeometry.ToScreen(relative, desktop.Bounds)
                : null;
        }
        finally
        {
            _selecting = false;
        }
    }

    /// <param name="showOptions">The bar starts with the options and waits for Record.</param>
    /// <param name="monitors">
    /// Monitor bounds when the area may be moved and resized before the recording; null for a fixed area (whole screen).
    /// </param>
    private void ShowControls(PixelRect area, bool showFrame, bool showOptions, IReadOnlyList<PixelRect>? monitors)
    {
        if (showFrame)
        {
            _frame = new RecordingFrame(area, showOptions ? monitors : null);
            _frame.AreaChanged += moved =>
            {
                _area = moved;
                _bar?.SetArea(moved);
            };
            _frame.Show();
        }
        _bar = new RecordingControlBar(area, showOptions, showOptions ? monitors : null);
        _bar.RecordClicked += () => _armed?.TrySetResult(true);
        _bar.SettingsClicked += () => _app.ShowSettings("PageVideo");
        _bar.AreaSizeChosen += (width, height) => ResizeArea(width, height, monitors);
        _bar.PauseClicked += TogglePause;
        _bar.StopClicked += Stop;
        _bar.CancelClicked += Cancel;
        _bar.Show();
    }

    /// <summary>Another size chosen in the options: around the same center, within its monitor.</summary>
    private void ResizeArea(int width, int height, IReadOnlyList<PixelRect>? monitors)
    {
        if (_armed == null || monitors == null) return;
        _area = RecordingArea.Resize(_area, width, height, monitors);
        _frame?.SetArea(_area);
        _bar?.SetArea(_area);
    }

    private async Task BeginAsync(PixelRect area)
    {
        _area = area;
        // Recorded directly in the chosen format and compression when the recorder can, else converted afterwards.
        // Off the UI thread: the HEVC encoders are looked for the first time.
        var choice = RecordingFormatChoice.From(Settings);
        var recorder = Platform.Recorder;
        int framerate = Settings.VideoFramerate;
        _choice = choice;
        _plan = await Task.Run(() => RecordingPlanner.Plan(choice, framerate, recorder.CanRecord));
        try
        {
            await StartRecorderAsync(area, Settings.VideoWebcam);
        }
        catch (Exception ex) when (_webcamActive && ex is not OperationCanceledException && !_cancelled)
        {
            // The camera could not be opened: record without it.
            ErrorLog.Write("Webcam", ex);
            DropRecording();
            await StartRecorderAsync(area, webcam: false);
            _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamBusy"));
        }
        if (_recording == null) return;
        if (_webcamActive && _recording.WebcamDropped)
        {
            // The recorder went on without the camera it could not open.
            _webcamActive = false;
            _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamBusy"));
        }
        if (_cancelled)
        {
            // Cancelled while the system was asking: stop at once, the file is deleted on completion.
            Stop();
            return;
        }

        _elapsed.Restart();
        _bar?.SetCanPause(_recording.CanPause);
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _bar?.SetRecording(_elapsed.Elapsed);
        _timer.Start();
        _bar?.SetRecording(TimeSpan.Zero);
    }

    /// <summary>Asks the platform to record into a new temporary file (<see cref="RecordingsFolder"/>).</summary>
    private async Task StartRecorderAsync(PixelRect area, bool webcam)
    {
        var platform = Platform;
        _tempPath = Path.Combine(RecordingsFolder(), $"{TempPrefix}{Guid.NewGuid():N}.mp4");
        var camera = webcam ? FindWebcam(platform) : null;
        _webcamActive = camera != null;
        var options = new RecordingOptions(
            area,
            Settings.VideoSystemAudio,
            Settings.VideoMicrophone,
            Settings.VideoShowClicks && platform.Has(PlatformFeatures.ClickHighlight),
            Math.Clamp(_plan?.Framerate ?? Settings.VideoFramerate, 5, 60),
            camera,
            Settings.VideoMicrophoneDevice,
            Settings.VideoClickZoom && platform.Has(PlatformFeatures.ClickZoom),
            _plan?.Encoding);

        _starting = new CancellationTokenSource();
        IScreenRecording recording;
        try
        {
            recording = await platform.Recorder.StartAsync(options, _tempPath, _starting.Token);
        }
        finally
        {
            _starting.Dispose();
            _starting = null;
        }
        // Events of a recording already finished (or replaced) are ignored.
        recording.Completed += () => Dispatcher.UIThread.Post(() =>
        {
            if (_recording == recording) _ = OnCompleteAsync();
        });
        recording.Failed += error => Dispatcher.UIThread.Post(() =>
        {
            if (_recording == recording) OnFailed(error);
        });
        _recording = recording;
    }

    /// <summary>
    /// The camera chosen in the settings (the first one when none is chosen). Null, with a warning (the recording
    /// goes on without it), when it is not connected.
    /// </summary>
    private WebcamOptions? FindWebcam(IPlatform platform)
    {
        WebcamInfo? camera = null;
        try
        {
            var cameras = platform.Recorder.Webcams();
            var wanted = Settings.VideoWebcamDevice;
            camera = string.IsNullOrEmpty(wanted)
                ? cameras.FirstOrDefault()
                : cameras.FirstOrDefault(c => string.Equals(c.Id, wanted, StringComparison.OrdinalIgnoreCase))
                  ?? cameras.FirstOrDefault(c => string.Equals(c.Name, wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
        }
        if (camera == null)
        {
            _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamNotFound"));
            return null;
        }
        // The size is kept between 10 and 40 % by AppSettings.Sanitize.
        return new WebcamOptions(camera.Id, Settings.VideoWebcamCorner, Settings.VideoWebcamSize);
    }

    /// <summary>Frees the current recording and its temporary file (before starting again).</summary>
    private void DropRecording()
    {
        var failed = _recording;
        _recording = null;
        try
        {
            failed?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo", ex);
        }
        DeleteTempFile(_tempPath);
    }

    /// <summary>
    /// The recording failed right after starting with the webcam (camera busy in another
    /// application, or unplugged): starts again without it rather than losing the recording.
    /// </summary>
    private async Task<bool> TryRestartWithoutWebcamAsync(string error)
    {
        if (!_webcamActive || _stopping || _cancelled || _elapsed.Elapsed > WebcamStartupWindow) return false;
        ErrorLog.Write("Webcam", error);
        DropRecording();
        try
        {
            await StartRecorderAsync(_area, webcam: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLog.Write("Enregistrement vidéo sans webcam", ex);
            return false;
        }
        if (_cancelled)
        {
            // Stopped while it was starting again: nothing worth keeping was recorded (deleted on completion).
            Stop();
            return true;
        }
        _elapsed.Restart();
        _paused = false;
        _bar?.SetPaused(false);
        _bar?.SetRecording(TimeSpan.Zero);
        _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamBusy"));
        return true;
    }

    private async Task OnCompleteAsync()
    {
        // Reported twice (the recorder failing to stop, then completing): handled once.
        if (_completing) return;
        _completing = true;
        var owner = _completion;
        var temp = _tempPath;
        try
        {
            await SaveRecordingAsync(owner, temp);
        }
        catch (Exception ex)
        {
            // Whatever failed, the service goes back to idle, and the user learns where the video is if it is still there.
            ErrorLog.Write("Fin de l'enregistrement vidéo", ex);
            if (owner != null && _completion == owner) Finish(null);
            var kept = temp != null && File.Exists(temp) ? temp : null;
            _app.Tray?.ShowError(Loc.T("Video.SaveFailed"), kept != null ? Loc.T("Video.KeptInTemp", ex.Message, kept) : ex.Message);
        }
    }

    /// <summary>The recorder is done: the file goes to the workspace (or to its conversion), then back to idle.</summary>
    private async Task SaveRecordingAsync(TaskCompletionSource<string?>? owner, string? temp)
    {
        bool keep = !_cancelled;
        _stopping = true;
        _timer?.Stop();
        _bar?.SetFinishing();
        string? saved = null;
        Exception? error = null;
        // AV1, WebM, a file size, or HEVC the recorder could not encode: converted once the recorder is idle.
        var conversion = keep && _plan != null ? RecordingPlanner.AfterRecording(_plan, _recording?.Codec ?? VideoCodec.H264) : null;
        if (conversion != null && temp != null && File.Exists(temp))
        {
            var job = new RecordingConversionJob(await Task.Run(() => KeepTempFile(temp)), Workspaces.ActiveFolder, "Video_" + CaptureService.TimeStamp(),
                conversion, _choice.KeepOriginal, _area);
            if (owner != null && _completion == owner) Finish(null);
            _app.Conversions.Enqueue(job);
            return;
        }
        if (temp != null && File.Exists(temp))
        {
            if (keep)
            {
                // Moving may copy a big file (workspace on another drive): not on the UI thread.
                var folder = Workspaces.ActiveFolder;
                var name = "Video_" + CaptureService.TimeStamp();
                try
                {
                    saved = await Task.Run(() =>
                    {
                        Directory.CreateDirectory(folder);
                        var path = FileOps.UniquePath(folder, name, ".mp4");
                        File.Move(temp, path);
                        return path;
                    });
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Enregistrement de la vidéo", ex);
                    error = ex;
                    temp = KeepTempFile(temp);
                }
            }
            else
            {
                DeleteTempFile(temp);
            }
        }

        // Unless a failure already ended this recording while the file was moving.
        bool current = owner != null && _completion == owner;
        bool restoring = current && _operation.HasHiddenWindow;
        if (current) Finish(saved);
        if (error != null)
        {
            // The recording is not lost: tell the user where it is.
            _app.Tray?.ShowError(Loc.T("Video.SaveFailed"), Loc.T("Video.KeptInTemp", error.Message, temp));
        }
        else if (saved != null)
        {
            try
            {
                Announce(saved, Loc.T("Video.SavedDetails", Path.GetFileName(saved)), showPanel: !restoring);
            }
            catch (Exception ex)
            {
                // The video is in the workspace all the same.
                ErrorLog.Write("Annonce de la vidéo", ex);
            }
        }
    }

    /// <summary>
    /// A video reached the workspace: shown in the gallery (and the quick panel, as after a capture), with a
    /// notification that opens it in the player.
    /// </summary>
    /// <param name="details">Text of the notification.</param>
    internal void Announce(string path, string details, bool showPanel = true)
    {
        _app.Captures.NotifySaved(path);
        if (showPanel && Settings.ShowPanelAfterCapture) _app.ShowQuickPanel();
        _app.Tray?.ShowInfo(Loc.T("Video.Saved"), details, () => _app.OpenPlayer(path));
        _app.Captures.WarnIfWorkspaceUnavailable();
    }

    /// <summary>
    /// The video could not reach the workspace, or waits for its conversion: give it a name that the cleanup of
    /// temporary files at startup leaves alone.
    /// </summary>
    private static string KeepTempFile(string temp)
    {
        try
        {
            var kept = FileOps.UniquePath(Path.GetDirectoryName(temp)!, "Snaplume video " + CaptureService.TimeStamp(), ".mp4");
            File.Move(temp, kept);
            return kept;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Conservation de la vidéo", ex);
            return temp;
        }
    }

    private static void DeleteTempFile(string? path)
    {
        if (path == null) return;
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression de la vidéo temporaire", ex);
        }
    }

    private async void OnFailed(string error)
    {
        try
        {
            if (await TryRestartWithoutWebcamAsync(error)) return;
        }
        catch (OperationCanceledException)
        {
            // Stopped or cancelled while it was starting again without the webcam (portal dialog): not a failure.
            DeleteTempFile(_tempPath);
            Finish(null);
            return;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo", ex);
        }
        ErrorLog.Write("Enregistrement vidéo", error);
        bool cancelled = _cancelled;
        DeleteTempFile(_tempPath);
        Finish(null);
        // A video the user cancelled is not missed.
        if (!cancelled) _app.Tray?.ShowError(Loc.T("Video.Failed"), error);
    }

    /// <summary>
    /// Back to idle: closes the recording interface, frees the recorder, brings back the hidden window,
    /// completes the pending <see cref="ToggleAsync"/> and then runs the <see cref="StopThen"/> actions.
    /// </summary>
    private void Finish(string? result)
    {
        _timer?.Stop();
        _timer = null;
        _armed?.TrySetResult(false);
        _armed = null;
        _bar?.Close();
        _bar = null;
        _frame?.Close();
        _frame = null;
        var recording = _recording;
        _recording = null;
        try
        {
            recording?.Dispose();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo", ex);
        }
        _tempPath = null;
        _webcamActive = false;
        _elapsed.Reset();
        _countdown?.Dispose();
        _countdown = null;
        _cancelled = false;
        _stopping = false;
        _paused = false;
        _completing = false;

        var completion = _completion;
        _completion = null;
        var after = _afterStop;
        _afterStop = null;

        _operation.RestoreWindow(activate: true);
        _operation.End();
        StateChanged?.Invoke();

        completion?.TrySetResult(result);
        // Run once the state is fully reset (e.g. quitting checks IsBusy again).
        if (after != null) Dispatcher.UIThread.Post(after);
    }
}
