using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snaplume.Export;

/// <summary>
/// PDF written directly (PDF 1.4), without a third-party library: each image is written to the file
/// as soon as it is read, so memory does not grow with the number of captures.
/// JPEG files are embedded as they are; other images are decoded and compressed (Flate), with their
/// transparency as a soft mask. Text is laid out by WPF (shaping, right-to-left, font fallback) and
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
    private static readonly Typeface RegularFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface BoldFace = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

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
                page.DrawText(DocumentGenerator.Caption(options, image), RegularFace, CaptionSize, MutedColor,
                    DocumentGenerator.Margin, y + CaptionGap, contentWidth, TextAlignment.Center, maxLines: 1, options.Culture);
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
            y += page.DrawText(title, BoldFace, TitleSize, TextColor, DocumentGenerator.Margin, y, width, TextAlignment.Left, maxLines: 3, options.Culture);
        if (options.IncludeDate)
        {
            var date = DocumentGenerator.FormatDate(options, DateTime.Now, longFormat: true);
            y += 2 + page.DrawText(date, RegularFace, DateSize, MutedColor, DocumentGenerator.Margin, y + 2, width, TextAlignment.Left, maxLines: 1,
                options.Culture, rightToLeft: options.Culture.TextInfo.IsRightToLeft);
        }
        return title.Length > 0 || options.IncludeDate ? y + 16 : y;
    }

    // ---- Images ----

    /// <returns>Object number of the image XObject.</returns>
    private static int WriteImage(PdfFile pdf, string path, CancellationToken cancellation)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        var frame = decoder.Frames[0];
        int width = frame.PixelWidth, height = frame.PixelHeight;

        // JPEG (RGB or grey): the file itself is valid PDF image data.
        if (decoder is JpegBitmapDecoder && (frame.Format == PixelFormats.Bgr24 || frame.Format == PixelFormats.Gray8))
        {
            int id = pdf.Reserve();
            pdf.Begin(id);
            var space = frame.Format == PixelFormats.Gray8 ? "/DeviceGray" : "/DeviceRGB";
            pdf.Write($"<< /Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace {space} /BitsPerComponent 8 /Filter /DCTDecode /Length {stream.Length} >>\nstream\n");
            stream.Position = 0;
            pdf.CopyFrom(stream);
            pdf.Write("\nendstream");
            pdf.End();
            return id;
        }

        // Anything else: RGB (+ alpha), decoded a band of rows at a time and compressed. The colors go
        // straight into the file (their length is written afterwards); only the alpha channel, which
        // compresses far better, waits in memory for its own object.
        var source = frame.Format == PixelFormats.Bgra32 ? (BitmapSource)frame : new FormatConvertedBitmap(frame, PixelFormats.Bgra32, null, 0);
        bool alpha = HasAlpha(frame);
        int smask = alpha ? pdf.Reserve() : 0;
        int image = pdf.Reserve();
        using var mask = alpha ? new MemoryStream() : null;
        var colorStream = pdf.BeginStream(image,
            $"/Type /XObject /Subtype /Image /Width {width} /Height {height} /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /FlateDecode{(alpha ? $" /SMask {smask} 0 R" : "")}");
        using (var colorZip = new ZLibStream(pdf.Output, CompressionLevel.Optimal, leaveOpen: true))
        using (var maskZip = mask != null ? new ZLibStream(mask, CompressionLevel.Optimal, leaveOpen: true) : null)
        {
            int stride = width * 4;
            var band = new byte[(long)stride * Math.Min(BandRows, height)];
            var rgb = new byte[width * 3 * Math.Min(BandRows, height)];
            var opacity = new byte[mask != null ? width * Math.Min(BandRows, height) : 0];
            for (int top = 0; top < height; top += BandRows)
            {
                // A huge image takes a while: the dialog's Cancel must not wait for the end of it.
                cancellation.ThrowIfCancellationRequested();
                int rows = Math.Min(BandRows, height - top);
                source.CopyPixels(new Int32Rect(0, top, width, rows), band, stride, 0);
                int pixels = width * rows;
                for (int p = 0, b = 0, c = 0; p < pixels; p++, b += 4, c += 3)
                {
                    rgb[c] = band[b + 2];
                    rgb[c + 1] = band[b + 1];
                    rgb[c + 2] = band[b];
                }
                colorZip.Write(rgb, 0, pixels * 3);
                if (maskZip != null)
                {
                    for (int p = 0, b = 3; p < pixels; p++, b += 4) opacity[p] = band[b];
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

    /// <summary>
    /// The pixel format can hold transparency (an alpha channel, or a palette with a transparent color).
    /// Captures saved by Snaplume are opaque (Bgr24): they get no soft mask.
    /// </summary>
    private static bool HasAlpha(BitmapSource frame)
    {
        var format = frame.Format;
        if (format == PixelFormats.Bgra32 || format == PixelFormats.Pbgra32 || format == PixelFormats.Rgba64 ||
            format == PixelFormats.Prgba64 || format == PixelFormats.Rgba128Float || format == PixelFormats.Prgba128Float)
            return true;
        try
        {
            return frame.Palette?.Colors.Any(c => c.A < 255) == true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return true; // unknown: keep the alpha channel to be safe
        }
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

        /// <summary>Text laid out by WPF in a box of the given width (wrapped up to maxLines, then "…").</summary>
        /// <returns>Height taken.</returns>
        public double DrawText(string text, Typeface face, double size, Color color, double x, double y, double width,
            TextAlignment alignment, int maxLines, CultureInfo culture, bool? rightToLeft = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return 0;
            bool rtl = rightToLeft ?? DocumentGenerator.IsRightToLeftText(text);
            var formatted = new FormattedText(text, culture, rtl ? FlowDirection.RightToLeft : FlowDirection.LeftToRight,
                face, size, Brushes.Black, 1.0)
            {
                MaxTextWidth = width,
                MaxLineCount = maxLines,
                Trimming = TextTrimming.CharacterEllipsis,
                TextAlignment = alignment,
            };
            // The origin is the top left of the box in both directions (right to left, "Left" is the start: the right edge).
            var geometry = formatted.BuildGeometry(new Point(x, y));
            _content.Append($"q {Rgb(color)} rg\n");
            AppendGeometry(geometry);
            _content.Append("Q\n");
            return formatted.Height;
        }

        private void AppendGeometry(Geometry geometry)
        {
            var path = PathGeometry.CreateFromGeometry(geometry);
            if (path.Figures.Any(f => f.Segments.Any(s => s is ArcSegment)))
                path = geometry.GetFlattenedPathGeometry(0.01, ToleranceType.Absolute);
            if (path.Figures.Count == 0) return;
            foreach (var figure in path.Figures)
            {
                var current = figure.StartPoint;
                _content.Append($"{N(current.X)} {N(current.Y)} m\n");
                foreach (var segment in figure.Segments)
                {
                    switch (segment)
                    {
                        case LineSegment line:
                            current = LineTo(line.Point);
                            break;
                        case PolyLineSegment poly:
                            foreach (var point in poly.Points) current = LineTo(point);
                            break;
                        case BezierSegment bezier:
                            current = CurveTo(bezier.Point1, bezier.Point2, bezier.Point3);
                            break;
                        case PolyBezierSegment poly:
                            for (int i = 0; i + 2 < poly.Points.Count; i += 3)
                                current = CurveTo(poly.Points[i], poly.Points[i + 1], poly.Points[i + 2]);
                            break;
                        case QuadraticBezierSegment quad:
                            current = QuadTo(current, quad.Point1, quad.Point2);
                            break;
                        case PolyQuadraticBezierSegment poly:
                            for (int i = 0; i + 1 < poly.Points.Count; i += 2)
                                current = QuadTo(current, poly.Points[i], poly.Points[i + 1]);
                            break;
                    }
                }
                if (figure.IsClosed) _content.Append("h\n");
            }
            // Nonzero winding, like the glyphs themselves: the geometry group built by WPF says "even-odd",
            // which would punch holes where contours overlap (joined Arabic letters, overlapping glyphs).
            _content.Append("f\n");
        }

        private Point LineTo(Point point)
        {
            _content.Append($"{N(point.X)} {N(point.Y)} l\n");
            return point;
        }

        private Point CurveTo(Point a, Point b, Point end)
        {
            _content.Append($"{N(a.X)} {N(a.Y)} {N(b.X)} {N(b.Y)} {N(end.X)} {N(end.Y)} c\n");
            return end;
        }

        /// <summary>Quadratic curve as the equivalent cubic one (PDF only has cubic curves).</summary>
        private Point QuadTo(Point start, Point control, Point end) =>
            CurveTo(start + (control - start) * (2.0 / 3), end + (control - end) * (2.0 / 3), end);

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
