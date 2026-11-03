namespace Snaplume.Imaging;

/// <summary>
/// Where an image goes on a printed page: one image per page, 1 pixel = 1/96 inch, reduced to fit inside the margins
/// (never enlarged), keeping its ratio, centred. Units are 1/96 inch unless said otherwise.
/// </summary>
public static class PrintLayout
{
    /// <summary>Margin kept around each image when printing, at least (0.4 inch).</summary>
    public const double MinMargin = 38.4;

    /// <summary>
    /// The margin of every side: the largest unprintable margin of the printer, at least <see cref="MinMargin"/>, and
    /// never more than a quarter of the page.
    /// </summary>
    /// <param name="width">Page width.</param>
    /// <param name="height">Page height.</param>
    /// <param name="left">Unprintable margin on the left (and the others), as the printer reports them.</param>
    public static double Margin(double width, double height, double left, double top, double right, double bottom)
    {
        double margin = Math.Max(MinMargin, Math.Max(Math.Max(left, top), Math.Max(right, bottom)));
        return Math.Min(margin, Math.Min(width, height) / 4);
    }

    /// <summary>The rectangle (left, top, width, height) of the image on a page of <paramref name="pageWidth"/> x <paramref name="pageHeight"/>.</summary>
    public static (double X, double Y, double Width, double Height) Fit(int imageWidth, int imageHeight,
        double pageWidth, double pageHeight, double margin)
    {
        double areaWidth = Math.Max(1, pageWidth - 2 * margin), areaHeight = Math.Max(1, pageHeight - 2 * margin);
        double scale = Math.Min(1, Math.Min(areaWidth / imageWidth, areaHeight / imageHeight));
        double width = imageWidth * scale, height = imageHeight * scale;
        return (margin + (areaWidth - width) / 2, margin + (areaHeight - height) / 2, width, height);
    }
}
