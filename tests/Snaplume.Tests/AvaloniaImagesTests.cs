using Avalonia.Headless.XUnit;
using Avalonia.Platform;
using Snaplume.Imaging;

namespace Snaplume.Tests;

/// <summary>Pixel buffers turned into Avalonia bitmaps in the form Skia draws without converting them at every frame.</summary>
public class AvaloniaImagesTests
{
    [AvaloniaFact]
    public void Opaque_image_gives_an_opaque_bitmap_with_the_same_pixels()
    {
        var image = new PixelBuffer(3, 2, new byte[]
        {
            1, 2, 3, 255, 40, 50, 60, 255, 200, 100, 0, 255,
            9, 8, 7, 255, 0, 0, 0, 255, 255, 255, 255, 255,
        });

        using var bitmap = AvaloniaImages.ToBitmap(image);

        Assert.Equal(AlphaFormat.Opaque, bitmap.AlphaFormat);
        Assert.Equal(image.Pixels, AvaloniaImages.ToPixelBuffer(bitmap).Pixels);
    }

    [AvaloniaFact]
    public void Transparent_image_gives_a_premultiplied_bitmap_read_back_as_straight_alpha()
    {
        // Opaque, half-transparent, fully transparent (its colour is lost), nearly opaque.
        var image = new PixelBuffer(4, 1, new byte[] { 10, 20, 30, 255, 200, 100, 50, 128, 90, 90, 90, 0, 255, 128, 0, 250 });

        using var bitmap = AvaloniaImages.ToBitmap(image);
        var back = AvaloniaImages.ToPixelBuffer(bitmap).Pixels;

        Assert.Equal(AlphaFormat.Premul, bitmap.AlphaFormat);
        Assert.Equal(new byte[] { 10, 20, 30, 255 }, back[..4]);
        Assert.Equal(128, back[7]);
        Assert.Equal(0, back[11]);
        Assert.Equal(250, back[15]);
        // Premultiplied storage keeps the colours of translucent pixels within one step.
        foreach (int i in new[] { 4, 5, 6, 12, 13, 14 }) Assert.InRange(back[i] - image.Pixels[i], -1, 1);
    }

    [Fact]
    public void Premultiplied_pixels_go_back_to_the_nearest_straight_value()
    {
        // 200 rows: several bands, done in parallel.
        var image = new PixelBuffer(2, 200);
        for (int i = 0; i < image.Pixels.Length; i += 8)
        {
            // 100, 50 and 1 out of 128 are 199.2, 99.6 and 2.0 in straight alpha; 200 out of 100 is clamped.
            new byte[] { 100, 50, 1, 128, 200, 0, 0, 100 }.CopyTo(image.Pixels, i);
        }

        AvaloniaImages.Unpremultiply(image);

        for (int i = 0; i < image.Pixels.Length; i += 8)
            Assert.Equal(new byte[] { 199, 100, 2, 128, 255, 0, 0, 100 }, image.Pixels[i..(i + 8)]);
    }
}
