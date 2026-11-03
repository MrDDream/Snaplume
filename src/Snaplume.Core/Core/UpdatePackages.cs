using System.Runtime.InteropServices;

namespace Snaplume.Core;

/// <summary>How the running copy of Snaplume was installed: it decides which file of a release replaces it.</summary>
public enum InstallKind
{
    /// <summary>Not recognized (development build, copied folder...): only the release page is offered.</summary>
    Unknown,
    /// <summary>Windows zip with the "portable" marker: the new version is unzipped next to it.</summary>
    Portable,
    /// <summary>Windows, Inno Setup installer (Snaplume-Setup-x.y.z.exe).</summary>
    InnoSetup,
    /// <summary>Windows, MSI package (Snaplume-x.y.z-x64.msi).</summary>
    Msi,
    /// <summary>Linux, Debian / Ubuntu package (snaplume_x.y.z-1_amd64.deb).</summary>
    Deb,
    /// <summary>Linux, Fedora / openSUSE package (snaplume-x.y.z-1.x86_64.rpm).</summary>
    Rpm,
}

/// <summary>The file of a release that replaces this copy, and the checksum list that vouches for it.</summary>
public sealed record UpdatePackage(InstallKind Kind, ReleaseAsset Package, ReleaseAsset Checksums);

/// <summary>
/// Which file of a release fits an installation, and what may be downloaded at all. The names are those written by
/// build/build-release.ps1 (Windows) and build/build-linux.ps1 with packaging/linux/nfpm.yaml (Linux); the checksum
/// lists ("&lt;sha256&gt;  &lt;file name&gt;" lines) are published next to them by .github/workflows/release.yml.
/// </summary>
public static class UpdatePackages
{
    public const string WindowsChecksums = "SHA256SUMS.txt";
    public const string LinuxChecksums = "SHA256SUMS-linux.txt";

    /// <summary>Packages are a few dozen megabytes; an announced or received size above this is refused.</summary>
    public const long MaxPackageBytes = 1024L * 1024 * 1024;

    /// <summary>Every download starts here: the files attached to the project's releases.</summary>
    private const string DownloadPrefix = "https://github.com/MrDDream/Snaplume/releases/download/";

    /// <summary>Where github.com redirects release downloads (signed, short-lived addresses).</summary>
    private static readonly string[] RedirectHosts = { "objects.githubusercontent.com", "release-assets.githubusercontent.com" };

    /// <summary>The package for <paramref name="kind"/> and its checksum list, or null if the release has not both.</summary>
    /// <param name="architecture">Of the system (Linux packages exist for x64 and arm64; Windows ones are x64 only).</param>
    public static UpdatePackage? Choose(UpdateChecker.Release release, InstallKind kind, Architecture architecture)
    {
        var checksumsName = ChecksumsName(kind);
        if (checksumsName == null) return null;
        var checksums = release.Assets.FirstOrDefault(a => a.Name == checksumsName);
        if (checksums == null) return null;

        // The exact name first; else the only file of the right type (a release number other than -1, a
        // pre-release version spelled differently by the packager...).
        var expected = ExpectedName(kind, release.DisplayVersion, architecture);
        var package = release.Assets.FirstOrDefault(a => a.Name == expected);
        if (package == null)
        {
            var candidates = release.Assets.Where(a => Matches(kind, a.Name, architecture)).ToList();
            if (candidates.Count == 1) package = candidates[0];
        }
        return package == null ? null : new UpdatePackage(kind, package, checksums);
    }

    internal static string? ChecksumsName(InstallKind kind) => kind switch
    {
        InstallKind.Portable or InstallKind.InnoSetup or InstallKind.Msi => WindowsChecksums,
        InstallKind.Deb or InstallKind.Rpm => LinuxChecksums,
        _ => null,
    };

    internal static string? ExpectedName(InstallKind kind, string version, Architecture architecture) => kind switch
    {
        InstallKind.Portable => $"Snaplume-{version}-portable.zip",
        InstallKind.InnoSetup => $"Snaplume-Setup-{version}.exe",
        InstallKind.Msi => $"Snaplume-{version}-x64.msi",
        InstallKind.Deb when DebianArchitecture(architecture) is { } arch => $"snaplume_{version}-1_{arch}.deb",
        InstallKind.Rpm when RpmArchitecture(architecture) is { } arch => $"snaplume-{version}-1.{arch}.rpm",
        _ => null,
    };

    private static bool Matches(InstallKind kind, string name, Architecture architecture) => kind switch
    {
        InstallKind.Portable => name.StartsWith("Snaplume-", StringComparison.Ordinal) && name.EndsWith("-portable.zip", StringComparison.Ordinal),
        InstallKind.InnoSetup => name.StartsWith("Snaplume-Setup-", StringComparison.Ordinal) && name.EndsWith(".exe", StringComparison.Ordinal),
        InstallKind.Msi => name.StartsWith("Snaplume-", StringComparison.Ordinal) && name.EndsWith("-x64.msi", StringComparison.Ordinal),
        InstallKind.Deb => DebianArchitecture(architecture) is { } arch
                           && name.StartsWith("snaplume_", StringComparison.Ordinal) && name.EndsWith($"_{arch}.deb", StringComparison.Ordinal),
        InstallKind.Rpm => RpmArchitecture(architecture) is { } arch
                           && name.StartsWith("snaplume-", StringComparison.Ordinal) && name.EndsWith($".{arch}.rpm", StringComparison.Ordinal),
        _ => false,
    };

    private static string? DebianArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "amd64",
        Architecture.Arm64 => "arm64",
        _ => null,
    };

    private static string? RpmArchitecture(Architecture architecture) => architecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.Arm64 => "aarch64",
        _ => null,
    };

    /// <summary>
    /// The family of a Linux distribution from /etc/os-release: ID, then each ID_LIKE entry ("ubuntu debian" for
    /// Linux Mint, "rhel centos fedora" for Rocky...). Used when neither dpkg nor rpm knows the installed copy.
    /// </summary>
    /// <returns><see cref="InstallKind.Deb"/>, <see cref="InstallKind.Rpm"/> or <see cref="InstallKind.Unknown"/>.</returns>
    public static InstallKind LinuxPackageKind(string osRelease)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var raw in osRelease.Split('\n'))
        {
            var line = raw.Trim();
            int equal = line.IndexOf('=');
            if (line.StartsWith('#') || equal <= 0) continue;
            var value = line[(equal + 1)..].Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            values[line[..equal].Trim()] = value;
        }
        var ids = new List<string>();
        if (values.TryGetValue("ID", out var id)) ids.Add(id);
        if (values.TryGetValue("ID_LIKE", out var like)) ids.AddRange(like.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        foreach (var name in ids.Select(i => i.ToLowerInvariant()))
        {
            if (name is "debian" or "ubuntu" or "raspbian") return InstallKind.Deb;
            if (name is "fedora" or "rhel" or "centos" or "suse" or "opensuse" or "mageia" || name.StartsWith("opensuse", StringComparison.Ordinal))
                return InstallKind.Rpm;
        }
        return InstallKind.Unknown;
    }

    /// <summary>
    /// The SHA-256 of each file of a checksum list ("&lt;64 hex digits&gt;  &lt;file name&gt;", as written by sha256sum and
    /// the build scripts; a '*' before the name, binary mode, is accepted). Other lines are ignored, and so is a name
    /// listed twice with different sums: such a list vouches for nothing.
    /// </summary>
    /// <returns>Lowercase hexadecimal sums by file name.</returns>
    public static IReadOnlyDictionary<string, string> ParseChecksums(string text)
    {
        var sums = new Dictionary<string, string>(StringComparer.Ordinal);
        var conflicts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r').Trim();
            if (line.Length < 66 || !char.IsWhiteSpace(line[64])) continue;
            var hash = line[..64];
            if (!hash.All(char.IsAsciiHexDigit)) continue;
            var name = line[65..].TrimStart();
            if (name.StartsWith('*')) name = name[1..];
            if (name.Length == 0) continue;
            hash = hash.ToLowerInvariant();
            if (sums.TryGetValue(name, out var previous) && previous != hash) conflicts.Add(name);
            sums[name] = hash;
        }
        foreach (var name in conflicts) sums.Remove(name);
        return sums;
    }

    /// <summary>A name that cannot leave the download folder: no separators, no "..", nothing invalid on any system.</summary>
    internal static bool IsPlainFileName(string name) =>
        name.Length is > 0 and <= 128 && name != "." && name != ".." && !FileOps.HasInvalidNameChars(name) && name.Trim() == name;

    /// <summary>
    /// The first address of a download: a file attached to one of the project's releases on github.com, over HTTPS
    /// ("…/releases/download/&lt;tag&gt;/&lt;file&gt;").
    /// </summary>
    public static bool IsAllowedDownload(Uri uri) => IsReleaseDownload(uri, DownloadPrefix);

    /// <summary>
    /// A file attached to a release of a GitHub project, over plain HTTPS: "<paramref name="prefix"/>&lt;tag&gt;/&lt;file&gt;",
    /// both plain names (nothing escaped, no "." or "..").
    /// </summary>
    /// <param name="prefix">"https://github.com/&lt;owner&gt;/&lt;project&gt;/releases/download/".</param>
    internal static bool IsReleaseDownload(Uri uri, string prefix)
    {
        if (!IsPlainHttps(uri) || !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase)) return false;
        var address = uri.GetLeftPart(UriPartial.Path);
        if (!address.StartsWith(prefix, StringComparison.Ordinal)) return false;
        var rest = address[prefix.Length..].Split('/');
        return rest.Length == 2 && rest.All(part => part.Length > 0 && part != "." && part != ".." && !part.Contains('%'));
    }

    /// <summary>
    /// Where a download may be redirected: another release file of the project, or GitHub's storage for release
    /// files, over HTTPS. Anything else (another host, plain HTTP, another port) is refused.
    /// </summary>
    public static bool IsAllowedRedirect(Uri uri) => IsAllowedDownload(uri) || IsReleaseStorage(uri);

    /// <summary>GitHub's storage for release files (where github.com redirects their downloads), over HTTPS.</summary>
    internal static bool IsReleaseStorage(Uri uri) =>
        IsPlainHttps(uri) && RedirectHosts.Any(host => string.Equals(uri.Host, host, StringComparison.OrdinalIgnoreCase));

    /// <summary>An absolute HTTPS address on the default port, without a user name or password.</summary>
    internal static bool IsPlainHttps(Uri uri) =>
        uri.IsAbsoluteUri && uri.Scheme == Uri.UriSchemeHttps && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo);
}
