using System.Buffers.Binary;
using System.IO.Compression;
using System.Reflection;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Export;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>WebP captures: saved lossless or lossy with their transparency, static ones are images, animated ones are not.</summary>
[Collection(ApplicationState.Name)]
public class WebPTests
{
    static WebPTests() => Loc.Initialize("fr");

    /// <summary>Colors that change everywhere, and an alpha going from transparent to opaque (0 in the first column).</summary>
    private static PixelBuffer Translucent(int width, int height)
    {
        var image = new PixelBuffer(width, height);
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
        {
            int i = y * image.Stride + x * 4;
            image.Pixels[i] = (byte)(x * 255 / (width - 1));
            image.Pixels[i + 1] = (byte)(y * 255 / (height - 1));
            image.Pixels[i + 2] = (byte)((x + y) % 256);
            image.Pixels[i + 3] = (byte)(x * 255 / (width - 1));
        }
        return image;
    }

    [Fact]
    public void Lossless_webp_keeps_every_pixel_and_the_transparency()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.webp");
        var image = Translucent(64, 40);
        ImageIO.Save(image, path);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(File.ReadAllBytes(path), 0, 4));
        var read = ImageIO.Load(path);
        Assert.Equal((64, 40), (read.Width, read.Height));
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            Assert.Equal(image.Pixels[i + 3], read.Pixels[i + 3]);
            // Fully transparent pixels have no color to keep.
            if (image.Pixels[i + 3] == 0) continue;
            for (int c = 0; c < 3; c++) Assert.Equal(image.Pixels[i + c], read.Pixels[i + c]);
        }
        Assert.True(ImageIO.IsImage(path));
        Assert.False(ImageIO.IsAnimation(path));
    }

    [Fact]
    public void Lossy_webp_is_close_keeps_the_transparency_and_is_smaller()
    {
        using var folder = new TempFolder();
        var lossless = folder.File("a.webp");
        var lossy = folder.File("b.webp");
        var image = Noise(200, 120);
        for (int i = 3; i < image.Pixels.Length; i += 4) image.Pixels[i] = (byte)(i / 4 % 200 < 100 ? 255 : 0);
        ImageIO.Save(image, lossless);
        ImageIO.Save(image, lossy, webpQuality: 60);
        Assert.True(new FileInfo(lossy).Length < new FileInfo(lossless).Length);

        var smooth = Translucent(64, 40);
        ImageIO.Save(smooth, lossy, webpQuality: 90);
        var read = ImageIO.Load(lossy);
        for (int i = 0; i < smooth.Pixels.Length; i += 4)
        {
            // The transparency of a lossy WebP is compressed apart, without loss.
            Assert.InRange(read.Pixels[i + 3], smooth.Pixels[i + 3] - 2, smooth.Pixels[i + 3] + 2);
        }
        int center = (20 * 64 + 50) * 4;
        for (int c = 0; c < 3; c++) Assert.InRange(read.Pixels[center + c], smooth.Pixels[center + c] - 24, smooth.Pixels[center + c] + 24);
    }

    [Fact]
    public void Static_webp_is_an_image_animated_webp_is_an_animation()
    {
        using var folder = new TempFolder();
        var still = folder.File("still.webp");
        var animated = folder.File("animated.webp");
        ImageIO.Save(Solid(30, 20, Colors.SteelBlue), still);
        // Lossy with transparency: a "VP8X" header too, without the animation flag.
        var lossyAlpha = folder.File("alpha.webp");
        ImageIO.Save(Translucent(30, 20), lossyAlpha, webpQuality: 80);
        File.WriteAllBytes(animated, AnimatedWebP(Solid(30, 20, Colors.Red), Solid(30, 20, Colors.Blue)));

        Assert.True(ImageIO.IsImage(still));
        Assert.True(ImageIO.IsImage(lossyAlpha));
        Assert.False(ImageIO.IsAnimation(lossyAlpha));
        Assert.True(ImageIO.IsAnimation(animated));
        Assert.False(ImageIO.IsImage(animated));
        // Both are shown in the workspace, by their extension only.
        Assert.True(ImageIO.IsCapture(still) && ImageIO.IsCapture(animated) && ImageIO.IsCapture(folder.File("not-yet.webp")));
        // Its first frame is its thumbnail and what a document shows.
        Assert.Equal((30, 20), ImageIO.ReadSize(animated));
        Assert.True(Near(At(ImageIO.Load(animated), 5, 5), Colors.Red));

        // The answer follows the file: replaced by a still image, it becomes an image.
        File.Copy(still, animated, overwrite: true);
        File.SetLastWriteTimeUtc(animated, DateTime.UtcNow.AddMinutes(1));
        Assert.True(ImageIO.IsImage(animated));
    }

    [Fact]
    public void Settings_keep_webp_and_its_extension()
    {
        var settings = new AppSettings { ImageFormat = "webp", WebpQuality = 500 };
        settings.Sanitize();
        Assert.Equal(("webp", ".webp", 100), (settings.ImageFormat, settings.ImageExtension, settings.WebpQuality));
        settings.ImageFormat = "tiff";
        settings.Sanitize();
        Assert.Equal(".png", settings.ImageExtension);
    }

    [Fact]
    public void Word_document_gets_a_png_of_a_webp_capture()
    {
        using var folder = new TempFolder();
        var capture = folder.File("capture.webp");
        ImageIO.Save(Solid(40, 30, Colors.Orange), capture);
        var docx = folder.File("out.docx");
        DocumentGenerator.Generate(new[] { capture }, new DocumentOptions { Kind = DocumentKind.Docx }, docx,
            cancellation: TestContext.Current.CancellationToken);
        using var zip = ZipFile.OpenRead(docx);
        var media = Assert.Single(zip.Entries, e => e.FullName.Contains("media/", StringComparison.Ordinal));
        using var stream = media.Open();
        var header = new byte[8];
        stream.ReadExactly(header);
        Assert.Equal(new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' }, header[..4]);
    }

    [AvaloniaFact]
    public void Editor_exports_webp_and_keeps_the_annotations_of_a_webp_capture()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.webp");
        ImageIO.Save(Solid(200, 120, Color.FromRgb(0xDD, 0xE6, 0xF0)), path);

        // Annotations of a WebP capture: written next to it, read back.
        var document = EditorDocument.Load(path);
        document.PushUndo();
        document.Annotations.Add(new RectangleAnnotation { Start = new(20, 20), End = new(100, 80), Color = Colors.Red, Filled = true });
        document.Save();
        var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
        Assert.True(File.Exists(basePath) && File.Exists(jsonPath));
        Assert.EndsWith("capture.webp.json", jsonPath);
        Assert.True(Near(At(ImageIO.Load(path), 60, 50), Colors.Red));
        var reopened = EditorDocument.Load(path);
        Assert.Single(reopened.Annotations);
        Assert.True(Near(At(reopened.BaseImage.Pixels, 60, 50), Color.FromRgb(0xDD, 0xE6, 0xF0)));

        // "Export" from the editor window to a .webp file.
        var window = new EditorWindow(path) { Width = 900, Height = 600 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            var export = folder.File("export.webp");
            typeof(EditorWindow).GetMethod("ExportTo", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { export });
            Assert.Equal("WEBP", Encoding.ASCII.GetString(File.ReadAllBytes(export), 8, 4));
            var exported = ImageIO.Load(export);
            Assert.Equal((200, 120), (exported.Width, exported.Height));
            Assert.True(Near(At(exported, 60, 50), Colors.Red));
            Assert.True(Near(At(exported, 150, 100), Color.FromRgb(0xDD, 0xE6, 0xF0)));
        }
        finally
        {
            typeof(EditorWindow).GetField("_closeConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(window, true);
            window.Close();
        }
    }

    // ---- An animated WebP, written by hand (Skia only writes still ones) ----

    /// <summary>"RIFF" + "WEBP" + VP8X (animation flag) + ANIM + one ANMF per frame, each holding a lossless frame.</summary>
    private static byte[] AnimatedWebP(params PixelBuffer[] frames)
    {
        int width = frames[0].Width, height = frames[0].Height;
        var body = new MemoryStream();
        body.Write("WEBP"u8);
        var vp8x = new byte[10];
        vp8x[0] = 0x02; // animation
        Write24(vp8x, 4, width - 1);
        Write24(vp8x, 7, height - 1);
        Chunk(body, "VP8X", vp8x);
        Chunk(body, "ANIM", new byte[6]); // background color, loop forever
        foreach (var frame in frames)
        {
            var still = ImageIO.EncodeWebP(frame);
            // A still lossless WebP is "RIFF" size "WEBP" then its "VP8L" chunk: the frame data.
            var bitstream = still.AsSpan(12).ToArray();
            var header = new byte[16];
            Write24(header, 6, width - 1);
            Write24(header, 9, height - 1);
            Write24(header, 12, 100); // 100 ms
            header[15] = 0x02; // no blending
            Chunk(body, "ANMF", header.Concat(bitstream).ToArray());
        }
        var file = new MemoryStream();
        file.Write("RIFF"u8);
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, (int)body.Length);
        file.Write(size);
        body.Position = 0;
        body.CopyTo(file);
        return file.ToArray();
    }

    private static void Chunk(Stream stream, string fourCc, byte[] data)
    {
        stream.Write(Encoding.ASCII.GetBytes(fourCc));
        var size = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(size, data.Length);
        stream.Write(size);
        stream.Write(data);
        if (data.Length % 2 == 1) stream.WriteByte(0);
    }

    private static void Write24(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
    }
}
