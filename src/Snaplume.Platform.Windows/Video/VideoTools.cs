using System.Runtime.CompilerServices;
using Snaplume.Core;
using Snaplume.Imaging;
using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Media.MediaProperties;
using Windows.Media.Transcoding;
using Windows.Storage;

namespace Snaplume.Platform.Windows.Video;

/// <summary>
/// Offline video editing with the Windows media engine (Windows.Media.Editing): extract of an MP4, frames for the
/// GIF export, thumbnails and the player. Nothing leaves the computer; the source file is only read.
/// </summary>
public sealed class VideoTools : IVideoTools
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
    public async Task ExportClipAsync(string source, string destination, TimeSpan start, TimeSpan end,
        IProgress<double>? progress, CancellationToken cancel)
    {
        await RenderAsync(source, destination, start, end, BuildProfile, progress, cancel);
    }

    public bool CanTranscode(VideoCodec codec) => codec == VideoCodec.H264 || HevcEncoders.Available;

    /// <summary>Re-encodes the part to H.264 or HEVC with the size, frame rate and bit rates asked.</summary>
    public Task TranscodeAsync(string source, string destination, TimeSpan start, TimeSpan end, VideoEncodeSettings settings,
        IProgress<double>? progress, CancellationToken cancel) =>
        RenderAsync(source, destination, start, end, clip => BuildProfile(clip, settings), progress, cancel);

    public async Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancel = default)
    {
        try
        {
            var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(path)).AsTask(cancel).ConfigureAwait(false);
            var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancel).ConfigureAwait(false);
            var video = clip.GetVideoEncodingProperties();
            double fps = video.FrameRate.Denominator > 0 ? (double)video.FrameRate.Numerator / video.FrameRate.Denominator : 0;
            return new VideoInfo((int)video.Width, (int)video.Height, fps, clip.EmbeddedAudioTracks.Count > 0, clip.OriginalDuration);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            ErrorLog.Write($"Lecture des propriétés de {path}", ex);
            return null;
        }
    }

    /// <summary>
    /// Renders the part of the video with the profile made for it into a new file, deleted again if the render
    /// fails or is cancelled.
    /// </summary>
    private static async Task RenderAsync(string source, string destination, TimeSpan start, TimeSpan end,
        Func<MediaClip, MediaEncodingProfile> profileFor, IProgress<double>? progress, CancellationToken cancel)
    {
        CheckDestination(source, destination);
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source)));
        var duration = clip.OriginalDuration;
        if (end <= TimeSpan.Zero || end > duration) end = duration;
        if (start < TimeSpan.Zero) start = TimeSpan.Zero;
        if (end - start < TimeSpan.FromMilliseconds(100)) throw new ArgumentException("The extract is too short.");
        clip.TrimTimeFromStart = start;
        clip.TrimTimeFromEnd = duration - end;
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        var profile = profileFor(clip);

        destination = Path.GetFullPath(destination);
        var folder = await StorageFolder.GetFolderFromPathAsync(Path.GetDirectoryName(destination)!);
        var target = await folder.CreateFileAsync(Path.GetFileName(destination), CreationCollisionOption.FailIfExists);
        try
        {
            var render = composition.RenderToFileAsync(target, MediaTrimmingPreference.Precise, profile);
            var reason = await render.AsTask(cancel, new Progress<double>(percent => progress?.Report(Math.Clamp(percent / 100, 0, 1))));
            if (reason != TranscodeFailureReason.None) throw new InvalidOperationException($"MediaComposition: {reason}");
        }
        catch
        {
            await DeleteQuietlyAsync(destination);
            throw;
        }
    }

    /// <summary>Frames of the video at the given times (clamped to its length), scaled to the size, opaque.</summary>
    /// <remarks>The decoding continues on the thread pool (ConfigureAwait(false)), never on the caller's UI thread.</remarks>
    public async IAsyncEnumerable<PixelBuffer> ReadFramesAsync(string source, IReadOnlyList<TimeSpan> times, int width, int height,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        var file = await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source)).AsTask(cancel).ConfigureAwait(false);
        var clip = await MediaClip.CreateFromFileAsync(file).AsTask(cancel).ConfigureAwait(false);
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        // The last picture of a video is a little before its end.
        var last = clip.OriginalDuration - TimeSpan.FromMilliseconds(20);
        var clamped = times.Select(t => t > last ? last : t < TimeSpan.Zero ? TimeSpan.Zero : t).ToList();

        for (int first = 0; first < clamped.Count; first += FrameBatch)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = clamped.Skip(first).Take(FrameBatch).ToList();
            var images = await composition.GetThumbnailsAsync(batch, width, height, VideoFramePrecision.NearestFrame).AsTask(cancel)
                .ConfigureAwait(false);
            if (images.Count != batch.Count)
            {
                foreach (var image in images) image.Dispose();
                throw new InvalidOperationException("Missing video frames.");
            }
            for (int i = 0; i < images.Count; i++)
            {
                PixelBuffer frame;
                using (var image = images[i])
                {
                    cancel.ThrowIfCancellationRequested();
                    frame = new PixelBuffer(width, height, await ReadPixelsAsync(image, width, height).ConfigureAwait(false)).MakeOpaque();
                }
                yield return frame;
            }
        }
    }

    public Task<PixelBuffer?> ThumbnailAsync(string path, int size, CancellationToken cancel = default) =>
        Task.Run(() =>
        {
            cancel.ThrowIfCancellationRequested();
            var image = ShellThumbnail.Get(Path.GetFullPath(path), size);
            if (image == null || (image.Width <= size && image.Height <= size)) return image;
            // The shell may return a bigger picture (SIIGBF_BIGGERSIZEOK): fit it.
            double scale = (double)size / Math.Max(image.Width, image.Height);
            return ImageIO.Resize(image, (int)Math.Round(image.Width * scale), (int)Math.Round(image.Height * scale));
        }, cancel);

    public IVideoPlayer CreatePlayer() => new MediaVideoPlayer();

    /// <summary>Decodes a thumbnail of the engine as BGRA, at exactly the size asked.</summary>
    private static async Task<byte[]> ReadPixelsAsync(ImageStream image, int width, int height)
    {
        var decoder = await BitmapDecoder.CreateAsync(image).AsTask().ConfigureAwait(false);
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)width,
            ScaledHeight = (uint)height,
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        var data = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask().ConfigureAwait(false);
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

    /// <summary>MP4 (H.264 or HEVC, and AAC) of the size, frame rate and bit rates asked.</summary>
    private static MediaEncodingProfile BuildProfile(MediaClip clip, VideoEncodeSettings settings)
    {
        var profile = settings.Codec == VideoCodec.Hevc
            ? MediaEncodingProfile.CreateHevc(VideoEncodingQuality.HD1080p)
            : MediaEncodingProfile.CreateMp4(VideoEncodingQuality.HD1080p);
        profile.Video.Width = (uint)Math.Max(2, settings.Width & ~1);
        profile.Video.Height = (uint)Math.Max(2, settings.Height & ~1);
        var (numerator, denominator) = FrameRate(settings.FramesPerSecond);
        profile.Video.FrameRate.Numerator = numerator;
        profile.Video.FrameRate.Denominator = denominator;
        profile.Video.Bitrate = (uint)Math.Max(100_000, settings.VideoBitrate);
        // The AAC encoder of Windows takes 96, 128, 160 or 192 kbit/s.
        profile.Audio = clip.EmbeddedAudioTracks.Count == 0 || settings.AudioBitrate <= 0
            ? null
            : AudioEncodingProperties.CreateAac(48000, 2, settings.AudioBitrate >= 128_000 ? 128_000u : 96_000u);
        return profile;
    }

    /// <summary>30 → 30/1, 29.97 → 30000/1001.</summary>
    private static (uint Numerator, uint Denominator) FrameRate(double fps)
    {
        if (fps <= 0 || !double.IsFinite(fps)) return (30, 1);
        if (Math.Abs(fps - Math.Round(fps)) < 0.01) return ((uint)Math.Round(fps), 1);
        return ((uint)Math.Round(fps * 1001), 1001);
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
