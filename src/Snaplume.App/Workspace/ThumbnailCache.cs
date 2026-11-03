using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using SkiaSharp;
using Snaplume.Core;
using Snaplume.Imaging;

namespace Snaplume.Workspace;

/// <summary>
/// Thumbnails shared by the galleries (quick panel, workspace window) and the document dialog. They are decoded
/// in the background at the displayed size, a few at a time, and kept in a size-capped LRU cache keyed by
/// path + last write time + file size (an edited capture gets a new key). Videos ask the platform for a frame.
/// An evicted thumbnail is only dropped, never disposed: a gallery may still show it, and the cache keeps no reference
/// to it (the galleries themselves let go of the thumbnails far from what they show).
/// </summary>
public static class ThumbnailCache
{
    private const long MaxBytes = 128L * 1024 * 1024;
    private const int MaxEntries = 4000;

    /// <param name="Dimensions">"1920 × 1080", empty for videos.</param>
    /// <param name="BoxWidth">Size (pixels) the image was decoded to fit in; reused for smaller boxes.</param>
    public sealed record Thumbnail(Bitmap Image, string Dimensions, int BoxWidth, int BoxHeight)
    {
        public long Bytes => (long)Image.PixelSize.Width * Image.PixelSize.Height * 4;
    }

    private readonly record struct Key(string Path, DateTime Modified, long Length);

    private static readonly object Lock = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Thumbnail Value)>> Entries = new();
    /// <summary>Most recently used first.</summary>
    private static readonly LinkedList<(Key Key, Thumbnail Value)> Recent = new();
    /// <summary>Latest key of each file, to drop the thumbnail of a previous version.</summary>
    private static readonly Dictionary<string, Key> LatestKey = new();
    private static readonly SemaphoreSlim Decoders = new(Math.Max(2, Environment.ProcessorCount));
    /// <summary>
    /// Large images are decoded one at a time: each needs its full size in memory first (a 50 MP PNG takes 200 MB), and a
    /// folder of them must not multiply that by the number of processors.
    /// </summary>
    private static readonly SemaphoreSlim LargeDecoders = new(1);
    private const long LargePixels = 16_000_000;

    private static long _bytes;

    /// <summary>
    /// Thumbnail fitting in a box of the given size (device pixels).
    /// Returns null when the file has no usable image (unknown format, damaged file...).
    /// </summary>
    /// <exception cref="IOException">The file could not be read now (e.g. still being written): try again later.</exception>
    /// <exception cref="OperationCanceledException">The gallery no longer needs it.</exception>
    public static async Task<Thumbnail?> GetAsync(string path, DateTime modifiedUtc, long length, bool isVideo,
        int boxWidth, int boxHeight, CancellationToken cancellation)
    {
        // Windows file names ignore case, Linux ones do not.
        var key = new Key(OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path, modifiedUtc, length);
        if (TryGet(key, boxWidth, boxHeight) is { } cached) return cached;

        await Decoders.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Another gallery may have decoded it while this one was waiting.
            if (TryGet(key, boxWidth, boxHeight) is { } decoded) return decoded;
            var thumbnail = isVideo
                ? await DecodeVideoAsync(path, boxWidth, boxHeight, cancellation).ConfigureAwait(false)
                : await Task.Run(() => DecodeImage(path, boxWidth, boxHeight, cancellation), cancellation).ConfigureAwait(false);
            if (thumbnail != null) Add(key, thumbnail);
            return thumbnail;
        }
        finally
        {
            Decoders.Release();
        }
    }

    private static Thumbnail? TryGet(Key key, int boxWidth, int boxHeight)
    {
        lock (Lock)
        {
            if (!Entries.TryGetValue(key, out var node)) return null;
            var thumbnail = node.Value.Value;
            if (thumbnail.BoxWidth < boxWidth || thumbnail.BoxHeight < boxHeight) return null; // too small for this gallery
            Recent.Remove(node);
            Recent.AddFirst(node);
            return thumbnail;
        }
    }

    private static void Add(Key key, Thumbnail thumbnail)
    {
        lock (Lock)
        {
            if (LatestKey.TryGetValue(key.Path, out var previous) && previous != key) Remove(previous);
            Remove(key);
            LatestKey[key.Path] = key;
            Entries[key] = Recent.AddFirst((key, thumbnail));
            _bytes += thumbnail.Bytes;
            while ((_bytes > MaxBytes || Entries.Count > MaxEntries) && Recent.Last is { } oldest && oldest.Value.Key != key)
                Remove(oldest.Value.Key);
        }
    }

    private static void Remove(Key key)
    {
        if (!Entries.Remove(key, out var node)) return;
        Recent.Remove(node);
        _bytes -= node.Value.Value.Bytes;
        if (LatestKey.TryGetValue(key.Path, out var latest) && latest == key) LatestKey.Remove(key.Path);
    }

    /// <summary>A frame of the video, from the platform (Windows media engine, ffmpeg...).</summary>
    private static async Task<Thumbnail?> DecodeVideoAsync(string path, int boxWidth, int boxHeight, CancellationToken cancellation)
    {
        if (!AppServices.HasPlatform) return null;
        var frame = await AppServices.Platform.Video.ThumbnailAsync(path, Math.Max(boxWidth, boxHeight), cancellation).ConfigureAwait(false);
        return frame == null ? null : new Thumbnail(AvaloniaImages.ToBitmap(frame), "", boxWidth, boxHeight);
    }

    /// <summary>
    /// Decodes an image at the thumbnail size: the size is read (and checked against <see cref="ImageIO.MaxPixels"/>)
    /// from the header first, JPEG files are decoded directly at a reduced scale, and the file is released at once.
    /// Images of more than <see cref="LargePixels"/> wait for <see cref="LargeDecoders"/>.
    /// </summary>
    private static Thumbnail? DecodeImage(string path, int boxWidth, int boxHeight, CancellationToken cancellation)
    {
        FileStream stream;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or UnauthorizedAccessException)
        {
            return null; // deleted meanwhile, or not readable at all: nothing to retry
        }

        using (stream)
        {
            try
            {
                using var codec = SKCodec.Create(stream);
                if (codec == null) return null;
                int width = codec.Info.Width, height = codec.Info.Height;
                if (width <= 0 || height <= 0 || (long)width * height > ImageIO.MaxPixels) return null;

                // Fit in the box like Stretch="Uniform", never larger than the image itself.
                double scale = Math.Min(1, Math.Min((double)boxWidth / width, (double)boxHeight / height));
                int targetWidth = Math.Max(1, (int)Math.Round(width * scale));
                int targetHeight = Math.Max(1, (int)Math.Round(height * scale));

                // JPEG can be decoded at 1/2, 1/4 or 1/8 of its size: much faster, if still at least the target size.
                var decodeSize = codec.GetScaledDimensions((float)scale);
                if (decodeSize.Width < targetWidth || decodeSize.Height < targetHeight) decodeSize = new SKSizeI(width, height);

                bool large = (long)decodeSize.Width * decodeSize.Height > LargePixels;
                if (large) LargeDecoders.Wait(cancellation);
                try
                {
                    var thumbnail = Decode(codec, decodeSize, targetWidth, targetHeight);
                    return thumbnail == null ? null : new Thumbnail(thumbnail, $"{width} × {height}", boxWidth, boxHeight);
                }
                finally
                {
                    if (large) LargeDecoders.Release();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                throw; // e.g. still being written: the gallery tries again later
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Damaged or partly written file: a later write changes the key and triggers a new attempt.
                return null;
            }
        }
    }

    /// <summary>Decodes the image at <paramref name="decodeSize"/>, then resizes it to the target size; null when it cannot be read.</summary>
    private static Bitmap? Decode(SKCodec codec, SKSizeI decodeSize, int targetWidth, int targetHeight)
    {
        var info = new SKImageInfo(decodeSize.Width, decodeSize.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        var result = codec.GetPixels(info, decoded.GetPixels());
        if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput)) return null;

        SKBitmap thumbnail = decoded;
        if (decodeSize.Width != targetWidth || decodeSize.Height != targetHeight)
        {
            thumbnail = decoded.Resize(new SKImageInfo(targetWidth, targetHeight, SKColorType.Bgra8888, SKAlphaType.Premul),
                new SKSamplingOptions(SKCubicResampler.Mitchell)) ?? decoded;
        }
        try
        {
            return ToAvalonia(thumbnail);
        }
        finally
        {
            if (!ReferenceEquals(thumbnail, decoded)) thumbnail.Dispose();
        }
    }

    /// <summary>Copies a premultiplied BGRA Skia bitmap into an Avalonia bitmap.</summary>
    private static Bitmap ToAvalonia(SKBitmap source)
    {
        var bitmap = new WriteableBitmap(new PixelSize(source.Width, source.Height), AvaloniaImages.Dpi, PixelFormat.Bgra8888, AlphaFormat.Premul);
        var bytes = source.Bytes;
        int rowBytes = source.Width * 4;
        using var locked = bitmap.Lock();
        for (int y = 0; y < source.Height; y++)
            Marshal.Copy(bytes, y * source.RowBytes, locked.Address + y * locked.RowBytes, rowBytes);
        return bitmap;
    }
}
