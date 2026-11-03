using System.Runtime.InteropServices;

namespace Snaplume.Imaging;

/// <summary>
/// Image in memory as 32-bit BGRA pixels, rows top to bottom, stride = width * 4, straight (not premultiplied)
/// alpha. The common format between the platform services (screen grabs, video frames, OCR input) and the
/// application, which turns it into an Avalonia bitmap where needed.
/// </summary>
public sealed class PixelBuffer
{
    public PixelBuffer(int width, int height, byte[]? pixels = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "Empty image");
        if ((long)width * height * 4 > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(width), "Image too large");
        Width = width;
        Height = height;
        Pixels = pixels ?? new byte[width * height * 4];
        if (Pixels.Length < width * height * 4) throw new ArgumentException("Buffer too small", nameof(pixels));
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride => Width * 4;
    public byte[] Pixels { get; }

    /// <summary>True when every pixel has an alpha of 255 (no transparency to keep or to composite).</summary>
    public bool IsOpaque()
    {
        var pixels = Pixels.AsSpan(0, Stride * Height);
        // Four bytes at a time where the system allows it: the alpha is the high byte of a little-endian BGRA pixel.
        if (BitConverter.IsLittleEndian)
        {
            foreach (uint pixel in MemoryMarshal.Cast<byte, uint>(pixels))
            {
                if (pixel < 0xFF000000) return false;
            }
            return true;
        }
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255) return false;
        }
        return true;
    }

    /// <summary>Screen copies leave the 4th byte undefined (BGRX): makes every pixel opaque.</summary>
    public PixelBuffer MakeOpaque()
    {
        for (int i = 3; i < Pixels.Length; i += 4) Pixels[i] = 255;
        return this;
    }

    /// <summary>Copy of an area (clamped to the image).</summary>
    public PixelBuffer Crop(int x, int y, int width, int height)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        width = Math.Clamp(width, 1, Width - x);
        height = Math.Clamp(height, 1, Height - y);
        var result = new PixelBuffer(width, height);
        for (int row = 0; row < height; row++)
            Buffer.BlockCopy(Pixels, (y + row) * Stride + x * 4, result.Pixels, row * result.Stride, width * 4);
        return result;
    }
}
