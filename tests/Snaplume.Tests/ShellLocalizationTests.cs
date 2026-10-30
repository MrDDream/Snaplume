using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snaplume.Tests;

/// <summary>
/// Texts of the application shell (src/Snaplume.App outside the editor, capture, recording and player folders):
/// every key used exists in French (Languages/fr.json or a Languages/_pending/*.json file), and the pending
/// shell keys are translated in the six languages with the same placeholders.
/// </summary>
public partial class ShellLocalizationTests
{
    private static readonly string[] Languages = { "fr", "en", "de", "es", "it", "ar" };
    private static readonly string[] OtherAreas = { "Editor", "Capturing", "Recording", "Player" };

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
    public void PendingShellKeysAreTranslatedEverywhere()
    {
        var pending = ReadPending(Path.Combine(LanguagesFolder, "_pending", "shell.json"));
        Assert.Equal(Languages.OrderBy(l => l), pending.Keys.OrderBy(l => l));
        var french = pending["fr"];
        Assert.NotEmpty(french);
        foreach (var language in Languages)
        {
            var texts = pending[language];
            Assert.Equal(french.Keys.OrderBy(k => k), texts.Keys.OrderBy(k => k));
            foreach (var (key, text) in texts)
            {
                Assert.False(string.IsNullOrWhiteSpace(text), $"{language}: {key} is empty");
                Assert.Equal(Placeholders(french[key]), Placeholders(text));
            }
        }
    }

    [Fact]
    public void PendingShellKeysAreNew()
    {
        // New keys only: the existing ones are reused from the language files, never redefined.
        var french = ReadLanguage(Path.Combine(LanguagesFolder, "fr.json"));
        var pending = ReadPending(Path.Combine(LanguagesFolder, "_pending", "shell.json"))["fr"];
        Assert.DoesNotContain(pending.Keys, french.ContainsKey);
    }

    [Fact]
    public void ShellTextsExistInFrench()
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
            if (OtherAreas.Contains(top) || top is "bin" or "obj") continue;
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
