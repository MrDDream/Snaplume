using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;
using A = DocumentFormat.OpenXml.Drawing;
using DW = DocumentFormat.OpenXml.Drawing.Wordprocessing;
using PIC = DocumentFormat.OpenXml.Drawing.Pictures;

namespace Snaplume.Export;

/// <summary>
/// Word document (.docx) with the Open XML SDK: inline pictures, one section per page when each
/// capture has its own page (so that each page can take the orientation of its image).
/// </summary>
internal static class DocxDocumentWriter
{
    private const long EmuPerPoint = 12700;
    private const int TwipsPerPoint = 20;
    private const string Font = "Segoe UI";
    /// <summary>Room kept for a caption paragraph, and for the title block, in points.</summary>
    private const double CaptionRoom = 30;
    private const double TitleRoom = 70;
    /// <summary>Word needs a little slack, or an image exactly as high as the page moves to the next one.</summary>
    private const double Slack = 14;

    public static void Write(IReadOnlyList<DocumentImage> images, DocumentOptions options, string path,
        IProgress<int>? progress, CancellationToken cancellation)
    {
        // The package keeps every part (so every image) in memory until it is closed: the Open XML SDK
        // reads the package relationships while parts are added, so a write-only (streaming) package
        // is refused ("Cannot retrieve parts of writeonly container"). DocumentGenerator therefore caps
        // the total size of the images of a Word document (MaxDocxImageBytes).
        using var stream = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None);
        using var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document);
        document.PackageProperties.Title = options.Title;
        document.PackageProperties.Creator = "Snaplume";
        document.PackageProperties.Created = DateTime.UtcNow;

        var main = document.AddMainDocumentPart();
        AddStyles(main);
        var body = new Body();
        main.Document = new Document(body);

        bool majorityLandscape = DocumentGenerator.MajorityLandscape(images);
        bool caption = DocumentGenerator.HasCaption(options);
        bool hasTitle = AddTitle(body, options);

        for (int i = 0; i < images.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = images[i];
            bool landscape = DocumentGenerator.IsLandscape(options, image, majorityLandscape);
            var (pageWidth, pageHeight) = DocumentGenerator.PageSize(options, landscape);
            double maxWidth = pageWidth - 2 * DocumentGenerator.Margin;
            double maxHeight = pageHeight - 2 * DocumentGenerator.Margin - Slack - (caption ? CaptionRoom : 0);
            // The first page also holds the title (one per page: the image must stay on it).
            if (i == 0 && hasTitle && options.OnePerPage) maxHeight -= TitleRoom;
            var (width, height) = DocumentGenerator.Fit(image, maxWidth, Math.Max(40, maxHeight));

            var part = main.AddImagePart(PartType(image));
            using (var file = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                part.FeedData(file);

            var picture = new Paragraph(
                new ParagraphProperties(
                    new KeepNext(),
                    new SpacingBetweenLines { Before = "0", After = caption ? "80" : "360" },
                    new Justification { Val = JustificationValues.Center }),
                new Run(new Drawing(Inline(main.GetIdOfPart(part), (uint)(i + 1), image.FileName, width, height))));
            body.Append(picture);

            Paragraph last = picture;
            if (caption)
            {
                last = TextParagraph(DocumentGenerator.Caption(options, image), "Caption", JustificationValues.Center);
                body.Append(last);
            }

            // One per page: each capture ends its own section (next page, with its own orientation).
            if (options.OnePerPage && i < images.Count - 1)
            {
                last.ParagraphProperties ??= new ParagraphProperties();
                last.ParagraphProperties.Append(Section(options, landscape));
            }
            progress?.Report(i + 1);
        }

        // Last section (or the whole document when flowing).
        bool lastLandscape = DocumentGenerator.IsLandscape(options, images[^1], majorityLandscape);
        body.Append(Section(options, lastLandscape));
        cancellation.ThrowIfCancellationRequested();
        main.Document.Save();
    }

    private static PartTypeInfo PartType(DocumentImage image) => image.MimeType switch
    {
        "image/jpeg" => ImagePartType.Jpeg,
        "image/gif" => ImagePartType.Gif,
        "image/bmp" => ImagePartType.Bmp,
        _ => ImagePartType.Png,
    };

    /// <returns>True when a title (or a date) was added.</returns>
    private static bool AddTitle(Body body, DocumentOptions options)
    {
        var title = options.Title.Trim();
        if (title.Length > 0) body.Append(TextParagraph(title, "Title", null));
        if (options.IncludeDate)
            body.Append(TextParagraph(DocumentGenerator.FormatDate(options, DateTime.Now, longFormat: true), "Subtitle", null));
        return title.Length > 0 || options.IncludeDate;
    }

    private static Paragraph TextParagraph(string text, string style, JustificationValues? justification)
    {
        bool rtl = DocumentGenerator.IsRightToLeftText(text);
        var properties = new ParagraphProperties(new ParagraphStyleId { Val = style });
        if (rtl) properties.Append(new BiDi());
        if (justification is { } value) properties.Append(new Justification { Val = value });
        var run = new Run(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
        if (rtl) run.PrependChild(new RunProperties(new RightToLeftText()));
        return new Paragraph(properties, run);
    }

    private static SectionProperties Section(DocumentOptions options, bool landscape)
    {
        var (width, height) = DocumentGenerator.PageSize(options, landscape);
        int margin = (int)Math.Round(DocumentGenerator.Margin * TwipsPerPoint);
        return new SectionProperties(
            new PageSize
            {
                Width = (UInt32Value)(uint)Math.Round(width * TwipsPerPoint),
                Height = (UInt32Value)(uint)Math.Round(height * TwipsPerPoint),
                Orient = landscape ? PageOrientationValues.Landscape : PageOrientationValues.Portrait,
            },
            new PageMargin
            {
                Top = margin,
                Bottom = margin,
                Left = (UInt32Value)(uint)margin,
                Right = (UInt32Value)(uint)margin,
                Header = 360U,
                Footer = 360U,
                Gutter = 0U,
            });
    }

    /// <summary>Picture placed in the text line, sized in EMU (1 point = 12700 EMU).</summary>
    private static DW.Inline Inline(string relationshipId, uint id, string name, double width, double height)
    {
        long cx = (long)Math.Round(width * EmuPerPoint);
        long cy = (long)Math.Round(height * EmuPerPoint);
        return new DW.Inline(
            new DW.Extent { Cx = cx, Cy = cy },
            new DW.EffectExtent { LeftEdge = 0L, TopEdge = 0L, RightEdge = 0L, BottomEdge = 0L },
            new DW.DocProperties { Id = id, Name = $"Picture {id}", Description = name },
            new DW.NonVisualGraphicFrameDrawingProperties(new A.GraphicFrameLocks { NoChangeAspect = true }),
            new A.Graphic(
                new A.GraphicData(
                    new PIC.Picture(
                        new PIC.NonVisualPictureProperties(
                            new PIC.NonVisualDrawingProperties { Id = 0U, Name = name },
                            new PIC.NonVisualPictureDrawingProperties()),
                        new PIC.BlipFill(
                            new A.Blip { Embed = relationshipId },
                            new A.Stretch(new A.FillRectangle())),
                        new PIC.ShapeProperties(
                            new A.Transform2D(
                                new A.Offset { X = 0L, Y = 0L },
                                new A.Extents { Cx = cx, Cy = cy }),
                            new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                            // Thin grey frame: screenshots often have white edges.
                            new A.Outline(new A.SolidFill(new A.RgbColorModelHex { Val = "D1D5DB" })) { Width = 6350 })))
                {
                    Uri = "http://schemas.openxmlformats.org/drawingml/2006/picture",
                }))
        {
            DistanceFromTop = 0U,
            DistanceFromBottom = 0U,
            DistanceFromLeft = 0U,
            DistanceFromRight = 0U,
        };
    }

    /// <summary>Default font and the Title / Subtitle / Caption styles (Word lists them, so they can be restyled).</summary>
    private static void AddStyles(MainDocumentPart main)
    {
        var stylesPart = main.AddNewPart<StyleDefinitionsPart>();
        stylesPart.Styles = new Styles(
            new DocDefaults(
                new RunPropertiesDefault(new RunPropertiesBaseStyle(
                    new RunFonts { Ascii = Font, HighAnsi = Font, ComplexScript = Font, EastAsia = Font },
                    new FontSize { Val = "20" },
                    new FontSizeComplexScript { Val = "20" })),
                new ParagraphPropertiesDefault(new ParagraphPropertiesBaseStyle(
                    new SpacingBetweenLines { After = "120", Line = "264", LineRule = LineSpacingRuleValues.Auto }))),
            new Style(
                new StyleName { Val = "Normal" },
                new PrimaryStyle())
            { Type = StyleValues.Paragraph, StyleId = "Normal", Default = true },
            new Style(
                new StyleName { Val = "Title" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { After = "60" }),
                new StyleRunProperties(
                    new Bold(), new BoldComplexScript(),
                    new Color { Val = "111827" },
                    new FontSize { Val = "40" }, new FontSizeComplexScript { Val = "40" }))
            { Type = StyleValues.Paragraph, StyleId = "Title" },
            new Style(
                new StyleName { Val = "Subtitle" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new KeepNext(), new SpacingBetweenLines { After = "320" }),
                new StyleRunProperties(new Color { Val = "6B7280" }))
            { Type = StyleValues.Paragraph, StyleId = "Subtitle" },
            new Style(
                new StyleName { Val = "caption" },
                new BasedOn { Val = "Normal" },
                new NextParagraphStyle { Val = "Normal" },
                new PrimaryStyle(),
                new StyleParagraphProperties(new SpacingBetweenLines { Before = "0", After = "360" }),
                new StyleRunProperties(
                    new Color { Val = "6B7280" },
                    new FontSize { Val = "18" }, new FontSizeComplexScript { Val = "18" }))
            { Type = StyleValues.Paragraph, StyleId = "Caption" });
        stylesPart.Styles.Save();
    }
}
