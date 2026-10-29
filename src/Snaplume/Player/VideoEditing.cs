using Snaplume.Recording;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Snaplume.Player;

/// <summary>What <see cref="VideoEditing.ExportGifAsync"/> makes of a video.</summary>
/// <param name="Start">Beginning of the part of the video turned into a GIF.</param>
/// <param name="End">End of that part (clamped to the length of the video).</param>
/// <param name="FramesPerSecond">Pictures per second of the GIF (5–30).</param>
/// <param name="Width">Size of the GIF, in pixels.</param>
/// <param name="Speed">Playback speed: 2 plays the video twice as fast (half the duration).</param>
/// <param name="Loop">The GIF plays again and again.</param>
/// <param name="Dither">Floyd–Steinberg dithering (smoother gradients, bigger file).</param>
public sealed record GifExportSettings(TimeSpan Start, TimeSpan End, int FramesPerSecond, int Width, int Height,
    double Speed, bool Loop, bool Dither)
{
    public const int MinFramesPerSecond = 5;
    public const int MaxFramesPerSecond = 30;
    /// <summary>Longest GIF accepted (memory and time stay reasonable).</summary>
    public const int MaxFrames = 3000;

    /// <summary>Times (from the start of the video) of the pictures of the GIF.</summary>
    public IReadOnlyList<TimeSpan> FrameTimes()
    {
        double step = Speed / FramesPerSecond; // seconds of video between two pictures
        var times = new TimeSpan[Math.Min(FrameCount, MaxFrames)];
        for (int i = 0; i < times.Length; i++) times[i] = Start + TimeSpan.FromSeconds(i * step);
        return times;
    }

    /// <summary>Number of pictures, without building the list.</summary>
    public int FrameCount => Math.Max(1, (int)Math.Ceiling(Math.Max(0, (End - Start).TotalSeconds) / (Speed / FramesPerSecond) - 1e-6));
}

/// <summary>
/// Offline video editing with the Windows media engine (Windows.Media.Editing): extract of an MP4
/// and conversion to an animated GIF. Nothing leaves the computer; the source file is only read.
/// </summary>
public static class VideoEditing
{
    /// <summary>Thumbnails asked to the engine at once (fewer round trips, bounded memory).</summary>
    private const int FrameBatch = 8;

    /// <summary>
    /// Writes the part of <paramref name="source"/> between <paramref name="start"/> and
    /// <paramref name="end"/> to <paramref name="destination"/> (a new MP4 file: never overwrites,
    /// deleted again if the export fails or is cancelled).
    /// </summary>
    /// <param name="progress">Receives 0 to 1.</param>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancel"/>.</exception>
    public static async Task ExportClipAsync(string source, string destination, TimeSpan start, TimeSpan end,
        IProgress<double>? progress, CancellationToken cancel)
    {
        CheckDestination(source, destination);
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        var duration = clip.OriginalDuration;
        if (end <= TimeSpan.Zero || end > duration) end = duration;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end - start < TimeSpan.FromMilliseconds(100)) throw new ArgumentException("The extract is too short.");
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = duration - end;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);

        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var target = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.FailIfExists);
        try
        {
            var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, BuildProfile(clip));
            var reason = await render.AsTask(cancel, new Progress<double>(percent => progress?.Report(Math.Clamp(percent / 100, 0, 1))));
            if (reason != TranscodeFailureReason.None) throw new InvalidOperationException($"MediaComposition: {reason}");
        }
        catch
        {
            await DeleteQuietlyAsync(destination);
            throw;
        }
    }

    /// <summary>
    /// Turns part of <paramref name="source"/> into an animated GIF written to
    /// <paramref name="destination"/> (a new file, deleted again on failure or cancellation).
    /// Runs on the thread pool.
    /// </summary>
    /// <param name="progress">Receives 0 to 1.</param>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancel"/>.</exception>
    public static Task ExportGifAsync(string source, string destination, GifExportSettings settings,
        IProgress<double>? progress, CancellationToken cancel) =>
        Task.Run(() => WriteGifAsync(source, destination, settings, progress, cancel), cancel);

    private static async Task WriteGifAsync(string source, string destination, GifExportSettings settings,
        IProgress<double>? progress, CancellationToken cancel)
    {
        CheckDestination(source, destination);
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(source));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        // The last picture of a video is a little before its end.
        var last = clip.OriginalDuration - TimeSpan.FromMilliseconds(20);
        var times = settings.FrameTimes().Select(t => t > last ? last : t < TimeSpan.Zero ? TimeSpan.Zero : t).ToList();
        var delays = GifEncoder.FrameDelays(times.Count, settings.FramesPerSecond);
        int width = settings.Width, height = settings.Height;

        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        try
        {
            using (var encoder = new GifEncoder(output, width, height,
                       new GifEncoderOptions { LoopForever = settings.Loop, Dither = settings.Dither }))
            {
                for (int first = 0; first < times.Count; first += FrameBatch)
                {
                    cancel.ThrowIfCancellationRequested();
                    var batch = times.Skip(first).Take(FrameBatch).ToList();
                    var images = await composition.GetThumbnailsAsync(batch, width, height, VideoFramePrecision.NearestFrame).AsTask(cancel);
                    for (int i = 0; i < images.Count; i++)
                    {
                        cancel.ThrowIfCancellationRequested();
                        using var image = images[i];
                        var pixels = await ReadPixelsAsync(image, width, height);
                        encoder.AddFrame(pixels, width * 4, delays[first + i]);
                    }
                    if (images.Count != batch.Count) throw new InvalidOperationException("Missing video frames.");
                    progress?.Report((double)Math.Min(times.Count, first + FrameBatch) / times.Count);
                }
                encoder.Finish();
            }
        }
        catch
        {
            output.Dispose();
            await DeleteQuietlyAsync(destination);
            throw;
        }
    }

    /// <summary>Decodes a thumbnail of the engine as BGRA, at exactly the size asked.</summary>
    private static async Task<byte[]> ReadPixelsAsync(ImageStream image, int width, int height)
    {
        var decoder = await BitmapDecoder.CreateAsync(image);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        return data.DetachPixelData();
    }

    /// <summary>MP4 (H.264 + AAC) with the size, frame rate and bit rate of the source.</summary>
    private static MediaEncodingProfile BuildProfile(MediaClip clip)
    {
        var profile = MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        var video = clip.GetVideoEncodingProperties();
        if (video.Width > 0 && video.Height > 0)
        {
            profile.Video.Width = video.Width & ~1u;
            profile.Video.Height = video.Height & ~1u;
        }
        if (video.FrameRate.Numerator > 0 && video.FrameRate.Denominator > 0)
        {
            profile.Video.FrameRate.Numerator = video.FrameRate.Numerator;
            profile.Video.FrameRate.Denominator = video.FrameRate.Denominator;
        }
        double fps = video.FrameRate.Denominator > 0 ? (double)video.FrameRate.Numerator / video.FrameRate.Denominator : 30;
        // Screen content compresses well: about 0.1 bit per pixel, at least what the source used.
        uint estimate = (uint)Math.Clamp(profile.Video.Width * (double)profile.Video.Height * fps * 0.1, 1_000_000, 40_000_000);
        profile.Video.Bitrate = Math.Max(video.Bitrate, estimate);
        if (clip.EmbeddedAudioTracks.Count == 0) profile.Audio = null; // no silent track
        return profile;
    }

    private static void CheckDestination(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The destination is the source file.", nameof(destination));
        if (File.Exists(destination)) throw new IOException($"{destination} already exists.");
    }

    /// <summary>Removes an unfinished output (the engine may still hold it for a moment).</summary>
    private static async Task DeleteQuietlyAsync(string path)
    {
        for (int attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (IOException)
            {
                await Task.Delay(200);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(200);
            }
        }
    }
}
