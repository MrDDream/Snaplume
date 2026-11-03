using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Capturing;

/// <summary>Copies an image to the clipboard (captures, the editor): the PNG flavour encoded in the background.</summary>
internal static class ClipboardCopy
{
    /// <summary>
    /// Larger images are not copied: the clipboard copy (bitmap + PNG) is slow and holds a lot of memory, and few
    /// applications accept such images.
    /// </summary>
    public const long MaxPixels = 50_000_000;

    public static bool IsTooLarge(PixelBuffer image) => (long)image.Width * image.Height > MaxPixels;

    /// <summary>Encodes the PNG flavour off the UI thread, then sets the clipboard from the calling (UI) thread.</summary>
    /// <returns>False if the clipboard stayed locked by another application.</returns>
    /// <exception cref="ArgumentException">The image is larger than <see cref="MaxPixels"/>.</exception>
    public static async Task<bool> CopyAsync(IImageClipboard clipboard, PixelBuffer image)
    {
        if (IsTooLarge(image)) throw new ArgumentException($"{image.Width} x {image.Height} px : trop grande pour le presse-papiers", nameof(image));
        var png = await Task.Run(() => ImageIO.EncodePng(image));
        return await clipboard.SetImageAsync(image, png);
    }
}
