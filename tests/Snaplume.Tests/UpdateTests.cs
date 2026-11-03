using System.IO.Compression;
using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Snaplume.Core;
using Snaplume.Platform.Linux.Shell;

namespace Snaplume.Tests;

/// <summary>
/// Updates from inside Snaplume: the release files read from the GitHub answer, the package chosen for each kind of
/// installation, the checksum list, the allowed addresses, the download (on a fake HTTP handler: no network) and the
/// portable extraction.
/// </summary>
public class UpdateTests : IDisposable
{
    private const string Download = "https://github.com/MrDDream/Snaplume/releases/download/v9.1.0/";
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SnaplumeUpdateTests_" + Guid.NewGuid().ToString("N"));

    public UpdateTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>The files of a 9.1.0 release, as build-release.ps1, build-linux.ps1 and release.yml name them.</summary>
    private static readonly string[] ReleaseFiles =
    {
        "Snaplume-9.1.0-portable.zip", "Snaplume-Setup-9.1.0.exe", "Snaplume-9.1.0-x64.msi", "SHA256SUMS.txt",
        "snaplume_9.1.0-1_amd64.deb", "snaplume_9.1.0-1_arm64.deb", "snaplume-9.1.0-1.x86_64.rpm", "snaplume-9.1.0-1.aarch64.rpm",
        "SHA256SUMS-linux.txt",
    };

    private static UpdateChecker.Release Release(params string[] names) =>
        new(new Version(9, 1, 0), "9.1.0", "https://github.com/MrDDream/Snaplume/releases/tag/v9.1.0",
            names.Select(n => new ReleaseAsset(n, new Uri(Download + n), 1000)).ToList());

    // ---- Release answer ----

    [Fact]
    public void ReleaseKeepsOnlyTheProjectsDownloads()
    {
        var json = $$"""
            {
              "tag_name": "v9.1.0",
              "html_url": "https://github.com/MrDDream/Snaplume/releases/tag/v9.1.0",
              "assets": [
                { "name": "Snaplume-9.1.0-portable.zip", "size": 52000000, "browser_download_url": "{{Download}}Snaplume-9.1.0-portable.zip" },
                { "name": "SHA256SUMS.txt", "size": 300, "browser_download_url": "{{Download}}SHA256SUMS.txt" },
                { "name": "other.zip", "size": 10, "browser_download_url": "https://example.com/other.zip" },
                { "name": "fork.zip", "size": 10, "browser_download_url": "https://github.com/someone/Snaplume/releases/download/v9.1.0/fork.zip" },
                { "name": "plain.zip", "size": 10, "browser_download_url": "http://github.com/MrDDream/Snaplume/releases/download/v9.1.0/plain.zip" },
                { "name": "../escape.exe", "size": 10, "browser_download_url": "{{Download}}escape.exe" },
                { "name": "empty.zip", "size": 0, "browser_download_url": "{{Download}}empty.zip" },
                { "name": "nosize.zip", "browser_download_url": "{{Download}}nosize.zip" }
              ]
            }
            """;
        var release = UpdateChecker.ParseRelease(Encoding.UTF8.GetBytes(json));
        Assert.Equal("9.1.0", release.DisplayVersion);
        Assert.Equal(new[] { "Snaplume-9.1.0-portable.zip", "SHA256SUMS.txt" }, release.Assets.Select(a => a.Name));
        Assert.Equal(52000000, release.Assets[0].Size);
        Assert.Equal(new Uri(Download + "Snaplume-9.1.0-portable.zip"), release.Assets[0].Url);
    }

    [Fact]
    public void ReleaseWithoutAssetsHasNone()
    {
        var release = UpdateChecker.ParseRelease("""{ "tag_name": "1.4", "html_url": "https://evil.example/" }"""u8.ToArray());
        Assert.Empty(release.Assets);
        Assert.Equal(UpdateChecker.ReleasesPage, release.Page);
    }

    // ---- Package choice ----

    [Theory]
    [InlineData(InstallKind.Portable, Architecture.X64, "Snaplume-9.1.0-portable.zip", "SHA256SUMS.txt")]
    [InlineData(InstallKind.InnoSetup, Architecture.X64, "Snaplume-Setup-9.1.0.exe", "SHA256SUMS.txt")]
    [InlineData(InstallKind.Msi, Architecture.X64, "Snaplume-9.1.0-x64.msi", "SHA256SUMS.txt")]
    [InlineData(InstallKind.InnoSetup, Architecture.Arm64, "Snaplume-Setup-9.1.0.exe", "SHA256SUMS.txt")]
    [InlineData(InstallKind.Deb, Architecture.X64, "snaplume_9.1.0-1_amd64.deb", "SHA256SUMS-linux.txt")]
    [InlineData(InstallKind.Deb, Architecture.Arm64, "snaplume_9.1.0-1_arm64.deb", "SHA256SUMS-linux.txt")]
    [InlineData(InstallKind.Rpm, Architecture.X64, "snaplume-9.1.0-1.x86_64.rpm", "SHA256SUMS-linux.txt")]
    [InlineData(InstallKind.Rpm, Architecture.Arm64, "snaplume-9.1.0-1.aarch64.rpm", "SHA256SUMS-linux.txt")]
    public void ThePackageFitsTheInstallation(InstallKind kind, Architecture architecture, string package, string checksums)
    {
        var plan = UpdatePackages.Choose(Release(ReleaseFiles), kind, architecture);
        Assert.NotNull(plan);
        Assert.Equal(kind, plan.Kind);
        Assert.Equal(package, plan.Package.Name);
        Assert.Equal(checksums, plan.Checksums.Name);
    }

    [Theory]
    [InlineData(InstallKind.Unknown, Architecture.X64)]
    [InlineData(InstallKind.Deb, Architecture.X86)]
    [InlineData(InstallKind.Rpm, Architecture.Arm)]
    public void NothingFitsAnUnknownInstallationOrArchitecture(InstallKind kind, Architecture architecture) =>
        Assert.Null(UpdatePackages.Choose(Release(ReleaseFiles), kind, architecture));

    [Fact]
    public void NoPackageWithoutItsChecksumList()
    {
        var release = Release("Snaplume-Setup-9.1.0.exe", "snaplume_9.1.0-1_amd64.deb", "SHA256SUMS-linux.txt");
        Assert.Null(UpdatePackages.Choose(release, InstallKind.InnoSetup, Architecture.X64));
        Assert.NotNull(UpdatePackages.Choose(release, InstallKind.Deb, Architecture.X64));
        Assert.Null(UpdatePackages.Choose(release, InstallKind.Portable, Architecture.X64));
    }

    [Fact]
    public void AnotherSpellingIsTakenOnlyWhenItIsTheOnlyOne()
    {
        var release = Release("snaplume_9.1.0-2_amd64.deb", "snaplume_9.1.0-2_arm64.deb", "SHA256SUMS-linux.txt");
        Assert.Equal("snaplume_9.1.0-2_amd64.deb", UpdatePackages.Choose(release, InstallKind.Deb, Architecture.X64)!.Package.Name);
        var ambiguous = Release("snaplume_9.1.0-2_amd64.deb", "snaplume_9.1.0-3_amd64.deb", "SHA256SUMS-linux.txt");
        Assert.Null(UpdatePackages.Choose(ambiguous, InstallKind.Deb, Architecture.X64));
    }

    [Theory]
    [InlineData("ID=ubuntu\nID_LIKE=debian\n", InstallKind.Deb)]
    [InlineData("NAME=\"Linux Mint\"\nID=linuxmint\nID_LIKE=\"ubuntu debian\"\n", InstallKind.Deb)]
    [InlineData("ID=debian\r\nVERSION_ID=\"13\"\r\n", InstallKind.Deb)]
    [InlineData("ID=fedora\nVERSION_ID=43\n", InstallKind.Rpm)]
    [InlineData("ID=\"rocky\"\nID_LIKE=\"rhel centos fedora\"\n", InstallKind.Rpm)]
    [InlineData("ID=\"opensuse-tumbleweed\"\nID_LIKE=\"opensuse suse\"\n", InstallKind.Rpm)]
    [InlineData("ID=arch\n", InstallKind.Unknown)]
    [InlineData("# ID=ubuntu\n", InstallKind.Unknown)]
    [InlineData("", InstallKind.Unknown)]
    public void DistributionFamilyComesFromOsRelease(string osRelease, InstallKind expected) =>
        Assert.Equal(expected, UpdatePackages.LinuxPackageKind(osRelease));

    // ---- Checksums ----

    [Fact]
    public void ChecksumListIsRead()
    {
        var a = new string('a', 64);
        var b = new string('B', 64);
        var text = $"{a}  Snaplume-9.1.0-portable.zip\r\n{b} *Snaplume-Setup-9.1.0.exe\n\nnot a line\n{new string('c', 63)}  short.zip\n"
                   + $"{new string('g', 64)}  nothex.zip\n{a}  twice.zip\n{new string('d', 64)}  twice.zip\n{a}  same.zip\n{a}  same.zip\n";
        var sums = UpdatePackages.ParseChecksums(text);
        Assert.Equal(a, sums["Snaplume-9.1.0-portable.zip"]);
        Assert.Equal(new string('b', 64), sums["Snaplume-Setup-9.1.0.exe"]);
        Assert.Equal(a, sums["same.zip"]);
        Assert.False(sums.ContainsKey("twice.zip"));
        Assert.Equal(3, sums.Count);
    }

    // ---- Addresses ----

    [Theory]
    [InlineData("https://github.com/MrDDream/Snaplume/releases/download/v9.1.0/Snaplume-9.1.0-x64.msi", true)]
    [InlineData("https://GitHub.com/MrDDream/Snaplume/releases/download/v9.1.0/SHA256SUMS.txt", true)]
    [InlineData("http://github.com/MrDDream/Snaplume/releases/download/v9.1.0/a.zip", false)]
    [InlineData("https://github.com:8443/MrDDream/Snaplume/releases/download/v9.1.0/a.zip", false)]
    [InlineData("https://user@github.com/MrDDream/Snaplume/releases/download/v9.1.0/a.zip", false)]
    [InlineData("https://github.com/MrDDream/Other/releases/download/v9.1.0/a.zip", false)]
    [InlineData("https://github.com/MrDDream/Snaplume/releases/download/v9.1.0/sub/a.zip", false)]
    [InlineData("https://github.com/MrDDream/Snaplume/releases/download/v9.1.0/../../../../evil/a.zip", false)]
    [InlineData("https://github.com.evil.example/MrDDream/Snaplume/releases/download/v9.1.0/a.zip", false)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2", false)]
    public void OnlyTheProjectsReleaseFilesAreDownloaded(string url, bool allowed) =>
        Assert.Equal(allowed, UpdatePackages.IsAllowedDownload(new Uri(url)));

    [Theory]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset-2e65be/1/2?X-Amz-Signature=x", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x", true)]
    [InlineData("https://github.com/MrDDream/Snaplume/releases/download/v9.1.0/a.zip", true)]
    [InlineData("http://objects.githubusercontent.com/a", false)]
    [InlineData("https://objects.githubusercontent.com.evil.example/a", false)]
    [InlineData("https://raw.githubusercontent.com/MrDDream/Snaplume/main/a", false)]
    [InlineData("https://example.com/a.zip", false)]
    public void RedirectsStayOnGitHubStorage(string url, bool allowed) =>
        Assert.Equal(allowed, UpdatePackages.IsAllowedRedirect(new Uri(url)));

    // ---- Download ----

    /// <summary>Answers from a table of addresses; anything else is a 404. Never reaches the network.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        public readonly Dictionary<string, Func<HttpResponseMessage>> Routes = new();
        public readonly List<Uri> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(Routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void File(string url, byte[] content) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

        public void Redirect(string url, string location) =>
            Routes[url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
    }

    private const string PackageName = "Snaplume-Setup-9.1.0.exe";
    private const string Storage = "https://objects.githubusercontent.com/github-production-release-asset/42/setup?signature=abc";
    private static readonly byte[] PackageBytes = Enumerable.Range(0, 300_000).Select(i => (byte)(i * 7)).ToArray();

    private static UpdatePackage Plan(long size) => new(InstallKind.InnoSetup,
        new ReleaseAsset(PackageName, new Uri(Download + PackageName), size),
        new ReleaseAsset("SHA256SUMS.txt", new Uri(Download + "SHA256SUMS.txt"), 200));

    private static byte[] Sums(byte[] content) =>
        Encoding.UTF8.GetBytes($"{Convert.ToHexStringLower(SHA256.HashData(content))}  {PackageName}\n{new string('0', 64)}  other.zip\n");

    [Fact]
    public async Task DownloadFollowsGitHubRedirectsAndChecksTheSum()
    {
        var handler = new FakeHandler();
        handler.File(Download + "SHA256SUMS.txt", Sums(PackageBytes));
        handler.Redirect(Download + PackageName, Storage);
        handler.File(Storage, PackageBytes);
        var reported = new List<long>();

        var path = await new UpdateDownloader(handler).DownloadAsync(Plan(PackageBytes.Length), _folder, new SyncProgress(reported.Add),
            TestContext.Current.CancellationToken);

        Assert.Equal(Path.Combine(_folder, PackageName), path);
        Assert.Equal(PackageBytes, File.ReadAllBytes(path));
        Assert.Equal(new[] { Download + "SHA256SUMS.txt", Download + PackageName, Storage }, handler.Requested.Select(u => u.AbsoluteUri));
        Assert.Equal(PackageBytes.Length, reported[^1]);
        Assert.False(File.Exists(path + ".part"));
    }

    [Fact]
    public async Task ADifferentSumDeletesTheFile()
    {
        var handler = new FakeHandler();
        var tampered = PackageBytes.ToArray();
        tampered[1234] ^= 0xFF;
        handler.File(Download + "SHA256SUMS.txt", Sums(PackageBytes));
        handler.File(Download + PackageName, tampered);

        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            new UpdateDownloader(handler).DownloadAsync(Plan(tampered.Length), _folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task APackageMissingFromTheListIsNotDownloaded()
    {
        var handler = new FakeHandler();
        handler.File(Download + "SHA256SUMS.txt", Encoding.UTF8.GetBytes($"{new string('0', 64)}  other.zip\n"));
        handler.File(Download + PackageName, PackageBytes);

        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            new UpdateDownloader(handler).DownloadAsync(Plan(PackageBytes.Length), _folder, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Requested, u => u.AbsoluteUri == Download + PackageName);
    }

    [Theory]
    [InlineData("https://example.com/setup.exe")]
    [InlineData("http://objects.githubusercontent.com/setup.exe")]
    public async Task ARedirectElsewhereIsRefused(string location)
    {
        var handler = new FakeHandler();
        handler.File(Download + "SHA256SUMS.txt", Sums(PackageBytes));
        handler.Redirect(Download + PackageName, location);
        handler.File(location, PackageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateDownloader(handler).DownloadAsync(Plan(PackageBytes.Length), _folder, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Requested, u => u.AbsoluteUri == location);
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task AFileLongerThanAnnouncedIsRefused()
    {
        var handler = new FakeHandler();
        handler.File(Download + "SHA256SUMS.txt", Sums(PackageBytes));
        handler.File(Download + PackageName, PackageBytes);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateDownloader(handler).DownloadAsync(Plan(PackageBytes.Length - 10), _folder, null, TestContext.Current.CancellationToken));
        Assert.Empty(Directory.EnumerateFileSystemEntries(_folder));
    }

    [Fact]
    public async Task AnAssetOutsideTheReleasesIsNeverRequested()
    {
        var handler = new FakeHandler();
        var plan = Plan(10) with { Package = new ReleaseAsset(PackageName, new Uri("https://example.com/" + PackageName), 10) };
        handler.File(Download + "SHA256SUMS.txt", Sums(PackageBytes));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            new UpdateDownloader(handler).DownloadAsync(plan, _folder, null, TestContext.Current.CancellationToken));
        Assert.DoesNotContain(handler.Requested, u => u.Host == "example.com");
    }

    /// <summary>Progress&lt;T&gt; posts to a synchronization context; this one reports at once.</summary>
    private sealed class SyncProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    // ---- Portable ----

    private string MakeZip(params (string Name, string Text)[] entries)
    {
        var zip = Path.Combine(_folder, "update.zip");
        using var archive = ZipFile.Open(zip, ZipArchiveMode.Create);
        foreach (var (name, text) in entries)
        {
            using var writer = new StreamWriter(archive.CreateEntry(name).Open());
            writer.Write(text);
        }
        return zip;
    }

    private string MakeCurrentCopy()
    {
        var current = Path.Combine(_folder, "apps", "Snaplume");
        Directory.CreateDirectory(current);
        File.WriteAllText(Path.Combine(current, "Snaplume.exe"), "old");
        File.WriteAllText(Path.Combine(current, "portable"), "");
        File.WriteAllText(Path.Combine(current, "settings.json"), "{\"Theme\":\"dark\"}");
        File.WriteAllText(Path.Combine(current, "presets.json"), "[]");
        File.WriteAllText(Path.Combine(current, "errors.log"), "old errors");
        return current;
    }

    [Fact]
    public void PortableUpdateGoesToANewFolderWithTheSettings()
    {
        var current = MakeCurrentCopy();
        var zip = MakeZip(("Snaplume/Snaplume.exe", "new"), ("Snaplume/portable", ""), ("Snaplume/runtimes/lib.dll", "lib"),
            ("Snaplume/README.txt", "readme"));

        var target = PortableUpdate.Install(zip, current + Path.DirectorySeparatorChar, "9.1.0", "Snaplume.exe");

        Assert.Equal(Path.Combine(_folder, "apps", "Snaplume-9.1.0"), target);
        Assert.Equal("new", File.ReadAllText(Path.Combine(target, "Snaplume.exe")));
        Assert.Equal("lib", File.ReadAllText(Path.Combine(target, "runtimes", "lib.dll")));
        Assert.True(File.Exists(Path.Combine(target, "portable")));
        Assert.Equal("{\"Theme\":\"dark\"}", File.ReadAllText(Path.Combine(target, "settings.json")));
        Assert.True(File.Exists(Path.Combine(target, "presets.json")));
        Assert.False(File.Exists(Path.Combine(target, "errors.log")));
        // The running copy is left alone.
        Assert.Equal("old", File.ReadAllText(Path.Combine(current, "Snaplume.exe")));
        Assert.Equal(new[] { "Snaplume", "Snaplume-9.1.0" },
            Directory.GetDirectories(Path.Combine(_folder, "apps")).Select(Path.GetFileName).Order());
    }

    [Fact]
    public void PortableUpdateNeverOverwritesAFolderAndAddsTheMarker()
    {
        var current = MakeCurrentCopy();
        var existing = Path.Combine(_folder, "apps", "Snaplume-9.1.0");
        Directory.CreateDirectory(existing);
        File.WriteAllText(Path.Combine(existing, "mine.txt"), "keep");
        var zip = MakeZip(("Snaplume/Snaplume.exe", "new"));

        var target = PortableUpdate.Install(zip, current, "9.1.0", "Snaplume.exe");

        Assert.Equal(existing + " (2)", target);
        Assert.True(File.Exists(Path.Combine(target, "portable")));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(existing, "mine.txt")));
    }

    [Theory]
    [InlineData("Snaplume/../../evil.txt")]
    [InlineData("../evil.txt")]
    public void PortableUpdateRefusesEntriesOutsideItsFolder(string entry)
    {
        var current = MakeCurrentCopy();
        var zip = MakeZip(("Snaplume/Snaplume.exe", "new"), (entry, "evil"));

        Assert.Throws<InvalidDataException>(() => PortableUpdate.Install(zip, current, "9.1.0", "Snaplume.exe"));
        Assert.Equal(new[] { "Snaplume" }, Directory.GetDirectories(Path.Combine(_folder, "apps")).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(_folder, "evil.txt")));
        Assert.False(File.Exists(Path.Combine(_folder, "apps", "evil.txt")));
    }

    [Fact]
    public void PortableUpdateWithoutTheExecutableIsRefused()
    {
        var current = MakeCurrentCopy();
        var zip = MakeZip(("Snaplume/README.txt", "readme"));

        Assert.Throws<InvalidDataException>(() => PortableUpdate.Install(zip, current, "9.1.0", "Snaplume.exe"));
        Assert.Single(Directory.GetDirectories(Path.Combine(_folder, "apps")));
    }

    // ---- Linux ----

    [Fact]
    public void ManualCommandQuotesThePackagePath() =>
        Assert.Equal("'/tmp/it'\\''s/a b.deb'", LinuxUpdateInstaller.Quote("/tmp/it's/a b.deb"));
}
