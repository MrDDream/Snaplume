using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;
using ZXing;
using ZXing.Common;

namespace Snaplume.Qr;

/// <summary>A code read in an image.</summary>
/// <param name="Format">Symbology, e.g. "QR_CODE", "EAN_13".</param>
public sealed record QrCode(string Format, string Text);

/// <summary>Reads QR codes and barcodes in images, offline (ZXing.Net).</summary>
public static class QrService
{
    /// <summary>Images smaller than this (largest side) are also tried enlarged: small on-screen codes then read better.</summary>
    private const int UpscaleBelow = 1200;

    /// <summary>Larger images are scaled down before decoding (e.g. a capture of several 4K monitors).</summary>
    private const long MaxDecodePixels = 16_000_000;

    private static readonly BarcodeFormat[] Formats =
    {
        BarcodeFormat.QR_CODE, BarcodeFormat.DATA_MATRIX, BarcodeFormat.AZTEC, BarcodeFormat.PDF_417,
        BarcodeFormat.EAN_13, BarcodeFormat.EAN_8, BarcodeFormat.UPC_A, BarcodeFormat.UPC_E,
        BarcodeFormat.CODE_128, BarcodeFormat.CODE_39, BarcodeFormat.CODE_93, BarcodeFormat.CODABAR, BarcodeFormat.ITF,
    };

    /// <summary>Shows the window with the codes read in an image file (gallery, editor).</summary>
    /// <returns>The window, or null if the file could not be read (the error is shown).</returns>
    public static QrWindow? ShowForFile(string path)
    {
        try
        {
            return Show(ImageIO.Load(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("QR code", ex);
            MessageBox.Show(ex.Message, "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Shows the window with the codes read in the image (the reading runs in the background).</summary>
    public static QrWindow Show(BitmapSource image)
    {
        var window = new QrWindow(image);
        window.Show();
        window.Activate();
        return window;
    }

    /// <summary>Reads every code of a BGRA image (stride = width * 4).</summary>
    /// <param name="cancel">Checked between the decoding passes (one pass cannot be interrupted).</param>
    public static IReadOnlyList<QrCode> Decode(byte[] bgra, int width, int height, CancellationToken cancel = default)
    {
        var codes = DecodeOnce(bgra, width, height);
        if (codes.Count == 0 && Math.Max(width, height) < UpscaleBelow)
        {
            cancel.ThrowIfCancellationRequested();
            // Codes of a few pixels per module: ZXing reads them better enlarged.
            var (big, bigWidth, bigHeight) = Enlarge(bgra, width, height, 2);
            codes = DecodeOnce(big, bigWidth, bigHeight);
        }
        return codes;
    }

    private static List<QrCode> DecodeOnce(byte[] bgra, int width, int height)
    {
        var reader = new BarcodeReaderGeneric
        {
            AutoRotate = true,
            Options = new DecodingOptions
            {
                TryHarder = true,
                TryInverted = true,
                PossibleFormats = Formats,
            },
        };
        var source = new RGBLuminanceSource(bgra, width, height, RGBLuminanceSource.BitmapFormat.BGRA32);
        var results = reader.DecodeMultiple(source) ?? Array.Empty<Result>();
        // The same code may be found twice (e.g. once inverted).
        return results
            .Where(r => r != null && !string.IsNullOrEmpty(r.Text))
            .Select(r => new QrCode(r.BarcodeFormat.ToString(), r.Text))
            .Distinct()
            .ToList();
    }

    /// <summary>
    /// Pixels of the image as BGRA (call on the image's thread unless it is frozen). Images above
    /// <see cref="MaxDecodePixels"/> are scaled down first: decoding them whole is slow and needs a lot
    /// of memory, and a code readable on screen is still readable at that size.
    /// </summary>
    internal static (byte[] Pixels, int Width, int Height) ToBgra(BitmapSource image)
    {
        long pixelCount = (long)image.PixelWidth * image.PixelHeight;
        if (pixelCount > MaxDecodePixels)
        {
            double scale = Math.Sqrt((double)MaxDecodePixels / pixelCount);
            image = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        }
        BitmapSource bgra = image.Format == PixelFormats.Bgra32 ? image : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int stride = bgra.PixelWidth * 4;
        var pixels = new byte[(long)stride * bgra.PixelHeight];
        bgra.CopyPixels(pixels, stride, 0);
        return (pixels, bgra.PixelWidth, bgra.PixelHeight);
    }

    /// <summary>Nearest-neighbour enlargement: keeps the sharp edges of the modules.</summary>
    private static (byte[] Pixels, int Width, int Height) Enlarge(byte[] bgra, int width, int height, int factor)
    {
        int w = width * factor, h = height * factor, rowBytes = w * 4;
        var result = new byte[(long)rowBytes * h];
        for (int y = 0; y < height; y++)
        {
            int target = y * factor * rowBytes;
            for (int x = 0; x < width; x++)
            {
                int s = (y * width + x) * 4;
                for (int f = 0; f < factor; f++)
                {
                    int t = target + (x * factor + f) * 4;
                    result[t] = bgra[s];
                    result[t + 1] = bgra[s + 1];
                    result[t + 2] = bgra[s + 2];
                    result[t + 3] = bgra[s + 3];
                }
            }
            for (int f = 1; f < factor; f++) Buffer.BlockCopy(result, target, result, target + f * rowBytes, rowBytes);
        }
        return (result, w, h);
    }

    /// <summary>
    /// The text as a web address if it is exactly one http or https URL (no other scheme is ever opened).
    /// Addresses made to mislead are refused: a user name before the host ("https://bank.com@evil.example")
    /// and invisible formatting characters (e.g. U+202E, which reverses the text shown).
    /// </summary>
    public static Uri? AsWebLink(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.Length == 0 || trimmed.Any(c => char.IsWhiteSpace(c) || char.IsControl(c)
                                                    || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format))
            return null;
        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
               && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
               && !string.IsNullOrEmpty(uri.Host)
               && uri.UserInfo.Length == 0
            ? uri
            : null;
    }

    /// <summary>
    /// Host of a link as the browser reaches it (ASCII, "xn--" for international names), followed by
    /// its Unicode form when different: look-alike letters of another alphabet then show.
    /// </summary>
    public static string DisplayHost(Uri link)
    {
        var ascii = link.IdnHost;
        var unicode = link.Host;
        return string.Equals(ascii, unicode, StringComparison.OrdinalIgnoreCase) ? ascii : $"{ascii} ({unicode})";
    }
}
