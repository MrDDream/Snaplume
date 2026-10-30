using System.Globalization;

namespace Snaplume.Core;

/// <summary>errors.log in the settings folder; may be written from any thread.</summary>
public static class ErrorLog
{
    /// <summary>Above this size the log becomes errors.log.old (one previous file is kept).</summary>
    private const long MaxSize = 1024 * 1024;

    private static readonly object Gate = new();

    public static string FilePath { get; } = Path.Combine(AppSettings.SettingsFolder, "errors.log");

    public static void Write(string context, Exception ex) => Append($"{context}{Environment.NewLine}{ex}");

    /// <summary>Problem without an exception, e.g. an ignored command-line argument.</summary>
    public static void Write(string context, string message) => Append($"{context}{Environment.NewLine}{message}");

    private static void Append(string entry)
    {
        try
        {
            var time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
            lock (Gate)
            {
                Directory.CreateDirectory(AppSettings.SettingsFolder);
                var file = new FileInfo(FilePath);
                if (file.Exists && file.Length > MaxSize) File.Move(FilePath, FilePath + ".old", overwrite: true);
                File.AppendAllText(FilePath, $"[{time}] {entry}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never crash the application.
        }
    }
}
