using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Threading;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>
/// Screen recording to MP4 through the platform's recorder (ScreenRecorderLib under Windows, GStreamer under Linux).
/// One recording at a time; the file goes to the active workspace when finished.
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

    public VideoService(App app)
    {
        _app = app;
        // Options changed in the settings window while the video options are shown.
        app.SettingsApplied += () => _bar?.RefreshOptions();
    }

    /// <summary>True from the start of the area selection until the video is saved (or abandoned).</summary>
    public bool IsBusy => _operation.IsBusy;

    /// <summary>True while the recorder runs (paused or not), until it is stopped.</summary>
    public bool IsRecording => _recording != null && !_stopping;

    public bool IsPaused => _paused && IsRecording;

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
    /// <returns>Path of the saved video, or null if cancelled or failed.</returns>
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
                else
                {
                    var desktop = await platform.Screen.CaptureDesktopAsync(includeCursor: false);
                    if (!_cancelled && await RegionSelectorWindow.PickAsync(desktop, Loc.T("Video.SelectTitle")) is PixelRect relative)
                        selected = CaptureGeometry.ToScreen(relative, desktop.Bounds);
                }
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
            }
            if (!systemChooses)
            {
                // With the portal, its dialog comes first: a countdown before it would mean nothing.
                for (int remaining = CountdownSeconds; remaining > 0 && !_cancelled; remaining--)
                {
                    _bar?.ShowCountdown(remaining);
                    await Task.Delay(1000);
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
            // Refused or cancelled in the system's permission dialog (Wayland portal): nothing to report.
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
            _starting?.Cancel();
            RegionSelectorWindow.CancelCurrent();
            return;
        }
        if (_stopping) return;
        _stopping = true;
        _bar?.SetFinishing();
        try
        {
            _recording.Stop();
        }
        catch (Exception ex)
        {
            // The recorder is gone: what it wrote is looked for as if it had completed.
            ErrorLog.Write("Arrêt de l'enregistrement vidéo", ex);
            _ = OnCompleteAsync();
        }
        StateChanged?.Invoke();
    }

    /// <summary>Stops without keeping the video.</summary>
    public void Cancel()
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

    public void TogglePause()
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

    /// <summary>Removes the temporary videos left by a crash (older than a day, so never one being recorded).</summary>
    public static void DeleteStaleTempFiles()
    {
        try
        {
            var limit = DateTime.UtcNow.AddDays(-1);
            foreach (var file in new DirectoryInfo(Path.GetTempPath()).EnumerateFiles(TempPrefix + "*.mp4"))
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

    /// <summary>Monitor under the mouse, absolute physical coordinates (the primary one if the mouse is unknown).</summary>
    private static PixelRect MonitorUnderCursor(IPlatform platform)
    {
        var monitors = platform.Screen.Monitors;
        if (monitors.Count == 0) throw new InvalidOperationException("No monitor");
        if (platform.Pointer.TryGetPosition(out int x, out int y))
            return CaptureGeometry.MonitorAt(monitors.Select(m => m.Bounds).ToList(), x, y);
        return (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).Bounds;
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

    /// <summary>Asks the platform to record into a new temporary file.</summary>
    private async Task StartRecorderAsync(PixelRect area, bool webcam)
    {
        var platform = Platform;
        _tempPath = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.mp4");
        var camera = webcam ? FindWebcam(platform) : null;
        _webcamActive = camera != null;
        var options = new RecordingOptions(
            area,
            Settings.VideoSystemAudio,
            Settings.VideoMicrophone,
            Settings.VideoShowClicks && platform.Has(PlatformFeatures.ClickHighlight),
            Math.Clamp(Settings.VideoFramerate, 5, 60),
            camera,
            Settings.VideoMicrophoneDevice,
            Settings.VideoClickZoom && platform.Has(PlatformFeatures.ClickZoom));

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
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo sans webcam", ex);
            return false;
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
        var owner = _completion;
        var temp = _tempPath;
        bool keep = !_cancelled;
        _stopping = true;
        _bar?.SetFinishing();
        string? saved = null;
        Exception? error = null;
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
            _app.Captures.NotifySaved(saved);
            if (!restoring && Settings.ShowPanelAfterCapture) _app.ShowQuickPanel();
            var path = saved;
            _app.Tray?.ShowInfo(Loc.T("Video.Saved"), Loc.T("Video.SavedDetails", Path.GetFileName(path)), () => _app.OpenPlayer(path));
        }
    }

    /// <summary>
    /// The video could not reach the workspace: give it a name that the cleanup of temporary
    /// files at startup leaves alone.
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
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo", ex);
        }
        ErrorLog.Write("Enregistrement vidéo", error);
        DeleteTempFile(_tempPath);
        Finish(null);
        _app.Tray?.ShowError(Loc.T("Video.Failed"), error);
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
        _cancelled = false;
        _stopping = false;
        _paused = false;

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
