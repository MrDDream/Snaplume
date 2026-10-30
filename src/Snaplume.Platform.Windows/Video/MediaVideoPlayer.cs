using System.Runtime.InteropServices.WindowsRuntime;
using Snaplume.Core;
using Snaplume.Imaging;
using Windows.Graphics.DirectX;
using Windows.Graphics.Imaging;
using Windows.Media;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;

namespace Snaplume.Platform.Windows.Video;

/// <summary>
/// Video playback with the Windows media engine (WinRT MediaPlayer, sound included) in "frame server" mode: the
/// engine does not draw anywhere, each new picture is copied into a Direct3D surface, read back as BGRA and handed
/// to the application, which shows it in an Avalonia bitmap.
/// </summary>
/// <remarks>
/// The read-back goes through the GPU and the CPU, so pictures are dropped when one is still being copied: the
/// player then shows fewer pictures per second, never late ones.
/// </remarks>
public sealed class MediaVideoPlayer : IVideoPlayer, IVideoPlayerSpeed
{
    private readonly MediaPlayer _player;
    private readonly object _lock = new();
    /// <summary>Two buffers used in turn: the application may still be reading the previous picture.</summary>
    private readonly PixelBuffer?[] _buffers = new PixelBuffer?[2];
    private int _nextBuffer;
    private VideoFrame? _surface;
    private int _copying;
    private int _pending;
    private volatile bool _disposed;
    private TaskCompletionSource<bool>? _opening;

    public MediaVideoPlayer()
    {
        _player = new MediaPlayer
        {
            AutoPlay = false,
            IsVideoFrameServerEnabled = true,
        };
        _player.VideoFrameAvailable += OnVideoFrameAvailable;
        _player.MediaOpened += OnMediaOpened;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaEnded += OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged += OnPlaybackStateChanged;
        // A seek while paused gives no new frame event: copy the picture shown at the new position.
        _player.PlaybackSession.SeekCompleted += (_, _) => _ = CopyFrameAsync();
    }

    public event Action<PixelBuffer>? FrameReady;
    public event Action? StateChanged;

    public TimeSpan Duration => Safe(() => _player.PlaybackSession.NaturalDuration, TimeSpan.Zero);
    public TimeSpan Position => Safe(() => _player.PlaybackSession.Position, TimeSpan.Zero);
    public bool IsPlaying => Safe(() => _player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing, false);
    public bool HasEnded { get; private set; }
    public string? Error { get; private set; }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    /// <summary>Playback speed of the media engine (the sound keeps its pitch).</summary>
    public double Speed
    {
        get => Safe(() => _player.PlaybackSession.PlaybackRate, 1.0);
        set => _player.PlaybackSession.PlaybackRate = Math.Clamp(value, 0.1, 8);
    }

    /// <summary>Opens the file; completes once it is ready to play or has failed (see <see cref="Error"/>).</summary>
    public async Task OpenAsync(string path)
    {
        Error = null;
        HasEnded = false;
        var opening = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _opening = opening;
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path));
            _player.Source = MediaSource.CreateFromStorageFile(file);
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Lecture de {path}", ex);
            Error = ex.Message;
            _opening = null;
            StateChanged?.Invoke();
            return;
        }
        await opening.Task;
    }

    public void Play()
    {
        if (_disposed) return;
        var session = _player.PlaybackSession;
        // At the end: play again from the start.
        if (HasEnded || (session.NaturalDuration > TimeSpan.Zero && session.Position >= session.NaturalDuration - TimeSpan.FromMilliseconds(50)))
            session.Position = TimeSpan.Zero;
        HasEnded = false;
        _player.Play();
    }

    public void Pause()
    {
        if (_disposed) return;
        _player.Pause();
    }

    public void Seek(TimeSpan position)
    {
        if (_disposed) return;
        var session = _player.PlaybackSession;
        var duration = session.NaturalDuration;
        if (position < TimeSpan.Zero) position = TimeSpan.Zero;
        if (duration > TimeSpan.Zero && position > duration) position = duration;
        HasEnded = false;
        session.Position = position;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        _player.VideoFrameAvailable -= OnVideoFrameAvailable;
        _player.MediaOpened -= OnMediaOpened;
        _player.MediaFailed -= OnMediaFailed;
        _player.MediaEnded -= OnMediaEnded;
        _player.PlaybackSession.PlaybackStateChanged -= OnPlaybackStateChanged;
        _opening?.TrySetResult(false);
        _player.Dispose();
        lock (_lock)
        {
            // A copy still running keeps the surface: it disposes it when it ends (see CopyFrameAsync).
            if (_copying == 0)
            {
                _surface?.Dispose();
                _surface = null;
            }
        }
    }

    private void OnMediaOpened(MediaPlayer sender, object args)
    {
        _opening?.TrySetResult(true);
        _opening = null;
        StateChanged?.Invoke();
        // The first picture, shown before playing.
        _ = CopyFrameAsync();
    }

    private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
    {
        Error = string.IsNullOrWhiteSpace(args.ErrorMessage) ? args.Error.ToString() : args.ErrorMessage;
        ErrorLog.Write("Lecteur vidéo", $"{args.Error}: {args.ErrorMessage} ({args.ExtendedErrorCode?.Message})");
        _opening?.TrySetResult(false);
        _opening = null;
        StateChanged?.Invoke();
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        HasEnded = true;
        StateChanged?.Invoke();
    }

    private void OnPlaybackStateChanged(MediaPlaybackSession sender, object args) => StateChanged?.Invoke();

    private void OnVideoFrameAvailable(MediaPlayer sender, object args) => _ = CopyFrameAsync();

    /// <summary>Copies the picture of the current position and hands it to the application; skipped while a copy runs.</summary>
    private async Task CopyFrameAsync()
    {
        if (Interlocked.Exchange(ref _copying, 1) == 1)
        {
            // Copy again when this one ends: the last picture (e.g. after a seek while paused) is never lost.
            Volatile.Write(ref _pending, 1);
            return;
        }
        try
        {
            VideoFrame surface;
            lock (_lock)
            {
                if (_disposed) return;
                var session = _player.PlaybackSession;
                int width = (int)session.NaturalVideoWidth, height = (int)session.NaturalVideoHeight;
                if (width <= 0 || height <= 0) return;
                if (_surface == null || _surface.Direct3DSurface.Description.Width != width || _surface.Direct3DSurface.Description.Height != height)
                {
                    _surface?.Dispose();
                    _surface = VideoFrame.CreateAsDirect3D11SurfaceBacked(DirectXPixelFormat.B8G8R8A8UIntNormalized, width, height);
                }
                surface = _surface;
                _player.CopyFrameToVideoSurface(surface.Direct3DSurface);
            }

            using var bitmap = await SoftwareBitmap.CreateCopyFromSurfaceAsync(surface.Direct3DSurface, BitmapAlphaMode.Premultiplied);
            if (_disposed) return;
            var buffer = NextBuffer(bitmap.PixelWidth, bitmap.PixelHeight);
            bitmap.CopyToBuffer(buffer.Pixels.AsBuffer());
            buffer.MakeOpaque();
            FrameReady?.Invoke(buffer);
        }
        catch (Exception ex) when (!_disposed)
        {
            // No picture decoded yet (just opened), or the device was lost: the next frame will try again.
            System.Diagnostics.Debug.WriteLine($"Video frame: {ex.Message}");
        }
        catch (Exception)
        {
            // Disposed meanwhile.
        }
        finally
        {
            bool again;
            lock (_lock)
            {
                _copying = 0;
                again = Interlocked.Exchange(ref _pending, 0) == 1 && !_disposed;
                if (_disposed)
                {
                    _surface?.Dispose();
                    _surface = null;
                }
            }
            if (again) _ = CopyFrameAsync();
        }
    }

    private PixelBuffer NextBuffer(int width, int height)
    {
        int index = _nextBuffer;
        _nextBuffer = 1 - index;
        var buffer = _buffers[index];
        if (buffer == null || buffer.Width != width || buffer.Height != height)
            _buffers[index] = buffer = new PixelBuffer(width, height);
        return buffer;
    }

    private static T Safe<T>(Func<T> read, T fallback)
    {
        try
        {
            return read();
        }
        catch (Exception)
        {
            // The player is closed.
            return fallback;
        }
    }
}
