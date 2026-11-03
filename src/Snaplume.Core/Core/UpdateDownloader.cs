using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;

namespace Snaplume.Core;

/// <summary>The downloaded file is not the one the release vouches for (it was deleted).</summary>
public sealed class UpdateVerificationException(string message) : Exception(message);

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
    /// <param name="progress">Bytes received so far (out of <see cref="ReleaseAsset.Size"/>).</param>
    /// <returns>Full path of the verified package.</returns>
    /// <exception cref="UpdateVerificationException">The SHA-256 differs from the list, or the file is not in it.</exception>
    /// <exception cref="HttpRequestException">No connection, or GitHub answered with an error.</exception>
    /// <exception cref="InvalidDataException">Refused address or redirect, unexpected size.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/>.</exception>
    public async Task<string> DownloadAsync(UpdatePackage update, string folder, IProgress<long>? progress, CancellationToken cancel)
    {
        var package = update.Package;
        if (!UpdatePackages.IsPlainFileName(package.Name)) throw new InvalidDataException($"Nom de fichier refusé : {package.Name}");
        using var http = CreateClient();

        var list = await DownloadTextAsync(http, update.Checksums.Url, cancel).ConfigureAwait(false);
        if (!UpdatePackages.ParseChecksums(list).TryGetValue(package.Name, out var expected))
            throw new UpdateVerificationException($"{package.Name} absent de {update.Checksums.Name}");

        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, package.Name);
        var part = path + ".part";
        try
        {
            string actual;
            await using (var file = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                actual = await DownloadToAsync(http, package, file, progress, cancel).ConfigureAwait(false);
            }
            if (actual != expected)
                throw new UpdateVerificationException($"Somme SHA-256 de {package.Name} différente : {actual} au lieu de {expected}");
            File.Move(part, path, overwrite: true);
            return path;
        }
        catch
        {
            TryDelete(part);
            throw;
        }
    }

    private HttpClient CreateClient()
    {
        var handler = _handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(20) };
        // No overall timeout: a slow connection may need minutes, and the user can cancel.
        var http = new HttpClient(handler, disposeHandler: _handler == null) { Timeout = Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Snaplume", UpdateChecker.CurrentVersion.ToString(3)));
        return http;
    }

    private static async Task<string> DownloadTextAsync(HttpClient http, Uri url, CancellationToken cancel)
    {
        using var response = await GetAsync(http, url, cancel).ConfigureAwait(false);
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
    private static async Task<string> DownloadToAsync(HttpClient http, ReleaseAsset package, Stream target, IProgress<long>? progress,
        CancellationToken cancel)
    {
        using var response = await GetAsync(http, package.Url, cancel).ConfigureAwait(false);
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
    private static async Task<HttpResponseMessage> GetAsync(HttpClient http, Uri url, CancellationToken cancel)
    {
        if (!UpdatePackages.IsAllowedDownload(url)) throw new InvalidDataException($"Adresse de téléchargement refusée : {url}");
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
                if (!UpdatePackages.IsAllowedRedirect(next)) throw new InvalidDataException($"Redirection refusée vers {next.Scheme}://{next.Authority}");
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
