using System.Reflection;
using System.Text;
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
    [InlineData("v\u0661.\u0662.\u0663", false, "")]
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

    [Theory]
    [InlineData(0xC0, 8, true)]
    [InlineData(0xC2, 8, true)]
    [InlineData(0xC3, 8, false)]  // lossless
    [InlineData(0xC9, 8, false)]  // arithmetic coding
    [InlineData(0xC1, 12, false)] // 12-bit samples
    public void Only_jpegs_a_pdf_reader_decodes_are_embedded_as_they_are(int marker, int precision, bool embedded)
    {
        var header = new byte[] { 0xFF, 0xD8, 0xFF, (byte)marker, 0x00, 0x11, (byte)precision, 0x00, 0x15, 0x00, 0x21, 0x03, 0, 0, 0, 0, 0, 0, 0, 0 };
        (int, int, int)? expected = embedded ? (33, 21, 3) : null;
        Assert.Equal(expected, PdfDocumentWriter.JpegInfo(header));
    }

    [Fact]
    public void A_pre_release_is_older_than_its_final_version()
    {
        UpdateChecker.Release Release(string tag)
        {
            Assert.True(UpdateChecker.TryParseVersion(tag, out var version, out var display));
            return new UpdateChecker.Release(version, display, UpdateChecker.ReleasesPage, Array.Empty<ReleaseAsset>());
        }

        Assert.True(Release("1.2.0-beta.1").IsPreRelease);
        Assert.False(Release("1.2.0-beta.1").IsNewerThan(new Version(1, 2, 0), currentIsPreRelease: false));
        Assert.True(Release("1.2.0-beta.1").IsNewerThan(new Version(1, 1, 9), currentIsPreRelease: false));
        Assert.True(Release("1.2.0").IsNewerThan(new Version(1, 2, 0), currentIsPreRelease: true));
        Assert.False(Release("1.2.0").IsNewerThan(new Version(1, 2, 0), currentIsPreRelease: false));
        Assert.False(Release("1.1.0").IsNewerThan(new Version(1, 2, 0), currentIsPreRelease: true));
    }

    [Fact]
    public void CommandLine_full_paths_follow_what_the_options_take()
    {
        var args = new[] { "--delay", "a.png", "--delay", "5", "b.png", "--workspace", "c.png", "--capture" };
        var full = CommandLine.WithFullPaths(args);
        // "--delay" takes a number only: "a.png" is a file, as Parse reads it.
        Assert.Equal(Path.GetFullPath("a.png"), full[1]);
        Assert.Equal("5", full[3]);
        Assert.Equal(Path.GetFullPath("b.png"), full[4]);
        Assert.Equal("c.png", full[6]);
        Assert.Equal("--capture", full[7]);
    }

    [Fact]
    public void Plural_texts_take_more_values()
    {
        var text = Loc.Plural(3, "Workspaces.MovedOne", "Workspaces.MovedMany", "Projet");
        Assert.Contains("3", text);
        Assert.Contains("Projet", text);
    }

    [Fact]
    public void An_invalid_path_is_not_sent_to_the_trash() =>
        Assert.False(FileOps.MoveToTrash(new[] { "bad\0name" }));

    [Fact]
    public void Word_text_loses_the_characters_xml_cannot_hold()
    {
        Assert.Equal("abc\U0001F600d", DocxDocumentWriter.XmlSafe("a\u0001b\uD800c\U0001F600\uDC00d"));
        Assert.Equal("plain", DocxDocumentWriter.XmlSafe("plain"));

        using var folder = new TempFolder();
        var png = folder.File("a.png");
        ImageIO.Save(Gradient(40, 30), png);
        var output = folder.File("doc.docx");
        var options = new DocumentOptions { Kind = DocumentKind.Docx, Title = "Rapport\u0001 \uD800" };
        DocumentGenerator.Generate(new[] { png }, options, output, cancellation: TestContext.Current.CancellationToken);
        using var document = DocumentFormat.OpenXml.Packaging.WordprocessingDocument.Open(output, false);
        Assert.Contains("Rapport", document.MainDocumentPart!.Document!.Body!.InnerText);
        Assert.Empty(Directory.EnumerateFiles(folder.Path, "*.tmp"));
    }

    [Fact]
    public void Word_size_counts_the_pixels_of_a_webp_and_the_temporary_file_is_hidden()
    {
        Assert.Equal(4_000_000, new DocumentImage("a.webp", 1000, 1000, DateTime.Now, 5_000).DocxBytes);
        Assert.Equal(5_000, new DocumentImage("a.png", 1000, 1000, DateTime.Now, 5_000).DocxBytes);

        var output = Path.Combine(Path.GetTempPath(), "report.pdf");
        var temp = DocumentGenerator.TempPathOf(output);
        Assert.Equal(Path.GetDirectoryName(output), Path.GetDirectoryName(temp));
        Assert.StartsWith(".report.pdf.", Path.GetFileName(temp));
        Assert.Equal(temp, DocumentGenerator.TempPathOf(output));
    }

    [Fact]
    public void Bmp_is_a_file_header_and_the_dib()
    {
        var image = new PixelBuffer(3, 2, new byte[]
        {
            10, 20, 30, 255, 200, 100, 50, 128, 0, 0, 0, 0,
            1, 2, 3, 255, 4, 5, 6, 255, 7, 8, 9, 255,
        });
        var bmp = ImageIO.EncodeBmp(image);
        Assert.Equal("BM"u8.ToArray(), bmp[..2]);
        Assert.Equal(bmp.Length, BitConverter.ToInt32(bmp, 2));
        Assert.Equal(54, BitConverter.ToInt32(bmp, 10));
        Assert.Equal(DibEncoder.Encode(image), bmp[14..]);
        var back = ImageIO.Decode(bmp);
        Assert.Equal((3, 2), (back.Width, back.Height));
        Assert.Equal(new byte[] { 255, 255, 255, 255 }, back.Pixels[8..12]);
        Assert.False(image.IsOpaque());
        Assert.True(back.IsOpaque());
    }

    [Fact]
    public void Long_words_are_cut_between_whole_characters()
    {
        var word = string.Concat(Enumerable.Repeat("\U0001F600e\u0301", 120));
        var (path, height) = TextOutline.Layout(word, 10, true, 0, 0, 60, TextAlign.Start, 3, false);
        Assert.False(path.IsEmpty);
        Assert.InRange(height, 20, 60); // three lines
    }
}
