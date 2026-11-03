using SkiaSharp;
using Snaplume.Core;

namespace Snaplume.Imaging;

/// <summary>Image files: reading, writing and the size limit. Uses SkiaSharp, the same on every system.</summary>
public static class ImageIO
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    /// <summary>Videos: recordings, and the WebM exports of the player.</summary>
    public static readonly string[] VideoExtensions = { ".mp4", ".webm" };

    /// <summary>
    /// Animated WebP exported by the player: shown in the workspace (its first frame as thumbnail) and opened in the
    /// default application, but not an image to edit, print or put in a document.
    /// </summary>
    public static readonly string[] AnimationExtensions = { ".webp" };

    public static bool IsAnimation(string path) =>
        AnimationExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Files shown in the workspace: images, videos and animations.</summary>
    public static bool IsCapture(string path) => IsImage(path) || IsVideo(path) || IsAnimation(path);

    /// <summary>Largest image handled (250 megapixels, 1 GB in memory as BGRA).</summary>
    public const long MaxPixels = 250_000_000;

    /// <summary>Refuses images too big to edit, before allocating anything.</summary>
    /// <exception cref="InvalidDataException">The image exceeds <see cref="MaxPixels"/>.</exception>
    public static void CheckSize(int width, int height)
    {
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException(Loc.T("App.ImageTooLarge", width, height));
    }

    /// <summary>Loads an image file (first frame of a GIF) without locking it, as BGRA.</summary>
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
    /// (PNG, JPEG with <paramref name="jpegQuality"/>, BMP). Keeps the creation date of the replaced file.
    /// </summary>
    public static void Save(PixelBuffer image, string path, int jpegQuality = 90)
    {
        var extension = Path.GetExtension(path).ToLowerInvariant();
        byte[] data = extension switch
        {
            ".jpg" or ".jpeg" => Encode(image, SKEncodedImageFormat.Jpeg, Math.Clamp(jpegQuality, 10, 100)),
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
