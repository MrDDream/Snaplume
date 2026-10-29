using System.Globalization;
using System.Text.Json;
using System.Windows.Markup;

namespace Snaplume.Core;

/// <summary>
/// Interface strings. Each language is an embedded Languages/&lt;code&gt;.json file of "key": "text";
/// a key missing from a translation falls back to English, then French (the reference language).
/// The language is chosen at startup; changing it takes effect on the next launch.
/// </summary>
public static class Loc
{
    /// <summary>Languages of the interface, with the culture used for dates and numbers.</summary>
    public static readonly IReadOnlyList<(string Code, string Name, string Culture)> Languages = new[]
    {
        ("fr", "Français", "fr-FR"),
        ("en", "English", "en-US"),
        ("de", "Deutsch", "de-DE"),
        ("es", "Español", "es-ES"),
        ("it", "Italiano", "it-IT"),
        // Arabic with the Gregorian calendar (ar-SA would show Hijri dates).
        ("ar", "العربية", "ar-AE"),
    };

    private static Dictionary<string, string> _strings = new();
    private static Dictionary<string, string> _english = new();
    private static Dictionary<string, string> _french = new();

    /// <summary>Code of the active language ("fr", "en"...).</summary>
    public static string Code { get; private set; } = "fr";

    public static CultureInfo Culture { get; private set; } = CultureInfo.GetCultureInfo("fr-FR");

    public static bool IsRightToLeft => Code == "ar";

    /// <param name="code">Language code, or empty for the Windows language (English if not translated).</param>
    public static void Initialize(string? code)
    {
        if (string.IsNullOrEmpty(code) || Languages.All(l => l.Code != code)) code = SystemLanguage;
        Code = code;
        Culture = CultureInfo.GetCultureInfo(Languages.First(l => l.Code == code).Culture);
        _french = Read("fr");
        _english = Read("en");
        _strings = code switch
        {
            "fr" => _french,
            "en" => _english,
            _ => Read(code),
        };
    }

    /// <summary>Windows display language if Snaplume speaks it, else English.</summary>
    public static string SystemLanguage
    {
        get
        {
            var system = CultureInfo.InstalledUICulture.TwoLetterISOLanguageName;
            var current = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Languages.Any(l => l.Code == current) ? current
                : Languages.Any(l => l.Code == system) ? system
                : "en";
        }
    }

    public static string T(string key) =>
        _strings.TryGetValue(key, out var text) || _english.TryGetValue(key, out text) || _french.TryGetValue(key, out text)
            ? text
            : key;

    /// <summary>Text with {0}, {1}... placeholders filled in. A malformed translation is shown as is.</summary>
    public static string T(string key, params object?[] args)
    {
        var text = T(key);
        try
        {
            return string.Format(Culture, text, args);
        }
        catch (FormatException)
        {
            return text;
        }
    }

    /// <summary>Count with the singular or plural text (French puts 0 in the singular, the other languages do not).</summary>
    public static string Plural(long count, string oneKey, string manyKey) =>
        T(count == 1 || (count == 0 && Code == "fr") ? oneKey : manyKey, count);

    private static Dictionary<string, string> Read(string code)
    {
        try
        {
            using var stream = typeof(Loc).Assembly.GetManifestResourceStream($"Snaplume.Languages.{code}.json");
            if (stream != null)
                return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new();
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Lecture de la langue {code}", ex);
        }
        return new();
    }
}

/// <summary>XAML: Text="{l:T Editor.Save}" with xmlns:l="clr-namespace:Snaplume.Core".</summary>
[MarkupExtensionReturnType(typeof(string))]
public sealed class TExtension : MarkupExtension
{
    public TExtension()
    {
    }

    public TExtension(string key) => Key = key;

    [ConstructorArgument("key")]
    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.T(Key);
}
