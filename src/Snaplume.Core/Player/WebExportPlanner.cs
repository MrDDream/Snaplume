using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>Formats of the "Export / compress" dialog of the player.</summary>
public enum WebFormat
{
    /// <summary>MP4, H.264 + AAC: plays everywhere; encoded by the system (Windows media engine, ffmpeg under Linux).</summary>
    Mp4H264,
    /// <summary>
    /// MP4, HEVC (H.265) + AAC: about 40 % smaller than H.264; not played by every browser (Firefox). Needs an HEVC
    /// encoder: the Windows one (graphics card or "HEVC Video Extensions"), or ffmpeg (hardware encoders, libx265).
    /// </summary>
    Mp4Hevc,
    /// <summary>MP4, AV1 + Opus: about half the size of H.264 for the same picture, recent players only. Needs ffmpeg.</summary>
    Mp4Av1,
    /// <summary>WebM, VP9 + Opus: for web pages and browsers. Needs ffmpeg.</summary>
    WebmVp9,
    /// <summary>Animated WebP, without sound: a much lighter GIF. Needs ffmpeg.</summary>
    WebP,
}

/// <summary>Quality presets used when no file size is aimed at.</summary>
public enum WebQuality
{
    High,
    Medium,
    Light,
    /// <summary>
    /// As close to the source as the format allows (same size and frame rate, a finer quality than <see cref="High"/>):
    /// the conversion of a recording without compression (Settings › Video). Not offered by the export dialog.
    /// </summary>
    Original,
}

public static class WebFormats
{
    public static readonly WebFormat[] All = { WebFormat.Mp4H264, WebFormat.Mp4Hevc, WebFormat.Mp4Av1, WebFormat.WebmVp9, WebFormat.WebP };

    /// <summary>The format offered before ffmpeg is installed: MP4 H.264, encoded by the system.</summary>
    public static readonly WebFormat[] Basic = { WebFormat.Mp4H264 };

    /// <summary>The formats offered: <see cref="Basic"/> without ffmpeg, <see cref="All"/> with it.</summary>
    public static IReadOnlyList<WebFormat> Offered(bool hasFfmpeg) => hasFfmpeg ? All : Basic;

    public static string Extension(WebFormat format) => format switch
    {
        WebFormat.WebmVp9 => ".webm",
        WebFormat.WebP => ".webp",
        _ => ".mp4",
    };

    /// <summary>Name kept in the settings ("mp4", "av1", "webm", "webp").</summary>
    public static string Id(WebFormat format) => format switch
    {
        WebFormat.Mp4Hevc => "hevc",
        WebFormat.Mp4Av1 => "av1",
        WebFormat.WebmVp9 => "webm",
        WebFormat.WebP => "webp",
        _ => "mp4",
    };

    public static WebFormat FromId(string? id) => All.FirstOrDefault(f => Id(f) == id);

    /// <summary>H.264 and HEVC may be encoded by the system (<see cref="IVideoTools.TranscodeAsync"/>); the others need ffmpeg.</summary>
    public static VideoCodec? SystemCodec(WebFormat format) => format switch
    {
        WebFormat.Mp4H264 => VideoCodec.H264,
        WebFormat.Mp4Hevc => VideoCodec.Hevc,
        _ => null,
    };

    /// <summary>The sound is AAC in these MP4 files (the Windows encoder makes no Opus), Opus in the others.</summary>
    public static bool UsesAac(WebFormat format) => format is WebFormat.Mp4H264 or WebFormat.Mp4Hevc;

    /// <summary>Animated WebP has no sound.</summary>
    public static bool HasSound(WebFormat format) => format != WebFormat.WebP;

    public static string QualityId(WebQuality quality) => quality switch
    {
        WebQuality.High => "high",
        WebQuality.Light => "light",
        WebQuality.Original => "original",
        _ => "medium",
    };

    public static WebQuality QualityFromId(string? id) => id switch
    {
        "high" => WebQuality.High,
        "light" => WebQuality.Light,
        "original" => WebQuality.Original,
        _ => WebQuality.Medium,
    };
}

/// <summary>File sizes offered by the dialog; any other one can be typed (<see cref="Custom"/>).</summary>
public static class SizeTargets
{
    public const string Off = "off";
    /// <summary>Discord without Nitro: 10 MB per file.</summary>
    public const string Discord = "discord";
    /// <summary>Discord Nitro Basic: 50 MB per file.</summary>
    public const string Nitro = "nitro";
    /// <summary>A safe size for an e-mail attachment (most services accept 20 to 25 MB).</summary>
    public const string Email = "email";
    public const string Custom = "custom";

    /// <summary>The dialog speaks of "MB" (Mo): decimal megabytes, the smaller unit, so a limit is never exceeded.</summary>
    public const long BytesPerMegabyte = 1_000_000;
    public const double MinMegabytes = 1;
    public const double MaxMegabytes = 4000;

    public static readonly string[] Presets = { Discord, Nitro, Email };

    /// <summary>Size of a preset, in MB; 0 for <see cref="Off"/> and <see cref="Custom"/>.</summary>
    public static double Megabytes(string id) => id switch
    {
        Discord => 10,
        Nitro => 50,
        Email => 20,
        _ => 0,
    };
}

/// <summary>What the "Export / compress" dialog asks for.</summary>
/// <param name="Start">Beginning of the part of the video exported.</param>
/// <param name="End">End of that part (clamped to the length of the video).</param>
/// <param name="TargetBytes">Largest file accepted, or null for <paramref name="Quality"/>.</param>
/// <param name="TargetId">The size chosen (<see cref="SizeTargets"/>), part of the file name.</param>
public sealed record WebExportSettings(TimeSpan Start, TimeSpan End, WebFormat Format, WebQuality Quality, long? TargetBytes, string TargetId);

/// <summary>How a video is encoded: size, frame rate, and either bit rates (a size is aimed at) or a quality.</summary>
/// <param name="VideoBitrate">
/// Bits per second of the picture: aimed at when a size is, else used by the encoders without a constant quality
/// (Windows, openh264, mpeg4); 0 when only <paramref name="Crf"/> or <paramref name="WebPQuality"/> rule.
/// </param>
/// <param name="AudioBitrate">Bits per second of the sound; 0 without sound.</param>
/// <param name="Crf">
/// Constant quality of the ffmpeg encoders (lower is better), used instead of the bit rate when set; null when a size
/// is aimed at.
/// </param>
/// <param name="WebPQuality">0 to 100, animated WebP only.</param>
public sealed record WebEncodePlan(int Width, int Height, double FramesPerSecond, int VideoBitrate, int AudioBitrate, int? Crf = null,
    int WebPQuality = 0)
{
    /// <summary>For <see cref="IVideoTools.TranscodeAsync"/>.</summary>
    public VideoEncodeSettings ToTranscode(VideoCodec codec, bool sizeMatters) =>
        new(codec, Width, Height, FramesPerSecond, VideoBitrate, AudioBitrate, sizeMatters, Crf);

    /// <summary>The plan of <see cref="IVideoTools.TranscodeAsync"/> settings (ffmpeg under Linux).</summary>
    public static WebEncodePlan From(VideoEncodeSettings settings) =>
        new(settings.Width, settings.Height, settings.FramesPerSecond, settings.VideoBitrate, settings.AudioBitrate, settings.Crf);
}

/// <summary>Result of <see cref="WebExportPlanner.PlanForTarget"/>: a plan, or the longest part that fits the size.</summary>
public sealed record WebTargetPlan(WebEncodePlan? Plan, TimeSpan MaxDuration)
{
    internal bool Fits => Plan != null;
}

/// <summary>
/// Chooses size, frame rate and bit rates of an export. With a file size to reach: the bits available per second
/// (minus a margin for the container and the sound) are spread over the pixels; when they get too few for a sharp
/// picture, the resolution goes down (1080p, 720p, 480p, keeping the shape) and the frame rate too (30, then 24).
/// Pure, unit-tested.
/// </summary>
public static class WebExportPlanner
{
    /// <summary>Part of the size given to the streams: the container and the encoder's overshoot take the rest.</summary>
    public const double SizeMargin = 0.95;
    /// <summary>Below this, even a 480p picture is mush: the size is refused.</summary>
    public const int MinVideoBitrate = 150_000;
    public const int MaxVideoBitrate = 40_000_000;
    /// <summary>Lowest bit rate of the Windows AAC encoder (it accepts 96, 128, 160 and 192 kbit/s).</summary>
    public const int AacBitrate = 96_000;
    public const int AacHighBitrate = 128_000;
    public const int OpusLowBitrate = 64_000;
    public const int OpusBitrate = 96_000;
    /// <summary>Opus gets <see cref="OpusBitrate"/> when the whole file has at least this many bits per second.</summary>
    private const int GenerousBitrate = 1_500_000;
    /// <summary>Heights (the shorter side, for portrait videos too) tried one after the other.</summary>
    public static readonly int[] Steps = { 1080, 720, 480 };
    public const double DefaultFramesPerSecond = 30;
    public const double ReducedFramesPerSecond = 24;

    /// <summary>
    /// Bits per pixel and per frame below which the picture of screen recordings gets blurry. Newer codecs need fewer
    /// (AV1 about half of H.264).
    /// </summary>
    public static double MinBitsPerPixel(WebFormat format) => format switch
    {
        WebFormat.Mp4Av1 => 0.022,
        WebFormat.WebmVp9 or WebFormat.Mp4Hevc => 0.03,
        _ => 0.045,
    };

    /// <summary>More than this is wasted on screen recordings: a generous size gives a smaller file than allowed.</summary>
    private static double MaxBitsPerPixel(WebFormat format) => format == WebFormat.Mp4H264 ? 0.15 : 0.1;

    /// <summary>Bit rate of the sound for the bits available per second, or 0 without sound.</summary>
    public static int AudioBitrate(WebFormat format, bool hasAudio, double totalBitrate)
    {
        if (!hasAudio || !WebFormats.HasSound(format)) return 0;
        if (WebFormats.UsesAac(format)) return AacBitrate;
        return totalBitrate >= GenerousBitrate ? OpusBitrate : OpusLowBitrate;
    }

    /// <summary>Plan for a file of at most <paramref name="targetBytes"/>, or the longest duration that fits.</summary>
    /// <param name="duration">Length of the exported part.</param>
    public static WebTargetPlan PlanForTarget(WebFormat format, long targetBytes, TimeSpan duration, VideoInfo source)
    {
        var maxDuration = MaxDuration(format, targetBytes, source);
        if (duration <= TimeSpan.Zero || targetBytes <= 0) return new WebTargetPlan(null, maxDuration);
        if (format == WebFormat.WebP) return PlanWebPForTarget(targetBytes, duration, source, maxDuration);

        double seconds = duration.TotalSeconds;
        double total = targetBytes * 8.0 * SizeMargin / seconds;
        int audio = AudioBitrate(format, source.HasAudio, total);
        double video = total - audio;
        if (video < MinVideoBitrate) return new WebTargetPlan(null, maxDuration);

        var steps = Ladder(source).ToList();
        var chosen = steps[^1];
        foreach (var step in steps)
        {
            if (video / (step.Width * (double)step.Height * step.Fps) >= MinBitsPerPixel(format))
            {
                chosen = step;
                break;
            }
        }
        double ceiling = chosen.Width * (double)chosen.Height * chosen.Fps * MaxBitsPerPixel(format);
        int bitrate = (int)Math.Clamp(Math.Min(video, ceiling), MinVideoBitrate, MaxVideoBitrate);
        return new WebTargetPlan(new WebEncodePlan(chosen.Width, chosen.Height, chosen.Fps, bitrate, audio), maxDuration);
    }

    /// <summary>
    /// The longest part that fits <paramref name="targetBytes"/>: the lowest picture bit rate (<see cref="MinVideoBitrate"/>)
    /// and the lowest sound bit rate.
    /// </summary>
    public static TimeSpan MaxDuration(WebFormat format, long targetBytes, VideoInfo source)
    {
        if (targetBytes <= 0) return TimeSpan.Zero;
        double perSecond = format == WebFormat.WebP
            ? WebPBits(MinWebPStep(source), WebPFloorBitsPerPixel)
            : MinVideoBitrate + AudioBitrate(format, source.HasAudio, 0);
        return TimeSpan.FromSeconds(Math.Floor(targetBytes * 8.0 * SizeMargin / perSecond));
    }

    /// <summary>The picture sizes and frame rates tried, from the source's own to 480p at 24 frames per second.</summary>
    internal static IEnumerable<(int Width, int Height, double Fps)> Ladder(VideoInfo source)
    {
        var (width, height) = Even(source.Width, source.Height);
        double fps = SourceFps(source);
        double capped = Math.Min(fps, DefaultFramesPerSecond);
        double reduced = Math.Min(fps, ReducedFramesPerSecond);
        int shortSide = Math.Min(width, height);
        if (fps > capped) yield return (width, height, fps);
        yield return (width, height, capped);
        foreach (var step in Steps)
        {
            if (step >= shortSide) continue;
            var (w, h) = Fit(width, height, step);
            yield return (w, h, capped);
            if (step <= 720 && reduced < capped) yield return (w, h, reduced);
        }
        // A source of 720p or less still gets its 24 frames per second step.
        if (shortSide <= 720 && reduced < capped && Steps.All(s => s >= shortSide)) yield return (width, height, reduced);
    }

    /// <summary>Plan for a quality preset: smaller pictures and fewer frames for the lighter ones.</summary>
    public static WebEncodePlan PlanForQuality(WebFormat format, WebQuality quality, VideoInfo source)
    {
        var (width, height) = Even(source.Width, source.Height);
        double fps = SourceFps(source);
        if (format == WebFormat.WebP)
        {
            var (shortSide, maxFps, webpQuality) = quality switch
            {
                WebQuality.High or WebQuality.Original => (1080, 20.0, 80),
                WebQuality.Medium => (720, 15.0, 70),
                _ => (540, 12.0, 55),
            };
            var (w, h) = Fit(width, height, shortSide);
            return new WebEncodePlan(w, h, Math.Min(fps, maxFps), 0, 0, WebPQuality: webpQuality);
        }

        var (maxShortSide, fpsCap) = quality switch
        {
            WebQuality.High or WebQuality.Original => (int.MaxValue, double.MaxValue),
            WebQuality.Medium => (1080, DefaultFramesPerSecond),
            _ => (720, DefaultFramesPerSecond),
        };
        var (pw, ph) = Fit(width, height, maxShortSide);
        double pfps = Math.Min(fps, fpsCap);
        bool fine = quality is WebQuality.High or WebQuality.Original;
        int audio = !source.HasAudio ? 0
            : WebFormats.UsesAac(format) ? (fine ? AacHighBitrate : AacBitrate)
            : fine ? OpusBitrate : OpusLowBitrate;
        // H.264 (0–51), HEVC (0–51), AV1 (0–63), VP9 (0–63): about the same look for the same preset.
        int crf = (format, quality) switch
        {
            (WebFormat.Mp4H264, WebQuality.Original) => 18,
            (WebFormat.Mp4Hevc, WebQuality.Original) => 21,
            (WebFormat.Mp4Av1, WebQuality.Original) => 27,
            (_, WebQuality.Original) => 28,
            (WebFormat.Mp4H264, WebQuality.High) => 20,
            (WebFormat.Mp4H264, WebQuality.Medium) => 24,
            (WebFormat.Mp4H264, _) => 28,
            (WebFormat.Mp4Hevc, WebQuality.High) => 23,
            (WebFormat.Mp4Hevc, WebQuality.Medium) => 27,
            (WebFormat.Mp4Hevc, _) => 31,
            (WebFormat.Mp4Av1, WebQuality.High) => 30,
            (WebFormat.Mp4Av1, WebQuality.Medium) => 38,
            (WebFormat.Mp4Av1, _) => 46,
            (_, WebQuality.High) => 31,
            (_, WebQuality.Medium) => 37,
            _ => 44,
        };
        if (WebFormats.SystemCodec(format) != null)
        {
            // The Windows encoder takes a bit rate, not a quality: bits per pixel per frame (HEVC needs about 40 % fewer).
            double bitsPerPixel = quality switch
            {
                WebQuality.Original => 0.16,
                WebQuality.High => 0.12,
                WebQuality.Medium => 0.07,
                _ => 0.045,
            } * (format == WebFormat.Mp4Hevc ? 0.6 : 1);
            int bitrate = (int)Math.Clamp(pw * (double)ph * pfps * bitsPerPixel, MinVideoBitrate, MaxVideoBitrate);
            return new WebEncodePlan(pw, ph, pfps, bitrate, audio, Crf: crf);
        }
        return new WebEncodePlan(pw, ph, pfps, 0, audio, Crf: crf);
    }

    /// <summary>
    /// The next attempt after a file came out larger than allowed (<paramref name="actualBytes"/>): a lower bit rate in
    /// proportion (a lower quality, then a smaller picture, for WebP); null when nothing smaller makes sense.
    /// </summary>
    public static WebEncodePlan? Shrink(WebFormat format, WebEncodePlan plan, long actualBytes, long targetBytes, TimeSpan duration, VideoInfo source)
    {
        if (actualBytes <= 0 || targetBytes <= 0) return null;
        double ratio = (double)targetBytes / actualBytes;
        if (format == WebFormat.WebP)
        {
            if (plan.WebPQuality > WebPMinQuality)
                return plan with { WebPQuality = Math.Max(WebPMinQuality, plan.WebPQuality - (ratio < 0.7 ? 25 : 15)) };
            var steps = WebPLadder(source).ToList();
            int index = steps.FindIndex(s => s.Width * s.Height * s.Fps < plan.Width * plan.Height * plan.FramesPerSecond - 1);
            if (index < 0) return null;
            var next = steps[index];
            return plan with { Width = next.Width, Height = next.Height, FramesPerSecond = next.Fps };
        }
        double seconds = Math.Max(0.1, duration.TotalSeconds);
        double audioBits = plan.AudioBitrate * seconds;
        // The sound is about as large as asked: only the picture went over.
        double videoBits = Math.Max(1, actualBytes * 8.0 - audioBits);
        double allowedVideoBits = targetBytes * 8.0 * SizeMargin - audioBits;
        int bitrate = (int)(plan.VideoBitrate * Math.Min(0.92, allowedVideoBits / videoBits * 0.97));
        return bitrate < MinVideoBitrate ? null : plan with { VideoBitrate = bitrate };
    }

    // ---- Animated WebP: no bit rate, the size is estimated from the pixels, then checked ----

    private const int WebPMinQuality = 35;
    /// <summary>Rough bits per pixel per frame of an animated WebP of a screen recording at quality 70 (unchanged areas cost little).</summary>
    private const double WebPBitsPerPixel = 0.1;
    /// <summary>The same at the lowest quality.</summary>
    private const double WebPFloorBitsPerPixel = 0.04;

    private static IEnumerable<(int Width, int Height, double Fps)> WebPLadder(VideoInfo source)
    {
        var (width, height) = Even(source.Width, source.Height);
        double fps = SourceFps(source);
        foreach (var (shortSide, maxFps) in new[] { (720, 15.0), (540, 12.0), (480, 10.0), (360, 10.0) })
        {
            var (w, h) = Fit(width, height, shortSide);
            yield return (w, h, Math.Min(fps, maxFps));
        }
    }

    private static (int Width, int Height, double Fps) MinWebPStep(VideoInfo source) => WebPLadder(source).Last();

    private static double WebPBits((int Width, int Height, double Fps) step, double bitsPerPixel) =>
        step.Width * (double)step.Height * step.Fps * bitsPerPixel;

    private static WebTargetPlan PlanWebPForTarget(long targetBytes, TimeSpan duration, VideoInfo source, TimeSpan maxDuration)
    {
        double budget = targetBytes * 8.0 * SizeMargin / duration.TotalSeconds;
        var steps = WebPLadder(source).ToList();
        foreach (var step in steps)
            if (WebPBits(step, WebPBitsPerPixel) <= budget) return new WebTargetPlan(new WebEncodePlan(step.Width, step.Height, step.Fps, 0, 0, WebPQuality: 70), maxDuration);
        var last = steps[^1];
        if (WebPBits(last, WebPFloorBitsPerPixel) > budget) return new WebTargetPlan(null, maxDuration);
        return new WebTargetPlan(new WebEncodePlan(last.Width, last.Height, last.Fps, 0, 0, WebPQuality: 50), maxDuration);
    }

    // ---- Sizes ----

    private static double SourceFps(VideoInfo source) =>
        source.FramesPerSecond is > 1 and < 1000 ? source.FramesPerSecond : DefaultFramesPerSecond;

    /// <summary>Even width and height (4:2:0 encoders need them), at least 2.</summary>
    internal static (int Width, int Height) Even(int width, int height) => (Math.Max(2, width & ~1), Math.Max(2, height & ~1));

    /// <summary>
    /// The size whose shorter side is at most <paramref name="shortSide"/>, same shape, even numbers; the size itself
    /// when it is already smaller (never enlarged).
    /// </summary>
    public static (int Width, int Height) Fit(int width, int height, int shortSide)
    {
        (width, height) = Even(width, height);
        int current = Math.Min(width, height);
        if (current <= shortSide) return (width, height);
        double scale = (double)shortSide / current;
        int w = width <= height ? shortSide : (int)Math.Round(width * scale / 2) * 2;
        int h = height < width ? shortSide : (int)Math.Round(height * scale / 2) * 2;
        return (Math.Max(2, w), Math.Max(2, h));
    }
}
