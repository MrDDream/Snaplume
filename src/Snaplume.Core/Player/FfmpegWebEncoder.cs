using System.Globalization;
using System.Text;
using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>The ffmpeg encoders used for a format: picture, and sound (null: the file has none).</summary>
public sealed record FfmpegEncoderChoice(string Video, string? Audio);

/// <summary>ffmpeg command lines of the web exports (as argument lists). Pure, unit-tested.</summary>
public static class FfmpegWebArguments
{
    /// <summary>
    /// Hardware HEVC encoders (NVIDIA, Intel, AMD, VA-API): "ffmpeg -encoders" lists them even without the graphics
    /// card, so they are tried on a few frames first (<see cref="FfmpegTools"/>) and dropped when they fail.
    /// </summary>
    public static readonly string[] HardwareEncoders = { "hevc_nvenc", "hevc_qsv", "hevc_amf", "hevc_vaapi" };

    /// <summary>The render node of the graphics card used by VA-API (the first one).</summary>
    public const string VaapiDevice = "/dev/dri/renderD128";

    /// <summary>Picture encoders by preference. Fedora's ffmpeg-free has no libx264; some builds lack SVT-AV1.</summary>
    /// <param name="windows">Windows prefers the graphics card for HEVC (libx265 is only in GPL builds found in PATH).</param>
    private static string[] VideoEncoders(WebFormat format, bool windows) => format switch
    {
        WebFormat.Mp4Hevc => windows
            ? new[] { "hevc_nvenc", "hevc_qsv", "hevc_amf", "libx265" }
            : new[] { "libx265", "hevc_vaapi", "hevc_nvenc", "hevc_qsv" },
        WebFormat.Mp4Av1 => new[] { "libsvtav1", "libaom-av1" },
        WebFormat.WebmVp9 => new[] { "libvpx-vp9" },
        WebFormat.WebP => new[] { "libwebp_anim", "libwebp" },
        _ => new[] { "libx264", "libopenh264", "mpeg4" },
    };

    /// <summary>Sound encoders by preference: Opus for WebM and AV1, AAC (built into ffmpeg) for H.264 and HEVC.</summary>
    private static string[] AudioEncoders(WebFormat format) => format switch
    {
        WebFormat.Mp4Av1 => new[] { "libopus", "aac" },
        WebFormat.WebmVp9 => new[] { "libopus", "libvorbis" },
        WebFormat.WebP => Array.Empty<string>(),
        _ => new[] { "aac" },
    };

    /// <summary>Encoders this ffmpeg has for the format, or null when it cannot make it (the format is greyed out).</summary>
    /// <param name="encoders">Usable encoders (<see cref="FfmpegInfo.Encoders"/>: hardware ones already tried).</param>
    public static FfmpegEncoderChoice? Choose(WebFormat format, IReadOnlySet<string> encoders, bool windows)
    {
        var video = VideoEncoders(format, windows).FirstOrDefault(encoders.Contains);
        if (video == null) return null;
        var audioChoices = AudioEncoders(format);
        if (audioChoices.Length == 0) return new FfmpegEncoderChoice(video, null);
        var audio = audioChoices.FirstOrDefault(encoders.Contains);
        return audio == null ? null : new FfmpegEncoderChoice(video, audio);
    }

    /// <summary>
    /// The encoders whose rate control uses a first pass (more exact sizes). SVT-AV1 and the hardware encoders have
    /// none in ffmpeg: one pass, then the size is checked (<see cref="WebVideoExport"/>).
    /// </summary>
    public static bool SupportsTwoPasses(string videoEncoder) => videoEncoder is "libx264" or "libx265" or "libvpx-vp9" or "libaom-av1";

    private static string Seconds(TimeSpan time) => Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    private static string Kilobits(double bitsPerSecond) =>
        ((long)Math.Max(1, bitsPerSecond / 1000)).ToString(CultureInfo.InvariantCulture) + "k";

    private static string Rate(double fps) => fps.ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A path inside the options of libx265 ("-x265-params pass=1:stats=..."): ':' separates the options there, so it
    /// is escaped (av_get_token rules), and Windows backslashes become slashes.
    /// </summary>
    internal static string EscapeOptionValue(string path)
    {
        var text = new StringBuilder();
        foreach (var c in path.Replace('\\', '/'))
        {
            if (c is ':' or '=' or '\'' or '\\' or ',') text.Append('\\');
            text.Append(c);
        }
        return text.ToString();
    }

    /// <summary>
    /// A tiny encoding of generated frames (no input file, no output file), to know whether a hardware encoder
    /// really works on this PC.
    /// </summary>
    public static List<string> ProbeEncoder(string encoder)
    {
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error" };
        if (encoder == "hevc_vaapi") args.AddRange(new[] { "-vaapi_device", VaapiDevice });
        args.AddRange(new[] { "-f", "lavfi", "-i", "color=c=black:s=320x240:r=25:d=0.2" });
        if (encoder == "hevc_vaapi") args.AddRange(new[] { "-vf", "format=nv12,hwupload" });
        args.AddRange(new[] { "-frames:v", "3", "-c:v", encoder, "-f", "null", "-" });
        return args;
    }

    /// <summary>
    /// One ffmpeg run. Input seeking (-ss before -i) lands on the exact frame when re-encoding.
    /// </summary>
    /// <param name="pass">0 for a single pass; 1 (analysis, no file written) or 2 with <paramref name="passLog"/>.</param>
    /// <param name="passLog">Prefix of the statistics files of the first pass (a full path).</param>
    /// <param name="hasAudio">The source has sound (kept when the format has sound).</param>
    public static List<string> Encode(WebFormat format, WebEncodePlan plan, FfmpegEncoderChoice encoders, string source, string destination,
        TimeSpan start, TimeSpan end, bool hasAudio, int pass = 0, string? passLog = null)
    {
        bool sound = hasAudio && encoders.Audio != null && plan.AudioBitrate > 0 && pass != 1;
        bool vaapi = encoders.Video == "hevc_vaapi";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        if (vaapi) args.AddRange(new[] { "-vaapi_device", VaapiDevice });
        args.AddRange(new[] { "-ss", Seconds(start), "-i", source, "-t", Seconds(end - start), "-map", "0:v:0" });
        if (sound) args.AddRange(new[] { "-map", "0:a:0?" });
        // Scaled with the same shape (even sizes, see WebExportPlanner.Fit), then the frame rate and the pixel format
        // of the encoder (NV12 for Intel and VA-API, sent to the graphics card for VA-API).
        var filter = string.Create(CultureInfo.InvariantCulture,
            $"scale={plan.Width}:{plan.Height}:flags=lanczos,setsar=1,fps={Rate(plan.FramesPerSecond)}");
        filter += encoders.Video switch
        {
            "libwebp_anim" or "libwebp" => "",
            "hevc_vaapi" => ",format=nv12,hwupload",
            "hevc_qsv" => ",format=nv12",
            _ => ",format=yuv420p",
        };
        args.AddRange(new[] { "-vf", filter });
        args.AddRange(VideoOptions(encoders.Video, plan));
        if (pass is 1 or 2 && passLog != null)
        {
            // libx265 takes its passes in its own options; the others the usual ones.
            if (encoders.Video == "libx265")
                args.AddRange(new[] { "-x265-params", string.Create(CultureInfo.InvariantCulture, $"pass={pass}:stats={EscapeOptionValue(passLog + ".x265.log")}") });
            else
                args.AddRange(new[] { "-pass", pass.ToString(CultureInfo.InvariantCulture), "-passlogfile", passLog });
        }
        // HEVC in MP4 tagged "hvc1": Apple devices and Safari play nothing else.
        if (format == WebFormat.Mp4Hevc) args.AddRange(new[] { "-tag:v", "hvc1" });

        if (sound) args.AddRange(new[] { "-c:a", encoders.Audio!, "-b:a", Kilobits(plan.AudioBitrate), "-ac", "2" });
        else args.Add("-an");

        if (pass == 1)
        {
            // The first pass only writes its statistics.
            args.AddRange(new[] { "-f", "null", "-" });
            return args;
        }
        args.AddRange(format switch
        {
            WebFormat.WebmVp9 => new[] { "-f", "webm" },
            WebFormat.WebP => new[] { "-loop", "0", "-f", "webp" },
            _ => new[] { "-movflags", "+faststart", "-f", "mp4" },
        });
        args.Add(destination);
        return args;
    }

    private static string[] VideoOptions(string encoder, WebEncodePlan plan)
    {
        var f = CultureInfo.InvariantCulture;
        string bitrate = Kilobits(plan.VideoBitrate);
        string maxrate = Kilobits(plan.VideoBitrate * 1.5);
        string bufsize = Kilobits(plan.VideoBitrate * 2.0);
        // A constant quality when the plan has one (no size aimed at), else the bit rate.
        bool quality = plan.Crf != null || plan.VideoBitrate <= 0;
        string crf = (plan.Crf ?? 32).ToString(f);
        return encoder switch
        {
            "libsvtav1" => quality
                ? new[] { "-c:v", "libsvtav1", "-preset", "8", "-crf", crf }
                : new[] { "-c:v", "libsvtav1", "-preset", "8", "-b:v", bitrate },
            "libaom-av1" => quality
                ? new[] { "-c:v", "libaom-av1", "-cpu-used", "6", "-row-mt", "1", "-crf", crf, "-b:v", "0" }
                : new[] { "-c:v", "libaom-av1", "-cpu-used", "6", "-row-mt", "1", "-b:v", bitrate },
            "libvpx-vp9" => quality
                ? new[] { "-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-crf", crf, "-b:v", "0" }
                : new[] { "-c:v", "libvpx-vp9", "-deadline", "good", "-cpu-used", "4", "-row-mt", "1", "-b:v", bitrate },
            "libwebp_anim" or "libwebp" => new[]
            {
                "-c:v", encoder, "-lossless", "0", "-quality", Math.Clamp(plan.WebPQuality, 0, 100).ToString(f), "-compression_level", "4",
            },
            "libx264" => quality
                ? new[] { "-c:v", "libx264", "-preset", "medium", "-crf", crf }
                : new[] { "-c:v", "libx264", "-preset", "medium", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize },
            "libx265" => quality
                ? new[] { "-c:v", "libx265", "-preset", "fast", "-crf", crf }
                : new[] { "-c:v", "libx265", "-preset", "fast", "-b:v", bitrate },
            "hevc_nvenc" => quality
                ? new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-cq", crf, "-b:v", "0" }
                : new[] { "-c:v", "hevc_nvenc", "-preset", "p5", "-rc", "vbr", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize },
            "hevc_qsv" => quality
                ? new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-global_quality", crf }
                : new[] { "-c:v", "hevc_qsv", "-preset", "medium", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize },
            "hevc_amf" => quality
                ? new[] { "-c:v", "hevc_amf", "-quality", "balanced", "-rc", "cqp", "-qp_i", crf, "-qp_p", crf }
                : new[] { "-c:v", "hevc_amf", "-quality", "balanced", "-rc", "vbr_peak", "-b:v", bitrate, "-maxrate", maxrate, "-bufsize", bufsize },
            "hevc_vaapi" => quality
                ? new[] { "-c:v", "hevc_vaapi", "-rc_mode", "CQP", "-qp", crf }
                : new[] { "-c:v", "hevc_vaapi", "-rc_mode", "VBR", "-b:v", bitrate, "-maxrate", maxrate },
            // The encoders without a constant quality (or not known here) get the bit rate of the plan.
            "libopenh264" => new[] { "-c:v", "libopenh264", "-b:v", bitrate, "-maxrate", bitrate },
            // MPEG-4 part 2: still an MP4 every player reads, when no H.264 encoder is built in.
            _ => new[] { "-c:v", encoder, "-b:v", bitrate },
        };
    }
}

/// <summary>Runs ffmpeg for a web export: one pass, or two when a size is aimed at and the encoder can.</summary>
public static class FfmpegWebEncoder
{
    /// <summary>Share of the progress bar given to the first of two passes (it is faster: no sound, no file).</summary>
    private const double FirstPassShare = 0.4;

    /// <summary>Encodes into <paramref name="destination"/>, overwritten (a temporary file of the caller).</summary>
    /// <param name="sizeMatters">A file size is aimed at: two passes when the encoder has them.</param>
    /// <exception cref="InvalidOperationException">This ffmpeg cannot make the format, or failed (its message).</exception>
    public static async Task EncodeAsync(IProcessRunner runner, FfmpegInfo ffmpeg, WebFormat format, WebEncodePlan plan, bool sizeMatters,
        string source, string destination, TimeSpan start, TimeSpan end, bool hasAudio, IProgress<double>? progress, CancellationToken cancel)
    {
        var encoders = FfmpegWebArguments.Choose(format, ffmpeg.Encoders, OperatingSystem.IsWindows())
                       ?? throw new InvalidOperationException($"ffmpeg ({ffmpeg.Path}) has no encoder for {format}");
        bool twoPasses = sizeMatters && plan.Crf == null && plan.VideoBitrate > 0 && FfmpegWebArguments.SupportsTwoPasses(encoders.Video);
        var duration = end - start;
        // The statistics of the first pass ("<prefix>-0.log" and others), in the temporary folder.
        var passLog = Path.Combine(Path.GetTempPath(), $"snaplume-pass-{Guid.NewGuid():N}");
        try
        {
            if (twoPasses)
            {
                await RunAsync(runner, ffmpeg.Path, FfmpegWebArguments.Encode(format, plan, encoders, source, destination, start, end, hasAudio, 1, passLog),
                    duration, progress, 0, FirstPassShare, cancel).ConfigureAwait(false);
                await RunAsync(runner, ffmpeg.Path, FfmpegWebArguments.Encode(format, plan, encoders, source, destination, start, end, hasAudio, 2, passLog),
                    duration, progress, FirstPassShare, 1, cancel).ConfigureAwait(false);
            }
            else
            {
                await RunAsync(runner, ffmpeg.Path, FfmpegWebArguments.Encode(format, plan, encoders, source, destination, start, end, hasAudio),
                    duration, progress, 0, 1, cancel).ConfigureAwait(false);
            }
        }
        finally
        {
            DeletePassLogs(passLog);
        }
    }

    private static async Task RunAsync(IProcessRunner runner, string ffmpeg, List<string> arguments, TimeSpan duration, IProgress<double>? progress,
        double from, double to, CancellationToken cancel)
    {
        var result = await runner.RunAsync(ffmpeg, arguments, line =>
        {
            if (FfmpegTools.ParseProgress(line, duration) is { } fraction) progress?.Report(from + (to - from) * fraction);
        }, null, cancel).ConfigureAwait(false);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ffmpeg ({result.ExitCode}): {result.Errors.Trim()}");
        progress?.Report(to);
    }

    private static void DeletePassLogs(string prefix)
    {
        try
        {
            var folder = Path.GetDirectoryName(prefix)!;
            foreach (var file in Directory.EnumerateFiles(folder, Path.GetFileName(prefix) + "*")) File.Delete(file);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression des statistiques de ffmpeg", ex);
        }
    }
}
