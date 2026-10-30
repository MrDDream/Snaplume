using System.Reflection;
using System.Text;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Export;
using Snaplume.Imaging;
using Snaplume.Qr;

namespace Snaplume.Tests;

public class CoreTests
{
    static CoreTests() => Loc.Initialize("fr");

    [Theory]
    [InlineData("v1.2.3", true, "1.2.3")]
    [InlineData("1.0", true, "1.0.0")]
    [InlineData("v2.0.0-beta.1", true, "2.0.0")]
    [InlineData("v1.2.3<script>", false, "")]
    [InlineData("", false, "")]
    [InlineData("v1.2.3.4", false, "")]
    public void UpdateChecker_parses_release_tags(string tag, bool ok, string expected)
    {
        Assert.Equal(ok, UpdateChecker.TryParseVersion(tag, out var version, out _));
        if (ok) Assert.Equal(expected, version.ToString(3));
    }

    [Theory]
    [InlineData("Ctrl+Shift+PrintScreen")]
    [InlineData("PrintScreen")]
    [InlineData("Alt+F13")]
    [InlineData("Win+A")]
    public void Hotkey_round_trips(string text)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey));
        Assert.Equal(text, hotkey.ToString());
    }

    [Theory]
    [InlineData("Ctrl+44")]
    [InlineData("Ctrl+A, B")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl")]
    public void Hotkey_refuses_invalid_text(string text) => Assert.False(Hotkey.TryParse(text, out _));

    [Fact]
    public void CommandLine_parses_options()
    {
        var line = CommandLine.Parse(new[] { "--capture", "scrolling", "--delay", "99", "--workspace", " Projet ", "--tray" });
        Assert.Equal(CaptureKind.Scrolling, line.Capture);
        Assert.Equal(60, line.DelaySeconds);
        Assert.Equal("Projet", line.Workspace);
        Assert.True(line.Tray);
        Assert.True(CommandLine.Parse(new[] { "--capture", "qr" }).Qr);
    }

    [Fact]
    public void Sanitize_replaces_characters_invalid_on_any_system()
    {
        Assert.Equal("a_b_c_d", FileOps.SanitizeFileName("a:b/c\\d"));
        Assert.Equal("Projet été", FileOps.SanitizeFileName("Projet été"));
    }

    [Fact]
    public void Translations_have_the_same_keys_in_every_language()
    {
        var reference = Keys("fr");
        foreach (var (code, _, _) in Loc.Languages)
        {
            var keys = Keys(code);
            Assert.Empty(reference.Except(keys));
            Assert.Empty(keys.Except(reference));
        }

        static HashSet<string> Keys(string code)
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Snaplume.Languages.{code}.json")!;
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!.Keys.ToHashSet();
        }
    }

    // ---- Images ----

    private static PixelBuffer Gradient(int w, int h, byte alpha = 255)
    {
        var image = new PixelBuffer(w, h);
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = (y * w + x) * 4;
            image.Pixels[i] = (byte)(x * 255 / w);
            image.Pixels[i + 1] = (byte)(y * 255 / h);
            image.Pixels[i + 2] = 128;
            image.Pixels[i + 3] = alpha;
        }
        return image;
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".bmp")]
    [InlineData(".jpg")]
    public void ImageIO_round_trips(string extension)
    {
        var path = Path.Combine(Path.GetTempPath(), "snaplume-test-" + Guid.NewGuid().ToString("N") + extension);
        try
        {
            var image = Gradient(64, 40);
            ImageIO.Save(image, path);
            var read = ImageIO.Load(path);
            Assert.Equal((64, 40), (read.Width, read.Height));
            Assert.Equal((64, 40), ImageIO.ReadSize(path));
            int tolerance = extension == ".jpg" ? 12 : 0;
            int i = (20 * 64 + 30) * 4;
            for (int c = 0; c < 3; c++) Assert.InRange(read.Pixels[i + c], image.Pixels[i + c] - tolerance, image.Pixels[i + c] + tolerance);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ImageIO_refuses_what_is_not_an_image()
    {
        Assert.Throws<InvalidDataException>(() => ImageIO.Decode(Encoding.ASCII.GetBytes("not an image")));
    }

    [Fact]
    public void Qr_code_written_then_read()
    {
        var writer = new ZXing.BarcodeWriterGeneric
        {
            Format = ZXing.BarcodeFormat.QR_CODE,
            Options = new ZXing.Common.EncodingOptions { Width = 200, Height = 200, Margin = 2 },
        };
        var matrix = writer.Encode("https://github.com/MrDDream/Snaplume");
        var image = new PixelBuffer(matrix.Width, matrix.Height);
        for (int y = 0; y < matrix.Height; y++)
        for (int x = 0; x < matrix.Width; x++)
        {
            byte v = matrix[x, y] ? (byte)0 : (byte)255;
            int i = (y * matrix.Width + x) * 4;
            image.Pixels[i] = image.Pixels[i + 1] = image.Pixels[i + 2] = v;
            image.Pixels[i + 3] = 255;
        }
        var codes = QrService.Decode(image, TestContext.Current.CancellationToken);
        Assert.Contains(codes, c => c.Text == "https://github.com/MrDDream/Snaplume" && c.Format == "QR_CODE");
        Assert.NotNull(QrService.AsWebLink(codes[0].Text));
        Assert.Null(QrService.AsWebLink("https://bank.com@evil.example"));
    }

    // ---- Documents ----

    [Fact]
    public void Pdf_with_arabic_title_and_transparent_image()
    {
        var dir = Path.Combine(Path.GetTempPath(), "snaplume-pdf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var png = Path.Combine(dir, "a.png");
            var jpg = Path.Combine(dir, "b.jpg");
            ImageIO.Save(Gradient(300, 200, alpha: 200), png);
            ImageIO.Save(Gradient(400, 300), jpg);
            var output = Path.Combine(dir, "doc.pdf");
            var options = new DocumentOptions { Title = "تقرير الاختبار — Rapport", IncludeDate = true, Culture = System.Globalization.CultureInfo.GetCultureInfo("ar-AE") };
            var images = new[] { png, jpg }.Select(p => DocumentGenerator.Probe(p)!).ToList();
            var write = typeof(DocumentGenerator).Assembly.GetType("Snaplume.Export.PdfDocumentWriter")!
                .GetMethod("Write", BindingFlags.Public | BindingFlags.Static)!;
            write.Invoke(null, new object?[] { images, options, output, null, CancellationToken.None });
            var text = Encoding.Latin1.GetString(File.ReadAllBytes(output));
            Assert.StartsWith("%PDF-1.4", text);
            Assert.Contains("/SMask", text);       // transparency of the PNG kept
            Assert.Contains("/DCTDecode", text);   // JPEG embedded as is
            Assert.Contains("%%EOF", text);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Text_outline_wraps_and_ends_with_ellipsis()
    {
        var (path, height) = TextOutline.Layout(string.Join(" ", Enumerable.Repeat("mot", 200)), 10, false, 0, 0, 100, TextAlign.Start, 2, false);
        Assert.False(path.IsEmpty);
        Assert.InRange(height, 15, 40); // two lines only
        Assert.InRange(path.Bounds.Right, 1, 101);
    }

    [Fact]
    public void Jpeg_header_gives_size_and_components()
    {
        var path = Path.Combine(Path.GetTempPath(), "snaplume-test-" + Guid.NewGuid().ToString("N") + ".jpg");
        try
        {
            ImageIO.Save(Gradient(33, 21), path);
            var info = typeof(DocumentGenerator).Assembly.GetType("Snaplume.Export.PdfDocumentWriter")!
                .GetMethod("JpegInfo", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, new object[] { File.ReadAllBytes(path) });
            Assert.Equal((33, 21, 3), ((int, int, int)?)info);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
