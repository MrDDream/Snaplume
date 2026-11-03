using System.Diagnostics;
using System.Runtime.CompilerServices;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Util;
using Snaplume.Player;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Cutting, GIF frames and thumbnails with the ffmpeg command-line program (a package dependency); playback
/// with GStreamer.
/// </summary>
internal sealed class FfmpegVideoTools : IVideoTools
{
    private readonly Lazy<string?> _ffmpeg = new(() => ExternalTools.Find("ffmpeg"));
    private string? _videoEncoder;

    /// <exception cref="InvalidOperationException">ffmpeg is not installed (message ready to show).</exception>
    private string Ffmpeg => _ffmpeg.Value ?? throw new InvalidOperationException(Loc.T("Linux.FfmpegMissing"));

    public IVideoPlayer CreatePlayer() => new GstVideoPlayer();

    /// <summary>H.264 with the ffmpeg of the system; HEVC goes through the web export's own ffmpeg path (libx265, VA-API).</summary>
    public bool CanTranscode(VideoCodec codec) => codec == VideoCodec.H264 && _ffmpeg.Value != null;

    public async Task TranscodeAsync(string source, string destination, TimeSpan start, TimeSpan end, VideoEncodeSettings settings,
        IProgress<double>? progress, CancellationToken cancel)
    {
        if (File.Exists(destination)) throw new IOException($"{destination} already exists.");
        var ffmpeg = await FfmpegTools.DetectAsync().ConfigureAwait(false)
                     ?? throw new InvalidOperationException(Loc.T("Linux.FfmpegMissing"));
        var format = settings.Codec == VideoCodec.Hevc ? WebFormat.Mp4Hevc : WebFormat.Mp4H264;
        var hasAudio = settings.AudioBitrate > 0 && await HasAudioAsync(ffmpeg.Path, source, cancel).ConfigureAwait(false);
        try
        {
            await FfmpegWebEncoder.EncodeAsync(ProcessRunner.Instance, ffmpeg, format, WebEncodePlan.From(settings), settings.SizeMatters,
                source, destination, start, end, hasAudio, progress, cancel).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                File.Delete(destination);
            }
            catch (Exception)
            {
                // The original error matters more.
            }
            throw;
        }
    }

    public async Task<VideoInfo?> ProbeAsync(string path, CancellationToken cancel = default)
    {
        if (_ffmpeg.Value is not { } ffmpeg) return null;
        try
        {
            var result = await ExternalTools.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", path }, TimeSpan.FromSeconds(15), cancel,
                environment: ExternalTools.CLocale).ConfigureAwait(false);
            return FfmpegTools.ParseInputInfo(result.Errors);
        }
        catch (TimeoutException ex)
        {
            ErrorLog.Write($"Lecture des propriétés de {path}", ex);
            return null;
        }
    }

    public async Task ExportClipAsync(string source, string destination, TimeSpan start, TimeSpan end, IProgress<double>? progress, CancellationToken cancel)
    {
        var ffmpeg = Ffmpeg;
        var encoder = await VideoEncoderAsync(ffmpeg, cancel).ConfigureAwait(false);
        var hasAudio = await HasAudioAsync(ffmpeg, source, cancel).ConfigureAwait(false);
        var full = Path.GetFullPath(destination);
        // Written beside the target and renamed: a cancelled or failed export leaves nothing half-written.
        var temp = Path.Combine(Path.GetDirectoryName(full)!, $".{Path.GetFileNameWithoutExtension(full)}.{Guid.NewGuid():N}.tmp.mp4");
        var duration = end - start;
        var info = ExternalTools.StartInfo(ffmpeg, FfmpegArguments.ExportClip(source, temp, start, end, encoder, hasAudio));
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started");
        try
        {
            var errors = process.StandardError.ReadToEndAsync(cancel);
            string? line;
            while ((line = await process.StandardOutput.ReadLineAsync(cancel).ConfigureAwait(false)) != null)
                if (FfmpegArguments.ParseProgress(line, duration) is { } fraction) progress?.Report(fraction);
            await process.WaitForExitAsync(cancel).ConfigureAwait(false);
            var errorText = await errors.ConfigureAwait(false);
            if (process.ExitCode != 0) throw new InvalidOperationException($"ffmpeg ({process.ExitCode}): {errorText.Trim()}");
            File.Move(temp, full, overwrite: true);
            progress?.Report(1);
        }
        catch
        {
            ExternalTools.Kill(process);
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // The original error matters more.
            }
            throw;
        }
    }

    public async IAsyncEnumerable<PixelBuffer> ReadFramesAsync(string source, IReadOnlyList<TimeSpan> times, int width, int height,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        if (times.Count == 0) yield break;
        var ffmpeg = Ffmpeg;
        width = Math.Max(2, width);
        height = Math.Max(2, height);
        PixelBuffer? last = null;
        if (FfmpegArguments.RegularInterval(times) is { } interval)
        {
            int produced = 0;
            await foreach (var frame in RunRawAsync(ffmpeg, FfmpegArguments.RawFrames(source, times[0], interval, times.Count, width, height),
                               width, height, times.Count, cancel).ConfigureAwait(false))
            {
                last = frame;
                produced++;
                yield return frame;
            }
            if (last == null) throw new InvalidDataException($"No picture at {times[0]} in {source}");
            // The video ended before the last time: the last picture stands for the remaining ones.
            for (int i = produced; i < times.Count; i++) yield return Copy(last);
            yield break;
        }
        foreach (var time in times)
        {
            PixelBuffer? frame = null;
            await foreach (var f in RunRawAsync(ffmpeg, FfmpegArguments.RawFrames(source, time, null, 1, width, height), width, height, 1, cancel)
                               .ConfigureAwait(false))
                frame = f;
            frame ??= last;
            if (frame == null) throw new InvalidDataException($"No picture at {time} in {source}");
            last = frame;
            yield return frame;
        }
    }

    private static PixelBuffer Copy(PixelBuffer frame) => new(frame.Width, frame.Height, (byte[])frame.Pixels.Clone());

    /// <summary>Reads width × height × 4 bytes per frame from ffmpeg's standard output.</summary>
    private static async IAsyncEnumerable<PixelBuffer> RunRawAsync(string ffmpeg, List<string> args, int width, int height, int count,
        [EnumeratorCancellation] CancellationToken cancel)
    {
        var info = ExternalTools.StartInfo(ffmpeg, args);
        info.RedirectStandardOutput = true;
        info.RedirectStandardError = true;
        using var process = Process.Start(info) ?? throw new InvalidOperationException("ffmpeg could not be started");
        var errors = process.StandardError.ReadToEndAsync(CancellationToken.None);
        var stream = process.StandardOutput.BaseStream;
        int produced = 0;
        try
        {
            for (int n = 0; n < count; n++)
            {
                var frame = new PixelBuffer(width, height);
                int read = 0;
                while (read < frame.Pixels.Length)
                {
                    int got = await stream.ReadAsync(frame.Pixels.AsMemory(read), cancel).ConfigureAwait(false);
                    if (got == 0) break;
                    read += got;
                }
                if (read < frame.Pixels.Length) break; // end of the video
                produced++;
                yield return frame.MakeOpaque();
            }
        }
        finally
        {
            ExternalTools.Kill(process);
            try
            {
                var text = await errors.ConfigureAwait(false);
                if (produced == 0 && text.Length > 0) ErrorLog.Write("Images de la vidéo (ffmpeg)", text.Trim());
            }
            catch (Exception)
            {
                // Only diagnostics.
            }
        }
    }

    public async Task<PixelBuffer?> ThumbnailAsync(string path, int size, CancellationToken cancel = default)
    {
        if (_ffmpeg.Value is not { } ffmpeg) return null;
        // One second in skips a black first frame; short videos fall back to the start.
        foreach (var at in new[] { TimeSpan.FromSeconds(1), TimeSpan.Zero })
        {
            try
            {
                var result = await ExternalTools.RunAsync(ffmpeg, FfmpegArguments.Thumbnail(path, at, Math.Max(16, size)),
                    TimeSpan.FromSeconds(20), cancel).ConfigureAwait(false);
                if (result.Succeeded && result.Output.Length > 0) return ImageIO.Decode(result.Output);
            }
            catch (Exception ex) when (ex is TimeoutException or InvalidDataException)
            {
                ErrorLog.Write($"Miniature de {path}", ex);
                return null;
            }
        }
        return null;
    }

    private async Task<string> VideoEncoderAsync(string ffmpeg, CancellationToken cancel)
    {
        if (_videoEncoder != null) return _videoEncoder;
        var result = await ExternalTools.RunAsync(ffmpeg, new[] { "-hide_banner", "-encoders" }, TimeSpan.FromSeconds(15), cancel).ConfigureAwait(false);
        var available = FfmpegArguments.ParseEncoders(result.Text);
        return _videoEncoder = FfmpegArguments.VideoEncoders.FirstOrDefault(available.Contains) ?? "mpeg4";
    }

    /// <summary>ffmpeg prints the streams of the input on its error output ("Stream #0:1: Audio: aac").</summary>
    private static async Task<bool> HasAudioAsync(string ffmpeg, string source, CancellationToken cancel)
    {
        var result = await ExternalTools.RunAsync(ffmpeg, new[] { "-hide_banner", "-nostdin", "-i", source }, TimeSpan.FromSeconds(15), cancel)
            .ConfigureAwait(false);
        return result.Errors.Contains(": Audio:", StringComparison.Ordinal);
    }
}
