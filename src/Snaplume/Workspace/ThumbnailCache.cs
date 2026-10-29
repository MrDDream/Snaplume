using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>
/// Thumbnails shared by the galleries (quick panel and workspace window). They are decoded
/// in the background at the displayed size, a few at a time, and kept in a size-capped
/// LRU cache keyed by path + last write time + file size (an edited capture gets a new key).
/// </summary>
public static class ThumbnailCache
{
    private const long MaxBytes = 128L * 1024 * 1024;
    private const int MaxEntries = 4000;

    /// <param name="Dimensions">"1920 × 1080", empty for videos.</param>
    /// <param name="BoxWidth">Size (pixels) the image was decoded to fit in; reused for smaller boxes.</param>
    public sealed record Thumbnail(BitmapSource Image, string Dimensions, int BoxWidth, int BoxHeight)
    {
        public long Bytes => (long)Image.PixelWidth * Image.PixelHeight * 4;
    }

    private readonly record struct Key(string Path, DateTime Modified, long Length);

    private static readonly object Lock = new();
    private static readonly Dictionary<Key, LinkedListNode<(Key Key, Thumbnail Value)>> Entries = new();
    /// <summary>Most recently used first.</summary>
    private static readonly LinkedList<(Key Key, Thumbnail Value)> Recent = new();
    /// <summary>Latest key of each file, to drop the thumbnail of a previous version.</summary>
    private static readonly Dictionary<string, Key> LatestKey = new();
    private static readonly SemaphoreSlim Decoders = new(Environment.ProcessorCount);
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
        var key = new Key(path.ToUpperInvariant(), modifiedUtc, length);
        if (TryGet(key, boxWidth, boxHeight) is { } cached) return cached;

        await Decoders.WaitAsync(cancellation).ConfigureAwait(false);
        try
        {
            cancellation.ThrowIfCancellationRequested();
            // Another gallery may have decoded it while this one was waiting.
            if (TryGet(key, boxWidth, boxHeight) is { } decoded) return decoded;
            var thumbnail = await Task.Run(() => Decode(path, isVideo, boxWidth, boxHeight), cancellation).ConfigureAwait(false);
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

    private static Thumbnail? Decode(string path, bool isVideo, int boxWidth, int boxHeight)
    {
        if (isVideo)
        {
            // Explorer's thumbnail (a frame of the video).
            var frame = Native.ShellThumbnail.Get(path, Math.Max(boxWidth, boxHeight));
            return frame == null ? null : new Thumbnail(frame, "", boxWidth, boxHeight);
        }

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
                // Header only: the size is known (and checked) before any pixel is decoded.
                var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                    BitmapCacheOption.None).Frames[0];
                int width = frame.PixelWidth, height = frame.PixelHeight;
                if (width <= 0 || height <= 0 || (long)width * height > ImageIO.MaxPixels) return null;

                stream.Position = 0;
                var image = new BitmapImage();
                image.BeginInit();
                // Decoded at the thumbnail size while the file is open, then the file is let go.
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                // Fit in the box like Stretch="Uniform", never larger than the image itself.
                if ((long)width * boxHeight >= (long)height * boxWidth) image.DecodePixelWidth = Math.Min(width, boxWidth);
                else image.DecodePixelHeight = Math.Min(height, boxHeight);
                image.EndInit();
                image.Freeze();
                return new Thumbnail(image, $"{width} × {height}", boxWidth, boxHeight);
            }
            catch (IOException)
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
}
