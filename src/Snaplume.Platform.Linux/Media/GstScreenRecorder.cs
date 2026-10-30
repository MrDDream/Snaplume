using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using Snaplume.Platform.Linux.Wayland;
using Snaplume.Platform.Linux.X11;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Screen recording with GStreamer in process: ximagesrc under X11, pipewiresrc on a ScreenCast portal stream
/// under Wayland, PulseAudio/PipeWire sources for the sound, v4l2src and compositor for the webcam, H.264 and
/// AAC in MP4.
/// </summary>
internal sealed class GstScreenRecorderFactory : IScreenRecorderFactory
{
    private readonly bool _wayland;

    public GstScreenRecorderFactory(bool wayland) => _wayland = wayland;

    public IReadOnlyList<WebcamInfo> Webcams() => Media.Webcams.List();

    public async Task<IScreenRecording> StartAsync(RecordingOptions options, string outputPath, CancellationToken cancel = default)
    {
        GStreamerRuntime.EnsureInit();
        var videoEncoder = RecordingPipeline.Choose(RecordingPipeline.VideoEncoders, GStreamerRuntime.Has)
                           ?? throw new InvalidOperationException(Loc.T("Linux.NoH264Encoder"));
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

        try
        {
            GstRecording recording;
            try
            {
                recording = GstRecording.Start(RecordingPipeline.Build(source, options.Framerate, videoEncoder, audioEncoder,
                    options.SystemAudio, options.Microphone, webcam, width, height), outputPath);
            }
            catch (InvalidOperationException ex) when (webcam != null)
            {
                // A busy or vanished camera fails the whole pipeline: record without it.
                ErrorLog.Write("Webcam pendant l'enregistrement", ex);
                webcamDropped = true;
                recording = GstRecording.Start(RecordingPipeline.Build(source, options.Framerate, videoEncoder, audioEncoder,
                    options.SystemAudio, options.Microphone, null, width, height), outputPath);
            }
            recording.WebcamDropped = webcamDropped;
            recording.Attach(cast, options.ShowClicks && !_wayland ? new X11ClickHighlighter() : null);
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
    public bool CanPause => true;

    /// <exception cref="InvalidOperationException">The pipeline could not be built or started.</exception>
    public static GstRecording Start(string description, string outputPath)
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

            Gst.gst_element_set_state(pipeline, Gst.StatePlaying);
            // Live sources: the change completes once every source produced (or failed to open).
            if (Gst.gst_element_get_state(pipeline, out _, out _, 8_000_000_000UL) == Gst.StateChangeFailure)
                throw new InvalidOperationException("GStreamer: " + (recording.PopError() ?? "the recording could not start"));
            if (recording.PopError() is { } failure) throw new InvalidOperationException("GStreamer: " + failure);
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

    internal void Attach(ScreenCastSession? cast, X11ClickHighlighter? clicks)
    {
        _cast = cast;
        _clicks = clicks;
    }

    private string? PopError()
    {
        var message = Gst.gst_bus_timed_pop_filtered(_bus, 0, Gst.MessageError);
        if (message == IntPtr.Zero) return null;
        try
        {
            return GStreamerRuntime.DescribeMessage(message);
        }
        finally
        {
            Gst.gst_mini_object_unref(message);
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
        }
    }

    public void Resume()
    {
        lock (_gate)
        {
            if (_pipeline == IntPtr.Zero || _stopping || !_paused) return;
            Gst.gst_element_set_state(_pipeline, Gst.StatePlaying);
            _paused = false;
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
        lock (_gate)
        {
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
            _clicks?.Dispose();
            _clicks = null;
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
