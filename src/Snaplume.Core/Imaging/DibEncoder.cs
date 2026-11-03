using System.Buffers.Binary;

namespace Snaplume.Imaging;

/// <summary>
/// Device-independent bitmaps (the Windows CF_DIB clipboard format, also what GDI prints): a BITMAPINFOHEADER followed
/// by the pixels. Pure code so that it can be tested on any system.
/// </summary>
public static class DibEncoder
{
    /// <summary>Size of BITMAPINFOHEADER, where the pixels of a packed DIB start (no colour table at 24 bits).</summary>
    public const int HeaderSize = 40;

    /// <summary>
    /// The image as a packed 24-bit DIB (bottom-up rows padded to 4 bytes), its transparent parts composited onto white:
    /// applications that read the bitmap rather than the PNG ignore alpha and would otherwise show the colour hidden
    /// under it (usually black corners and shadows).
    /// </summary>
    public static byte[] Encode(PixelBuffer image)
    {
        int width = image.Width, height = image.Height;
        int stride = RowSize(width);
        if (HeaderSize + (long)stride * height > Array.MaxLength) throw new ArgumentOutOfRangeException(nameof(image), "Image too large");
        var dib = new byte[HeaderSize + stride * height];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, HeaderSize);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        // Positive height: bottom-up, the form every reader supports.
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteUInt16LittleEndian(header[12..], 1); // planes
        BinaryPrimitives.WriteUInt16LittleEndian(header[14..], 24); // bits per pixel
        // biCompression = BI_RGB (0), biSizeImage, 96 DPI (3780 pixels per metre), no colour table.
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], stride * height);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], 3780);
        BinaryPrimitives.WriteInt32LittleEndian(header[28..], 3780);

        var source = image.Pixels;
        Parallel.For(0, height, y =>
        {
            int from = y * image.Stride;
            int to = HeaderSize + (height - 1 - y) * stride;
            for (int x = 0; x < width; x++, from += 4, to += 3)
            {
                int alpha = source[from + 3];
                if (alpha == 255)
                {
                    dib[to] = source[from];
                    dib[to + 1] = source[from + 1];
                    dib[to + 2] = source[from + 2];
                }
                else
                {
                    // Straight alpha over white: c * a + 255 * (1 - a), rounded.
                    int white = 255 * (255 - alpha);
                    dib[to] = (byte)((source[from] * alpha + white + 127) / 255);
                    dib[to + 1] = (byte)((source[from + 1] * alpha + white + 127) / 255);
                    dib[to + 2] = (byte)((source[from + 2] * alpha + white + 127) / 255);
                }
            }
        });
        return dib;
    }

    /// <summary>Bytes per row of a 24-bit DIB: 3 per pixel, rounded up to a multiple of 4.</summary>
    public static int RowSize(int width) => (width * 3 + 3) & ~3;
}
