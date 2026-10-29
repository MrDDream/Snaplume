using System.Globalization;
using System.Net;
using System.Text;

namespace Snaplume.Export;

/// <summary>
/// One self-contained HTML file: the images are embedded (base64), streamed from their files
/// in chunks so that a large capture is never held in memory as a whole.
/// </summary>
internal static class HtmlDocumentWriter
{
    /// <summary>Multiple of 3, so that each chunk encodes to base64 without padding in the middle.</summary>
    private const int ChunkSize = 3 * 16 * 1024;

    public static void Write(IReadOnlyList<DocumentImage> images, DocumentOptions options, string path,
        IProgress<int>? progress, CancellationToken cancellation)
    {
        using var writer = new StreamWriter(path, append: false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        var title = options.Title.Trim();
        bool majorityLandscape = DocumentGenerator.MajorityLandscape(images);
        bool caption = DocumentGenerator.HasCaption(options);

        writer.Write("<!DOCTYPE html>\n<html lang=\"");
        writer.Write(Encode(options.Language));
        writer.Write("\">\n<head>\n<meta charset=\"utf-8\">\n<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">\n");
        writer.Write("<meta name=\"generator\" content=\"Snaplume\">\n<title>");
        writer.Write(Encode(title.Length > 0 ? title : "Snaplume"));
        writer.Write("</title>\n<style>\n");
        writer.Write(Css(options, majorityLandscape));
        writer.Write("</style>\n</head>\n<body>\n<main>\n");

        if (title.Length > 0 || options.IncludeDate)
        {
            writer.Write("<header>\n");
            if (title.Length > 0) writer.Write($"<h1 dir=\"auto\">{Encode(title)}</h1>\n");
            if (options.IncludeDate)
                writer.Write($"<p dir=\"auto\">{Encode(DocumentGenerator.FormatDate(options, DateTime.Now, longFormat: true))}</p>\n");
            writer.Write("</header>\n");
        }

        var buffer = new byte[ChunkSize];
        for (int i = 0; i < images.Count; i++)
        {
            cancellation.ThrowIfCancellationRequested();
            var image = images[i];
            bool landscape = DocumentGenerator.IsLandscape(options, image, majorityLandscape);
            var classes = landscape ? "landscape" : "portrait";
            if (options.OnePerPage && i < images.Count - 1) classes += " page";

            writer.Write($"<figure class=\"{classes}\">\n<img width=\"{image.PixelWidth}\" height=\"{image.PixelHeight}\" alt=\"");
            writer.Write(Encode(image.FileName));
            writer.Write($"\" src=\"data:{image.MimeType};base64,");
            using (var file = new FileStream(image.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                int read;
                while ((read = ReadFull(file, buffer)) > 0)
                {
                    writer.Write(Convert.ToBase64String(buffer, 0, read));
                    cancellation.ThrowIfCancellationRequested();
                }
            }
            writer.Write("\">\n");
            if (caption) writer.Write($"<figcaption dir=\"auto\">{Encode(DocumentGenerator.Caption(options, image))}</figcaption>\n");
            writer.Write("</figure>\n");
            progress?.Report(i + 1);
        }
        writer.Write("</main>\n</body>\n</html>\n");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);

    /// <summary>Fills the buffer unless the end of the file is reached (keeps chunks a multiple of 3 bytes).</summary>
    private static int ReadFull(Stream stream, byte[] buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>Clean screen style; when printed, the page size and orientations chosen in the dialog.</summary>
    private static string Css(DocumentOptions options, bool majorityLandscape)
    {
        // Named pages: each capture on its own page takes the orientation of its image (Auto),
        // otherwise the whole document has one orientation.
        bool perImage = options.OnePerPage && options.Orientation == DocumentOrientation.Auto;
        bool allLandscape = options.Orientation == DocumentOrientation.Landscape ||
                            (!options.OnePerPage && options.Orientation == DocumentOrientation.Auto && majorityLandscape);
        var pageRule = perImage ? "figure.landscape { page: landscape; }" : allLandscape ? "main { page: landscape; }" : "";
        var size = options.PageSize == DocumentPageSize.Letter ? "letter" : "A4";
        var (portraitWidth, portraitHeight) = DocumentGenerator.PortraitSize(options.PageSize);
        // Printable height of a page in millimetres, minus the caption.
        const double margin = 14;
        double ToMm(double points) => points / 72 * 25.4;
        string Mm(double value) => value.ToString("0.#", CultureInfo.InvariantCulture) + "mm";
        double captionMm = DocumentGenerator.HasCaption(options) ? 12 : 2;
        var portraitMax = Mm(ToMm(portraitHeight) - 2 * margin - captionMm);
        var landscapeMax = Mm(ToMm(portraitWidth) - 2 * margin - captionMm);

        return $$"""
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body { margin: 0; background: #f3f4f6; color: #111827;
                   font-family: "Segoe UI", system-ui, -apple-system, "Helvetica Neue", Arial, sans-serif; line-height: 1.45; }
            main { max-width: 1120px; margin: 0 auto; padding: 40px 24px 56px; }
            header { margin-bottom: 28px; }
            h1 { font-size: 28px; font-weight: 650; margin: 0 0 4px; overflow-wrap: anywhere; }
            header p { margin: 0; color: #6b7280; }
            figure { margin: 0 0 36px; text-align: center; }
            figure img { display: inline-block; max-width: 100%; height: auto; background: #fff;
                         border: 1px solid #d1d5db; border-radius: 2px; box-shadow: 0 1px 4px rgba(17, 24, 39, .08); }
            figcaption { margin-top: 8px; color: #6b7280; font-size: 13px; overflow-wrap: anywhere; }
            @page { size: {{size}} portrait; margin: {{Mm(margin)}}; }
            @page landscape { size: {{size}} landscape; }
            @media print {
              body { background: #fff; }
              main { max-width: none; padding: 0; }
              figure { break-inside: avoid; margin-bottom: 18px; }
              figure.page { break-after: page; }
              figure img { box-shadow: none; }
              figure.portrait img { max-height: {{portraitMax}}; width: auto; }
              figure.landscape img { max-height: {{landscapeMax}}; width: auto; }
              {{pageRule}}
            }

            """;
    }
}
