using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Snaplume.Core;

/// <summary>The downloaded file is not the one the release vouches for (it was deleted).</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

/// <summary>A downloaded file and the SHA-256 it was verified against (lowercase hexadecimal).</summary>
public sealed record VerifiedDownload(string Path, string Sha256);

/// <summary>
/// Downloads the package of an update (see <see cref="UpdatePackages.Choose"/>) and checks it against the SHA-256
/// list published with the release. Only the project's release files are fetched, from github.com and the storage
/// it redirects to (<see cref="UpdatePackages.IsAllowedRedirect"/>): each redirect is followed by hand to check it.
/// </summary>
public sealed class UpdateDownloader
{
    /// <summary>A checksum list has a few lines.</summary>
    private const int MaxChecksumsBytes = 64 * 1024;
    private const int MaxRedirects = 5;

    private readonly HttpMessageHandler? _handler;

    /// <param name="handler">For the tests; by default a handler that does not follow redirects on its own.</param>
    public UpdateDownloader(HttpMessageHandler? handler = null) => _handler = handler;

    /// <summary>
    /// Downloads the package into <paramref name="folder"/> (a ".part" file until it is complete and verified).
    /// </summary>
    /// <param name="folder">A folder only this user can write to (<see cref="PrivateFolder"/>); on Linux and macOS, a
    /// link or a folder others may write to is refused.</param>
    /// <param name="progress">Bytes received so far (out of <see cref="ReleaseAsset.Size"/>).</param>
    /// <returns>Full path of the verified package.</returns>
    /// <exception cref="UpdateVerificationException">The SHA-256 differs from the list, or the file is not in it.</exception>
    /// <exception cref="HttpRequestException">No connection, or GitHub answered with an error.</exception>
    /// <exception cref="InvalidDataException">Refused address or redirect, unexpected size.</exception>
    /// <exception cref="IOException">The folder is refused, or cannot be written.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/>.</exception>
    public async Task<string> DownloadAsync(UpdatePackage update, string folder, IProgress<long>? progress, CancellationToken cancel) =>
        (await DownloadVerifiedAsync(update, folder, progress, cancel).ConfigureAwait(false)).Path;

    /// <summary>
    /// <see cref="DownloadAsync"/>, with the SHA-256 the package was checked against: the installer step checks it again
    /// (<see cref="HasSha256"/>) right before running the package.
    /// </summary>
    /// <inheritdoc cref="DownloadAsync" path="/exception"/>
    public async Task<VerifiedDownload> DownloadVerifiedAsync(UpdatePackage update, string folder, IProgress<long>? progress,
        CancellationToken cancel)
    {
        var package = update.Package;
        if (!UpdatePackages.IsPlainFileName(package.Name)) throw new InvalidDataException($"Nom de fichier refusé : {package.Name}");
        using var http = CreateClient();

        var list = await DownloadTextAsync(http, update.Checksums.Url, UpdateRules, cancel).ConfigureAwait(false);
        if (!UpdatePackages.ParseChecksums(list).TryGetValue(package.Name, out var expected))
            throw new UpdateVerificationException($"{package.Name} absent de {update.Checksums.Name}");

        var path = await DownloadCheckedAsync(http, package, expected, UpdateRules, folder, progress, cancel).ConfigureAwait(false);
        return new VerifiedDownload(path, expected);
    }

    /// <summary>
    /// Downloads a file whose SHA-256 is written in Snaplume itself (the ffmpeg module, <see cref="Player.FfmpegModule"/>),
    /// with the same checks as an update: allowed addresses and redirects only, exact size, ".part" file until verified,
    /// a folder of this user's only.
    /// </summary>
    /// <param name="rules">Where the download may start and be redirected to.</param>
    /// <returns>Full path of the verified file.</returns>
    /// <exception cref="UpdateVerificationException">The SHA-256 differs (the file was deleted).</exception>
    public async Task<string> DownloadPinnedAsync(ReleaseAsset file, string expectedSha256, AddressRules rules, string folder,
        IProgress<long>? progress, CancellationToken cancel)
    {
        if (!UpdatePackages.IsPlainFileName(file.Name)) throw new InvalidDataException($"Nom de fichier refusé : {file.Name}");
        using var http = CreateClient();
        return await DownloadCheckedAsync(http, file, expectedSha256.ToLowerInvariant(), rules, folder, progress, cancel).ConfigureAwait(false);
    }

    /// <summary>
    /// Downloads into a new ".part" file of <paramref name="folder"/> (never an existing file or link reused), renamed to
    /// the file's name once its SHA-256 is <paramref name="expected"/> (lowercase hexadecimal).
    /// </summary>
    private static async Task<string> DownloadCheckedAsync(HttpClient http, ReleaseAsset file, string expected, AddressRules rules, string folder,
        IProgress<long>? progress, CancellationToken cancel)
    {
        PrepareFolder(folder);
        var path = Path.Combine(folder, file.Name);
        var part = path + ".part";
        // Left by an interrupted download: deleted (a link itself, not what it points to), then created anew.
        File.Delete(part);
        try
        {
            string actual;
            await using (var stream = new FileStream(part, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                actual = await DownloadToAsync(http, file, rules, stream, progress, cancel).ConfigureAwait(false);
            }
            if (actual != expected)
                throw new UpdateVerificationException($"Somme SHA-256 de {file.Name} différente : {actual} au lieu de {expected}");
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    /// <summary>
    /// A folder for downloads that only this user can write to, created if needed: %LOCALAPPDATA%\Snaplume\downloads\
    /// <paramref name="name"/> on Windows; $XDG_CACHE_HOME/snaplume/<paramref name="name"/> (~/.cache by default)
    /// elsewhere, created with no access for other users. Never the shared temporary folder (/tmp), where anyone may
    /// create the folder or a link first.
    /// </summary>
    /// <param name="name">A plain folder name ("update", "ffmpeg").</param>
    /// <exception cref="IOException">The folder is a link, or other users may write to it.</exception>
    /// <exception cref="UnauthorizedAccessException">The folder belongs to another user.</exception>
    public static string PrivateFolder(string name)
    {
        if (OperatingSystem.IsWindows())
            return PrivateFolderIn(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snaplume", "downloads"), name);
        var cache = Environment.GetEnvironmentVariable("XDG_CACHE_HOME");
        if (string.IsNullOrEmpty(cache) || !Path.IsPathFullyQualified(cache))
            cache = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache");
        return PrivateFolderIn(Path.Combine(cache, "snaplume"), name);
    }

    /// <summary><see cref="PrivateFolder"/> in another parent folder (the tests).</summary>
    internal static string PrivateFolderIn(string parent, string name)
    {
        if (!UpdatePackages.IsPlainFileName(name)) throw new ArgumentException($"Nom de dossier refusé : {name}", nameof(name));
        var folder = Path.Combine(parent, name);
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(folder);
            if (new DirectoryInfo(folder).Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException($"Dossier de téléchargement refusé (lien) : {folder}");
            return folder;
        }
        PrepareFolder(parent);
        PrepareFolder(folder);
        return folder;
    }

    /// <summary>
    /// Creates the folder of a download. On Linux and macOS, created with no access for other users; refused when it
    /// is a link or others may write to it, or when it belongs to another user (only its owner can change its mode,
    /// done here to take the access of others away).
    /// </summary>
    private static void PrepareFolder(string folder)
    {
        if (OperatingSystem.IsWindows())
        {
            // The temporary and application data folders of Windows are the user's own.
            Directory.CreateDirectory(folder);
            return;
        }
        const UnixFileMode OwnerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        var info = Directory.CreateDirectory(folder, OwnerOnly);
        if (info.LinkTarget != null) throw new IOException($"Dossier de téléchargement refusé (lien symbolique) : {folder}");
        if ((info.UnixFileMode & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw new IOException($"Dossier de téléchargement refusé (modifiable par d'autres utilisateurs) : {folder}");
        // Fails (UnauthorizedAccessException) unless this user owns the folder.
        File.SetUnixFileMode(folder, OwnerOnly);
    }

    /// <summary>
    /// The file's SHA-256 is <paramref name="expectedHex"/> (hexadecimal, any case), compared in constant time: checked
    /// again right before a downloaded package is run (<see cref="VerifiedDownload.Sha256"/>).
    /// </summary>
    /// <returns>False when it differs, or the file cannot be read.</returns>
    public static bool HasSha256(string path, string expectedHex)
    {
        byte[] expected;
        try
        {
            expected = Convert.FromHexString(expectedHex);
        }
        catch (FormatException)
        {
            return false;
        }
        if (expected.Length != SHA256.HashSizeInBytes) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(stream), expected);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Vérification d'un fichier téléchargé", ex);
            return false;
        }
    }

    /// <summary>Addresses a download may start from, and those it may be redirected to.</summary>
    public sealed record AddressRules(Func<Uri, bool> IsAllowed, Func<Uri, bool> IsAllowedRedirect);

    /// <summary>The project's release files, and GitHub's storage behind them.</summary>
    private static readonly AddressRules UpdateRules = new(UpdatePackages.IsAllowedDownload, UpdatePackages.IsAllowedRedirect);

    private HttpClient CreateClient()
    {
        var handler = _handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        // No overall timeout: a slow connection may need minutes, and the user can cancel.
        var http = new HttpClient(handler, disposeHandler: _handler == null) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snaplume", UpdateChecker.CurrentVersion.ToString(3)));
        return http;
    }

    private static async Task<string> DownloadTextAsync(HttpClient http, Uri url, AddressRules rules, CancellationToken cancel)
    {
        using var response = await GetAsync(http, url, rules, cancel).ConfigureAwait(false);
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        var body = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, cancel).ConfigureAwait(false)) > 0)
        {
            body.Write(chunk, 0, read);
            if (body.Length > MaxChecksumsBytes) throw new InvalidDataException("Liste des sommes de contrôle trop longue");
        }
        return Encoding.UTF8.GetString(body.ToArray());
    }

    /// <returns>Lowercase hexadecimal SHA-256 of what was written.</returns>
    private static async Task<string> DownloadToAsync(HttpClient http, ReleaseAsset package, AddressRules rules, Stream target, IProgress<long>? progress,
        CancellationToken cancel)
    {
        using var response = await GetAsync(http, package.Url, rules, cancel).ConfigureAwait(false);
        if (response.Content.Headers.ContentLength is { } length && length != package.Size)
            throw new InvalidDataException($"Taille inattendue pour {package.Name} : {length} octets au lieu de {package.Size}");
        await using var stream = await response.Content.ReadAsStreamAsync(cancel).ConfigureAwait(false);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long received = 0;
        long reported = 0;
        int read;
        while ((read = await stream.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            received += read;
            if (received > package.Size) throw new InvalidDataException($"{package.Name} dépasse la taille annoncée ({package.Size} octets)");
            hash.AppendData(buffer, 0, read);
            await target.WriteAsync(buffer.AsMemory(0, read), cancel).ConfigureAwait(false);
            // About every 256 KB, not for each block.
            if (received - reported >= 256 * 1024 || received == package.Size)
            {
                reported = received;
                progress?.Report(received);
            }
        }
        if (received != package.Size) throw new InvalidDataException($"Téléchargement incomplet de {package.Name} : {received} octets sur {package.Size}");
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    /// <summary>GET with the redirects followed by hand, each one checked against the allowed hosts.</summary>
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri url, AddressRules rules, CancellationToken cancel)
    {
        if (!rules.IsAllowed(url)) throw new InvalidDataException($"Adresse de téléchargement refusée : {url}");
        for (int redirects = 0; ; redirects++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancel).ConfigureAwait(false);
            if (IsRedirect(response.StatusCode))
            {
                var location = response.Headers.Location;
                response.Dispose();
                if (location == null) throw new InvalidDataException("Redirection sans adresse");
                if (redirects >= MaxRedirects) throw new InvalidDataException("Trop de redirections");
                var next = location.IsAbsoluteUri ? location : new Uri(url, location);
                if (!rules.IsAllowedRedirect(next)) throw new InvalidDataException($"Redirection refusée vers {next.Scheme}://{next.Authority}");
                url = next;
                continue;
            }
            try
            {
                response.EnsureSuccessStatusCode();
            }
            catch
            {
                response.Dispose();
                throw;
            }
            return response;
        }
    }

    private static bool IsRedirect(HttpStatusCode status) =>
        status is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression d'un téléchargement incomplet", ex);
        }
    }
}
