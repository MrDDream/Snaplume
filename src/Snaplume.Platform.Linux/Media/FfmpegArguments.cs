using System.Globalization;
using Snaplume.Player;

namespace Snaplume.Platform.Linux.Media;

/// <summary>ffmpeg command lines (as argument lists) and the parsing of its output. Pure, unit-tested.</summary>
internal static class FfmpegArguments
{
    private static string Seconds(TimeSpan time) => Math.Max(0, time.TotalSeconds).ToString("0.###", CultureInfo.InvariantCulture);

    /// <summary>
    /// A local file for ffmpeg: "file:" and the full path keep any name a file name ("concat:a|b", "http:x" or
    /// "-x" would otherwise be a protocol or an option). Inputs also get "-protocol_whitelist file", so neither
    /// a playlist nor a reference inside the file can make ffmpeg open anything but local files.
    /// </summary>
    internal static string FileUrl(string path) => "file:" + Path.GetFullPath(path);

    /// <summary>ffmpeg describing a file's streams on its error output (it then complains that no output is given).</summary>
    public static List<string> Probe(string source) =>
        new() { "-hide_banner", "-nostdin", "-protocol_whitelist", "file", "-i", FileUrl(source) };

    /// <summary>
    /// Re-encodes the part of the video: the cut lands on the exact frame, not on the nearest key frame as a
    /// stream copy would. Input seeking (-ss before -i) is frame-accurate when transcoding.
    /// </summary>
    public static List<string> ExportClip(string source, string destination, TimeSpan start, TimeSpan end, string videoEncoder, bool hasAudio)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1",
            "-ss", Seconds(start), "-protocol_whitelist", "file", "-i", FileUrl(source), "-t", Seconds(end - start),
            "-map", "0:v:0", "-map", "0:a:0?",
            // 4:2:0 encoders need even sizes; a window recorded under Wayland may have an odd one.
            "-vf", "scale=trunc(iw/2)*2:trunc(ih/2)*2",
        };
        args.AddRange(VideoEncoderOptions(videoEncoder));
        if (hasAudio) args.AddRange(new[] { "-c:a", "aac", "-b:a", "160k" });
        else args.Add("-an");
        args.AddRange(new[] { "-movflags", "+faststart", "-f", "mp4", FileUrl(destination) });
        return args;
    }

    /// <summary>Distributions ship different H.264 encoders (Fedora's ffmpeg-free has no libx264).</summary>
    public static readonly string[] VideoEncoders = { "libx264", "libopenh264", "mpeg4" };

    private static string[] VideoEncoderOptions(string encoder) => encoder switch
    {
        "libx264" => new[] { "-c:v", "libx264", "-preset", "veryfast", "-crf", "20", "-pix_fmt", "yuv420p" },
        "libopenh264" => new[] { "-c:v", "libopenh264", "-b:v", "8M", "-pix_fmt", "yuv420p" },
        // MPEG-4 part 2: still an MP4 every player reads, when no H.264 encoder is built in.
        _ => new[] { "-c:v", "mpeg4", "-q:v", "3", "-pix_fmt", "yuv420p" },
    };

    /// <summary>Encoder names from "ffmpeg -encoders" (shared with the web exports, <see cref="FfmpegTools.ParseEncoders"/>).</summary>
    public static HashSet<string> ParseEncoders(string text) => FfmpegTools.ParseEncoders(text);

    /// <summary>Progress between 0 and 1 from a "-progress" line (<see cref="FfmpegTools.ParseProgress"/>), or null.</summary>
    public static double? ParseProgress(string line, TimeSpan duration) => FfmpegTools.ParseProgress(line, duration);

    /// <summary>
    /// When the times are evenly spaced (GIF export), one ffmpeg run produces all the frames with the fps
    /// filter; the interval is returned, or null when they are not.
    /// </summary>
    public static TimeSpan? RegularInterval(IReadOnlyList<TimeSpan> times)
    {
        if (times.Count < 2) return null;
        var interval = times[1] - times[0];
        if (interval <= TimeSpan.Zero) return null;
        for (int i = 2; i < times.Count; i++)
            if (Math.Abs((times[i] - times[i - 1] - interval).TotalMilliseconds) > 1) return null;
        return interval;
    }

    /// <summary>Raw BGRA frames scaled to the size, starting at <paramref name="start"/>.</summary>
    /// <param name="interval">Time between frames, or null for a single frame.</param>
    public static List<string> RawFrames(string source, TimeSpan start, TimeSpan? interval, int count, int width, int height)
    {
        var f = CultureInfo.InvariantCulture;
        string scale = string.Create(f, $"scale={width}:{height}:flags=bicubic");
        string filter = interval is { } i ? string.Create(f, $"fps=fps={1 / i.TotalSeconds:0.######}:round=near,{scale}") : scale;
        var args = new List<string> { "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Seconds(start), "-protocol_whitelist", "file", "-i", FileUrl(source) };
        if (interval is { } span) args.AddRange(new[] { "-t", Seconds(span * count + TimeSpan.FromSeconds(1)) });
        args.AddRange(new[] { "-an", "-vf", filter, "-frames:v", count.ToString(f), "-pix_fmt", "bgra", "-f", "rawvideo", "pipe:1" });
        return args;
    }

    /// <summary>One PNG frame, at most <paramref name="size"/> pixels wide or high, never enlarged.</summary>
    public static List<string> Thumbnail(string source, TimeSpan at, int size)
    {
        var f = CultureInfo.InvariantCulture;
        return new List<string>
        {
            "-hide_banner", "-nostdin", "-loglevel", "error", "-ss", Seconds(at), "-protocol_whitelist", "file", "-i", FileUrl(source), "-frames:v", "1", "-an",
            "-vf", string.Create(f, $"scale=w='min({size},iw)':h='min({size},ih)':force_original_aspect_ratio=decrease"),
            "-f", "image2pipe", "-c:v", "png", "pipe:1",
        };
    }
}
