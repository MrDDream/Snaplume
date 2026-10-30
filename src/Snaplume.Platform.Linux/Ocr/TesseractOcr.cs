using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Ocr;

/// <summary>
/// Text recognition with the Tesseract command-line program. Its language packs (tesseract-ocr-fra...) are
/// named with ISO 639-2 codes; Snaplume shows and stores BCP 47 tags, as on Windows.
/// </summary>
internal sealed class TesseractOcr : IOcrEngine
{
    private readonly Lazy<string?> _tesseract = new(() => ExternalTools.Find("tesseract"));
    private readonly Lazy<IReadOnlyList<(string Code, OcrLanguage Language)>> _languages;

    public TesseractOcr() => _languages = new(ReadLanguages);

    public IReadOnlyList<OcrLanguage> Languages => _languages.Value.Select(l => l.Language).ToList();

    public OcrLanguage? DefaultLanguage => ChooseDefault(_languages.Value, Loc.Code)?.Language;

    public async Task<string> RecognizeAsync(PixelBuffer image, string? languageTag, CancellationToken cancel = default)
    {
        var tesseract = _tesseract.Value ?? throw new InvalidOperationException(Loc.T("Linux.TesseractMissing"));
        var languages = _languages.Value;
        if (languages.Count == 0) throw new InvalidOperationException(Loc.T("Linux.TesseractNoLanguage"));
        var chosen = languages.FirstOrDefault(l => string.Equals(l.Language.Tag, languageTag, StringComparison.OrdinalIgnoreCase));
        var code = chosen.Code ?? ChooseDefault(languages, Loc.Code)!.Value.Code;

        // A private folder: the picture may show anything, other users must not read it.
        var folder = Directory.CreateTempSubdirectory("snaplume-ocr-");
        try
        {
            var input = Path.Combine(folder.FullName, "image.png");
            await File.WriteAllBytesAsync(input, ImageIO.EncodePng(image), cancel).ConfigureAwait(false);
            var result = await ExternalTools.RunAsync(tesseract, new[] { input, "stdout", "-l", code, "--psm", "3" },
                TimeSpan.FromMinutes(2), cancel).ConfigureAwait(false);
            if (!result.Succeeded) throw new InvalidOperationException($"tesseract ({result.ExitCode}): {result.Errors.Trim()}");
            return CleanText(result.Text);
        }
        finally
        {
            try
            {
                folder.Delete(recursive: true);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Dossier temporaire de l'OCR", ex);
            }
        }
    }

    private IReadOnlyList<(string Code, OcrLanguage Language)> ReadLanguages()
    {
        if (_tesseract.Value is not { } tesseract) return Array.Empty<(string, OcrLanguage)>();
        try
        {
            var result = ExternalTools.Run(tesseract, new[] { "--list-langs" }, TimeSpan.FromSeconds(10), environment: ExternalTools.CLocale);
            // Tesseract 3 printed the list on the error output.
            return ParseLanguages(result.Text + "\n" + result.Errors);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Langues de Tesseract", ex);
            return Array.Empty<(string, OcrLanguage)>();
        }
    }

    /// <summary>Codes listed by "tesseract --list-langs", without the script-detection and equation models.</summary>
    internal static IReadOnlyList<(string Code, OcrLanguage Language)> ParseLanguages(string output)
    {
        var result = new List<(string, OcrLanguage)>();
        foreach (var raw in output.Split('\n'))
        {
            var code = raw.Trim();
            if (code.Length == 0 || code.Contains(' ') || code.Contains(':') || code is "osd" or "equ" || code.StartsWith("script/", StringComparison.Ordinal))
                continue;
            if (result.Any(r => r.Item1 == code)) continue;
            result.Add((code, ToLanguage(code)));
        }
        return result.OrderBy(r => r.Item2.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    private static readonly Dictionary<string, (string Tag, string Name)> Known = new()
    {
        ["eng"] = ("en-US", "English"),
        ["fra"] = ("fr-FR", "Français"),
        ["deu"] = ("de-DE", "Deutsch"),
        ["spa"] = ("es-ES", "Español"),
        ["ita"] = ("it-IT", "Italiano"),
        ["ara"] = ("ar", "العربية"),
        ["por"] = ("pt-PT", "Português"),
        ["nld"] = ("nl-NL", "Nederlands"),
        ["pol"] = ("pl-PL", "Polski"),
        ["rus"] = ("ru-RU", "Русский"),
        ["ukr"] = ("uk-UA", "Українська"),
        ["ces"] = ("cs-CZ", "Čeština"),
        ["slk"] = ("sk-SK", "Slovenčina"),
        ["hun"] = ("hu-HU", "Magyar"),
        ["ron"] = ("ro-RO", "Română"),
        ["bul"] = ("bg-BG", "Български"),
        ["ell"] = ("el-GR", "Ελληνικά"),
        ["tur"] = ("tr-TR", "Türkçe"),
        ["swe"] = ("sv-SE", "Svenska"),
        ["dan"] = ("da-DK", "Dansk"),
        ["nor"] = ("nb-NO", "Norsk"),
        ["fin"] = ("fi-FI", "Suomi"),
        ["cat"] = ("ca-ES", "Català"),
        ["heb"] = ("he-IL", "עברית"),
        ["fas"] = ("fa-IR", "فارسی"),
        ["hin"] = ("hi-IN", "हिन्दी"),
        ["jpn"] = ("ja-JP", "日本語"),
        ["kor"] = ("ko-KR", "한국어"),
        ["chi_sim"] = ("zh-Hans", "中文（简体）"),
        ["chi_tra"] = ("zh-Hant", "中文（繁體）"),
        ["vie"] = ("vi-VN", "Tiếng Việt"),
        ["tha"] = ("th-TH", "ไทย"),
        ["ind"] = ("id-ID", "Bahasa Indonesia"),
    };

    /// <summary>Tag and name of a Tesseract language; unknown packs keep their code as both.</summary>
    internal static OcrLanguage ToLanguage(string code) =>
        Known.TryGetValue(code, out var known) ? new OcrLanguage(known.Tag, known.Name) : new OcrLanguage(code, code);

    /// <summary>The interface language when its pack is installed, else English, else the first one.</summary>
    internal static (string Code, OcrLanguage Language)? ChooseDefault(IReadOnlyList<(string Code, OcrLanguage Language)> languages, string uiLanguage)
    {
        if (languages.Count == 0) return null;
        foreach (var (code, language) in languages)
            if (language.Tag.Split('-')[0].Equals(uiLanguage, StringComparison.OrdinalIgnoreCase)) return (code, language);
        foreach (var entry in languages)
            if (entry.Code == "eng") return entry;
        return languages[0];
    }

    /// <summary>Lines as Windows OCR gives them: no form feed, no trailing spaces, no blank lines.</summary>
    internal static string CleanText(string text) =>
        string.Join("\n", text.Replace("\f", "").Replace("\r", "").Split('\n').Select(l => l.TrimEnd()).Where(l => l.Length > 0));
}
