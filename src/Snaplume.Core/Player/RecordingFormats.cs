using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>Compressions of Settings › Video › Video format: none, a quality preset, or a file size (<see cref="SizeTargets"/>).</summary>
public static class RecordingCompression
{
    /// <summary>The recording as the recorder makes it (MP4 H.264 is then never converted).</summary>
    public const string None = "none";
    public const string High = "high";
    public const string Medium = "medium";
    public const string Light = "light";

    /// <summary>In the order of the list: none, the presets, then the sizes of the export dialog.</summary>
    public static readonly string[] All =
        { None, High, Medium, Light, SizeTargets.Discord, SizeTargets.Nitro, SizeTargets.Email, SizeTargets.Custom };

    /// <summary>A known value, else <see cref="None"/>.</summary>
    public static string Normalize(string? id) => All.FirstOrDefault(c => c == id) ?? None;

    /// <summary>The compression aims at a file size.</summary>
    public static bool IsSize(string id) => id is SizeTargets.Discord or SizeTargets.Nitro or SizeTargets.Email or SizeTargets.Custom;

    /// <summary>The quality preset, or null for <see cref="None"/> and the sizes.</summary>
    public static WebQuality? Quality(string id) => id switch
    {
        High => WebQuality.High,
        Medium => WebQuality.Medium,
        Light => WebQuality.Light,
        _ => null,
    };
}

public static class RecordingFormats
{
    /// <summary>The formats a recording can be kept in: those of the export dialog but animated WebP (an image, no sound).</summary>
    public static readonly WebFormat[] All = { WebFormat.Mp4H264, WebFormat.Mp4Hevc, WebFormat.Mp4Av1, WebFormat.WebmVp9 };

    /// <summary>The format of a settings value, MP4 H.264 when unknown.</summary>
    public static WebFormat FromId(string? id) => WebFormats.FromId(id) is var format && All.Contains(format) ? format : WebFormat.Mp4H264;

    /// <summary>
    /// The formats listed in the settings, with the rules of the export dialog (<see cref="WebVideoExport.Offered"/>):
    /// MP4 H.264 alone until there is an ffmpeg; with one, the others too, greyed with the reason when this ffmpeg
    /// cannot make them.
    /// </summary>
    public static IReadOnlyList<WebFormatAvailability> Offered(Func<VideoCodec, bool> canTranscode, FfmpegInfo? ffmpeg, bool windows) =>
        WebVideoExport.Offered(canTranscode, ffmpeg, windows).Where(a => All.Contains(a.Format)).ToList();

    /// <summary>
    /// The export dialog of the player opens with the format and compression of the recordings, once they are saved
    /// in the settings (it then remembers its own last choices). "No compression" is the High quality preset there.
    /// </summary>
    public static void ApplyToExportDialog(RecordingFormatChoice choice, AppSettings settings)
    {
        settings.WebExportFormat = WebFormats.Id(choice.Format);
        settings.WebExportQuality = WebFormats.QualityId(choice.Quality ?? WebQuality.High);
        settings.WebExportTarget = RecordingCompression.IsSize(choice.Compression) ? choice.Compression : SizeTargets.Off;
        if (choice.Compression == SizeTargets.Custom) settings.WebExportMegabytes = choice.CustomMegabytes;
    }
}

/// <summary>Format and compression chosen for the recordings (Settings › Video).</summary>
/// <param name="Compression">One of <see cref="RecordingCompression.All"/>.</param>
/// <param name="CustomMegabytes">The size of <see cref="SizeTargets.Custom"/>, in MB.</param>
/// <param name="KeepOriginal">A converted recording keeps its MP4 H.264 original next to it.</param>
public sealed record RecordingFormatChoice(WebFormat Format, string Compression, double CustomMegabytes = 25, bool KeepOriginal = false)
{
    public static RecordingFormatChoice From(AppSettings settings) =>
        new(RecordingFormats.FromId(settings.VideoFormat), RecordingCompression.Normalize(settings.VideoCompression),
            settings.VideoCompressionMegabytes, settings.VideoKeepOriginal);

    public WebQuality? Quality => RecordingCompression.Quality(Compression);

    /// <summary>The largest file accepted in MB, or 0 without a size.</summary>
    public double TargetMegabytes => Compression == SizeTargets.Custom
        ? Math.Clamp(CustomMegabytes, SizeTargets.MinMegabytes, SizeTargets.MaxMegabytes)
        : SizeTargets.Megabytes(Compression);

    public long? TargetBytes => RecordingCompression.IsSize(Compression) ? (long)(TargetMegabytes * SizeTargets.BytesPerMegabyte) : null;
}

/// <summary>Why a recording is converted after it stops, or that it is not.</summary>
public enum RecordingRoute
{
    /// <summary>The recorder writes the chosen format and compression itself: no conversion.</summary>
    Direct,
    /// <summary>AV1 and WebM: no recorder encodes them live (ffmpeg would lose the system sound, the webcam, the clicks).</summary>
    NotLiveEncodable,
    /// <summary>HEVC, but the recorder has no HEVC encoder here (graphics card, HEVC Video Extensions, x265enc...).</summary>
    NoLiveEncoder,
    /// <summary>A file size: the bit rate depends on the final length, unknown while recording.</summary>
    SizeTarget,
}

/// <summary>The conversion that follows a recording (<see cref="RecordingConversion"/>).</summary>
/// <param name="TargetBytes">Largest file accepted, or null for <paramref name="Quality"/>.</param>
/// <param name="TargetId">The size chosen (<see cref="SizeTargets"/>), part of the file name when the original is kept.</param>
public sealed record RecordingConversionTarget(WebFormat Format, WebQuality Quality, long? TargetBytes, string TargetId)
{
    /// <summary>The whole recording.</summary>
    public WebExportSettings ToSettings(TimeSpan duration) => new(TimeSpan.Zero, duration, Format, Quality, TargetBytes, TargetId);
}

/// <summary>What the recorder is asked for, and the conversion afterwards (null when the recorder makes the file itself).</summary>
/// <param name="Framerate">Frames per second recorded (capped by the Medium and Light presets).</param>
public sealed record RecordingPlan(RecordingEncoding Encoding, int Framerate, RecordingRoute Route, RecordingConversionTarget? Conversion);

/// <summary>
/// Chooses between recording directly in the chosen format and compression, and recording MP4 H.264 then converting
/// the file once the recording stops. Pure, unit-tested.
/// </summary>
/// <remarks>
/// H.264, and HEVC when the recorder has an HEVC encoder, are recorded directly, the quality presets becoming the
/// recorder's quality (and picture size for Medium and Light, like <see cref="WebExportPlanner.PlanForQuality"/>).
/// AV1, WebM, HEVC without a live encoder, and every file size are recorded in H.264 at a finer quality, then converted.
/// </remarks>
public static class RecordingPlanner
{
    /// <summary>Quality of an H.264 recording that is converted afterwards: the conversion loses a little more.</summary>
    public const int IntermediateQuality = 80;

    /// <summary>The recorder's quality (1 to 100) for a preset; <see cref="RecordingEncoding.DefaultQuality"/> without one.</summary>
    public static int RecorderQuality(WebQuality? quality) => quality switch
    {
        WebQuality.High => 62,
        WebQuality.Medium => 50,
        WebQuality.Light => 40,
        _ => RecordingEncoding.DefaultQuality,
    };

    /// <summary>Shorter side of the picture for a preset (0: the size of the area), as the export presets do.</summary>
    public static int MaxShortSide(WebQuality? quality) => quality switch
    {
        WebQuality.Medium => 1080,
        WebQuality.Light => 720,
        _ => 0,
    };

    /// <summary>The Medium and Light presets record at most 30 frames per second.</summary>
    public static int Framerate(int framerate, WebQuality? quality) =>
        quality is WebQuality.Medium or WebQuality.Light ? Math.Min(framerate, (int)WebExportPlanner.DefaultFramesPerSecond) : framerate;

    /// <param name="framerate">Frames per second of the settings.</param>
    /// <param name="canRecord">What the recorder can encode live (<see cref="IScreenRecorderFactory.CanRecord"/>); asked for HEVC only.</param>
    public static RecordingPlan Plan(RecordingFormatChoice choice, int framerate, Func<VideoCodec, bool> canRecord)
    {
        var quality = choice.Quality;
        var intermediate = new RecordingEncoding(VideoCodec.H264, IntermediateQuality);
        if (choice.TargetBytes is { } target)
            return new RecordingPlan(intermediate, framerate, RecordingRoute.SizeTarget,
                new RecordingConversionTarget(choice.Format, WebQuality.High, target, choice.Compression));

        VideoCodec? live = choice.Format switch
        {
            WebFormat.Mp4H264 => VideoCodec.H264,
            WebFormat.Mp4Hevc => VideoCodec.Hevc,
            _ => null,
        };
        if (live is { } codec && (codec == VideoCodec.H264 || canRecord(codec)))
            return new RecordingPlan(new RecordingEncoding(codec, RecorderQuality(quality), MaxShortSide(quality)), Framerate(framerate, quality),
                RecordingRoute.Direct, null);
        return new RecordingPlan(intermediate, framerate, live == null ? RecordingRoute.NotLiveEncodable : RecordingRoute.NoLiveEncoder,
            new RecordingConversionTarget(choice.Format, quality ?? WebQuality.Original, null, SizeTargets.Off));
    }

    /// <summary>
    /// The conversion once the recording is written: the planned one; or, when the recorder could not start its HEVC
    /// encoder and wrote H.264 instead, a conversion to HEVC (the preset's size and quality were already applied).
    /// </summary>
    public static RecordingConversionTarget? AfterRecording(RecordingPlan plan, VideoCodec recorded)
    {
        if (plan.Conversion != null || plan.Encoding.Codec == recorded) return plan.Conversion;
        return new RecordingConversionTarget(WebFormat.Mp4Hevc, WebQuality.Original, null, SizeTargets.Off);
    }

    /// <summary>The sentence under the choice in the settings: recorded directly, or converted afterwards and why.</summary>
    public static string Describe(RecordingPlan plan, WebFormat format)
    {
        var name = Loc.T("Player.Web.Format." + WebFormats.Id(format));
        return plan.Route switch
        {
            RecordingRoute.NotLiveEncodable => Loc.T("Settings.VideoFormat.RouteNotLive", name),
            RecordingRoute.NoLiveEncoder => Loc.T("Settings.VideoFormat.RouteNoEncoder", name),
            RecordingRoute.SizeTarget => Loc.T("Settings.VideoFormat.RouteSize"),
            _ => Loc.T("Settings.VideoFormat.RouteDirect", name),
        };
    }
}

/// <summary>How the conversion of a recording ended.</summary>
/// <param name="Path">The file to show: the converted one, or the original when the conversion failed or was cancelled.</param>
/// <param name="Original">The MP4 H.264 original when it is kept (asked for, or the conversion did not succeed); else null.</param>
/// <param name="Problem">Why the original was kept instead (ready to show); null when converted or cancelled.</param>
/// <param name="OriginalLeftAt">
/// Converted, the original was to be kept but could not be moved into the workspace: where it stayed (the temporary
/// folder of the recordings); else null.
/// </param>
public sealed record RecordingConversionResult(string Path, string? Original, string? Problem, bool Cancelled, string? OriginalLeftAt = null)
{
    public bool Converted => Problem == null && !Cancelled;
}

/// <summary>
/// Converts a finished recording (MP4 H.264, still in the temporary folder) into the workspace. The converted file
/// takes the recording's name ("Video_….webm", "Video_….mp4"); the original is deleted only once the conversion
/// succeeded. Kept on request, it takes that name and the converted file gets the export's ("Video_….av1.mp4",
/// "Video_….discord.mp4"). When the conversion fails, is refused (too long for the size) or is cancelled, the
/// original goes to the workspace as if there had been no conversion, and the reason is returned.
/// </summary>
public static class RecordingConversion
{
    /// <summary>Encodes the whole of <paramref name="source"/> into the new file <paramref name="destination"/> (<see cref="WebVideoExport.ExportAsync"/>).</summary>
    public delegate Task Encoder(string source, string destination, WebExportSettings settings, VideoInfo info, IProgress<double>? progress,
        CancellationToken cancel);

    /// <param name="recorded">The recording (deleted or moved into <paramref name="folder"/>).</param>
    /// <param name="baseName">Name of the recording without extension ("Video_2026-11-03_14-22-05").</param>
    /// <param name="probe">Size, frame rate, sound and length of the recording, or null when unreadable.</param>
    /// <exception cref="IOException">The original could not be moved into the workspace (it stays where it is).</exception>
    public static async Task<RecordingConversionResult> RunAsync(string recorded, string folder, string baseName, RecordingConversionTarget target,
        bool keepOriginal, Func<string, CancellationToken, Task<VideoInfo?>> probe, Encoder encode, IProgress<double>? progress,
        CancellationToken cancel)
    {
        string? problem;
        try
        {
            var info = await probe(recorded, cancel).ConfigureAwait(false);
            if (info == null || info.Duration <= TimeSpan.Zero || info.Width <= 0 || info.Height <= 0)
                throw new InvalidDataException($"Vidéo illisible : {recorded}");
            var settings = target.ToSettings(info.Duration);
            // Too long for the size: said before encoding anything.
            if (WebVideoExport.Plan(settings, info, out var refusal) == null) throw new WebExportException(refusal);
            Directory.CreateDirectory(folder);
            var destination = FileOps.UniquePath(folder, ConvertedBaseName(baseName, settings, keepOriginal), WebFormats.Extension(target.Format));
            await encode(recorded, destination, settings, info, progress, cancel).ConfigureAwait(false);
            if (!keepOriginal)
            {
                await VideoGifExport.DeleteQuietlyAsync(recorded).ConfigureAwait(false);
                return new RecordingConversionResult(destination, null, null, false);
            }
            try
            {
                var original = await Task.Run(() => MoveIntoWorkspace(recorded, folder, baseName), CancellationToken.None).ConfigureAwait(false);
                return new RecordingConversionResult(destination, original, null, false);
            }
            catch (Exception ex)
            {
                // The conversion succeeded: only the copy asked for is missing, it stays in the temporary folder.
                ErrorLog.Write("Conservation de l'enregistrement d'origine", ex);
                return new RecordingConversionResult(destination, null, null, false, recorded);
            }
        }
        catch (Exception) when (cancel.IsCancellationRequested)
        {
            // Cancelled (ffmpeg killed): the original instead.
            var kept = await Task.Run(() => MoveIntoWorkspace(recorded, folder, baseName), CancellationToken.None).ConfigureAwait(false);
            return new RecordingConversionResult(kept, kept, null, true);
        }
        catch (WebExportException ex)
        {
            // Expected (size out of reach, format not available here): the message says what to do.
            ErrorLog.Write("Conversion de l'enregistrement", ex.Message);
            problem = ex.Message;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Conversion de l'enregistrement", ex);
            problem = Loc.T("Video.ConvertFailed");
        }
        var path = await Task.Run(() => MoveIntoWorkspace(recorded, folder, baseName), CancellationToken.None).ConfigureAwait(false);
        return new RecordingConversionResult(path, path, problem, false);
    }

    /// <summary>
    /// Name of the converted file: the recording's when it replaces it, else the export's ("Video_….hevc",
    /// "Video_….discord"; a WebM keeps the plain name, its extension differs).
    /// </summary>
    public static string ConvertedBaseName(string baseName, WebExportSettings settings, bool keepOriginal) =>
        keepOriginal ? WebVideoExport.BaseName(baseName + ".mp4", settings) : baseName;

    /// <summary>Moves the original into the workspace as "<paramref name="baseName"/>.mp4" (a free name).</summary>
    public static string MoveIntoWorkspace(string recorded, string folder, string baseName)
    {
        Directory.CreateDirectory(folder);
        var path = FileOps.UniquePath(folder, baseName, ".mp4");
        File.Move(recorded, path);
        return path;
    }
}
