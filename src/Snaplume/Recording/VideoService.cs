using System.Diagnostics;
using System.Windows;
using System.Windows.Threading;
using Snaplume.Capturing;
using Snaplume.Core;
using ScreenRecorderLib;
using WinForms = System.Windows.Forms;

namespace Snaplume.Recording;

/// <summary>
/// Screen recording to MP4 (H.264 + AAC) with ScreenRecorderLib / Media Foundation.
/// One recording at a time; the file goes to the active workspace when finished.
/// </summary>
/// <remarks>
/// States: idle → starting (area selection, countdown) → recording → finishing (file written and
/// moved) → idle. <see cref="Finish"/> is the only way back to idle and resets everything.
/// </remarks>
public sealed class VideoService
{
    private const int CountdownSeconds = 3;
    private const string TempPrefix = "Snaplume_";
    /// <summary>
    /// A failure this soon after the start, with the webcam, is taken for a camera that could not
    /// be opened (busy, unplugged): the recording starts again without it.
    /// </summary>
    private static readonly TimeSpan WebcamStartupWindow = TimeSpan.FromSeconds(4);

    private readonly App _app;
    private readonly ScreenOperation _operation = new();
    private readonly Stopwatch _elapsed = new();
    private Recorder? _recorder;
    private RecordingControlBar? _bar;
    private RecordingFrame? _frame;
    private DispatcherTimer? _timer;
    private TaskCompletionSource<string?>? _completion;
    private string? _tempPath;
    private PixelRect _area;
    /// <summary>The current recorder inlays the webcam.</summary>
    private bool _webcamActive;
    private Action? _afterStop;
    private bool _cancelled;
    private bool _stopping;

    public VideoService(App app) => _app = app;

    /// <summary>From the start of the area selection until the video is saved (or abandoned).</summary>
    public bool IsBusy => _operation.IsBusy;

    /// <summary>
    /// Raised on the UI thread at every change: start, recording begun, paused or resumed,
    /// finishing, back to idle (to update menus and buttons).
    /// </summary>
    public event Action? StateChanged;

    private AppSettings Settings => _app.Settings;

    /// <summary>Starts a recording, or stops the current one.</summary>
    /// <param name="fullScreen">Record the monitor under the cursor instead of selecting an area.</param>
    /// <param name="hide">Window hidden during the recording (e.g. the quick panel).</param>
    /// <param name="durationSeconds">Stops automatically after this time (command line).</param>
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
        StateChanged?.Invoke();
        try
        {
            await _operation.SettleAsync(settleMs);

            PixelRect? selected = null;
            ScreenSnapshot? snap = null;
            if (!_cancelled)
            {
                snap = ScreenSnapshot.Take(includeCursor: false);
                selected = fullScreen ? snap.MonitorUnderCursor() : RegionSelectorWindow.Pick(snap, Loc.T("Video.SelectTitle"));
            }
            if (_cancelled || snap == null || selected is not PixelRect relative || relative.Width < 16 || relative.Height < 16)
            {
                Finish(null);
                return await completion.Task;
            }

            // The area is chosen: screenshots may be taken during the countdown and the recording.
            _operation.EndInteractive();

            // H.264 needs even dimensions.
            var area = relative.Offset(snap.Bounds.X, snap.Bounds.Y);
            area = area with { Width = area.Width & ~1, Height = area.Height & ~1 };

            ShowControls(area, showFrame: !fullScreen);
            for (int remaining = CountdownSeconds; remaining > 0 && !_cancelled; remaining--)
            {
                _bar?.ShowCountdown(remaining);
                await Task.Delay(1000);
            }
            if (_cancelled)
            {
                Finish(null);
                return await completion.Task;
            }

            Begin(area);
            StateChanged?.Invoke();

            if (durationSeconds > 0)
            {
                await Task.WhenAny(completion.Task, Task.Delay(TimeSpan.FromSeconds(durationSeconds)));
                if (!completion.Task.IsCompleted) Stop();
            }
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

    /// <summary>Stops and saves the recording (or aborts the selection / countdown).</summary>
    public void Stop()
    {
        if (!IsBusy) return;
        if (_recorder == null)
        {
            _cancelled = true;
            RegionSelectorWindow.CancelCurrent();
            return;
        }
        if (_stopping) return;
        _stopping = true;
        _bar?.SetFinishing();
        _recorder.Stop();
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
            _app.Dispatcher.BeginInvoke(action);
            return;
        }
        _afterStop += action;
        Stop();
    }

    public void TogglePause()
    {
        if (_recorder == null || _stopping) return;
        if (_recorder.Status == RecorderStatus.Recording) _recorder.Pause();
        else if (_recorder.Status == RecorderStatus.Paused) _recorder.Resume();
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

    private void ShowControls(PixelRect area, bool showFrame)
    {
        if (showFrame)
        {
            _frame = new RecordingFrame(area);
            _frame.Show();
        }
        _bar = new RecordingControlBar(area);
        _bar.PauseClicked += TogglePause;
        _bar.StopClicked += Stop;
        _bar.CancelClicked += Cancel;
        _bar.Show();
    }

    private void Begin(PixelRect area)
    {
        _area = area;
        try
        {
            StartRecorder(area, Settings.VideoWebcam);
        }
        catch (Exception ex) when (_webcamActive)
        {
            // The camera could not be opened: record without it.
            ErrorLog.Write("Webcam", ex);
            var failed = _recorder;
            _recorder = null;
            failed?.Dispose();
            DeleteTempFile(_tempPath);
            StartRecorder(area, webcam: false);
            _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamBusy"));
        }

        _elapsed.Restart();
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => _bar?.SetRecording(_elapsed.Elapsed);
        _timer.Start();
        _bar?.SetRecording(TimeSpan.Zero);
    }

    /// <summary>Creates the recorder and starts writing to a new temporary file.</summary>
    private void StartRecorder(PixelRect area, bool webcam)
    {
        _tempPath = Path.Combine(Path.GetTempPath(), $"{TempPrefix}{Guid.NewGuid():N}.mp4");
        var options = BuildOptions(area);
        _webcamActive = webcam && AddWebcamOverlay(options, area);
        var recorder = Recorder.CreateRecorder(options);
        var dispatcher = _app.Dispatcher;
        // Events of a recorder already finished (or replaced) are ignored.
        recorder.OnRecordingComplete += (_, _) => dispatcher.BeginInvoke(() =>
        {
            if (_recorder == recorder) _ = OnCompleteAsync();
        });
        recorder.OnRecordingFailed += (_, e) => dispatcher.BeginInvoke(() =>
        {
            if (_recorder == recorder) OnFailed(e.Error);
        });
        recorder.OnStatusChanged += (_, e) => dispatcher.BeginInvoke(() =>
        {
            if (_recorder == recorder) OnStatusChanged(e.Status);
        });
        _recorder = recorder;
        recorder.Record(_tempPath);
    }

    /// <summary>
    /// Adds the webcam picture-in-picture to the options. False (with a warning, the recording
    /// goes on without it) when the chosen camera is not connected.
    /// </summary>
    private bool AddWebcamOverlay(RecorderOptions options, PixelRect area)
    {
        RecordableCamera? camera = null;
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            var wanted = Settings.VideoWebcamDevice;
            camera = string.IsNullOrEmpty(wanted)
                ? cameras.FirstOrDefault()
                : cameras.FirstOrDefault(c => string.Equals(c.DeviceName, wanted, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
        }
        if (camera == null)
        {
            _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamNotFound"));
            return false;
        }

        // Size relative to the recorded area (the output frame), height from the camera's ratio
        // (the percentage is kept between 10 and 40 by AppSettings.Sanitize).
        int width = Math.Max(64, area.Width * Settings.VideoWebcamSize / 100);
        int margin = Math.Max(8, Math.Min(area.Width, area.Height) / 40);
        var anchor = Settings.VideoWebcamCorner switch
        {
            "TopLeft" => Anchor.TopLeft,
            "TopRight" => Anchor.TopRight,
            "BottomLeft" => Anchor.BottomLeft,
            _ => Anchor.BottomRight,
        };
        options.OverlayOptions = new OverLayOptions
        {
            Overlays = new List<RecordingOverlayBase>
            {
                new VideoCaptureOverlay(camera.DeviceName)
                {
                    AnchorPoint = anchor,
                    Offset = new ScreenSize(margin, margin), // from the anchored corner, inwards
                    Size = new ScreenSize(width, 0), // 0: height follows the camera's aspect ratio
                    Stretch = StretchMode.Uniform,
                },
            },
        };
        return true;
    }

    /// <summary>
    /// The recording failed right after starting with the webcam (camera busy in another
    /// application, or unplugged): starts again without it rather than losing the recording.
    /// </summary>
    private bool TryRestartWithoutWebcam(string error)
    {
        if (!_webcamActive || _stopping || _cancelled || _elapsed.Elapsed > WebcamStartupWindow) return false;
        ErrorLog.Write("Webcam", error);
        var failed = _recorder;
        _recorder = null;
        failed?.Dispose();
        DeleteTempFile(_tempPath);
        try
        {
            StartRecorder(_area, webcam: false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement vidéo sans webcam", ex);
            return false;
        }
        _elapsed.Restart();
        _bar?.SetRecording(TimeSpan.Zero);
        _app.Tray?.ShowWarning(Loc.T("Video.WebcamUnavailable"), Loc.T("Video.WebcamBusy"));
        return true;
    }

    private RecorderOptions BuildOptions(PixelRect area)
    {
        // Record only the monitors covered by the area; the library lays them out like the desktop.
        var displays = Recorder.GetDisplays();
        var sources = new List<RecordingSourceBase>();
        PixelRect? covered = null;
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var bounds = PixelRect.From(screen.Bounds);
            if (bounds.Intersect(area).IsEmpty) continue;
            var display = displays.FirstOrDefault(d => string.Equals(d.DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase));
            if (display == null) continue;
            sources.Add(new DisplayRecordingSource(display.DeviceName));
            covered = covered is PixelRect c ? c.Union(bounds) : bounds;
        }
        if (covered is not PixelRect origin)
        {
            origin = PixelRect.From(WinForms.Screen.PrimaryScreen!.Bounds);
            sources.Add(DisplayRecordingSource.MainMonitor);
        }
        var crop = area.Intersect(origin).Offset(-origin.X, -origin.Y);

        var audioSources = new List<AudioSourceBase>();
        if (Settings.VideoSystemAudio && LoopbackAudioSource.Default is { } speakers) audioSources.Add(speakers);
        if (Settings.VideoMicrophone && CaptureAudioSource.Default is { } microphone) audioSources.Add(microphone);

        var options = new RecorderOptions
        {
            SourceOptions = new SourceOptions(),
            OutputOptions = new OutputOptions
            {
                RecorderMode = RecorderMode.Video,
                SourceRect = new ScreenRect(crop.X, crop.Y, crop.Width, crop.Height),
            },
            AudioOptions = new AudioOptions
            {
                IsAudioEnabled = audioSources.Count > 0,
                AudioSources = audioSources,
            },
            MouseOptions = new MouseOptions
            {
                IsMousePointerEnabled = true,
                IsMouseClicksDetected = Settings.VideoShowClicks,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High },
                Quality = 70,
                Framerate = Math.Clamp(Settings.VideoFramerate, 5, 60),
                IsFixedFramerate = true, // constant frame rate: plays and edits everywhere
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,
            },
        };
        options.SourceOptions.RecordingSources.AddRange(sources);
        return options;
    }

    private void OnStatusChanged(RecorderStatus status)
    {
        switch (status)
        {
            case RecorderStatus.Paused:
                _elapsed.Stop();
                _bar?.SetPaused(true);
                StateChanged?.Invoke();
                break;
            case RecorderStatus.Recording:
                _elapsed.Start();
                _bar?.SetPaused(false);
                StateChanged?.Invoke();
                break;
        }
        _bar?.SetRecording(_elapsed.Elapsed);
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

    private void OnFailed(string error)
    {
        if (TryRestartWithoutWebcam(error)) return;
        ErrorLog.Write("Enregistrement vidéo", error);
        DeleteTempFile(_tempPath);
        Finish(null);
        _app.Tray?.ShowError(Loc.T("Video.Failed"), error);
    }

    /// <summary>
    /// Back to idle: closes the recording UI, frees the recorder, brings back the hidden window,
    /// completes the pending <see cref="ToggleAsync"/> and then runs the <see cref="StopThen"/> actions.
    /// </summary>
    private void Finish(string? result)
    {
        _timer?.Stop();
        _timer = null;
        _bar?.Close();
        _bar = null;
        _frame?.Close();
        _frame = null;
        var recorder = _recorder;
        _recorder = null;
        recorder?.Dispose();
        _tempPath = null;
        _webcamActive = false;
        _elapsed.Reset();
        _cancelled = false;
        _stopping = false;

        var completion = _completion;
        _completion = null;
        var after = _afterStop;
        _afterStop = null;

        _operation.RestoreWindow(activate: true);
        _operation.End();
        StateChanged?.Invoke();

        completion?.TrySetResult(result);
        // Run once the state is fully reset (e.g. quitting checks IsBusy again).
        if (after != null) _app.Dispatcher.BeginInvoke(after);
    }
}
