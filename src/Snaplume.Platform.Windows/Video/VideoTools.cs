using System.Runtime.CompilerServices;
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

        destination = Path.GetFullPath(destination);
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

    /// <summary>Frames of the video at the given times (clamped to its length), scaled to the size, opaque.</summary>
    public async IAsyncEnumerable<PixelBuffer> ReadFramesAsync(string source, IReadOnlyList<TimeSpan> times, int width, int height,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        var clip = await MediaClip.CreateFromFileAsync(await StorageFile.GetFileFromPathAsync(Path.GetFullPath(source)));
        var composition = new MediaComposition();
        composition.Clips.Add(clip);
        // The last picture of a video is a little before its end.
        var last = clip.OriginalDuration - TimeSpan.FromMilliseconds(20);
        var clamped = times.Select(t => t > last ? last : t < TimeSpan.Zero ? TimeSpan.Zero : t).ToList();

        for (int first = 0; first < clamped.Count; first += FrameBatch)
        {
            cancel.ThrowIfCancellationRequested();
            var batch = clamped.Skip(first).Take(FrameBatch).ToList();
            var images = await composition.GetThumbnailsAsync(batch, width, height, VideoFramePrecision.NearestFrame).AsTask(cancel);
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
                    frame = new PixelBuffer(width, height, await ReadPixelsAsync(image, width, height)).MakeOpaque();
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
