using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Interop;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Video player on GStreamer's playbin: the pictures go to an appsink as BGRA and are handed to the window by
/// <see cref="FrameReady"/>, the sound to the default output. A worker thread pulls the pictures and reads the
/// bus; playbin itself is thread-safe, so the commands come straight from the UI thread.
/// </summary>
internal sealed unsafe class GstVideoPlayer : IVideoPlayer
{
    private const ulong Second = 1_000_000_000UL;

    private readonly object _gate = new();
    private IntPtr _playbin;
    private IntPtr _sink;
    private IntPtr _bus;
    private Thread? _worker;
    private volatile bool _disposed;
    private volatile bool _playing;
    private PixelBuffer? _frame;
    private double _volume = 1;

    public event Action<PixelBuffer>? FrameReady;
    public event Action? StateChanged;

    public TimeSpan Duration { get; private set; }
    public bool IsPlaying => _playing;
    public bool HasEnded { get; private set; }
    public string? Error { get; private set; }

    public TimeSpan Position
    {
        get
        {
            lock (_gate)
            {
                if (_playbin == IntPtr.Zero) return TimeSpan.Zero;
                return Gst.gst_element_query_position(_playbin, Gst.FormatTime, out long ns) != 0 && ns >= 0
                    ? TimeSpan.FromTicks(ns / 100)
                    : TimeSpan.Zero;
            }
        }
    }

    public double Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0, 1);
            lock (_gate)
                if (_playbin != IntPtr.Zero) Gst.SetDouble(_playbin, "volume", _volume);
        }
    }

    public Task OpenAsync(string path) => Task.Run(() =>
    {
        try
        {
            GStreamerRuntime.EnsureInit();
            lock (_gate)
            {
                if (_disposed) return;
                _playbin = Gst.gst_element_factory_make("playbin", IntPtr.Zero);
                if (_playbin == IntPtr.Zero) throw new InvalidOperationException(Loc.T("Linux.GStreamerMissing"));
                var bin = Gst.gst_parse_bin_from_description(
                    "videoconvert ! video/x-raw,format=BGRA ! appsink name=frames sync=true max-buffers=2 drop=true", 1, out var error);
                if (bin == IntPtr.Zero) throw new InvalidOperationException("GStreamer: " + Gst.TakeError(error));
                _sink = Gst.gst_bin_get_by_name(bin, "frames");
                Gst.SetElement(_playbin, "video-sink", bin); // playbin takes the floating reference
                Gst.SetString(_playbin, "uri", FileUris.FromPath(Path.GetFullPath(path)));
                Gst.SetDouble(_playbin, "volume", _volume);
                _bus = Gst.gst_element_get_bus(_playbin);
                Gst.gst_element_set_state(_playbin, Gst.StatePaused);
            }
            // Pausing prerolls: the file is opened, decoded up to the first picture, and its duration known.
            var result = Gst.gst_element_get_state(_playbin, out _, out _, 15 * Second);
            if (result == Gst.StateChangeFailure)
                throw new InvalidOperationException("GStreamer: " + (PopError() ?? "the file could not be opened"));
            ReadDuration();
            _worker = new Thread(Work) { IsBackground = true, Name = "Snaplume player" };
            _worker.Start();
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            // The technical details ("GStreamer: ...") go to the log; the window shows a sentence.
            Error = ex is InvalidOperationException && !ex.Message.StartsWith("GStreamer:", StringComparison.Ordinal)
                ? ex.Message
                : Loc.T("Linux.PlayerError");
            ErrorLog.Write($"Ouverture de la vidéo {path}", ex);
        }
        Raise();
    });

    public void Play()
    {
        lock (_gate)
        {
            if (_playbin == IntPtr.Zero) return;
            if (HasEnded)
            {
                Gst.gst_element_seek_simple(_playbin, Gst.FormatTime, Gst.SeekFlagFlush | Gst.SeekFlagKeyUnit, 0);
                HasEnded = false;
            }
            Gst.gst_element_set_state(_playbin, Gst.StatePlaying);
            _playing = true;
        }
        Raise();
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_playbin == IntPtr.Zero) return;
            Gst.gst_element_set_state(_playbin, Gst.StatePaused);
            _playing = false;
        }
        Raise();
    }

    /// <summary>Accurate seek: frame stepping and the in/out points need the exact picture, not the nearest key frame.</summary>
    public void Seek(TimeSpan position)
    {
        lock (_gate)
        {
            if (_playbin == IntPtr.Zero) return;
            long ns = Math.Max(0, position.Ticks) * 100;
            Gst.gst_element_seek_simple(_playbin, Gst.FormatTime, Gst.SeekFlagFlush | Gst.SeekFlagAccurate, ns);
            HasEnded = false;
        }
    }

    private void Work()
    {
        while (!_disposed)
        {
            ReadBus();
            IntPtr sample;
            lock (_gate)
            {
                if (_sink == IntPtr.Zero) return;
            }
            // Playing: the next picture at its time. Paused: the picture after a seek (preroll).
            sample = _playing
                ? Gst.gst_app_sink_try_pull_sample(_sink, 50_000_000UL)
                : Gst.gst_app_sink_try_pull_preroll(_sink, 50_000_000UL);
            if (sample == IntPtr.Zero) continue;
            try
            {
                if (ToFrame(sample) is { } frame) FrameReady?.Invoke(frame);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Image de la vidéo", ex);
            }
            finally
            {
                Gst.gst_mini_object_unref(sample);
            }
        }
    }

    private PixelBuffer? ToFrame(IntPtr sample)
    {
        var caps = Gst.gst_sample_get_caps(sample);
        var buffer = Gst.gst_sample_get_buffer(sample);
        if (caps == IntPtr.Zero || buffer == IntPtr.Zero) return null;
        var structure = Gst.gst_caps_get_structure(caps, 0);
        if (Gst.gst_structure_get_int(structure, "width", out int width) == 0
            || Gst.gst_structure_get_int(structure, "height", out int height) == 0 || width <= 0 || height <= 0)
            return null;
        var map = new Gst.MapInfo();
        if (Gst.gst_buffer_map(buffer, &map, Gst.MapRead) == 0) return null;
        try
        {
            if (_frame == null || _frame.Width != width || _frame.Height != height) _frame = new PixelBuffer(width, height);
            int sourceStride = (int)((long)map.Size / height);
            if (sourceStride < width * 4) return null;
            fixed (byte* target = _frame.Pixels)
            {
                for (int y = 0; y < height; y++)
                    Buffer.MemoryCopy(map.Data + (long)y * sourceStride, target + (long)y * _frame.Stride, _frame.Stride, _frame.Stride);
            }
            return _frame;
        }
        finally
        {
            Gst.gst_buffer_unmap(buffer, &map);
        }
    }

    private void ReadBus()
    {
        while (true)
        {
            IntPtr message;
            lock (_gate)
            {
                if (_bus == IntPtr.Zero) return;
                message = Gst.gst_bus_timed_pop_filtered(_bus, 0, Gst.MessageEos | Gst.MessageError | Gst.MessageDurationChanged);
            }
            if (message == IntPtr.Zero) return;
            try
            {
                int type = GStreamerRuntime.MessageType(message);
                if (type == Gst.MessageEos)
                {
                    HasEnded = true;
                    _playing = false;
                    lock (_gate)
                        if (_playbin != IntPtr.Zero) Gst.gst_element_set_state(_playbin, Gst.StatePaused);
                }
                else if (type == Gst.MessageError)
                {
                    ErrorLog.Write("Lecture de la vidéo", GStreamerRuntime.DescribeMessage(message));
                    Error = Loc.T("Linux.PlayerError");
                    _playing = false;
                }
                else if (type == Gst.MessageDurationChanged)
                {
                    ReadDuration();
                }
            }
            finally
            {
                Gst.gst_mini_object_unref(message);
            }
            Raise();
        }
    }

    private void ReadDuration()
    {
        lock (_gate)
        {
            if (_playbin != IntPtr.Zero && Gst.gst_element_query_duration(_playbin, Gst.FormatTime, out long ns) != 0 && ns > 0)
                Duration = TimeSpan.FromTicks(ns / 100);
        }
    }

    private string? PopError()
    {
        lock (_gate)
        {
            if (_bus == IntPtr.Zero) return null;
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
    }

    private void Raise()
    {
        try
        {
            StateChanged?.Invoke();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecteur vidéo", ex);
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _worker?.Join(1000);
        lock (_gate)
        {
            if (_playbin != IntPtr.Zero)
            {
                Gst.gst_element_set_state(_playbin, Gst.StateNull);
                Gst.gst_object_unref(_playbin);
                _playbin = IntPtr.Zero;
            }
            if (_sink != IntPtr.Zero)
            {
                Gst.gst_object_unref(_sink);
                _sink = IntPtr.Zero;
            }
            if (_bus != IntPtr.Zero)
            {
                Gst.gst_object_unref(_bus);
                _bus = IntPtr.Zero;
            }
        }
    }
}
