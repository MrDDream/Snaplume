using System.Runtime.InteropServices.WindowsRuntime;
using System.Text;
using Snaplume.Core;
using Snaplume.Imaging;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;

namespace Snaplume.Platform.Windows.Ocr;

/// <summary>
/// Text recognition with the OCR engine built into Windows: offline, nothing leaves the computer.
/// The available languages are the Windows language packs that include OCR.
/// </summary>
public sealed class WindowsOcr : IOcrEngine
{
    public IReadOnlyList<OcrLanguage> Languages
    {
        get
        {
            try
            {
                return OcrEngine.AvailableRecognizerLanguages.Select(ToOcrLanguage).ToList();
            }
            catch (Exception ex)
            {
                // Windows N editions without the media features, a broken language pack...
                ErrorLog.Write("Langues de reconnaissance de texte", ex);
                return Array.Empty<OcrLanguage>();
            }
        }
    }

    /// <summary>The user's Windows languages, else the first installed one.</summary>
    public OcrLanguage? DefaultLanguage => FindDefault() is { } language ? ToOcrLanguage(language) : null;

    /// <summary>Recognized text, one line per line of text.</summary>
    /// <exception cref="InvalidOperationException">No OCR language is installed (message ready to show).</exception>
    public async Task<string> RecognizeAsync(PixelBuffer image, string? languageTag, CancellationToken cancel = default)
    {
        var language = Find(languageTag) ?? FindDefault()
                       ?? throw new InvalidOperationException(Loc.T("Ocr.NoLanguage"));
        var engine = OcrEngine.TryCreateFromLanguage(language)
                     ?? throw new InvalidOperationException(Loc.T("Ocr.NoLanguage"));

        var prepared = await Task.Run(() => Prepare(image), cancel);
        using var bitmap = SoftwareBitmap.CreateCopyFromBuffer(prepared.Pixels.AsBuffer(), BitmapPixelFormat.Bgra8,
            prepared.Width, prepared.Height, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap).AsTask(cancel);
        var lines = result.Lines;
        var text = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) text.Append(Environment.NewLine);
            text.Append(lines[i].Text);
        }
        return text.ToString();
    }

    private static OcrLanguage ToOcrLanguage(Language language) => new(language.LanguageTag, language.DisplayName);

    private static Language? FindDefault()
    {
        try
        {
            if (OcrEngine.TryCreateFromUserProfileLanguages()?.RecognizerLanguage is { } language) return language;
            var languages = OcrEngine.AvailableRecognizerLanguages;
            return languages.Count > 0 ? languages[0] : null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Langues de reconnaissance de texte", ex);
            return null;
        }
    }

    /// <summary>Installed OCR language with this tag, or null.</summary>
    private static Language? Find(string? languageTag)
    {
        if (string.IsNullOrEmpty(languageTag)) return null;
        try
        {
            return OcrEngine.AvailableRecognizerLanguages.FirstOrDefault(l => l.LanguageTag == languageTag);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Small screenshots are enlarged (the engine reads small text better), big ones fitted to its limit.</summary>
    private static PixelBuffer Prepare(PixelBuffer image)
    {
        double max = OcrEngine.MaxImageDimension;
        double longest = Math.Max(image.Width, image.Height);
        double scale = longest * 2 <= max && image.Height < 1200 ? 2 : Math.Min(1, max / longest);
        if (Math.Abs(scale - 1) < 0.01) return image;
        return ImageIO.Resize(image, Math.Max(1, (int)Math.Round(image.Width * scale)), Math.Max(1, (int)Math.Round(image.Height * scale)));
    }
}
