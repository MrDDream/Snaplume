using System.Globalization;
using System.IO.Compression;
using System.Text;
using Avalonia.Media;
using SkiaSharp;
using Snaplume.Imaging;

namespace Snaplume.Export;

/// <summary>
/// PDF written directly (PDF 1.4), without a third-party library: each image is written to the file
/// as soon as it is read, so memory does not grow with the number of captures.
/// JPEG files are embedded as they are; other images are decoded and compressed (Flate), with their
/// transparency as a soft mask. Text is shaped by HarfBuzz (right-to-left, font fallback, see <see cref="TextOutline"/>) and
/// stored as vector outlines: it prints sharp in every language but cannot be selected.
/// </summary>
internal static class PdfDocumentWriter
{
    private const double TitleSize = 20;
    private const double DateSize = 10;
    private const double CaptionSize = 9;
    /// <summary>Space between an image and its caption, and room taken by the caption line.</summary>
    private const double CaptionGap = 6;
    private const double CaptionHeight = CaptionGap + CaptionSize * 1.6;
    /// <summary>Rows decoded at a time for non-JPEG images.</summary>
    private const int BandRows = 256;

    private static readonly Color TextColor = Color.FromRgb(0x11, 0x18, 0x27);
    private static readonly Color MutedColor = Color.FromRgb(0x6B, 0x72, 0x80);
    private static readonly Color FrameColor = Color.FromRgb(0xD1, 0xD5, 0xDB);

    public static void Write(IReadOnlyList<DocumentImage> images, DocumentOptions options, string path,
        IProgress<int>? progress, CancellationToken cancellation)
    {
        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
        var pdf = new PdfFile(file);
        int catalog = pdf.Reserve();
        int pages = pdf.Reserve();
        var pageIds = new List<int>();

        bool majorityLandscape = DocumentGenerator.MajorityLandscape(images);
        bool caption = DocumentGenerator.HasCaption(options);
        double captionRoom = caption ? CaptionHeight : 0;

        PageBuilder? page = null;
        double y = 0;
        for (int i = 0; i < images.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = images[i];
            bool landscape = DocumentGenerator.IsLandscape(options, image, majorityLandscape);
            var (pageWidth, pageHeight) = DocumentGenerator.PageSize(options, landscape);
            double contentWidth = pageWidth - 2 * DocumentGenerator.Margin;
            double bottom = pageHeight - DocumentGenerator.Margin;

            // Largest size an image may take: a whole empty page.
            var (width, height) = DocumentGenerator.Fit(image, contentWidth, bottom - DocumentGenerator.Margin - captionRoom);

            if (page == null || options.OnePerPage || y + height + captionRoom > bottom)
            {
                if (page != null) pageIds.Add(page.Finish(pdf, pages));
                page = new PageBuilder(pageWidth, pageHeight);
                y = DocumentGenerator.Margin;
                if (i == 0) y = DrawTitle(page, options, contentWidth, y);
                // Below the title: shrink the image to what is left of the page.
                if (y + height + captionRoom > bottom)
                    (width, height) = DocumentGenerator.Fit(image, contentWidth, Math.Max(40, bottom - y - captionRoom));
            }
            else
            {
                y += DocumentGenerator.Gap;
            }

            double x = DocumentGenerator.Margin + (contentWidth - width) / 2;
            var name = page.AddImage(WriteImage(pdf, image.Path, cancellation));
            page.DrawImage(name, x, y, width, height);
            page.StrokeRectangle(x, y, width, height, FrameColor, 0.5);
            y += height;
            if (caption)
            {
                page.DrawText(DocumentGenerator.Caption(options, image), bold: false, CaptionSize, MutedColor,
                    DocumentGenerator.Margin, y + CaptionGap, contentWidth, TextAlign.Center, maxLines: 1);
                y += CaptionHeight;
            }
            progress?.Report(i + 1);
        }
        if (page != null) pageIds.Add(page.Finish(pdf, pages));
        cancellation.ThrowIfCancellationRequested();

        pdf.Begin(pages);
        pdf.Write($"<< /Type /Pages /Count {pageIds.Count} /Kids [{string.Join(" ", pageIds.Select(id => $"{id} 0 R"))}] >>");
        pdf.End();
        pdf.Begin(catalog);
        pdf.Write($"<< /Type /Catalog /Pages {pages} 0 R >>");
        pdf.End();
        int info = pdf.Reserve();
        pdf.Begin(info);
        var now = DateTime.Now;
        var offset = TimeZoneInfo.Local.GetUtcOffset(now);
        // Gregorian calendar and ASCII digits whatever the culture of the interface (e.g. ar-SA: Um Al-Qura).
        var invariant = CultureInfo.InvariantCulture;
        var date = string.Create(invariant,
            $"D:{now.ToString("yyyyMMddHHmmss", invariant)}{(offset < TimeSpan.Zero ? '-' : '+')}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'");
        pdf.Write($"<< /Title {PdfFile.TextString(options.Title.Trim())} /Creator (Snaplume) /Producer (Snaplume) /CreationDate ({date}) >>");
        pdf.End();
        pdf.Close(catalog, info);
    }

    /// <returns>Top of the free space below the title.</returns>
    private static double DrawTitle(PageBuilder page, DocumentOptions options, double width, double y)
    {
        var title = options.Title.Trim();
        if (title.Length > 0)
            y += page.DrawText(title, bold: true, TitleSize, TextColor, DocumentGenerator.Margin, y, width, TextAlign.Start, maxLines: 3);
        if (options.IncludeDate)
        {
            var date = DocumentGenerator.FormatDate(options, DateTime.Now, longFormat: true);
            y += 2 + page.DrawText(date, bold: false, DateSize, MutedColor, DocumentGenerator.Margin, y + 2, width, TextAlign.Start, maxLines: 1,
                rightToLeft: options.Culture.TextInfo.IsRightToLeft);
        }
        return title.Length > 0 || options.IncludeDate ? y + 16 : y;
    }

    // ---- Images ----

    /// <returns>Object number of the image XObject.</returns>
    private static int WriteImage(PdfFile pdf, string path, CancellationToken cancellation)
    {
        var data = File.ReadAllBytes(path);

        // JPEG (RGB or grey, not CMYK): the file itself is valid PDF image data.
        if (JpegInfo(data) is (int jpegWidth, int jpegHeight, int components) && components is 1 or 3)
        {
            int id = pdf.Reserve();
            pdf.Begin(id);
            var space = components == 1 ? "/DeviceGray" : "/DeviceRGB";
            pdf.Write($"<< /Type /XObject /Subtype /Image /Width {jpegWidth} /Height {jpegHeight} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode /Length {data.Length} >>\nstream\n");
            pdf.CopyFrom(new MemoryStream(data, writable: false));
            pdf.Write("\nendstream");
            pdf.End();
            return id;
        }

        // Anything else: RGB (+ alpha), compressed a band of rows at a time. The colors go straight into the
        // file (their length is written afterwards); only the alpha channel, which compresses far better,
        // waits in memory for its own object.
        var source = ImageIO.Decode(data);
        int width = source.Width, height = source.Height;
        bool alpha = HasAlpha(source);
        int smask = alpha ? pdf.Reserve() : 0;
        int image = pdf.Reserve();
        using var mask = alpha ? new MemoryStream() : null;
        var colorStream = pdf.BeginStream(image,
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode{(alpha ? $" /SMask {smask} 0 R" : "")}");
        using (var colorZip = new ZLibStream(pdf.Output, CompressionLevel.Optimal, leaveOpen: true))
        using (var maskZip = mask != null ? new ZLibStream(mask, CompressionLevel.Optimal, leaveOpen: true) : null)
        {
            var band = source.Pixels;
            var rgb = new byte[width * 3 * Math.Min(BandRows, height)];
            var opacity = new byte[mask != null ? width * Math.Min(BandRows, height) : 0];
            for (int top = 0; top < height; top += BandRows)
            {
                // A huge image takes a while: the dialog's Cancel must not wait for the end of it.
                cancellation.ThrowIfCancellationRequested();
                int rows = Math.Min(BandRows, height - top);
                int pixels = width * rows;
                for (int p = 0, b = top * source.Stride, c = 0; p < pixels; p++, b += 4, c += 3)
                {
                    rgb[c] = band[b + 2];
                    rgb[c + 1] = band[b + 1];
                    rgb[c + 2] = band[b];
                }
                colorZip.Write(rgb, 0, pixels * 3);
                if (maskZip != null)
                {
                    for (int p = 0, b = top * source.Stride + 3; p < pixels; p++, b += 4) opacity[p] = band[b];
                    maskZip.Write(opacity, 0, pixels);
                }
            }
        }
        pdf.EndStream(colorStream);

        if (mask != null)
        {
            pdf.Begin(smask);
            pdf.Write($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceGray /BitsPerComponent 8 /Filter /FlateDecode /Length {mask.Length} >>\nstream\n");
            mask.Position = 0;
            pdf.CopyFrom(mask);
            pdf.Write("\nendstream");
            pdf.End();
        }
        return image;
    }

    /// <summary>Some pixel is not fully opaque. Captures saved by Snaplume are opaque: they get no soft mask.</summary>
    private static bool HasAlpha(PixelBuffer image)
    {
        var pixels = image.Pixels;
        for (int i = 3; i < pixels.Length; i += 4)
        {
            if (pixels[i] != 255) return true;
        }
        return false;
    }

    /// <summary>Size and number of color components of a baseline or progressive JPEG file, from its SOF marker; null if not a JPEG.</summary>
    internal static (int Width, int Height, int Components)? JpegInfo(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int i = 2;
        while (i + 9 < data.Length)
        {
            if (data[i] != 0xFF) return null;
            byte marker = data[i + 1];
            if (marker == 0xFF) { i++; continue; }
            if (marker is 0xD8 or 0x01 || marker is >= 0xD0 and <= 0xD7) { i += 2; continue; }
            int length = data[i + 2] << 8 | data[i + 3];
            // SOF0..SOF15, except DHT (C4), JPG (C8) and DAC (CC).
            if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
            {
                int height = data[i + 5] << 8 | data[i + 6];
                int width = data[i + 7] << 8 | data[i + 8];
                int components = data[i + 9];
                return width > 0 && height > 0 ? (width, height, components) : null;
            }
            if (marker == 0xDA) return null; // image data before any frame header
            i += 2 + length;
        }
        return null;
    }

    // ---- Page content ----

    /// <summary>Content of one page, in points with the origin at the top left (y downwards).</summary>
    private sealed class PageBuilder
    {
        private readonly double _width;
        private readonly double _height;
        private readonly StringBuilder _content = new();
        private readonly List<int> _images = new();

        public PageBuilder(double width, double height)
        {
            _width = width;
            _height = height;
            // Flip the PDF space (origin bottom left, y up) to the layout space.
            _content.Append($"1 0 0 -1 0 {N(height)} cm\n");
        }

        public string AddImage(int id)
        {
            _images.Add(id);
            return "Im" + _images.Count;
        }

        public void DrawImage(string name, double x, double y, double width, double height)
        {
            // The image space is upside down in the flipped page space.
            _content.Append($"q {N(width)} 0 0 {N(-height)} {N(x)} {N(y + height)} cm /{name} Do Q\n");
        }

        public void StrokeRectangle(double x, double y, double width, double height, Color color, double thickness)
        {
            _content.Append($"q {Rgb(color)} RG {N(thickness)} w {N(x)} {N(y)} {N(width)} {N(height)} re S Q\n");
        }

        /// <summary>Text in a box of the given width (wrapped up to maxLines, then "…"), see <see cref="TextOutline"/>.</summary>
        /// <param name="rightToLeft">Lines start at the right edge; by default when the text is mostly right-to-left.</param>
        /// <returns>Height taken.</returns>
        public double DrawText(string text, bool bold, double size, Color color, double x, double y, double width,
            TextAlign alignment, int maxLines, bool? rightToLeft = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            bool rtl = rightToLeft ?? DocumentGenerator.IsRightToLeftText(text);
            var (path, height) = TextOutline.Layout(text, size, bold, x, y, width, alignment, maxLines, rtl);
            using (path)
            {
                _content.Append($"q {Rgb(color)} rg\n");
                AppendPath(path);
                _content.Append("Q\n");
            }
            return height;
        }

        private void AppendPath(SKPath path)
        {
            if (path.IsEmpty) return;
            using var iterator = path.CreateRawIterator();
            var points = new SKPoint[4];
            SKPathVerb verb;
            while ((verb = iterator.Next(points)) != SKPathVerb.Done)
            {
                switch (verb)
                {
                    case SKPathVerb.Move:
                        _content.Append($"{N(points[0].X)} {N(points[0].Y)} m\n");
                        break;
                    case SKPathVerb.Line:
                        LineTo(points[1]);
                        break;
                    case SKPathVerb.Quad:
                        QuadTo(points[0], points[1], points[2]);
                        break;
                    case SKPathVerb.Conic:
                        // Glyph outlines rarely have conics: turned into quadratic curves.
                        foreach (var (control, end, start) in ConicToQuads(points[0], points[1], points[2], iterator.ConicWeight()))
                            QuadTo(start, control, end);
                        break;
                    case SKPathVerb.Cubic:
                        CurveTo(points[1], points[2], points[3]);
                        break;
                    case SKPathVerb.Close:
                        _content.Append("h\n");
                        break;
                }
            }
            // Nonzero winding, like the glyphs themselves: even-odd would punch holes where contours overlap
            // (joined Arabic letters, overlapping glyphs).
            _content.Append("f\n");
        }

        private static IEnumerable<(SKPoint Control, SKPoint End, SKPoint Start)> ConicToQuads(SKPoint p0, SKPoint p1, SKPoint p2, float weight)
        {
            var quads = SKPath.ConvertConicToQuads(p0, p1, p2, weight, 2);
            for (int i = 0; i + 2 < quads.Length; i += 2) yield return (quads[i + 1], quads[i + 2], quads[i]);
        }

        private void LineTo(SKPoint point) => _content.Append($"{N(point.X)} {N(point.Y)} l\n");

        private void CurveTo(SKPoint a, SKPoint b, SKPoint end) =>
            _content.Append($"{N(a.X)} {N(a.Y)} {N(b.X)} {N(b.Y)} {N(end.X)} {N(end.Y)} c\n");

        /// <summary>Quadratic curve as the equivalent cubic one (PDF only has cubic curves).</summary>
        private void QuadTo(SKPoint start, SKPoint control, SKPoint end) =>
            CurveTo(start + new SKPoint((control.X - start.X) * 2f / 3, (control.Y - start.Y) * 2f / 3),
                end + new SKPoint((control.X - end.X) * 2f / 3, (control.Y - end.Y) * 2f / 3), end);

        /// <summary>Writes the content stream and the page object.</summary>
        /// <returns>Object number of the page.</returns>
        public int Finish(PdfFile pdf, int parent)
        {
            var bytes = Encoding.ASCII.GetBytes(_content.ToString());
            using var compressed = new MemoryStream();
            using (var zip = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) zip.Write(bytes);

            int contents = pdf.Reserve();
            pdf.Begin(contents);
            pdf.Write($"<< /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n");
            compressed.Position = 0;
            pdf.CopyFrom(compressed);
            pdf.Write("\nendstream");
            pdf.End();

            int page = pdf.Reserve();
            pdf.Begin(page);
            var xobjects = string.Join(" ", _images.Select((id, i) => $"/Im{i + 1} {id} 0 R"));
            pdf.Write($"<< /Type /Page /Parent {parent} 0 R /MediaBox [0 0 {N(_width)} {N(_height)}] " +
                      $"/Resources << /ProcSet [/PDF /ImageC /ImageB] /XObject << {xobjects} >> >> /Contents {contents} 0 R >>");
            pdf.End();
            return page;
        }

        private static string Rgb(Color color) => $"{N(color.R / 255.0)} {N(color.G / 255.0)} {N(color.B / 255.0)}";
    }

    private static string N(double value) => Math.Round(value, 3).ToString("0.###", CultureInfo.InvariantCulture);

    // ---- File structure ----

    /// <summary>Objects written in sequence, with the cross-reference table at the end.</summary>
    private sealed class PdfFile
    {
        private readonly Stream _stream;
        private readonly List<long> _offsets = new() { 0 };

        public PdfFile(Stream stream)
        {
            _stream = stream;
            // Binary marker: tells transfer tools that the file is not text.
            Write("%PDF-1.4\n%âãÏÓ\n", Encoding.Latin1);
        }

        /// <summary>Reserves an object number (the object may be written later).</summary>
        public int Reserve()
        {
            _offsets.Add(-1);
            return _offsets.Count - 1;
        }

        public void Begin(int id)
        {
            _offsets[id] = _stream.Position;
            Write($"{id} 0 obj\n");
        }

        public void End() => Write("\nendobj\n");

        /// <summary>The file itself, to write the data of a stream begun with <see cref="BeginStream"/>.</summary>
        public Stream Output => _stream;

        /// <summary>
        /// Begins a stream object whose length is not known yet: it refers to an object written by
        /// <see cref="EndStream"/>, so the data can be written straight to the file.
        /// </summary>
        /// <param name="dictionary">Entries of the stream dictionary, without /Length.</param>
        public (int LengthId, long Start) BeginStream(int id, string dictionary)
        {
            int lengthId = Reserve();
            Begin(id);
            Write($"<< {dictionary} /Length {lengthId} 0 R >>\nstream\n");
            return (lengthId, _stream.Position);
        }

        public void EndStream((int LengthId, long Start) stream)
        {
            long length = _stream.Position - stream.Start;
            Write("\nendstream");
            End();
            Begin(stream.LengthId);
            Write(length.ToString(CultureInfo.InvariantCulture));
            End();
        }

        public void Write(string text) => Write(text, Encoding.ASCII);

        private void Write(string text, Encoding encoding) => _stream.Write(encoding.GetBytes(text));

        public void CopyFrom(Stream source) => source.CopyTo(_stream);

        public void Close(int root, int info)
        {
            long xref = _stream.Position;
            var table = new StringBuilder();
            table.Append($"xref\n0 {_offsets.Count}\n0000000000 65535 f \n");
            for (int i = 1; i < _offsets.Count; i++)
            {
                if (_offsets[i] < 0) table.Append("0000000000 65535 f \n"); // reserved, never written
                else table.Append($"{_offsets[i]:D10} 00000 n \n");
            }
            table.Append($"trailer\n<< /Size {_offsets.Count} /Root {root} 0 R /Info {info} 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            Write(table.ToString());
            _stream.Flush();
        }

        /// <summary>PDF text string: UTF-16 with a byte order mark, in hexadecimal (any language).</summary>
        public static string TextString(string text)
        {
            var hex = new StringBuilder("<FEFF");
            foreach (var c in text) hex.Append(((int)c).ToString("X4", CultureInfo.InvariantCulture));
            return hex.Append('>').ToString();
        }
    }
}
