using System.Numerics;
using Snaplume.Imaging;

namespace Snaplume.Platform.Linux.X11;

/// <summary>Turns X server pixels into Snaplume's BGRA buffers. Pure functions, unit-tested on Windows.</summary>
internal static class PixelConversion
{
    /// <summary>Layout of an XImage in ZPixmap format.</summary>
    /// <param name="LsbFirst">Byte order of the image (LSBFirst = 0 in Xlib).</param>
    internal readonly record struct ImageLayout(int Width, int Height, int BytesPerLine, int BitsPerPixel, bool LsbFirst,
        ulong RedMask, ulong GreenMask, ulong BlueMask);

    /// <summary>
    /// Copies an XImage into <paramref name="target"/> at (<paramref name="targetX"/>, <paramref name="targetY"/>),
    /// opaque. The usual 24-bit depth stored in 32 bits, little-endian, is a straight row copy; other masks and
    /// sizes (16-bit, 30-bit "deep colour", big-endian servers) go through the generic path.
    /// </summary>
    public static void Copy(ReadOnlySpan<byte> source, ImageLayout layout, PixelBuffer target, int targetX = 0, int targetY = 0)
    {
        int width = Math.Min(layout.Width, target.Width - targetX);
        int height = Math.Min(layout.Height, target.Height - targetY);
        if (width <= 0 || height <= 0) return;
        var dst = target.Pixels.AsSpan();
        bool direct = layout.BitsPerPixel == 32 && layout.LsbFirst
            && layout.RedMask == 0xFF0000 && layout.GreenMask == 0xFF00 && layout.BlueMask == 0xFF;
        if (direct)
        {
            for (int y = 0; y < height; y++)
            {
                var row = dst.Slice((targetY + y) * target.Stride + targetX * 4, width * 4);
                source.Slice(y * layout.BytesPerLine, width * 4).CopyTo(row);
                for (int i = 3; i < row.Length; i += 4) row[i] = 255;
            }
            return;
        }

        var red = Channel.Of(layout.RedMask);
        var green = Channel.Of(layout.GreenMask);
        var blue = Channel.Of(layout.BlueMask);
        int bytesPerPixel = layout.BitsPerPixel / 8;
        if (bytesPerPixel is < 2 or > 4) throw new NotSupportedException($"{layout.BitsPerPixel} bits per pixel");
        for (int y = 0; y < height; y++)
        {
            var row = source.Slice(y * layout.BytesPerLine);
            int o = (targetY + y) * target.Stride + targetX * 4;
            for (int x = 0; x < width; x++, o += 4)
            {
                ulong pixel = 0;
                var bytes = row.Slice(x * bytesPerPixel, bytesPerPixel);
                for (int b = 0; b < bytesPerPixel; b++)
                    pixel |= (ulong)bytes[layout.LsbFirst ? b : bytesPerPixel - 1 - b] << (8 * b);
                dst[o] = blue.Read(pixel);
                dst[o + 1] = green.Read(pixel);
                dst[o + 2] = red.Read(pixel);
                dst[o + 3] = 255;
            }
        }
    }

    /// <summary>One colour channel of a pixel mask, scaled to 8 bits.</summary>
    private readonly record struct Channel(int Shift, ulong Max)
    {
        public static Channel Of(ulong mask) => mask == 0
            ? new Channel(0, 0)
            : new Channel(BitOperations.TrailingZeroCount(mask), mask >> BitOperations.TrailingZeroCount(mask));

        public byte Read(ulong pixel) => Max == 0 ? (byte)0 : (byte)(((pixel >> Shift) & Max) * 255 / Max);
    }

    /// <summary>
    /// Draws the mouse pointer (XFixes: premultiplied ARGB, one pixel per 32-bit value) over the capture.
    /// </summary>
    /// <param name="left">Position of the top-left pixel of the pointer image in the capture.</param>
    public static void DrawCursor(PixelBuffer target, int left, int top, int width, int height, ReadOnlySpan<uint> argb)
    {
        var dst = target.Pixels;
        for (int y = 0; y < height; y++)
        {
            int ty = top + y;
            if (ty < 0 || ty >= target.Height) continue;
            for (int x = 0; x < width; x++)
            {
                int tx = left + x;
                if (tx < 0 || tx >= target.Width) continue;
                uint p = argb[y * width + x];
                int a = (int)(p >> 24);
                if (a == 0) continue;
                int o = ty * target.Stride + tx * 4;
                int inverse = 255 - a;
                // Premultiplied source over an opaque destination.
                dst[o] = (byte)Math.Min(255, (int)(p & 0xFF) + dst[o] * inverse / 255);
                dst[o + 1] = (byte)Math.Min(255, (int)((p >> 8) & 0xFF) + dst[o + 1] * inverse / 255);
                dst[o + 2] = (byte)Math.Min(255, (int)((p >> 16) & 0xFF) + dst[o + 2] * inverse / 255);
                dst[o + 3] = 255;
            }
        }
    }
}
