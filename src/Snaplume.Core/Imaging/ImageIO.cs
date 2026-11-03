using SkiaSharp;
using Snaplume.Core;

namespace Snaplume.Imaging;

/// <summary>Image files: reading, writing and the size limit. Uses SkiaSharp, the same on every system.</summary>
public static class ImageIO
{
    /// <summary>Image files (a WebP may also be an animation: see <see cref="IsAnimation"/>).</summary>
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp" };

    /// <summary>Videos: recordings, and the WebM exports of the player.</summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".webm" };

    /// <summary>Lossless WebP for <see cref="Save"/> (a lower value is the quality of a lossy WebP).</summary>
    public const int WebPLossless = 100;

    /// <summary>Animated WebP files already looked at, by full path: the header is read once per version of a file.</summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, (DateTime Modified, long Length, bool Animated)> WebPKinds =
        new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>Most entries of <see cref="WebPKinds"/> before it starts again (a few bytes each).</summary>
    private const int MaxWebPKinds = 8192;

    private static bool HasExtension(string path, string[] extensions) =>
        extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// An animated WebP (exported by the player): shown in the workspace (its first frame as thumbnail) and opened in
    /// the default application, but not an image to edit, print or put in a document. A WebP of one frame is an image.
    /// </summary>
    public static bool IsAnimation(string path) =>
        Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase) && IsAnimatedWebP(path);

    /// <summary>An image to edit, copy, read (OCR, QR codes), print...: by its extension, an animated WebP excepted.</summary>
    public static bool IsImage(string path) => HasExtension(path, Extensions) && !IsAnimation(path);

    public static bool IsVideo(string path) => HasExtension(path, VideoExtensions);

    /// <summary>Files shown in the workspace: images, animations and videos (by their extension only: no file is read).</summary>
    public static bool IsCapture(string path) => HasExtension(path, Extensions) || IsVideo(path);

    /// <summary>
    /// True for a WebP of several frames. Its header says so (the animation flag of the "VP8X" chunk): only a file with
    /// that flag is decoded further, to count its frames. The answer is kept while the file keeps its date and length.
    /// </summary>
    private static bool IsAnimatedWebP(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            var key = info.FullName;
            if (WebPKinds.TryGetValue(key, out var known) && known.Modified == info.LastWriteTimeUtc && known.Length == info.Length)
                return known.Animated;
            bool animated;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                Span<byte> header = stackalloc byte[21];
                animated = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) == header.Length && HasAnimationFlag(header);
                if (animated)
                {
                    stream.Position = 0;
                    using var codec = SKCodec.Create(stream);
                    animated = codec?.FrameCount > 1;
                }
            }
            if (WebPKinds.Count >= MaxWebPKinds) WebPKinds.Clear();
            WebPKinds[key] = (info.LastWriteTimeUtc, info.Length, animated);
            return animated;
        }
        catch (Exception)
        {
            // Unreadable for now (locked, deleted meanwhile): an image, as its extension says.
            return false;
        }
    }

    /// <summary>"RIFF" size "WEBP", then a "VP8X" chunk whose flags have the animation bit (0x02).</summary>
    internal static bool HasAnimationFlag(ReadOnlySpan<byte> header) =>
        header.Length >= 21 && header[..4].SequenceEqual("RIFF"u8) && header[8..12].SequenceEqual("WEBP"u8)
        && header[12..16].SequenceEqual("VP8X"u8) && (header[20] & 0x02) != 0;

    /// <summary>Largest image handled (250 megapixels, 1 GB in memory as BGRA).</summary>
    public const long MaxPixels = 250_000_000;

    /// <summary>Refuses images too big to edit, before allocating anything.</summary>
    /// <exception cref="InvalidDataException">The image exceeds <see cref="MaxPixels"/>.</exception>
    public static void CheckSize(int width, int height)
    {
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException(Loc.T("App.ImageTooLarge", width, height));
    }

    /// <summary>Loads an image file (first frame of a GIF or an animated WebP) without locking it, as BGRA.</summary>
    /// <exception cref="InvalidDataException">Not a readable image, or too large.</exception>
    public static PixelBuffer Load(string path) => Decode(File.ReadAllBytes(path));

    /// <summary>Decodes an image file in memory (PNG, JPEG, BMP, GIF...), as BGRA; the size is checked before the pixels are read.</summary>
    /// <exception cref="InvalidDataException">Not a readable image, or too large.</exception>
    public static PixelBuffer Decode(byte[] data)
    {
        using var codec = SKCodec.Create(new MemoryStream(data, writable: false)) ?? throw new InvalidDataException(Loc.T("App.ImageUnreadable"));
        var info = codec.Info;
        CheckSize(info.Width, info.Height);
        var buffer = new PixelBuffer(info.Width, info.Height);
        var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = buffer.Pixels)
            {
                var result = codec.GetPixels(target, (IntPtr)p);
                if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    throw new InvalidDataException(Loc.T("App.ImageUnreadable") + $" ({result})");
            }
        }
        return buffer;
    }

    /// <summary>Width and height from the header only; null if it is not a readable image.</summary>
    public static (int Width, int Height)? ReadSize(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var codec = SKCodec.Create(stream);
            return codec == null ? null : (codec.Info.Width, codec.Info.Height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Saves atomically (a temporary file renamed over the target); the format follows the extension
    /// (PNG, JPEG with <paramref name="jpegQuality"/>, WebP with <paramref name="webpQuality"/>, BMP). Keeps the
    /// creation date of the replaced file.
    /// </summary>
    /// <param name="webpQuality"><see cref="WebPLossless"/>, or the quality of a lossy WebP (10 to 99).</param>
    public static void Save(PixelBuffer image, string path, int jpegQuality = 90, int webpQuality = WebPLossless)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        byte[] data = extension switch
        {
            ".jpg" or ".jpeg" => Encode(image, SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 10, 100)),
            ".webp" => EncodeWebP(image, webpQuality),
            ".bmp" => EncodeBmp(image),
            _ => EncodePng(image),
        };
        var full = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(full)!;
        Directory.CreateDirectory(directory);
        DateTime? created = File.Exists(full) ? File.GetCreationTimeUtc(full) : null;
        var temp = Path.Combine(directory, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var stream = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Write(data);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, full, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (Exception) { /* the original error matters more */ }
            throw;
        }
        if (created is DateTime c)
        {
            try { File.SetCreationTimeUtc(full, c); } catch (Exception) { /* not supported everywhere */ }
        }
    }

    public static byte[] EncodePng(PixelBuffer image) => Encode(image, SKEncodedImageFormat.Png, 100);

    /// <summary>
    /// WebP, transparency kept: lossless at <see cref="WebPLossless"/> (exact pixels, usually much smaller than PNG),
    /// else lossy at that quality (10 to 99; the transparency itself stays lossless).
    /// </summary>
    public static byte[] EncodeWebP(PixelBuffer image, int quality = WebPLossless)
    {
        // Lossless: the "quality" is the effort spent compressing; 75 is libwebp's default.
        var options = quality >= WebPLossless
            ? new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossless, 75)
            : new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, Math.Clamp(quality, 10, 99));
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = image.Pixels)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)p, image.Stride);
                using var data = pixmap.Encode(options) ?? throw new InvalidOperationException("Encoding WebP failed");
                return data.ToArray();
            }
        }
    }

    private static byte[] Encode(PixelBuffer image, SKEncodedImageFormat format, int quality)
    {
        // JPEG has no transparency: the pixels are composited on white first.
        var alpha = format == SKEncodedImageFormat.Jpeg ? SKAlphaType.Opaque : SKAlphaType.Unpremul;
        var source = format == SKEncodedImageFormat.Jpeg ? OnWhite(image) : image;
        var info = new SKImageInfo(source.Width, source.Height, SKColorType.Bgra8888, alpha);
        unsafe
        {
            fixed (byte* p = source.Pixels)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)p, source.Stride);
                using var data = pixmap.Encode(format, quality) ?? throw new InvalidOperationException($"Encoding {format} failed");
                return data.ToArray();
            }
        }
    }

    /// <summary>Pixels drawn on a white background (formats without transparency).</summary>
    private static PixelBuffer OnWhite(PixelBuffer image)
    {
        var src = image.Pixels;
        bool opaque = true;
        for (int i = 3; i < src.Length && opaque; i += 4) opaque = src[i] == 255;
        if (opaque) return image;
        var result = new PixelBuffer(image.Width, image.Height);
        var dst = result.Pixels;
        for (int i = 0; i < src.Length; i += 4)
        {
            int a = src[i + 3];
            dst[i] = (byte)((src[i] * a + 255 * (255 - a)) / 255);
            dst[i + 1] = (byte)((src[i + 1] * a + 255 * (255 - a)) / 255);
            dst[i + 2] = (byte)((src[i + 2] * a + 255 * (255 - a)) / 255);
            dst[i + 3] = 255;
        }
        return result;
    }

    /// <summary>24-bit BMP (Skia writes no BMP): bottom-up rows padded to 4 bytes, on white.</summary>
    internal static byte[] EncodeBmp(PixelBuffer image)
    {
        var source = OnWhite(image);
        int rowBytes = (source.Width * 3 + 3) & ~3;
        int pixelBytes = rowBytes * source.Height;
        var data = new byte[54 + pixelBytes];
        void Int(int offset, int value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 4), value);
        void Short(int offset, short value) => BitConverter.TryWriteBytes(data.AsSpan(offset, 2), value);
        data[0] = (byte)'B';
        data[1] = (byte)'M';
        Int(2, data.Length);
        Int(10, 54);
        Int(14, 40);
        Int(18, source.Width);
        Int(22, source.Height);
        Short(26, 1);
        Short(28, 24);
        Int(34, pixelBytes);
        Int(38, 3780); // 96 DPI
        Int(42, 3780);
        for (int y = 0; y < source.Height; y++)
        {
            int src = (source.Height - 1 - y) * source.Stride, dst = 54 + y * rowBytes;
            for (int x = 0; x < source.Width; x++, src += 4, dst += 3)
            {
                data[dst] = source.Pixels[src];
                data[dst + 1] = source.Pixels[src + 1];
                data[dst + 2] = source.Pixels[src + 2];
            }
        }
        return data;
    }

    /// <summary>Resized copy, high quality (QR decoding, thumbnails, GIF frames).</summary>
    public static PixelBuffer Resize(PixelBuffer image, int width, int height)
    {
        width = Math.Max(1, width);
        height = Math.Max(1, height);
        var result = new PixelBuffer(width, height);
        var srcInfo = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        var dstInfo = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* s = image.Pixels)
            fixed (byte* d = result.Pixels)
            {
                using var src = new SKPixmap(srcInfo, (IntPtr)s, image.Stride);
                using var dst = new SKPixmap(dstInfo, (IntPtr)d, result.Stride);
                src.ScalePixels(dst, new SKSamplingOptions(SKCubicResampler.Mitchell));
            }
        }
        return result;
    }
}
