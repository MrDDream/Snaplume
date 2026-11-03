using System.Buffers.Binary;
using Snaplume.Imaging;

namespace Snaplume.Tests;

/// <summary>The pure parts of the Windows clipboard and printing: the bitmap flavour and the page layout.</summary>
public class WindowsImagingTests
{
    [Fact]
    public void Dib_is_a_bottom_up_24_bit_bitmap_with_padded_rows()
    {
        // 3 x 2: rows of 9 bytes padded to 12.
        var image = new PixelBuffer(3, 2);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = (byte)i;
            image.Pixels[i + 1] = (byte)(i + 1);
            image.Pixels[i + 2] = (byte)(i + 2);
            image.Pixels[i + 3] = 255;
        }

        var dib = DibEncoder.Encode(image);

        Assert.Equal(12, DibEncoder.RowSize(3));
        Assert.Equal(DibEncoder.HeaderSize + 12 * 2, dib.Length);
        Assert.Equal(40, BinaryPrimitives.ReadInt32LittleEndian(dib));
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(4)));
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(8)));
        Assert.Equal(24, BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16))); // BI_RGB
        Assert.Equal(24, BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(20)));
        // The image's top row is the bitmap's last one.
        int top = DibEncoder.HeaderSize + 12;
        Assert.Equal(new byte[] { 0, 1, 2, 4, 5, 6, 8, 9, 10 }, dib.AsSpan(top, 9).ToArray());
        int bottom = DibEncoder.HeaderSize;
        Assert.Equal(new byte[] { 12, 13, 14 }, dib.AsSpan(bottom, 3).ToArray());
    }

    [Fact]
    public void Dib_puts_transparent_pixels_on_white()
    {
        // Transparent black, half-transparent pure blue (BGRA), opaque red.
        var image = new PixelBuffer(3, 1, new byte[] { 0, 0, 0, 0, 255, 0, 0, 128, 0, 0, 255, 255 });

        var pixels = DibEncoder.Encode(image).AsSpan(DibEncoder.HeaderSize, 9).ToArray();

        Assert.Equal(new byte[] { 255, 255, 255, 255, 127, 127, 0, 0, 255 }, pixels);
    }

    [Theory]
    // Letter page (816 x 1056), small image: kept at 1 pixel = 1/96 inch, centred.
    [InlineData(400, 300, 816, 1056, 38.4, 208, 378, 400, 300)]
    // Too wide: reduced to the width between the margins, keeping the ratio.
    [InlineData(2000, 1000, 816, 1056, 48, 48, 348, 720, 360)]
    // Too tall.
    [InlineData(500, 2000, 800, 1000, 50, 287.5, 50, 225, 900)]
    public void Print_layout_fits_the_image_inside_the_margins(int imageWidth, int imageHeight, double pageWidth, double pageHeight,
        double margin, double x, double y, double width, double height)
    {
        var rect = PrintLayout.Fit(imageWidth, imageHeight, pageWidth, pageHeight, margin);

        Assert.Equal(x, rect.X, 6);
        Assert.Equal(y, rect.Y, 6);
        Assert.Equal(width, rect.Width, 6);
        Assert.Equal(height, rect.Height, 6);
    }

    [Theory]
    // Printers with small unprintable margins: the minimum margin.
    [InlineData(816, 1056, 6, 6, 6, 6, PrintLayout.MinMargin)]
    // A larger unprintable margin on one side wins (all sides get it, the image stays centred).
    [InlineData(816, 1056, 6, 60, 6, 6, 60)]
    // Never more than a quarter of the page.
    [InlineData(200, 300, 90, 0, 0, 0, 50)]
    public void Print_margin_covers_the_unprintable_area(double width, double height, double left, double top, double right,
        double bottom, double expected)
    {
        Assert.Equal(expected, PrintLayout.Margin(width, height, left, top, right, bottom), 6);
    }
}
