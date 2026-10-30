using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace Snaplume.Imaging;

/// <summary>Conversions between <see cref="PixelBuffer"/> and Avalonia bitmaps (96 DPI: 1 unit = 1 pixel).</summary>
public static class AvaloniaImages
{
    public static readonly Vector Dpi = new(96, 96);

    /// <summary>A new Avalonia bitmap with the pixels (straight alpha, converted by Avalonia as needed).</summary>
    public static WriteableBitmap ToBitmap(PixelBuffer image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), Dpi, PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var locked = bitmap.Lock();
        for (int y = 0; y < image.Height; y++)
            Marshal.Copy(image.Pixels, y * image.Stride, locked.Address + y * locked.RowBytes, image.Stride);
        return bitmap;
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
        if (bitmap.AlphaFormat == AlphaFormat.Premul) Unpremultiply(image.Pixels);
        if (bitmap.Format is { } format && format == PixelFormat.Rgba8888) SwapRedBlue(image.Pixels);
        return image;
    }

    private static void Unpremultiply(byte[] p)
    {
        for (int i = 0; i < p.Length; i += 4)
        {
            int a = p[i + 3];
            if (a is 0 or 255) continue;
            p[i] = (byte)Math.Min(255, p[i] * 255 / a);
            p[i + 1] = (byte)Math.Min(255, p[i + 1] * 255 / a);
            p[i + 2] = (byte)Math.Min(255, p[i + 2] * 255 / a);
        }
    }

    private static void SwapRedBlue(byte[] p)
    {
        for (int i = 0; i < p.Length; i += 4) (p[i], p[i + 2]) = (p[i + 2], p[i]);
    }
}
