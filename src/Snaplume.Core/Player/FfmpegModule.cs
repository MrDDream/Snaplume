using System.IO.Compression;
using System.Runtime.InteropServices;
using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>A file to download whose SHA-256 is known in advance.</summary>
/// <param name="Version">FFmpeg version of the build, shown in the settings.</param>
public sealed record PinnedPackage(string Version, Uri Url, string FileName, long Size, string Sha256)
{
    public ReleaseAsset Asset => new(FileName, Url, Size);
}

/// <summary>
/// The "web formats" complementary module of Windows: ffmpeg is not shipped with Snaplume; on request it is downloaded
/// from the BtbN builds on GitHub (LGPL variant: SVT-AV1, libvpx, Opus, libwebp, no GPL parts), checked against the
/// SHA-256 written below, and unzipped into %LOCALAPPDATA%\Snaplume\modules\ffmpeg (next to the executable for a
/// portable copy). Linux uses the ffmpeg of the system (a dependency of the package).
/// </summary>
public static class FfmpegModule
{
    /// <summary>
    /// The build: a dated release (the last build of each month is kept two years by BtbN's retention policy), not
    /// "latest", so the checksum below stays right. Release branch 8.1 ("lgpl-shared": the DLLs beside ffmpeg.exe,
    /// half the download of the static build). The SHA-256 is the one GitHub publishes for the asset (its "digest" in
    /// the release API) and the one in the release's checksums.sha256.
    /// </summary>
    public static readonly PinnedPackage Package = new(
        "8.1.3",
        new Uri("https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg-n8.1.3-9-g29e619e767-win64-lgpl-shared-8.1.zip"),
        "ffmpeg-n8.1.3-9-g29e619e767-win64-lgpl-shared-8.1.zip",
        80_733_362,
        "3e47bda1607740550141e37c0e49d1e5182b34699f15adfd137ee266d346811a");

    /// <summary>The releases of the BtbN builds: the only place a module download may start from.</summary>
    private const string DownloadPrefix = "https://github.com/BtbN/FFmpeg-Builds/releases/download/";

    /// <summary>The extracted files are a few hundred megabytes at most; more means a wrong archive.</summary>
    private const long MaxExtractedBytes = 1024L * 1024 * 1024;

    /// <summary>The module exists only for Windows x64 (the build downloaded).</summary>
    public static bool IsSupported => OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture == Architecture.X64;

    /// <summary>%LOCALAPPDATA%\Snaplume\modules\ffmpeg, or "modules\ffmpeg" next to the executable of a portable copy.</summary>
    public static string Folder => AppSettings.IsPortable
        ? Path.Combine(AppContext.BaseDirectory, "modules", "ffmpeg")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snaplume", "modules", "ffmpeg");

    public static string ExecutablePath => ExecutableIn(Folder);

    private static string ExecutableIn(string folder) => Path.Combine(folder, "bin", "ffmpeg.exe");

    public static bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>Where the archive waits while it downloads (emptied before and after): a folder of the user's only.</summary>
    private static string DownloadFolder => UpdateDownloader.PrivateFolder("ffmpeg");

    /// <summary>
    /// The first address of the download: a file of a BtbN release, over HTTPS; the redirect may also go to GitHub's
    /// storage for release files.
    /// </summary>
    public static bool IsAllowedDownload(Uri uri) => UpdatePackages.IsReleaseDownload(uri, DownloadPrefix);

    public static bool IsAllowedRedirect(Uri uri) => IsAllowedDownload(uri) || UpdatePackages.IsReleaseStorage(uri);

    /// <summary>Downloads, verifies and unzips the module, replacing an older one.</summary>
    /// <param name="progress">Bytes received so far (out of <see cref="PinnedPackage.Size"/>).</param>
    /// <param name="package">For the tests; by default <see cref="Package"/>.</param>
    /// <param name="folder">For the tests; by default <see cref="Folder"/>.</param>
    /// <param name="handler">For the tests: answers instead of the network.</param>
    /// <exception cref="UpdateVerificationException">The archive is not the one expected (deleted).</exception>
    /// <exception cref="HttpRequestException">No connection, or GitHub answered with an error.</exception>
    /// <exception cref="InvalidDataException">Refused address or redirect, unexpected size or content.</exception>
    public static async Task InstallAsync(IProgress<long>? progress, CancellationToken cancel, PinnedPackage? package = null, string? folder = null,
        HttpMessageHandler? handler = null)
    {
        package ??= Package;
        folder ??= Folder;
        var downloads = folder == Folder ? DownloadFolder : Path.Combine(Path.GetDirectoryName(folder)!, "download");
        EmptyFolder(downloads);
        try
        {
            var rules = new UpdateDownloader.AddressRules(IsAllowedDownload, IsAllowedRedirect);
            var zip = await new UpdateDownloader(handler).DownloadPinnedAsync(package.Asset, package.Sha256, rules, downloads, progress, cancel)
                .ConfigureAwait(false);
            cancel.ThrowIfCancellationRequested();
            await Task.Run(() => Unpack(zip, folder, package.Version), cancel).ConfigureAwait(false);
        }
        finally
        {
            EmptyFolder(downloads);
            FfmpegTools.Invalidate();
        }
    }

    /// <summary>
    /// <see cref="InstallAsync"/> for the interface (Settings › Video, the "Export / compress" dialog): the failure is
    /// logged and returned as a message ready to show; null when the module is installed, or the download cancelled.
    /// </summary>
    public static async Task<string?> TryInstallAsync(IProgress<long>? progress, CancellationToken cancel)
    {
        try
        {
            await InstallAsync(progress, cancel).ConfigureAwait(false);
            return null;
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            return null;
        }
        catch (UpdateVerificationException ex)
        {
            ErrorLog.Write("Vérification du module ffmpeg téléchargé", ex.Message);
            return Loc.T("Settings.Ffmpeg.ChecksumMismatch");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            ErrorLog.Write("Téléchargement du module ffmpeg", ex);
            return Loc.T("Update.Offline");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Installation du module ffmpeg", ex);
            return Loc.T("Settings.Ffmpeg.InstallFailed", ex.Message);
        }
    }

    /// <summary>Size of the download in (decimal) megabytes, as file managers and browsers show it: about 81.</summary>
    public static double DownloadMegabytes => Package.Size / 1_000_000.0;

    /// <summary>"Downloading… 12.3 / 77.0 MB (16 %)", for the progress of <see cref="TryInstallAsync"/>.</summary>
    public static string ProgressText(long received, long total)
    {
        double fraction = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
        return Loc.T("Settings.Ffmpeg.Downloading") + "  " + Loc.T("Update.Progress", received / 1048576.0, total / 1048576.0, fraction * 100);
    }

    /// <summary>Removes the module (an export using it must be finished: its files are in use until then).</summary>
    /// <exception cref="IOException">A file is still in use.</exception>
    public static void Uninstall(string? folder = null)
    {
        folder ??= Folder;
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        finally
        {
            FfmpegTools.Invalidate();
        }
    }

    /// <summary>Space the module takes on the disk, in bytes (0 when it is not installed or cannot be read).</summary>
    public static long InstalledBytes(string? folder = null)
    {
        try
        {
            var root = new DirectoryInfo(folder ?? Folder);
            return root.Exists ? root.EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length) : 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Taille du module ffmpeg", ex);
            return 0;
        }
    }

    /// <summary>Version written by <see cref="InstallAsync"/>, or "" when unknown.</summary>
    public static string InstalledVersion(string? folder = null)
    {
        try
        {
            var file = Path.Combine(folder ?? Folder, "version.txt");
            return File.Exists(file) ? File.ReadAllText(file).Trim() : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Unzips the programs (bin\ffmpeg.exe and its DLLs) and the licence of the archive's single top folder into a
    /// staging folder, then puts it in place of <paramref name="folder"/>. Anything else of the archive (ffplay,
    /// ffprobe, headers, documentation) is left out; an entry leaving its folder makes the whole archive refused.
    /// </summary>
    internal static void Unpack(string zip, string folder, string version)
    {
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
        var staging = folder + ".new";
        DeleteFolder(staging);
        try
        {
            ExtractPrograms(zip, staging);
            if (!File.Exists(ExecutableIn(staging))) throw new InvalidDataException($"bin/ffmpeg.exe absent de {Path.GetFileName(zip)}");
            File.WriteAllText(Path.Combine(staging, "version.txt"), version);
            DeleteFolder(folder);
            Directory.Move(staging, folder);
        }
        catch
        {
            DeleteFolder(staging);
            throw;
        }
    }

    private static void ExtractPrograms(string zip, string destination)
    {
        using var archive = ZipFile.OpenRead(zip);
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination)) + Path.DirectorySeparatorChar;
        Directory.CreateDirectory(full);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var parts = name.Split('/');
            if (parts.Any(p => p is "" or "." or ".." || p.Contains(':'))) throw new InvalidDataException($"Entrée refusée dans {Path.GetFileName(zip)} : {entry.FullName}");
            // "<top>/bin/<file>" and "<top>/LICENSE.txt" only.
            string? relative = parts.Length switch
            {
                3 when parts[1] == "bin" && IsProgramFile(parts[2]) => Path.Combine("bin", parts[2]),
                2 when parts[1].Equals("LICENSE.txt", StringComparison.OrdinalIgnoreCase) => "LICENSE.txt",
                _ => null,
            };
            if (relative == null) continue;
            total += entry.Length;
            if (total > MaxExtractedBytes) throw new InvalidDataException($"{Path.GetFileName(zip)} : contenu trop volumineux");
            var path = Path.GetFullPath(Path.Combine(full, relative));
            if (!path.StartsWith(full, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException($"Entrée refusée dans {Path.GetFileName(zip)} : {entry.FullName}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            entry.ExtractToFile(path, overwrite: false);
        }
    }

    /// <summary>ffmpeg.exe and the libraries it loads (avcodec-62.dll...); not ffplay nor ffprobe.</summary>
    private static bool IsProgramFile(string name) =>
        UpdatePackages.IsPlainFileName(name)
        && (name.Equals("ffmpeg.exe", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase));

    private static void DeleteFolder(string folder)
    {
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
    }

    private static void EmptyFolder(string folder)
    {
        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression du téléchargement de ffmpeg", ex);
        }
    }
}
