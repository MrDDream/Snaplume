using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Snaplume.Imaging;

/// <summary>Conversions between <see cref="PixelBuffer"/> and Avalonia bitmaps (96 DPI: 1 unit = 1 pixel).</summary>
public static class AvaloniaImages
{
    public static readonly Vector Dpi = new(96, 96);

    /// <summary>Rows per parallel chunk when premultiplying a big image, or back.</summary>
    private const int RowsPerChunk = 64;

    /// <summary>
    /// A new Avalonia bitmap with the pixels, in the form Skia draws without converting them at every frame: opaque
    /// when every pixel is (screen copies), premultiplied otherwise. A straight-alpha bitmap would be premultiplied
    /// again each time it is drawn (about 15 ms per frame for a 2560 x 1440 image in software rendering).
    /// </summary>
    public static WriteableBitmap ToBitmap(PixelBuffer image)
    {
        bool opaque = image.IsOpaque();
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), Dpi, PixelFormat.Bgra8888,
            opaque ? AlphaFormat.Opaque : AlphaFormat.Premul);
        using var locked = bitmap.Lock();
        if (opaque)
        {
            for (int y = 0; y < image.Height; y++)
                Marshal.Copy(image.Pixels, y * image.Stride, locked.Address + y * locked.RowBytes, image.Stride);
        }
        else
        {
            var address = locked.Address;
            int rowBytes = locked.RowBytes;
            Parallel.For(0, (image.Height + RowsPerChunk - 1) / RowsPerChunk, chunk =>
            {
                int end = Math.Min(image.Height, (chunk + 1) * RowsPerChunk);
                for (int y = chunk * RowsPerChunk; y < end; y++)
                    Premultiply(image.Pixels.AsSpan(y * image.Stride, image.Stride), address + y * rowBytes);
            });
        }
        return bitmap;
    }

    /// <summary>One row of straight-alpha BGRA to premultiplied, rounded as Skia does.</summary>
    private static unsafe void Premultiply(ReadOnlySpan<byte> source, IntPtr target)
    {
        var p = (byte*)target;
        for (int i = 0; i < source.Length; i += 4)
        {
            int a = source[i + 3];
            if (a == 255)
            {
                p[i] = source[i];
                p[i + 1] = source[i + 1];
                p[i + 2] = source[i + 2];
            }
            else
            {
                p[i] = (byte)((source[i] * a + 127) / 255);
                p[i + 1] = (byte)((source[i + 1] * a + 127) / 255);
                p[i + 2] = (byte)((source[i + 2] * a + 127) / 255);
            }
            p[i + 3] = (byte)a;
        }
    }

    /// <summary>The pixels of an Avalonia bitmap (rendered images, loaded files) as straight-alpha BGRA.</summary>
    public static PixelBuffer ToPixelBuffer(Bitmap bitmap)
    {
        var size = bitmap.PixelSize;
        var image = new PixelBuffer(size.Width, size.Height);
        var handle = GCHandle.Alloc(image.Pixels, GCHandleType.Pinned);
        try
        {
            bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), image.Pixels.Length, image.Stride);
        }
        finally
        {
            handle.Free();
        }
        // Rendered bitmaps are premultiplied: back to straight alpha, as everywhere else.
        if (bitmap.AlphaFormat == AlphaFormat.Premul) Unpremultiply(image);
        if (bitmap.Format is { } format && format == PixelFormat.Rgba8888) SwapRedBlue(image.Pixels);
        return image;
    }

    /// <summary>Premultiplied pixels back to straight alpha, rounded to the nearest value; in parallel bands of rows.</summary>
    internal static void Unpremultiply(PixelBuffer image)
    {
        var p = image.Pixels;
        int stride = image.Stride, height = image.Height;
        Parallel.For(0, (height + RowsPerChunk - 1) / RowsPerChunk, chunk =>
        {
            int end = Math.Min(height, (chunk + 1) * RowsPerChunk) * stride;
            for (int i = chunk * RowsPerChunk * stride; i < end; i += 4)
            {
                int a = p[i + 3];
                if (a is 0 or 255) continue;
                int half = a / 2;
                p[i] = (byte)Math.Min(255, (p[i] * 255 + half) / a);
                p[i + 1] = (byte)Math.Min(255, (p[i + 1] * 255 + half) / a);
                p[i + 2] = (byte)Math.Min(255, (p[i + 2] * 255 + half) / a);
            }
        });
    }

    private static void SwapRedBlue(byte[] p)
    {
        for (int i = 0; i < p.Length; i += 4) (p[i], p[i + 2]) = (p[i + 2], p[i]);
    }
}
