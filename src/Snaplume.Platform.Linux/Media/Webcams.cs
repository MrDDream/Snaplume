using System.Text.RegularExpressions;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Video4Linux cameras: /dev/videoN with their name in /sys/class/video4linux/videoN/name. A camera usually
/// registers several nodes (picture, metadata); only the first node of each device (index 0) records pictures.
/// </summary>
internal static partial class Webcams
{
    [GeneratedRegex(@"^video\d+$")]
    private static partial Regex NodeName();

    public static IReadOnlyList<WebcamInfo> List() => List("/sys/class/video4linux", "/dev");

    internal static IReadOnlyList<WebcamInfo> List(string sysFolder, string devFolder)
    {
        var result = new List<(int Number, WebcamInfo Info)>();
        try
        {
            if (!Directory.Exists(sysFolder)) return Array.Empty<WebcamInfo>();
            foreach (var folder in Directory.EnumerateDirectories(sysFolder).Concat(SymlinkedEntries(sysFolder)).Distinct())
            {
                var node = Path.GetFileName(folder);
                if (!NodeName().IsMatch(node)) continue;
                var index = ReadLine(Path.Combine(folder, "index"));
                if (index != null && index != "0") continue;
                var device = Path.Combine(devFolder, node).Replace('\\', '/');
                var name = ReadLine(Path.Combine(folder, "name")) ?? node;
                result.Add((int.Parse(node.AsSpan(5)), new WebcamInfo(device, name)));
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Core.ErrorLog.Write("Liste des webcams", ex);
        }
        return result.OrderBy(r => r.Number).Select(r => r.Info).ToList();
    }

    /// <summary>The entries of /sys/class are symbolic links, which EnumerateDirectories may not list as folders.</summary>
    private static IEnumerable<string> SymlinkedEntries(string folder) =>
        Directory.EnumerateFileSystemEntries(folder).Where(e => new FileInfo(e).LinkTarget != null);

    private static string? ReadLine(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } text ? text : null : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>The camera to use: the chosen one if connected, the first one for an empty choice, else null.</summary>
    public static WebcamInfo? Resolve(IReadOnlyList<WebcamInfo> cameras, string? device) =>
        string.IsNullOrEmpty(device) ? cameras.FirstOrDefault() : cameras.FirstOrDefault(c => c.Id == device);
}
