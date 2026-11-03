using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using Snaplume.Platform.Linux.Wayland;
using Snaplume.Platform.Linux.X11;
using Snaplume.Recording;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Screen recording with GStreamer in process: ximagesrc under X11, pipewiresrc on a ScreenCast portal stream
/// under Wayland, PulseAudio/PipeWire sources for the sound, v4l2src and compositor for the webcam, H.264 (or HEVC) and
/// AAC in MP4.
/// </summary>
internal sealed class GstScreenRecorderFactory : IScreenRecorderFactory
{
    private readonly bool _wayland;

    public GstScreenRecorderFactory(bool wayland) => _wayland = wayland;

    public IReadOnlyList<WebcamInfo> Webcams() => Media.Webcams.List();

    public IReadOnlyList<AudioDeviceInfo> Microphones() => AudioInputs.List();

    /// <summary>H.264 always (its absence is reported when recording starts); HEVC with x265enc or a VA-API encoder.</summary>
    public bool CanRecord(VideoCodec codec)
    {
        if (codec == VideoCodec.H264) return true;
        try
        {
            GStreamerRuntime.EnsureInit();
            return HevcEncoder() != null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Encodeurs GStreamer", ex);
            return false;
        }
    }

    private static string? HevcEncoder() =>
        GStreamerRuntime.Has("h265parse") ? RecordingPipeline.Choose(RecordingPipeline.HevcEncoders, GStreamerRuntime.Has) : null;

    /// <remarks>
    /// Runs on the thread pool: GStreamer's initialisation, the plugin lookups and the start of the pipeline
    /// (seconds while the live sources open) block, and the caller is usually the UI thread.
    /// </remarks>
    public Task<IScreenRecording> StartAsync(RecordingOptions options, string outputPath, CancellationToken cancel = default) =>
        Task.Run(() => StartCoreAsync(options, outputPath, cancel));

    private async Task<IScreenRecording> StartCoreAsync(RecordingOptions options, string outputPath, CancellationToken cancel)
    {
        GStreamerRuntime.EnsureInit();
        var h264Encoder = RecordingPipeline.Choose(RecordingPipeline.VideoEncoders, GStreamerRuntime.Has)
                          ?? throw new InvalidOperationException(Loc.T("Linux.NoH264Encoder"));
        var encoding = options.Encoding ?? RecordingEncoding.Default;
        var hevcEncoder = encoding.Codec == VideoCodec.Hevc ? HevcEncoder() : null;
        if (encoding.Codec == VideoCodec.Hevc && hevcEncoder == null)
            ErrorLog.Write("Enregistrement en HEVC", "Aucun encodeur HEVC GStreamer (x265enc, vah265enc, vaapih265enc) : H.264");
        var videoEncoder = hevcEncoder ?? h264Encoder;
        bool wantsAudio = options.SystemAudio || options.Microphone;
        var audioEncoder = wantsAudio ? RecordingPipeline.Choose(RecordingPipeline.AudioEncoders, GStreamerRuntime.Has) : null;
        if (wantsAudio && audioEncoder == null) ErrorLog.Write("Enregistrement vidéo", "Aucun encodeur audio GStreamer : vidéo sans son");

        ScreenCastSession? cast = null;
        RecordingSource source;
        int width, height;
        if (_wayland)
        {
            cast = await ScreenCastSession.StartAsync(showCursor: true, cancel).ConfigureAwait(false);
            source = new PipeWireSource(cast.FileDescriptor, cast.Stream.Node, cast.Stream.Width, cast.Stream.Height);
            (width, height) = (cast.Stream.Width & ~1, cast.Stream.Height & ~1);
        }
        else
        {
            var area = RecordingPipeline.EvenArea(options.Area);
            source = new X11AreaSource(area, ShowPointer: true);
            (width, height) = (area.Width, area.Height);
        }

        bool webcamDropped = false;
        WebcamPlacement? webcam = null;
        if (options.Webcam is { } cam)
        {
            var device = Media.Webcams.Resolve(Media.Webcams.List(), cam.Device);
            if (device == null || width <= 0 || height <= 0 || !GStreamerRuntime.Has("v4l2src") || !GStreamerRuntime.Has("compositor"))
                webcamDropped = true;
            else
                webcam = RecordingPipeline.PlaceWebcam(device.Id, cam.Corner, cam.SizePercent, width, height);
        }

        // Zoom on clicks crops the X11 picture while recording (videocrop) and scales it back (videoscale).
        bool zoom = options.ClickZoom && !_wayland && width >= 4 && height >= 4;
        if (zoom && !(GStreamerRuntime.Has("videocrop") && GStreamerRuntime.Has("videoscale")))
        {
            ErrorLog.Write("Zoom sur les clics", "Éléments GStreamer videocrop/videoscale absents : vidéo sans zoom");
            zoom = false;
        }
        var microphoneDevice = options.Microphone ? options.MicrophoneDevice : "";
        if (microphoneDevice.Length > 0 && AudioInputs.List().All(m => m.Id != microphoneDevice))
        {
            ErrorLog.Write("Microphone", $"Microphone introuvable, celui par défaut est utilisé : {microphoneDevice}");
            microphoneDevice = "";
        }

        GstRecording Start(string encoder, WebcamPlacement? camera) =>
            GstRecording.Start(RecordingPipeline.Build(source, options.Framerate, encoder, audioEncoder,
                options.SystemAudio, options.Microphone, camera, width, height, zoom, encoding), outputPath, microphoneDevice);

        try
        {
            GstRecording recording;
            try
            {
                recording = Start(videoEncoder, webcam);
            }
            catch (InvalidOperationException ex) when (videoEncoder != h264Encoder)
            {
                // An HEVC encoder present but not working (VA-API without a driver...): H.264, converted afterwards.
                ErrorLog.Write("Enregistrement en HEVC", ex);
                videoEncoder = h264Encoder;
                try
                {
                    recording = Start(videoEncoder, webcam);
                }
                catch (InvalidOperationException webcamEx) when (webcam != null)
                {
                    ErrorLog.Write("Webcam pendant l'enregistrement", webcamEx);
                    webcamDropped = true;
                    recording = Start(videoEncoder, null);
                }
            }
            catch (InvalidOperationException ex) when (webcam != null)
            {
                // A busy or vanished camera fails the whole pipeline: record without it.
                ErrorLog.Write("Webcam pendant l'enregistrement", ex);
                webcamDropped = true;
                recording = Start(videoEncoder, null);
            }
            recording.WebcamDropped = webcamDropped;
            recording.Codec = videoEncoder == h264Encoder ? VideoCodec.H264 : VideoCodec.Hevc;
            var driver = zoom ? recording.CreateZoom(RecordingPipeline.EvenArea(options.Area)) : null;
            bool showClicks = options.ShowClicks && !_wayland;
            var clicks = showClicks || driver != null ? new X11ClickHighlighter(showClicks, driver != null ? driver.OnClick : null) : null;
            recording.Attach(cast, clicks, driver);
            return recording;
        }
        catch
        {
            cast?.Dispose();
            throw;
        }
    }
}

/// <summary>A running pipeline; its bus is read by a background thread that reports the end or the failure.</summary>
internal sealed class GstRecording : IScreenRecording
{
    private static readonly TimeSpan FinishTimeout = TimeSpan.FromSeconds(20);

    private readonly object _gate = new();
    private readonly ManualResetEventSlim _finished = new();
    private IntPtr _pipeline;
    private IntPtr _bus;
    private Thread? _busThread;
    private ScreenCastSession? _cast;
    private X11ClickHighlighter? _clicks;
    private ClickZoomDriver? _zoom;
    /// <summary>The videocrop element of the zoom on clicks (a reference of ours), or zero.</summary>
    private IntPtr _crop;
    private bool _stopping;
    private bool _paused;

    private GstRecording(IntPtr pipeline)
    {
        _pipeline = pipeline;
        _bus = Gst.gst_element_get_bus(pipeline);
    }

    public event Action? Completed;
    public event Action<string>? Failed;

    public bool WebcamDropped { get; set; }
    public VideoCodec Codec { get; set; } = VideoCodec.H264;
    public bool CanPause => true;

    /// <param name="microphoneDevice">Sound server name of the microphone, empty for the default one.</param>
    /// <exception cref="InvalidOperationException">The pipeline could not be built or started.</exception>
    public static GstRecording Start(string description, string outputPath, string microphoneDevice = "")
    {
        var pipeline = Gst.gst_parse_launch(description, out var error);
        if (pipeline == IntPtr.Zero) throw new InvalidOperationException("GStreamer: " + Gst.TakeError(error));
        if (error != IntPtr.Zero) ErrorLog.Write("Pipeline d'enregistrement", Gst.TakeError(error)); // recoverable (unknown property...)
        var recording = new GstRecording(pipeline);
        try
        {
            var sink = Gst.gst_bin_get_by_name(pipeline, "out");
            if (sink == IntPtr.Zero) throw new InvalidOperationException("GStreamer: no file sink");
            Gst.SetString(sink, "location", outputPath);
            Gst.gst_object_unref(sink);
            var mux = Gst.gst_bin_get_by_name(pipeline, RecordingPipeline.MuxElement);
            if (mux != IntPtr.Zero)
            {
                // mp4mux removes its spool when it stops.
                Gst.SetString(mux, "faststart-file", RecordingPipeline.FastStartFile(outputPath));
                Gst.gst_object_unref(mux);
            }
            if (microphoneDevice.Length > 0)
            {
                // Set as a property, like the file name: a name from the sound server is never parsed.
                var mic = Gst.gst_bin_get_by_name(pipeline, RecordingPipeline.MicrophoneElement);
                if (mic != IntPtr.Zero)
                {
                    Gst.SetString(mic, "device", microphoneDevice);
                    Gst.gst_object_unref(mic);
                }
            }
            recording._crop = Gst.gst_bin_get_by_name(pipeline, RecordingPipeline.ZoomElement);

            Gst.gst_element_set_state(pipeline, Gst.StatePlaying);
            // Live sources: the change completes once every source produced (or failed to open).
            if (Gst.gst_element_get_state(pipeline, out _, out _, 8_000_000_000UL) == Gst.StateChangeFailure)
                throw new InvalidOperationException("GStreamer: " + (GStreamerRuntime.PopError(recording._bus) ?? "the recording could not start"));
            if (GStreamerRuntime.PopError(recording._bus) is { } failure) throw new InvalidOperationException("GStreamer: " + failure);
        }
        catch
        {
            recording.Release();
            throw;
        }
        recording._busThread = new Thread(recording.WatchBus) { IsBackground = true, Name = "Snaplume recording bus" };
        recording._busThread.Start();
        return recording;
    }

    internal void Attach(ScreenCastSession? cast, X11ClickHighlighter? clicks, ClickZoomDriver? zoom)
    {
        lock (_gate)
        {
            _cast = cast;
            _clicks = clicks;
            _zoom = zoom;
        }
    }

    /// <summary>The zoom on clicks of this pipeline, or null when it was built without its videocrop.</summary>
    /// <param name="area">Recorded area, absolute X11 pixels (where the clicks are).</param>
    internal ClickZoomDriver? CreateZoom(PixelRect area) => _crop == IntPtr.Zero ? null : new ClickZoomDriver(area, view => ApplyZoom(area, view));

    /// <summary>Crops the picture to the view (relative to the area); videoscale brings it back to the size of the video.</summary>
    private bool ApplyZoom(PixelRect area, PixelRect view)
    {
        lock (_gate)
        {
            if (_pipeline == IntPtr.Zero || _crop == IntPtr.Zero || _stopping) return false;
            if (_paused) return false;
            Gst.SetInt(_crop, "left", view.X);
            Gst.SetInt(_crop, "top", view.Y);
            Gst.SetInt(_crop, "right", Math.Max(0, area.Width - view.Right));
            Gst.SetInt(_crop, "bottom", Math.Max(0, area.Height - view.Bottom));
            return true;
        }
    }

    private void WatchBus()
    {
        string? failure = null;
        var stopDeadline = long.MaxValue;
        while (true)
        {
            var message = Gst.gst_bus_timed_pop_filtered(_bus, 200_000_000UL, Gst.MessageEos | Gst.MessageError | Gst.MessageWarning);
            if (message == IntPtr.Zero)
            {
                lock (_gate)
                {
                    if (_stopping && stopDeadline == long.MaxValue) stopDeadline = Environment.TickCount64 + (long)FinishTimeout.TotalMilliseconds;
                }
                if (Environment.TickCount64 > stopDeadline)
                {
                    failure = "the end of the recording was not confirmed in time; the file may be incomplete";
                    break;
                }
                continue;
            }
            try
            {
                int type = GStreamerRuntime.MessageType(message);
                if (type == Gst.MessageEos) break;
                if (type == Gst.MessageWarning)
                {
                    ErrorLog.Write("Avertissement GStreamer (enregistrement)", GStreamerRuntime.DescribeMessage(message, warning: true));
                    continue;
                }
                failure = GStreamerRuntime.DescribeMessage(message);
                break;
            }
            finally
            {
                Gst.gst_mini_object_unref(message);
            }
        }
        Release();
        _finished.Set();
        try
        {
            if (failure == null) Completed?.Invoke();
            else Failed?.Invoke(failure);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Fin de l'enregistrement", ex);
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_pipeline == IntPtr.Zero || _stopping || _paused) return;
            Gst.gst_element_set_state(_pipeline, Gst.StatePaused);
            _paused = true;
            if (_zoom != null) _zoom.Paused = true;
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pipeline == IntPtr.Zero || _stopping || !_paused) return;
            Gst.gst_element_set_state(_pipeline, Gst.StatePlaying);
            _paused = false;
            if (_zoom != null) _zoom.Paused = false;
        }
    }

    /// <summary>
    /// Sends end-of-stream: every element finishes its work and mp4mux writes the index; the bus thread then
    /// sees EOS, closes the pipeline and raises <see cref="Completed"/>.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            if (_pipeline == IntPtr.Zero || _stopping) return;
            _stopping = true;
            // A paused live source would never let the end through.
            if (_paused) Gst.gst_element_set_state(_pipeline, Gst.StatePlaying);
            _paused = false;
            Gst.gst_element_send_event(_pipeline, Gst.gst_event_new_eos());
        }
    }

    private void Release()
    {
        X11ClickHighlighter? clicks;
        ClickZoomDriver? zoom;
        lock (_gate)
        {
            (clicks, _clicks) = (_clicks, null);
            (zoom, _zoom) = (_zoom, null);
        }
        // Outside the lock: the zoom waits for a view being applied, which takes the lock.
        clicks?.Dispose();
        zoom?.Dispose();
        lock (_gate)
        {
            if (_crop != IntPtr.Zero)
            {
                Gst.gst_object_unref(_crop);
                _crop = IntPtr.Zero;
            }
            if (_pipeline != IntPtr.Zero)
            {
                Gst.gst_element_set_state(_pipeline, Gst.StateNull);
                Gst.gst_object_unref(_pipeline);
                _pipeline = IntPtr.Zero;
            }
            if (_bus != IntPtr.Zero)
            {
                Gst.gst_object_unref(_bus);
                _bus = IntPtr.Zero;
            }
            _cast?.Dispose();
            _cast = null;
        }
    }

    public void Dispose()
    {
        if (_busThread == null)
        {
            Release();
            return;
        }
        Stop();
        _finished.Wait(FinishTimeout + TimeSpan.FromSeconds(2));
    }
}
