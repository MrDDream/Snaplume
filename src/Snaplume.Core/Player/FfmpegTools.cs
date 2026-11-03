using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>Where the ffmpeg used by the exports comes from.</summary>
public enum FfmpegSource
{
    /// <summary>The complementary module installed by Snaplume (Windows, <see cref="FfmpegModule"/>).</summary>
    Module,
    /// <summary>Found in PATH on Windows (installed by the user).</summary>
    Path,
    /// <summary>The system's package (Linux, a dependency of Snaplume's package).</summary>
    System,
}

/// <summary>An ffmpeg program and what it can encode.</summary>
/// <param name="Version">As printed by "ffmpeg -version" ("n8.1.3-9-g29e619e767", "7.1.1"...), or "".</param>
/// <param name="Encoders">
/// Encoder names of "ffmpeg -encoders" ("libsvtav1", "libvpx-vp9"...); the hardware ones only when they worked on
/// this PC (<see cref="FfmpegWebArguments.HardwareEncoders"/>).
/// </param>
public sealed record FfmpegInfo(string Path, FfmpegSource Source, string Version, IReadOnlySet<string> Encoders);

/// <summary>What a finished program left behind.</summary>
public sealed record ProcessOutcome(int ExitCode, string Output, string Errors);

/// <summary>Runs a command-line program; replaced by a fake in the tests.</summary>
public interface IProcessRunner
{
    /// <summary>Runs <paramref name="executable"/> to the end (killed when cancelled or too long).</summary>
    /// <param name="outputLine">Receives each line of the standard output as it comes, or null.</param>
    /// <exception cref="TimeoutException">The program ran longer than <paramref name="timeout"/>.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancel"/>.</exception>
    Task<ProcessOutcome> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine, TimeSpan? timeout,
        CancellationToken cancel);
}

/// <summary>
/// Starts programs without a shell: arguments are passed one by one (ArgumentList), so a file name with quotes,
/// spaces or "$(...)" stays a file name. No console window on Windows.
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    public static readonly ProcessRunner Instance = new();

    /// <summary>Output kept for the caller (encoders list, probe); progress lines beyond it are only passed on.</summary>
    private const int MaxKeptChars = 1 << 20;

    public async Task<ProcessOutcome> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine, TimeSpan? timeout,
        CancellationToken cancel)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = false,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException($"{executable} could not be started");
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        if (timeout is { } limit) timer.CancelAfter(limit);
        Task<string>? errors = null;
        try
        {
            errors = ReadKeptAsync(process.StandardError, timer.Token);
            var output = new StringBuilder();
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(timer.Token).ConfigureAwait(false)) != null)
            {
                outputLine?.Invoke(line);
                if (output.Length < MaxKeptChars) output.Append(line).Append('\n');
            }
            await process.WaitForExitAsync(timer.Token).ConfigureAwait(false);
            return new ProcessOutcome(process.ExitCode, output.ToString(), await errors.ConfigureAwait(false));
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            Observe(errors);
            if (cancel.IsCancellationRequested) throw;
            throw new TimeoutException($"{Path.GetFileName(executable)} did not finish within {timeout?.TotalSeconds:0} s");
        }
        catch
        {
            Kill(process);
            Observe(errors);
            throw;
        }
    }

    /// <summary>The error output, the first <see cref="MaxKeptChars"/> characters kept, the rest read and dropped.</summary>
    private static async Task<string> ReadKeptAsync(StreamReader reader, CancellationToken cancel)
    {
        var kept = new StringBuilder();
        var buffer = new char[4096];
        int read;
        while ((read = await reader.ReadAsync(buffer, cancel).ConfigureAwait(false)) > 0)
        {
            if (kept.Length < MaxKeptChars) kept.Append(buffer, 0, Math.Min(read, MaxKeptChars - kept.Length));
        }
        return kept.ToString();
    }

    /// <summary>The error output is no longer awaited once the program is killed: its failure is observed, not raised later.</summary>
    private static void Observe(Task? task) =>
        task?.ContinueWith(t => _ = t.Exception, CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

    private static void Kill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception)
        {
            // Already gone.
        }
    }
}

/// <summary>
/// Finds ffmpeg (the module of <see cref="FfmpegModule"/>, then PATH) and reads what it can do; parses its output.
/// The answer is kept until <see cref="Invalidate"/> (install or removal of the module).
/// </summary>
public static class FfmpegTools
{
    private static readonly object Gate = new();
    private static Task<FfmpegInfo?>? _detected;

    /// <summary>The ffmpeg the exports use, or null when there is none (or it does not start).</summary>
    public static Task<FfmpegInfo?> DetectAsync()
    {
        lock (Gate)
        {
            // On the thread pool: the search (files, PATH, programs started) never runs on the caller's thread.
            return _detected ??= Task.Run(DetectSafelyAsync);
        }
    }

    /// <summary>Forgets the ffmpeg found: the next <see cref="DetectAsync"/> looks again.</summary>
    public static void Invalidate()
    {
        lock (Gate) _detected = null;
    }

    private static async Task<FfmpegInfo?> DetectSafelyAsync()
    {
        try
        {
            return await DetectAsync(ProcessRunner.Instance, FfmpegModule.IsSupported ? FfmpegModule.ExecutablePath : null,
                Environment.GetEnvironmentVariable("PATH"), OperatingSystem.IsWindows()).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Recherche de ffmpeg", ex);
            return null;
        }
    }

    /// <param name="modulePath">ffmpeg of the module, used first when the file exists.</param>
    /// <param name="pathVariable">The PATH searched next.</param>
    internal static async Task<FfmpegInfo?> DetectAsync(IProcessRunner runner, string? modulePath, string? pathVariable, bool windows)
    {
        var candidates = new List<(string Path, FfmpegSource Source)>();
        if (modulePath != null && File.Exists(modulePath)) candidates.Add((modulePath, FfmpegSource.Module));
        if (FindInPath(pathVariable, windows) is { } found) candidates.Add((found, windows ? FfmpegSource.Path : FfmpegSource.System));
        foreach (var (path, source) in candidates)
        {
            try
            {
                var timeout = TimeSpan.FromSeconds(20);
                var version = await runner.RunAsync(path, new[] { "-hide_banner", "-version" }, null, timeout, CancellationToken.None).ConfigureAwait(false);
                var encoders = await runner.RunAsync(path, new[] { "-hide_banner", "-encoders" }, null, timeout, CancellationToken.None)
                    .ConfigureAwait(false);
                if (encoders.ExitCode != 0) throw new InvalidOperationException($"ffmpeg -encoders ({encoders.ExitCode}): {encoders.Errors.Trim()}");
                var usable = ParseEncoders(encoders.Output);
                await DropBrokenHardwareAsync(runner, path, usable).ConfigureAwait(false);
                return new FfmpegInfo(path, source, ParseVersion(version.Output), usable);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A damaged module (missing DLL...): the one in PATH, if any.
                ErrorLog.Write($"Démarrage de ffmpeg ({path})", ex);
            }
        }
        return null;
    }

    /// <summary>
    /// Removes from <paramref name="encoders"/> the hardware encoders that fail a tiny encoding: ffmpeg lists them
    /// whatever the graphics card (an NVIDIA encoder on an Intel PC, no driver...).
    /// </summary>
    private static async Task DropBrokenHardwareAsync(IProcessRunner runner, string ffmpeg, HashSet<string> encoders)
    {
        foreach (var encoder in FfmpegWebArguments.HardwareEncoders.Where(encoders.Contains).ToList())
        {
            bool works;
            try
            {
                var result = await runner.RunAsync(ffmpeg, FfmpegWebArguments.ProbeEncoder(encoder), null, TimeSpan.FromSeconds(15),
                    CancellationToken.None).ConfigureAwait(false);
                works = result.ExitCode == 0;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Timed out, did not start, crashed the driver...: not usable here, the other encoders still are.
                works = false;
            }
            if (!works) encoders.Remove(encoder);
        }
    }

    /// <summary>
    /// The ffmpeg executable of the first PATH folder that has one; relative entries (".", "bin") are skipped, the
    /// answer would depend on the current folder.
    /// </summary>
    internal static string? FindInPath(string? pathVariable, bool windows)
    {
        // A session started by a minimal launcher may have no PATH: the usual folders of Linux.
        if (string.IsNullOrEmpty(pathVariable)) pathVariable = windows ? null : "/usr/local/bin:/usr/bin:/bin";
        if (string.IsNullOrEmpty(pathVariable)) return null;
        var name = windows ? "ffmpeg.exe" : "ffmpeg";
        foreach (var raw in pathVariable.Split(windows ? ';' : ':', StringSplitOptions.RemoveEmptyEntries))
        {
            var folder = raw.Trim().Trim('"');
            if (folder.Length == 0 || !Path.IsPathFullyQualified(folder)) continue;
            try
            {
                var candidate = Path.Combine(folder, name);
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception)
            {
                // An invalid entry: the next one.
            }
        }
        return null;
    }

    /// <summary>"ffmpeg version n8.1.3-9-g29e619e767 Copyright (c)..." → "n8.1.3-9-g29e619e767".</summary>
    internal static string ParseVersion(string text)
    {
        var match = Regex.Match(text, @"ffmpeg version (\S+)");
        return match.Success ? match.Groups[1].Value : "";
    }

    /// <summary>
    /// The version for people: "n8.1.3-9-g29e619e767" → "8.1.3", "7.1.1-1ubuntu2" → "7.1.1", "N-121000-g1234abcd" (a
    /// build of the development branch) → "N-121000"; anything else as is, "" when unknown.
    /// </summary>
    public static string ShortVersion(string version)
    {
        var release = Regex.Match(version, @"^n?(\d+(?:\.\d+)+)");
        if (release.Success) return release.Groups[1].Value;
        var master = Regex.Match(version, @"^(N-\d+)");
        return master.Success ? master.Groups[1].Value : version;
    }

    /// <summary>Encoder names from "ffmpeg -encoders" (lines like " V....D libx264  libx264 H.264 ...").</summary>
    public static HashSet<string> ParseEncoders(string text)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        bool table = false;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("------", StringComparison.Ordinal))
            {
                table = true;
                continue;
            }
            if (!table) continue;
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[0].Length == 6) result.Add(parts[1]);
        }
        return result;
    }

    /// <summary>Progress between 0 and 1 from a "-progress" line ("out_time_us=1234567"), or null.</summary>
    public static double? ParseProgress(string line, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero) return null;
        foreach (var key in new[] { "out_time_us=", "out_time_ms=" }) // out_time_ms is in microseconds too (historical ffmpeg bug)
        {
            if (line.StartsWith(key, StringComparison.Ordinal)
                && long.TryParse(line.AsSpan(key.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) && us >= 0)
                return Math.Clamp(us / 1_000_000.0 / duration.TotalSeconds, 0, 1);
        }
        return line.Trim() == "progress=end" ? 1 : null;
    }

    /// <summary>
    /// What "ffmpeg -i file" prints of the input on its error output: "Duration: 00:01:02.50", then
    /// "Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv), 1920x1080 [SAR 1:1 DAR 16:9], 4500 kb/s, 30 fps, ..."
    /// and "Stream #0:1: Audio: aac ...". Null without a video stream.
    /// </summary>
    /// <remarks>
    /// Only the description of the first input is read (from "Input #0" to the next "Input #" or "Output #"), and only
    /// its own "Stream #0:" lines: a warning printed before it, or a second input, cannot pass for the file. Values out
    /// of reason (a side over <see cref="MaxVideoSide"/>, a length over <see cref="MaxDuration"/>) are left unknown.
    /// </remarks>
    public static VideoInfo? ParseInputInfo(string text)
    {
        var f = CultureInfo.InvariantCulture;
        int width = 0, height = 0;
        double fps = 0;
        bool audio = false, video = false, input = false;
        var duration = TimeSpan.Zero;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Input #", StringComparison.Ordinal) || line.StartsWith("Output #", StringComparison.Ordinal))
            {
                if (input) break;
                input = line.StartsWith("Input #0,", StringComparison.Ordinal);
                continue;
            }
            if (!input) continue;
            var d = Regex.Match(line, @"^Duration: (\d{1,6}):(\d{2}):(\d{2}(?:\.\d{1,6})?)");
            if (d.Success)
            {
                if (int.TryParse(d.Groups[1].Value, NumberStyles.None, f, out int hours) && int.TryParse(d.Groups[2].Value, NumberStyles.None, f, out int minutes)
                    && double.TryParse(d.Groups[3].Value, NumberStyles.AllowDecimalPoint, f, out double seconds))
                {
                    var length = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes) + TimeSpan.FromSeconds(seconds);
                    if (length <= MaxDuration) duration = length;
                }
                continue;
            }
            if (!line.StartsWith("Stream #0:", StringComparison.Ordinal)) continue;
            if (line.Contains(": Audio:", StringComparison.Ordinal))
            {
                audio = true;
                continue;
            }
            if (video || !line.Contains(": Video:", StringComparison.Ordinal)) continue;
            // A cover picture of an audio file is no video.
            if (line.Contains("(attached pic)", StringComparison.Ordinal)) continue;
            var size = Regex.Match(line, @"[ ,](\d{2,5})x(\d{2,5})[ ,\[]");
            if (!size.Success || !int.TryParse(size.Groups[1].Value, NumberStyles.None, f, out int w) || !int.TryParse(size.Groups[2].Value, NumberStyles.None, f, out int h)
                || w is < 1 or > MaxVideoSide || h is < 1 or > MaxVideoSide)
                continue;
            video = true;
            width = w;
            height = h;
            var rate = Regex.Match(line, @", (\d{1,6}(?:\.\d{1,6})?)(k?) fps");
            if (rate.Success && double.TryParse(rate.Groups[1].Value, NumberStyles.AllowDecimalPoint, f, out double r))
            {
                r *= rate.Groups[2].Value == "k" ? 1000 : 1;
                if (r <= MaxFramesPerSecond) fps = r;
            }
        }
        return video ? new VideoInfo(width, height, fps, audio, duration) : null;
    }

    /// <summary>Largest picture side believed from ffmpeg's description (the encoders stop at 16384).</summary>
    public const int MaxVideoSide = 16384;

    /// <summary>Longest video believed from ffmpeg's description; a longer one has an unknown length.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(1000);

    /// <summary>Highest frame rate believed from ffmpeg's description; a higher one is unknown (0).</summary>
    public const double MaxFramesPerSecond = 1000;
}
