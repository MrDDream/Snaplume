using System.Runtime.CompilerServices;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Tests.Fakes;

/// <summary>A video that does not exist: each frame is a plain color that depends on its time.</summary>
public sealed class FakeVideoTools : IVideoTools
{
    /// <summary>Frames given before the stream ends early (a broken file); null: all of them.</summary>
    public int? FramesAvailable { get; init; }
    /// <summary>Frames are returned at this size instead of the one asked.</summary>
    public (int Width, int Height)? NativeSize { get; init; }
    public List<TimeSpan> RequestedTimes { get; } = new();

    public Task ExportClipAsync(string source, string destination, TimeSpan start, TimeSpan end, IProgress<double>? progress, CancellationToken cancel) =>
        throw new NotSupportedException();

    public async IAsyncEnumerable<PixelBuffer> ReadFramesAsync(string source, IReadOnlyList<TimeSpan> times, int width, int height,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        RequestedTimes.AddRange(times);
        int count = FramesAvailable is int n ? Math.Min(n, times.Count) : times.Count;
        var (w, h) = NativeSize ?? (width, height);
        for (int i = 0; i < count; i++)
        {
            cancel.ThrowIfCancellationRequested();
            await Task.Yield();
            var frame = new PixelBuffer(w, h);
            byte v = (byte)(times[i].TotalMilliseconds / 10 % 256);
            for (int p = 0; p < frame.Pixels.Length; p += 4)
            {
                frame.Pixels[p] = v;
                frame.Pixels[p + 1] = (byte)(255 - v);
                frame.Pixels[p + 2] = 128;
                frame.Pixels[p + 3] = 255;
            }
            yield return frame;
        }
    }

    public Task<PixelBuffer?> ThumbnailAsync(string path, int size, CancellationToken cancel = default) => Task.FromResult<PixelBuffer?>(null);

    /// <summary>Codecs the fake system encoder makes.</summary>
    public HashSet<VideoCodec> SystemCodecs { get; init; } = new() { VideoCodec.H264 };
    /// <summary>Size of the file a re-encoding writes, from its settings; by default the bit rates over 10 s.</summary>
    public Func<VideoEncodeSettings, long> TranscodedSize { get; init; } = s => (s.VideoBitrate + s.AudioBitrate) * 10L / 8;
    public List<VideoEncodeSettings> Transcodes { get; } = new();
    public VideoInfo? Info { get; init; }

    public bool CanTranscode(VideoCodec codec) => SystemCodecs.Contains(codec);

    public async Task TranscodeAsync(string source, string destination, TimeSpan start, TimeSpan end, VideoEncodeSettings settings,
        IProgress<double>? progress, CancellationToken cancel)
    {
        cancel.ThrowIfCancellationRequested();
        if (File.Exists(destination)) throw new IOException($"{destination} already exists.");
        Transcodes.Add(settings);
        await File.WriteAllBytesAsync(destination, new byte[TranscodedSize(settings)], cancel);
        progress?.Report(1);
    }

    public Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancel = default) => Task.FromResult(Info);

    public IVideoPlayer CreatePlayer() => throw new NotSupportedException();
}
