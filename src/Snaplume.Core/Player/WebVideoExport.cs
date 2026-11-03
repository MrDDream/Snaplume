using System.Globalization;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>An export that cannot be done or did not reach its size; the message is ready to show.</summary>
public sealed class WebExportException(string message) : Exception(message);

/// <summary>Who encodes a format: the system (<see cref="IVideoTools.TranscodeAsync"/>), ffmpeg, or nobody here.</summary>
public enum WebEncoderRoute
{
    Unavailable,
    System,
    Ffmpeg,
}

/// <summary>Whether a format can be made, by whom, and why not (shown under the greyed format).</summary>
/// <param name="Reason">Ready to show; "" when available.</param>
/// <param name="Encoder">The ffmpeg encoder used ("libsvtav1"...), or null.</param>
public sealed record WebFormatAvailability(WebFormat Format, WebEncoderRoute Route, string Reason, string? Encoder = null)
{
    public bool IsAvailable => Route != WebEncoderRoute.Unavailable;
}

/// <summary>
/// "Export / compress" of the player: a part of the video re-encoded in a lighter format, with a quality preset or
/// under a file size (<see cref="WebExportPlanner"/>). H.264 and HEVC go to the system's encoder when it has one, the
/// rest to ffmpeg (<see cref="FfmpegWebEncoder"/>). A file over the size is encoded again at a lower bit rate (twice
/// at most). Nothing leaves the computer; the source file is only read.
/// </summary>
public static class WebVideoExport
{
    /// <summary>Encodings of one export when a size is aimed at: the first one, and up to two smaller ones.</summary>
    public const int MaxAttempts = 3;

    /// <summary>Who can make <paramref name="format"/> on this computer.</summary>
    /// <param name="canTranscode">The system encoders (<see cref="IVideoTools.CanTranscode"/>).</param>
    /// <param name="windows">Windows: the missing ffmpeg is the module of the settings; Linux: the system package.</param>
    public static WebFormatAvailability Availability(WebFormat format, Func<VideoCodec, bool> canTranscode, FfmpegInfo? ffmpeg, bool windows)
    {
        if (WebFormats.SystemCodec(format) is { } codec && canTranscode(codec)) return new WebFormatAvailability(format, WebEncoderRoute.System, "");
        if (ffmpeg != null && FfmpegWebArguments.Choose(format, ffmpeg.Encoders, windows) is { } choice)
            return new WebFormatAvailability(format, WebEncoderRoute.Ffmpeg, "", choice.Video);
        if (format == WebFormat.Mp4Hevc)
            return new WebFormatAvailability(format, WebEncoderRoute.Unavailable, Loc.T(windows ? "Player.Web.NoHevcWindows" : "Player.Web.NoHevcLinux"));
        if (ffmpeg == null)
            return new WebFormatAvailability(format, WebEncoderRoute.Unavailable, Loc.T(windows ? "Player.Web.NeedsModule" : "Player.Web.NeedsFfmpeg"));
        return new WebFormatAvailability(format, WebEncoderRoute.Unavailable, Loc.T("Player.Web.MissingEncoder", MissingEncoderName(format, ffmpeg.Encoders)));
    }

    /// <summary>What this ffmpeg lacks for the format, for the message.</summary>
    private static string MissingEncoderName(WebFormat format, IReadOnlySet<string> encoders) => format switch
    {
        WebFormat.Mp4Av1 when !encoders.Contains("libsvtav1") && !encoders.Contains("libaom-av1") => "libsvtav1",
        WebFormat.Mp4Av1 => "libopus",
        WebFormat.WebmVp9 when !encoders.Contains("libvpx-vp9") => "libvpx-vp9",
        WebFormat.WebmVp9 => "libopus",
        WebFormat.WebP => "libwebp",
        _ => "libx264",
    };

    /// <summary>
    /// Writes the export to <paramref name="destination"/> (a new file: never overwrites; nothing is left behind if
    /// it fails or is cancelled). Runs on the thread pool.
    /// </summary>
    /// <param name="info">Size, frame rate and sound of the source.</param>
    /// <param name="progress">Receives 0 to 1 (again from 0 when a too large file is encoded again).</param>
    /// <param name="runner">For the tests; by default the real programs.</param>
    /// <exception cref="WebExportException">The format cannot be made here, or the size cannot be reached.</exception>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancel"/>.</exception>
    public static Task ExportAsync(IVideoTools tools, FfmpegInfo? ffmpeg, string source, string destination, WebExportSettings settings,
        VideoInfo info, IProgress<double>? progress, CancellationToken cancel, IProcessRunner? runner = null) =>
        Task.Run(() => RunAsync(tools, ffmpeg, source, destination, settings, info, progress, cancel, runner ?? ProcessRunner.Instance), cancel);

    private static async Task RunAsync(IVideoTools tools, FfmpegInfo? ffmpeg, string source, string destination, WebExportSettings settings,
        VideoInfo info, IProgress<double>? progress, CancellationToken cancel, IProcessRunner runner)
    {
        VideoGifExport.CheckDestination(source, destination);
        var format = settings.Format;
        var availability = Availability(format, tools.CanTranscode, ffmpeg, OperatingSystem.IsWindows());
        if (!availability.IsAvailable) throw new WebExportException(availability.Reason);
        var duration = settings.End - settings.Start;
        var plan = Plan(settings, info, out var refusal) ?? throw new WebExportException(refusal);

        var full = Path.GetFullPath(destination);
        // Encoded beside the target and renamed: a cancelled or failed export leaves nothing half-written.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.tmp{WebFormats.Extension(format)}");
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                cancel.ThrowIfCancellationRequested();
                if (availability.Route == WebEncoderRoute.System)
                {
                    await tools.TranscodeAsync(source, temp, settings.Start, settings.End,
                        plan.ToTranscode(WebFormats.SystemCodec(format)!.Value, settings.TargetBytes != null), progress, cancel).ConfigureAwait(false);
                }
                else
                {
                    await FfmpegWebEncoder.EncodeAsync(runner, ffmpeg!, format, plan, settings.TargetBytes != null, source, temp,
                        settings.Start, settings.End, info.HasAudio, progress, cancel).ConfigureAwait(false);
                }
                if (settings.TargetBytes is not { } target) break;
                long size = new FileInfo(temp).Length;
                if (size <= target) break;
                var next = attempt < MaxAttempts ? WebExportPlanner.Shrink(format, plan, size, target, duration, info) : null;
                await VideoGifExport.DeleteQuietlyAsync(temp).ConfigureAwait(false);
                if (next == null) throw new WebExportException(Loc.T("Player.Web.TargetMissed", Megabytes(size), Megabytes(target)));
                ErrorLog.Write("Export compressé", $"{Megabytes(size):0.##} Mo au lieu de {Megabytes(target):0.##} Mo : nouvel essai ({next})");
                plan = next;
            }
            File.Move(temp, full);
            progress?.Report(1);
        }
        catch
        {
            await VideoGifExport.DeleteQuietlyAsync(temp).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The plan of the settings, or null with the reason (ready to show) when the size cannot be reached.</summary>
    public static WebEncodePlan? Plan(WebExportSettings settings, VideoInfo info, out string refusal)
    {
        refusal = "";
        if (settings.TargetBytes is not { } target) return WebExportPlanner.PlanForQuality(settings.Format, settings.Quality, info);
        var result = WebExportPlanner.PlanForTarget(settings.Format, target, settings.End - settings.Start, info);
        if (result.Plan == null) refusal = Loc.T("Player.Web.TooLong", Megabytes(target), FormatDuration(result.MaxDuration));
        return result.Plan;
    }

    public static double Megabytes(long bytes) => (double)bytes / SizeTargets.BytesPerMegabyte;

    /// <summary>"1:05", "12:30" or "1:02:03".</summary>
    public static string FormatDuration(TimeSpan time) =>
        time.TotalHours >= 1
            ? time.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{(int)time.TotalMinutes}:{time.Seconds:00}");

    /// <summary>
    /// Base name of the export, next to the video: "name.discord", "name.av1", "name.20mb.hevc", "name.compressed"
    /// (the extension is added by the caller).
    /// </summary>
    public static string BaseName(string source, WebExportSettings settings)
    {
        var name = Path.GetFileNameWithoutExtension(source);
        var parts = new List<string> { name };
        if (settings.TargetBytes is { } target)
        {
            parts.Add(settings.TargetId is SizeTargets.Discord or SizeTargets.Nitro or SizeTargets.Email
                ? settings.TargetId
                : string.Create(CultureInfo.InvariantCulture, $"{Megabytes(target):0.#}mb"));
        }
        switch (settings.Format)
        {
            case WebFormat.Mp4Hevc:
                parts.Add("hevc");
                break;
            case WebFormat.Mp4Av1:
                parts.Add("av1");
                break;
            case WebFormat.Mp4H264 when settings.TargetBytes == null:
                // Same extension as the source: a word tells them apart.
                parts.Add("compressed");
                break;
        }
        return FileOps.SanitizeFileName(string.Join('.', parts));
    }
}
