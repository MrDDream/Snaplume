using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Snaplume.Core;

/// <summary>
/// Text recognition with the OCR engine built into Windows: offline, nothing leaves the computer.
/// The available languages are the Windows language packs that include OCR.
/// </summary>
public static class OcrService
{
    public static IReadOnlyList<Language> Languages => OcrEngine.AvailableRecognizerLanguages;

    /// <summary>Language used when none is chosen: the user's Windows languages, else the first installed one.</summary>
    public static Language? DefaultLanguage
    {
        get
        {
            if (OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage is { } language) return language;
            var languages = Languages;
            return languages.Count > 0 ? languages[0] : null;
        }
    }

    /// <summary>Installed OCR language with this tag, or null.</summary>
    public static Language? Find(string? languageTag)
    {
        if (string.IsNullOrEmpty(languageTag)) return null;
        var languages = Languages;
        for (int i = 0; i < languages.Count; i++)
        {
            if (languages[i].LanguageTag == languageTag) return languages[i];
        }
        return null;
    }

    /// <summary>Recognized text, one line per line of text. Throws if no OCR language is installed.</summary>
    public static async Task<string> RecognizeAsync(BitmapSource image, string? languageTag = null)
    {
        var language = Find(languageTag) ?? DefaultLanguage
                       ?? throw new InvalidOperationException(Loc.T("Ocr.NoLanguage"));
        var engine = OcrEngine.TryCreateFromLanguage(language)
                     ?? throw new InvalidOperationException(Loc.T("Ocr.NoLanguage"));

        using var bitmap = ToSoftwareBitmap(Prepare(image));
        var result = await engine.RecognizeAsync(bitmap);
        var lines = result.Lines;
        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) text.Append(Environment.NewLine);
            text.Append(lines[i].Text);
        }
        return text.ToString();
    }

    /// <summary>Small screenshots are enlarged (the engine reads small text better), big ones fitted to its limit.</summary>
    private static BitmapSource Prepare(BitmapSource image)
    {
        double max = OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.PixelWidth, image.PixelHeight);
        double scale = longest * 2 <= max && image.PixelHeight < 1200 ? 2 : Math.Min(1, max / longest);
        if (Math.Abs(scale - 1) < 0.01) return image;
        var scaled = new TransformedBitmap(image, new ScaleTransform(scale, scale));
        scaled.Freeze();
        return scaled;
    }

    /// <summary>Copies the BGRA pixels straight into a Windows bitmap (no encoding round trip).</summary>
    private static SoftwareBitmap ToSoftwareBitmap(BitmapSource image)
    {
        BitmapSource bgra = image.Format == PixelFormats.Bgra32
            ? image
            : new FormatConvertedBitmap(image, PixelFormats.Bgra32, null, 0);
        int width = bgra.PixelWidth, height = bgra.PixelHeight;
        int stride = width * 4;
        var pixels = new byte[(long)stride * height];
        bgra.CopyPixels(pixels, stride, 0);
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, width, height, BitmapAlphaMode.Premultiplied);
    }
}
