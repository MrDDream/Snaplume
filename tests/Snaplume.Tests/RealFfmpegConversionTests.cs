using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;
using Snaplume.Tests.Fakes;

namespace Snaplume.Tests;

/// <summary>
/// The conversions of recordings run for real with an installed ffmpeg, on small clips generated in a temporary
/// folder: skipped unless SNAPLUME_REAL_FFMPEG names an ffmpeg executable (the module's bin\ffmpeg.exe, or "ffmpeg"
/// of Linux). Checks that every command line is accepted, that the files play (read back by ffmpeg) and the sizes.
/// </summary>
public class RealFfmpegConversionTests(ITestOutputHelper output)
{
    static RealFfmpegConversionTests() => Loc.Initialize("fr");

    private static string? Executable => Environment.GetEnvironmentVariable("SNAPLUME_REAL_FFMPEG") is { Length: > 0 } path ? path : null;

    [Fact]
    public async Task Recordings_convert_into_every_format_with_the_real_ffmpeg()
    {
        Assert.SkipUnless(Executable != null, "SNAPLUME_REAL_FFMPEG is not set");
        var cancel = TestContext.Current.CancellationToken;
        var runner = ProcessRunner.Instance;
        var ffmpeg = await FfmpegTools.DetectAsync(runner, Executable, null, OperatingSystem.IsWindows())
                     ?? throw new InvalidOperationException("ffmpeg does not start");
        output.WriteLine($"ffmpeg {ffmpeg.Version} ({FfmpegTools.ShortVersion(ffmpeg.Version)}); HEVC: "
                         + string.Join(", ", FfmpegWebArguments.HardwareEncoders.Append("libx265").Where(ffmpeg.Encoders.Contains)));
        using var temp = new TempFolder();

        // A 1280×720 recording at 30 frames per second with sound, in H.264 like the recorder's.
        var clip = temp.File("clip.mp4");
        await Run(runner, new[]
        {
            "-hide_banner", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=1280x720:rate=30", "-f", "lavfi", "-i",
            "sine=frequency=440:sample_rate=48000", "-t", "20", "-c:v", "libopenh264", "-b:v", "6M", "-c:a", "aac", "-b:a", "160k", "-shortest", clip,
        }, cancel);
        var info = await Probe(runner, clip, cancel) ?? throw new InvalidOperationException("clip unreadable");
        output.WriteLine($"Clip: {info.Width}×{info.Height}, {info.FramesPerSecond} fps, {info.Duration.TotalSeconds:0.0} s, "
                         + $"{new FileInfo(clip).Length / 1e6:0.00} Mo");

        // The system's encoders are left out: every format goes to ffmpeg (H.264 included, libopenh264).
        var tools = new FakeVideoTools { SystemCodecs = new HashSet<VideoCodec>() };
        var targets = new List<RecordingConversionTarget>
        {
            new(WebFormat.Mp4Av1, WebQuality.Original, null, SizeTargets.Off),
            new(WebFormat.Mp4Av1, WebQuality.Light, null, SizeTargets.Off),
            new(WebFormat.WebmVp9, WebQuality.Medium, null, SizeTargets.Off),
            new(WebFormat.WebmVp9, WebQuality.High, 4_000_000, SizeTargets.Custom),
            new(WebFormat.Mp4Av1, WebQuality.High, 2_000_000, SizeTargets.Custom),
            new(WebFormat.Mp4H264, WebQuality.High, 3_000_000, SizeTargets.Custom),
            new(WebFormat.Mp4Hevc, WebQuality.Original, null, SizeTargets.Off),
            new(WebFormat.Mp4Hevc, WebQuality.High, 3_000_000, SizeTargets.Custom),
        };
        var failures = new List<string>();
        int n = 0;
        foreach (var target in targets)
        {
            var availability = WebVideoExport.Availability(target.Format, tools.CanTranscode, ffmpeg, OperatingSystem.IsWindows());
            if (!availability.IsAvailable)
            {
                output.WriteLine($"{target.Format} {target.Quality} {target.TargetBytes}: unavailable here ({availability.Reason})");
                continue;
            }
            var recorded = temp.File($"recorded-{++n}.mp4");
            File.Copy(clip, recorded);
            var folder = Path.Combine(temp.Path, "workspace");
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var result = await RecordingConversion.RunAsync(recorded, folder, $"Video_{n}", target, keepOriginal: false,
                (path, token) => Probe(runner, path, token),
                (source, destination, settings, videoInfo, progress, token) =>
                    WebVideoExport.ExportAsync(tools, ffmpeg, source, destination, settings, videoInfo, progress, token, runner),
                null, cancel);
            var label = $"{target.Format} {target.Quality}{(target.TargetBytes is { } t ? $" ≤ {t / 1e6:0.#} Mo" : "")} [{availability.Encoder}]";
            if (!result.Converted)
            {
                failures.Add($"{label}: {result.Problem}");
                output.WriteLine($"{label}: FAILED {result.Problem}");
                continue;
            }
            var converted = await Probe(runner, result.Path, cancel);
            long size = new FileInfo(result.Path).Length;
            output.WriteLine($"{label}: {Path.GetFileName(result.Path)} {size / 1e6:0.00} Mo, "
                             + $"{converted?.Width}×{converted?.Height} {converted?.FramesPerSecond} fps, {converted?.Duration.TotalSeconds:0.0} s, "
                             + $"son {converted?.HasAudio}, {watch.Elapsed.TotalSeconds:0.0} s");
            Assert.NotNull(converted);
            Assert.InRange(converted!.Duration.TotalSeconds, 19, 21);
            Assert.True(converted.HasAudio);
            if (target.TargetBytes is { } limit) Assert.True(size <= limit, $"{label}: {size} > {limit}");
            Assert.False(File.Exists(recorded));
        }
        Assert.Empty(failures);
    }

    private static async Task Run(IProcessRunner runner, IReadOnlyList<string> arguments, CancellationToken cancel)
    {
        var result = await runner.RunAsync(Executable!, arguments, null, TimeSpan.FromMinutes(2), cancel);
        if (result.ExitCode != 0) throw new InvalidOperationException($"ffmpeg ({result.ExitCode}): {result.Errors}");
    }

    /// <summary>What "ffmpeg -i" says of a file (it exits with an error: no output given), with the options of the exports.</summary>
    private static async Task<VideoInfo?> Probe(IProcessRunner runner, string path, CancellationToken cancel)
    {
        var result = await runner.RunAsync(Executable!, FfmpegWebArguments.Probe(path), null, TimeSpan.FromSeconds(30), cancel);
        return FfmpegTools.ParseInputInfo(result.Errors);
    }
}
