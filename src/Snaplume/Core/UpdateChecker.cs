using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Snaplume.Core;

/// <summary>
/// Asks GitHub for the latest published version of Snaplume. It is the only request Snaplume ever sends: when the
/// user clicks "Check for updates", or once a day if they turned on <see cref="AppSettings.CheckForUpdates"/>.
/// It carries nothing about the user besides what any HTTPS request does (address, "Snaplume/x.y.z" user agent).
/// Nothing is downloaded or installed: the release page is opened in the browser.
/// </summary>
public static partial class UpdateChecker
{
    public const string ReleasesPage = "https://github.com/MrDDream/Snaplume/releases/latest";
    private const string LatestReleaseApi = "https://api.github.com/repos/MrDDream/Snaplume/releases/latest";
    /// <summary>Only a page of the project's releases is ever opened, whatever the answer says.</summary>
    private const string ReleasePagePrefix = "https://github.com/MrDDream/Snaplume/releases/";
    /// <summary>The answer is a few kilobytes; anything much bigger is not read.</summary>
    private const int MaxResponseBytes = 1024 * 1024;

    /// <param name="Version">Compared with <see cref="CurrentVersion"/> (major.minor.build).</param>
    /// <param name="DisplayVersion">As published, e.g. "1.2.0".</param>
    /// <param name="Page">Release page to open.</param>
    public sealed record Release(Version Version, string DisplayVersion, string Page)
    {
        public bool IsNewer => Version > CurrentVersion;
    }

    public static Version CurrentVersion
    {
        get
        {
            var v = typeof(UpdateChecker).Assembly.GetName().Version ?? new Version(0, 0, 0);
            return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        }
    }

    /// <summary>Latest release published on GitHub (pre-releases and drafts excluded).</summary>
    /// <exception cref="HttpRequestException">No connection, or GitHub answered with an error.</exception>
    /// <exception cref="TaskCanceledException">Timeout, or <paramref name="cancel"/>.</exception>
    /// <exception cref="InvalidDataException">The answer is not a release Snaplume understands.</exception>
    public static async Task<Release> GetLatestAsync(CancellationToken cancel = default)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        using var request = new HttpRequestMessage(HttpMethod.Get, LatestReleaseApi);
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("Snaplume", CurrentVersion.ToString(3)));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();

        var body = new MemoryStream();
        await using (var stream = await response.Content.ReadAsStreamAsync(cancel))
        {
            var chunk = new byte[16 * 1024];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancel)) > 0)
            {
                body.Write(chunk, 0, read);
                if (body.Length > MaxResponseBytes) throw new InvalidDataException("Réponse de GitHub trop longue");
            }
        }

        using var json = JsonDocument.Parse(body.ToArray());
        var root = json.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Réponse de GitHub inattendue");
        var tag = StringProperty(root, "tag_name");
        if (!TryParseVersion(tag, out var version, out var display)) throw new InvalidDataException($"Version inattendue : {tag}");
        var page = StringProperty(root, "html_url");
        if (page == null || !page.StartsWith(ReleasePagePrefix, StringComparison.Ordinal) || !Uri.IsWellFormedUriString(page, UriKind.Absolute))
            page = ReleasesPage;
        return new Release(version, display, page);
    }

    private static string? StringProperty(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>"v1.2.3" or "1.2.3-beta.1"; only digits, dots and a short suffix are accepted (the text is shown).</summary>
    internal static bool TryParseVersion(string? tag, out Version version, out string display)
    {
        version = new Version(0, 0, 0);
        display = "";
        var match = tag == null ? null : VersionPattern().Match(tag.Trim());
        if (match is not { Success: true }) return false;
        if (!int.TryParse(match.Groups[1].Value, out int major) || !int.TryParse(match.Groups[2].Value, out int minor)) return false;
        int build = 0;
        if (match.Groups[3].Success && !int.TryParse(match.Groups[3].Value, out build)) return false;
        version = new Version(major, minor, build);
        display = tag!.Trim().TrimStart('v', 'V');
        return true;
    }

    [GeneratedRegex(@"^[vV]?(\d{1,5})\.(\d{1,5})(?:\.(\d{1,5}))?(?:-[0-9A-Za-z.-]{1,30})?$")]
    private static partial Regex VersionPattern();
}
