using System.Globalization;
using System.Windows.Media.Imaging;

namespace Snaplume.Export;

public enum DocumentKind
{
    Pdf,
    Docx,
    Html,
}

public enum DocumentPageSize
{
    A4,
    Letter,
}

public enum DocumentOrientation
{
    Portrait,
    Landscape,
    /// <summary>
    /// One capture per page: each page follows its image. Flowing: the whole document
    /// follows the majority of the images.
    /// </summary>
    Auto,
}

/// <summary>Choices of the "Create a document" dialog. Independent of the interface (usable without UI).</summary>
public sealed class DocumentOptions
{
    public DocumentKind Kind { get; set; } = DocumentKind.Pdf;
    /// <summary>Shown at the top of the document; empty for none.</summary>
    public string Title { get; set; } = "";
    /// <summary>True: one capture per page. False: captures follow each other.</summary>
    public bool OnePerPage { get; set; } = true;
    public DocumentPageSize PageSize { get; set; } = DocumentPageSize.A4;
    public DocumentOrientation Orientation { get; set; } = DocumentOrientation.Auto;
    public bool IncludeFileName { get; set; } = true;
    /// <summary>Capture date in each caption, and the creation date under the title.</summary>
    public bool IncludeDate { get; set; }
    /// <summary>Formats the dates.</summary>
    public CultureInfo Culture { get; set; } = CultureInfo.CurrentCulture;
    /// <summary>Language of the HTML document ("fr", "en"...).</summary>
    public string Language { get; set; } = "en";

    public static string ExtensionOf(DocumentKind kind) => kind switch
    {
        DocumentKind.Docx => ".docx",
        DocumentKind.Html => ".html",
        _ => ".pdf",
    };
}

/// <summary>What was written: the captures that could not be read are left out.</summary>
public sealed record DocumentResult(string Path, IReadOnlyList<string> Skipped);

/// <summary>The images are too large, all together, for a Word document (built in memory).</summary>
public sealed class DocumentTooLargeException(long bytes, long maxBytes)
    : IOException($"The images take {bytes} bytes, more than the {maxBytes} accepted for a Word document.")
{
    public long Bytes { get; } = bytes;
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>One capture of the document: only its header is read beforehand, never its pixels.</summary>
internal sealed record DocumentImage(string Path, int PixelWidth, int PixelHeight, DateTime Created, long Bytes)
{
    public bool IsLandscape => PixelWidth > PixelHeight;

    /// <summary>Size at 100 % in points (captures are 96 DPI: 1 pixel = 0.75 point).</summary>
    public double NaturalWidth => PixelWidth * 0.75;
    public double NaturalHeight => PixelHeight * 0.75;

    public string FileName => System.IO.Path.GetFileName(Path);

    public string MimeType => System.IO.Path.GetExtension(Path).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg",
        ".gif" => "image/gif",
        ".bmp" => "image/bmp",
        _ => "image/png",
    };
}

/// <summary>
/// Builds a PDF, Word or HTML document from image files, offline. Pure API (paths + options → file):
/// call it from a background thread. Images are read and written one at a time; only the Word writer
/// keeps the image files in memory until the document is complete (the Open XML package is written on
/// closing, and the SDK cannot write it as a stream): their total size is capped.
/// </summary>
public static class DocumentGenerator
{
    /// <summary>Page margin, in points (about 14 mm).</summary>
    internal const double Margin = 40;
    /// <summary>Space between two captures that follow each other, in points.</summary>
    internal const double Gap = 22;

    /// <summary>Largest total size of the image files of a Word document (they are all held in memory).</summary>
    public const long MaxDocxImageBytes = 512L * 1024 * 1024;

    /// <summary>Writes the document to <paramref name="outputPath"/> (replaced if it exists).</summary>
    /// <param name="progress">Number of captures written so far.</param>
    /// <exception cref="InvalidDataException">None of the files is a readable image.</exception>
    /// <exception cref="DocumentTooLargeException">Word document whose images exceed <see cref="MaxDocxImageBytes"/>.</exception>
    /// <exception cref="OperationCanceledException">Cancelled: nothing is left behind.</exception>
    public static DocumentResult Generate(IReadOnlyList<string> paths, DocumentOptions options, string outputPath,
        IProgress<int>? progress = null, CancellationToken cancellation = default)
    {
        outputPath = Path.GetFullPath(outputPath);
        var images = new List<DocumentImage>(paths.Count);
        var skipped = new List<string>();
        foreach (var path in paths)
        {
            cancellation.ThrowIfCancellationRequested();
            if (Probe(path) is { } image) images.Add(image);
            else skipped.Add(path);
        }
        if (images.Count == 0) throw new InvalidDataException("No readable image.");
        if (options.Kind == DocumentKind.Docx && images.Sum(i => i.Bytes) is var total && total > MaxDocxImageBytes)
            throw new DocumentTooLargeException(total, MaxDocxImageBytes);

        // Written next to the target, then moved: a failure never leaves a half-written document.
        var temp = TempPathOf(outputPath);
        try
        {
            switch (options.Kind)
            {
                case DocumentKind.Docx:
                    DocxDocumentWriter.Write(images, options, temp, progress, cancellation);
                    break;
                case DocumentKind.Html:
                    HtmlDocumentWriter.Write(images, options, temp, progress, cancellation);
                    break;
                default:
                    PdfDocumentWriter.Write(images, options, temp, progress, cancellation);
                    break;
            }
            cancellation.ThrowIfCancellationRequested();
            File.Move(temp, outputPath, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // The original error matters more.
            }
            throw;
        }
        return new DocumentResult(outputPath, skipped);
    }

    /// <summary>
    /// File the document is written to before it is moved to <paramref name="outputPath"/>. Deleted when the
    /// generation fails or is cancelled; the dialog also deletes it if Snaplume quits in the middle.
    /// </summary>
    internal static string TempPathOf(string outputPath) => Path.GetFullPath(outputPath) + ".tmp";

    /// <summary>Size and date of an image, from its header only. Null if it is not a readable image (or too large).</summary>
    private static DocumentImage? Probe(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None);
            var frame = decoder.Frames[0];
            if (frame.PixelWidth <= 0 || frame.PixelHeight <= 0) return null;
            // Same limit as the editor: beyond it, decoding alone would take gigabytes.
            if ((long)frame.PixelWidth * frame.PixelHeight > Snaplume.Core.ImageIO.MaxPixels) return null;
            return new DocumentImage(Path.GetFullPath(path), frame.PixelWidth, frame.PixelHeight, File.GetCreationTime(path), stream.Length);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    // ---- Layout shared by the writers ----

    /// <summary>Portrait page size in points.</summary>
    internal static (double Width, double Height) PortraitSize(DocumentPageSize size) =>
        size == DocumentPageSize.Letter ? (612, 792) : (595.28, 841.89);

    /// <summary>Page size in points for the given orientation.</summary>
    internal static (double Width, double Height) PageSize(DocumentOptions options, bool landscape)
    {
        var (w, h) = PortraitSize(options.PageSize);
        return landscape ? (h, w) : (w, h);
    }

    /// <summary>Orientation of the page holding this image (one per page), or of the whole document (flowing).</summary>
    internal static bool IsLandscape(DocumentOptions options, DocumentImage image, bool majorityLandscape) => options.Orientation switch
    {
        DocumentOrientation.Landscape => true,
        DocumentOrientation.Portrait => false,
        _ => options.OnePerPage ? image.IsLandscape : majorityLandscape,
    };

    internal static bool MajorityLandscape(IReadOnlyList<DocumentImage> images) =>
        images.Count(i => i.IsLandscape) * 2 > images.Count;

    /// <summary>Displayed size: the natural size, reduced to fit the box (never enlarged).</summary>
    internal static (double Width, double Height) Fit(DocumentImage image, double maxWidth, double maxHeight)
    {
        double scale = Math.Min(1, Math.Min(maxWidth / image.NaturalWidth, maxHeight / image.NaturalHeight));
        return (Math.Max(1, image.NaturalWidth * scale), Math.Max(1, image.NaturalHeight * scale));
    }

    internal static bool HasCaption(DocumentOptions options) => options.IncludeFileName || options.IncludeDate;

    internal static string Caption(DocumentOptions options, DocumentImage image)
    {
        var parts = new List<string>(2);
        if (options.IncludeFileName) parts.Add(image.FileName);
        if (options.IncludeDate) parts.Add(FormatDate(options, image.Created, longFormat: false));
        return string.Join("  ·  ", parts);
    }

    internal static string FormatDate(DocumentOptions options, DateTime date, bool longFormat) =>
        date.ToString(longFormat ? "D" : "g", options.Culture);

    /// <summary>True when the first letter with a direction is Hebrew or Arabic.</summary>
    internal static bool IsRightToLeftText(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '֐' and <= 'ࣿ' or >= 'יִ' and <= '﷿' or >= 'ﹰ' and <= '﻿') return true;
            if (char.IsLetter(c)) return false;
        }
        return false;
    }
}
