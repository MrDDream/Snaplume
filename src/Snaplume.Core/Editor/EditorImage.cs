using Avalonia;
using Avalonia.Media.Imaging;
using Snaplume.Imaging;

namespace Snaplume.Editor;

/// <summary>
/// Base image of a document, never modified once created: a change of the image (crop, resize, inserted image)
/// is a new <see cref="EditorImage"/>, so that the undo history can share them. The pixels are kept for the
/// pixel work (saving, pixelating, inserting) and an Avalonia bitmap is made once, on first use, for drawing.
/// </summary>
public sealed class EditorImage
{
    private WriteableBitmap? _bitmap;
    /// <summary>The whole image pixelated, per block size (see <see cref="Pixelated"/>).</summary>
    private readonly Dictionary<int, (PixelBuffer Pixels, WriteableBitmap? Bitmap)> _pixelated = new();

    /// <param name="pixels">Taken over: the caller must not change it afterwards.</param>
    public EditorImage(PixelBuffer pixels) => Pixels = pixels;

    public PixelBuffer Pixels { get; }
    public int Width => Pixels.Width;
    public int Height => Pixels.Height;
    public Rect Bounds => new(0, 0, Width, Height);

    /// <summary>The image for drawing, 96 DPI: one unit is one pixel.</summary>
    public Bitmap Bitmap => _bitmap ??= AvaloniaImages.ToBitmap(Pixels);

    /// <summary>
    /// The whole image pixelated on a grid anchored at its corner, computed once per block size: moving or
    /// resizing a pixelated area only changes its clip, so the blocks never shimmer, and pixelating an already
    /// pixelated area gives the same pixels.
    /// </summary>
    public Bitmap PixelatedBitmap(int block)
    {
        lock (_pixelated)
        {
            var entry = PixelatedEntry(block);
            if (entry.Bitmap != null) return entry.Bitmap;
            var bitmap = AvaloniaImages.ToBitmap(entry.Pixels);
            _pixelated[block] = (entry.Pixels, bitmap);
            return bitmap;
        }
    }

    /// <summary>Pixels of <see cref="PixelatedBitmap"/> (burnt into the base image kept on disk).</summary>
    public PixelBuffer PixelatedPixels(int block)
    {
        lock (_pixelated) return PixelatedEntry(block).Pixels;
    }

    private (PixelBuffer Pixels, WriteableBitmap? Bitmap) PixelatedEntry(int block)
    {
        if (!_pixelated.TryGetValue(block, out var entry))
        {
            entry = (Pixelate(Pixels, Math.Max(1, block)), null);
            _pixelated[block] = entry;
        }
        return entry;
    }

    /// <summary>Each block of the image replaced by its mean color, opaque.</summary>
    internal static PixelBuffer Pixelate(PixelBuffer source, int block)
    {
        var result = new PixelBuffer(source.Width, source.Height);
        var src = source.Pixels;
        var dst = result.Pixels;
        int stride = source.Stride;
        for (int by = 0; by < source.Height; by += block)
        {
            int bh = Math.Min(block, source.Height - by);
            for (int bx = 0; bx < source.Width; bx += block)
            {
                int bw = Math.Min(block, source.Width - bx);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < by + bh; y++)
                {
                    int i = y * stride + bx * 4;
                    for (int x = 0; x < bw; x++, i += 4)
                    {
                        b += src[i];
                        g += src[i + 1];
                        r += src[i + 2];
                    }
                }
                int n = bw * bh;
                byte bb = (byte)(b / n), gg = (byte)(g / n), rr = (byte)(r / n);
                for (int y = by; y < by + bh; y++)
                {
                    int i = y * stride + bx * 4;
                    for (int x = 0; x < bw; x++, i += 4)
                    {
                        dst[i] = bb;
                        dst[i + 1] = gg;
                        dst[i + 2] = rr;
                        dst[i + 3] = 255;
                    }
                }
            }
        }
        return result;
    }
}

/// <summary>
/// Rectangle helpers with the behaviour the editor was written for: a rectangle between two points in any
/// order, and empty results instead of negative sizes (Avalonia's <see cref="Rect"/> keeps what it is given).
/// </summary>
internal static class Rects
{
    /// <summary>The rectangle between two corners, whatever their order.</summary>
    public static Rect Between(Point a, Point b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    /// <summary>Grown on every side (shrunk when negative); null when nothing is left.</summary>
    public static Rect? Inflate(Rect r, double amount)
    {
        double w = r.Width + 2 * amount, h = r.Height + 2 * amount;
        if (w < 0 || h < 0) return null;
        return new Rect(r.X - amount, r.Y - amount, w, h);
    }

    /// <summary>Grown on every side; an amount larger than half the size gives an empty rectangle at the center.</summary>
    public static Rect Grow(Rect r, double amount) =>
        Inflate(r, amount) ?? new Rect(r.Center, r.Center);

    public static Rect Union(Rect a, Rect b)
    {
        double left = Math.Min(a.Left, b.Left), top = Math.Min(a.Top, b.Top);
        return new Rect(left, top, Math.Max(a.Right, b.Right) - left, Math.Max(a.Bottom, b.Bottom) - top);
    }

    public static Rect Union(Rect a, Point p) => Union(a, new Rect(p, p));

    /// <summary>The common part; null when the rectangles do not touch.</summary>
    public static Rect? Intersect(Rect a, Rect b)
    {
        double left = Math.Max(a.Left, b.Left), top = Math.Max(a.Top, b.Top);
        double right = Math.Min(a.Right, b.Right), bottom = Math.Min(a.Bottom, b.Bottom);
        return right < left || bottom < top ? null : new Rect(left, top, right - left, bottom - top);
    }

    /// <summary>True when the rectangles overlap or touch.</summary>
    public static bool Touch(Rect a, Rect b) => Intersect(a, b) != null;

    public static Rect Offset(Rect r, Vector v) => new(r.X + v.X, r.Y + v.Y, r.Width, r.Height);
}
