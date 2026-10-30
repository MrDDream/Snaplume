using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snaplume.Tests;

/// <summary>
/// Texts of the application (src/Snaplume.App): every key used exists in French (Languages/fr.json or a
/// Languages/_pending/*.json file), and every translation has the placeholders of the French text.
/// </summary>
public partial class ShellLocalizationTests
{
    private static readonly string[] Languages = { "fr", "en", "de", "es", "it", "ar" };

    private static string RepositoryRoot
    {
        get
        {
            for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
            {
                if (File.Exists(Path.Combine(dir.FullName, "Snaplume.slnx"))) return dir.FullName;
            }
            throw new DirectoryNotFoundException("Snaplume.slnx not found above the test folder");
        }
    }

    private static string LanguagesFolder => Path.Combine(RepositoryRoot, "src", "Snaplume.Core", "Languages");

    [Fact]
    public void TranslationsHaveTheSamePlaceholdersAsFrench()
    {
        var french = ReadLanguage(Path.Combine(LanguagesFolder, "fr.json"));
        foreach (var language in Languages)
        {
            var texts = ReadLanguage(Path.Combine(LanguagesFolder, language + ".json"));
            foreach (var (key, text) in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key} is empty");
                if (french.TryGetValue(key, out var reference))
                    // A translation may leave a value out (Arabic "one image" has no number), never use one French has not.
                    Assert.True(Placeholders(text).All(Placeholders(reference).Contains), $"{language}: {key} uses a placeholder French has not");
            }
        }
    }

    [Fact]
    public void AppTextsExistInFrench()
    {
        var known = new HashSet<string>(ReadLanguage(Path.Combine(LanguagesFolder, "fr.json")).Keys);
        var pendingFolder = Path.Combine(LanguagesFolder, "_pending");
        if (Directory.Exists(pendingFolder))
        {
            foreach (var file in Directory.EnumerateFiles(pendingFolder, "*.json"))
            {
                if (ReadPending(file).TryGetValue("fr", out var french)) known.UnionWith(french.Keys);
            }
        }

        var app = Path.Combine(RepositoryRoot, "src", "Snaplume.App");
        var missing = new List<string>();
        foreach (var file in Directory.EnumerateFiles(app, "*.*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(app, file);
            var top = relative.Split(Path.DirectorySeparatorChar)[0];
            if (top is "bin" or "obj") continue;
            var extension = Path.GetExtension(file);
            if (extension is not (".cs" or ".axaml")) continue;
            foreach (var key in UsedKeys(File.ReadAllLines(file), extension == ".axaml"))
            {
                if (!known.Contains(key)) missing.Add($"{relative}: {key}");
            }
        }
        Assert.Empty(missing);
    }

    /// <summary>Keys of {l:T Key} in XAML, and literal keys on the lines calling Loc.T / Loc.Plural in C#.</summary>
    private static IEnumerable<string> UsedKeys(string[] lines, bool xaml)
    {
        foreach (var line in lines)
        {
            if (xaml)
            {
                foreach (Match match in XamlKey().Matches(line)) yield return match.Groups[1].Value;
            }
            else if (line.Contains("Loc.T(", StringComparison.Ordinal) || line.Contains("Loc.Plural(", StringComparison.Ordinal))
            {
                // Keys built at run time ("Settings.Video.Corner" + corner) are left out.
                foreach (Match match in CodeKey().Matches(line)) yield return match.Groups[1].Value;
            }
        }
    }

    private static string[] Placeholders(string text) =>
        Placeholder().Matches(text).Select(m => m.Value).Distinct().OrderBy(p => p).ToArray();

    private static Dictionary<string, string> ReadLanguage(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file)) ?? new();

    private static Dictionary<string, Dictionary<string, string>> ReadPending(string file) =>
        JsonSerializer.Deserialize<Dictionary<string, Dictionary<string, string>>>(File.ReadAllText(file)) ?? new();

    [GeneratedRegex(@"\{l:T\s+([A-Za-z0-9.]+)\s*\}")]
    private static partial Regex XamlKey();

    [GeneratedRegex(@"""([A-Z][A-Za-z0-9]*(?:\.[A-Za-z0-9]+)+)""(?!\s*\+)")]
    private static partial Regex CodeKey();

    [GeneratedRegex(@"\{\d+(?:[,:][^}]*)?\}")]
    private static partial Regex Placeholder();
}
