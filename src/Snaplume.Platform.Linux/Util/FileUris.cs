using System.Text;

namespace Snaplume.Platform.Linux.Util;

/// <summary>
/// file:// URIs as the desktop exchanges them (portals, file manager, clipboard, GStreamer): every byte of the
/// UTF-8 path outside the unreserved set is percent-encoded, so the result never contains spaces, quotes or '#'.
/// </summary>
internal static class FileUris
{
    public static string FromPath(string path)
    {
        if (!path.StartsWith('/')) throw new ArgumentException("Absolute path expected", nameof(path));
        var builder = new StringBuilder("file://", path.Length + 16);
        foreach (var b in Encoding.UTF8.GetBytes(path))
        {
            char c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '_' or '.' or '~')
                builder.Append(c);
            else
                builder.Append('%').Append(b.ToString("X2"));
        }
        return builder.ToString();
    }

    /// <summary>Local path of a file:// URI ("file:///a%20b", "file://localhost/a"), or null for anything else.</summary>
    public static string? ToPath(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return null;
        uri = uri.Trim();
        if (!uri.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return null;
        var rest = uri.Substring("file://".Length);
        if (rest.StartsWith("localhost/", StringComparison.OrdinalIgnoreCase)) rest = rest.Substring("localhost".Length);
        if (!rest.StartsWith('/')) return null; // another host: not a local file
        var bytes = new List<byte>(rest.Length);
        for (int i = 0; i < rest.Length; i++)
        {
            if (rest[i] == '%' && i + 2 < rest.Length && IsHex(rest[i + 1]) && IsHex(rest[i + 2]))
            {
                bytes.Add(Convert.ToByte(rest.Substring(i + 1, 2), 16));
                i += 2;
            }
            else
            {
                bytes.AddRange(Encoding.UTF8.GetBytes(rest[i].ToString()));
            }
        }
        var path = Encoding.UTF8.GetString(bytes.ToArray());
        return path.Contains('\0') ? null : path;
    }

    private static bool IsHex(char c) => char.IsAsciiHexDigit(c);

    /// <summary>text/uri-list content: one URI per line, CRLF separated (RFC 2483).</summary>
    public static string UriList(IEnumerable<string> paths) =>
        string.Concat(paths.Select(p => FromPath(p) + "\r\n"));

    /// <summary>Paths of a text/uri-list, skipping comments and non-local URIs.</summary>
    public static IReadOnlyList<string> ParseUriList(string text) =>
        text.Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(ToPath)
            .OfType<string>()
            .ToList();
}
