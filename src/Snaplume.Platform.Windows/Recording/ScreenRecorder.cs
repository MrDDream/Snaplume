using System.Diagnostics;
using ScreenRecorderLib;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform.Windows.Capturing;

namespace Snaplume.Platform.Windows.Recording;

/// <summary>Screen recording to MP4 (H.264 + AAC) with ScreenRecorderLib / Media Foundation.</summary>
public sealed class ScreenRecorderFactory : IScreenRecorderFactory
{
    public IReadOnlyList<WebcamInfo> Webcams()
    {
        try
        {
            return Recorder.GetSystemVideoCaptureDevices()
                .Select(c => new WebcamInfo(c.DeviceName, string.IsNullOrWhiteSpace(c.FriendlyName) ? c.DeviceName : c.FriendlyName))
                .ToList();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
            return Array.Empty<WebcamInfo>();
        }
    }

    public Task<IScreenRecording> StartAsync(RecordingOptions options, string outputPath, CancellationToken cancel = default)
    {
        cancel.ThrowIfCancellationRequested();
        var recording = new ScreenRecording(options, outputPath);
        try
        {
            recording.Start();
        }
        catch
        {
            recording.Dispose();
            throw;
        }
        return Task.FromResult<IScreenRecording>(recording);
    }
}

/// <summary>
/// One recording. When the webcam cannot be opened (busy in another application, unplugged), the recording starts
/// again without it rather than being lost, and <see cref="WebcamDropped"/> becomes true.
/// </summary>
internal sealed class ScreenRecording : IScreenRecording
{
    /// <summary>
    /// A failure this soon after the start, with the webcam, is taken for a camera that could not
    /// be opened (busy, unplugged): the recording starts again without it.
    /// </summary>
    private static readonly TimeSpan WebcamStartupWindow = TimeSpan.FromSeconds(4);

    private readonly object _lock = new();
    private readonly RecordingOptions _options;
    private readonly string _outputPath;
    private readonly Stopwatch _sinceStart = new();
    private Recorder? _recorder;
    /// <summary>The current recorder inlays the webcam.</summary>
    private bool _webcamActive;
    private bool _stopping;
    private bool _finished;
    private bool _disposed;

    public ScreenRecording(RecordingOptions options, string outputPath)
    {
        _options = options;
        _outputPath = outputPath;
    }

    public event Action? Completed;
    public event Action<string>? Failed;

    public bool WebcamDropped { get; private set; }
    public bool CanPause => true;

    public void Start()
    {
        lock (_lock)
        {
            try
            {
                StartRecorder(_options.Webcam);
            }
            catch (Exception ex) when (_webcamActive)
            {
                // The camera could not be opened: record without it.
                ErrorLog.Write("Webcam", ex);
                DropRecorder();
                WebcamDropped = true;
                StartRecorder(null);
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            if (_recorder?.Status == RecorderStatus.Recording) _recorder.Pause();
        }
    }

    public void Resume()
    {
        lock (_lock)
        {
            if (_recorder?.Status == RecorderStatus.Paused) _recorder.Resume();
        }
    }

    public void Stop()
    {
        lock (_lock)
        {
            if (_stopping || _finished) return;
            _stopping = true;
            // Null while the webcam is being dropped: the new recorder is stopped as soon as it starts.
            _recorder?.Stop();
        }
    }

    public void Dispose()
    {
        Recorder? recorder;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            recorder = _recorder;
            _recorder = null;
        }
        // Outside the lock: disposing may wait for the library's thread, which may be waiting for the lock in a callback.
        recorder?.Dispose();
    }

    /// <summary>Creates the recorder and starts writing the file. Called under the lock.</summary>
    private void StartRecorder(WebcamOptions? webcam)
    {
        var options = BuildOptions(_options);
        _webcamActive = webcam != null && AddWebcamOverlay(options, webcam);
        var recorder = Recorder.CreateRecorder(options);
        // Events of a recorder already replaced (webcam dropped) are ignored.
        recorder.OnRecordingComplete += (_, _) => OnComplete(recorder);
        recorder.OnRecordingFailed += (_, e) => OnFailed(recorder, e.Error);
        _recorder = recorder;
        _sinceStart.Restart();
        recorder.Record(_outputPath);
    }

    private void OnComplete(Recorder recorder)
    {
        lock (_lock)
        {
            if (_recorder != recorder || _finished) return;
            _finished = true;
        }
        Completed?.Invoke();
    }

    private void OnFailed(Recorder recorder, string error)
    {
        lock (_lock)
        {
            if (_recorder != recorder || _finished) return;
            if (_webcamActive && !_stopping && !_disposed && _sinceStart.Elapsed <= WebcamStartupWindow)
            {
                // Not from the library's own callback thread: disposing the recorder there could wait for itself.
                _ = Task.Run(() => RestartWithoutWebcam(recorder, error));
                return;
            }
            _finished = true;
        }
        Failed?.Invoke(error);
    }

    /// <summary>
    /// The recording failed right after starting with the webcam (camera busy in another
    /// application, or unplugged): starts again without it rather than losing the recording.
    /// </summary>
    private void RestartWithoutWebcam(Recorder failed, string error)
    {
        lock (_lock)
        {
            if (_recorder != failed || _finished || _disposed) return;
            _recorder = null;
            _webcamActive = false;
            WebcamDropped = true;
        }
        ErrorLog.Write("Webcam", error);
        // Outside the lock, like in Dispose.
        failed.Dispose();
        DeleteOutput();

        string? failure = null;
        lock (_lock)
        {
            if (_finished || _disposed) return;
            try
            {
                StartRecorder(null);
                // Stop() was called while the webcam was being dropped.
                if (_stopping) _recorder?.Stop();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Enregistrement vidéo sans webcam", ex);
                _finished = true;
                failure = ex.Message;
            }
        }
        if (failure != null) Failed?.Invoke(failure);
    }

    /// <summary>Frees the current recorder and deletes what it wrote (at the start, before any callback can run).</summary>
    private void DropRecorder()
    {
        var recorder = _recorder;
        _recorder = null;
        _webcamActive = false;
        recorder?.Dispose();
        DeleteOutput();
    }

    private void DeleteOutput()
    {
        try
        {
            File.Delete(_outputPath);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression de la vidéo temporaire", ex);
        }
    }

    /// <summary>
    /// Adds the webcam picture-in-picture to the options. False when the chosen camera is not connected (the
    /// recording goes on without it).
    /// </summary>
    private bool AddWebcamOverlay(RecorderOptions options, WebcamOptions webcam)
    {
        RecordableCamera? camera = null;
        try
        {
            var cameras = Recorder.GetSystemVideoCaptureDevices();
            camera = string.IsNullOrEmpty(webcam.Device)
                ? cameras.FirstOrDefault()
                : cameras.FirstOrDefault(c => string.Equals(c.DeviceName, webcam.Device, StringComparison.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des webcams", ex);
        }
        if (camera == null)
        {
            ErrorLog.Write("Webcam", $"Caméra introuvable : {webcam.Device}");
            WebcamDropped = true;
            return false;
        }

        var area = _options.Area;
        // Size relative to the recorded area (the output frame), height from the camera's ratio.
        int width = Math.Max(64, area.Width * Math.Clamp(webcam.SizePercent, 5, 100) / 100);
        int margin = Math.Max(8, Math.Min(area.Width, area.Height) / 40);
        var anchor = webcam.Corner switch
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

    private static RecorderOptions BuildOptions(RecordingOptions settings)
    {
        // H.264 needs even dimensions.
        var area = settings.Area with { Width = Math.Max(2, settings.Area.Width & ~1), Height = Math.Max(2, settings.Area.Height & ~1) };

        // Record only the monitors covered by the area; the library lays them out like the desktop.
        var displays = Recorder.GetDisplays();
        var sources = new List<RecordingSourceBase>();
        PixelRect? covered = null;
        foreach (var monitor in ScreenCapture.EnumerateMonitors())
        {
            if (monitor.Bounds.Intersect(area).IsEmpty) continue;
            var display = displays.FirstOrDefault(d => string.Equals(d.DeviceName, monitor.Name, StringComparison.OrdinalIgnoreCase));
            if (display == null) continue;
            sources.Add(new DisplayRecordingSource(display.DeviceName));
            covered = covered is PixelRect c ? c.Union(monitor.Bounds) : monitor.Bounds;
        }
        if (covered is not PixelRect origin)
        {
            var monitors = ScreenCapture.EnumerateMonitors();
            origin = (monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0]).Bounds;
            sources.Add(DisplayRecordingSource.MainMonitor);
        }
        var crop = area.Intersect(origin).Offset(-origin.X, -origin.Y);
        crop = crop with { Width = crop.Width & ~1, Height = crop.Height & ~1 };

        var audioSources = new List<AudioSourceBase>();
        if (settings.SystemAudio && LoopbackAudioSource.Default is { } speakers) audioSources.Add(speakers);
        if (settings.Microphone && CaptureAudioSource.Default is { } microphone) audioSources.Add(microphone);

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
                IsMouseClicksDetected = settings.ShowClicks,
            },
            VideoEncoderOptions = new VideoEncoderOptions
            {
                Encoder = new H264VideoEncoder { BitrateMode = H264BitrateControlMode.Quality, EncoderProfile = H264Profile.High },
                Quality = 70,
                Framerate = Math.Clamp(settings.Framerate, 5, 60),
                IsFixedFramerate = true, // constant frame rate: plays and edits everywhere
                IsHardwareEncodingEnabled = true,
                IsMp4FastStartEnabled = true,
            },
        };
        options.SourceOptions.RecordingSources.AddRange(sources);
        return options;
    }
}
