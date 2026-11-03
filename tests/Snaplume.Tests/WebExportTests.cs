using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;
using Snaplume.Tests.Fakes;

namespace Snaplume.Tests;

/// <summary>
/// The player's "Export / compress": the plan for a file size (bit rates, smaller pictures, refusal), the quality
/// presets, the ffmpeg command lines, the choice of encoders (HEVC hardware tried first), the export with its size
/// check and retries, and the ffmpeg module (allowed addresses, checksum, extraction). No network, no ffmpeg: programs
/// are faked (<see cref="FakeRunner"/>) and so is the HTTP handler.
/// </summary>
public class WebExportTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "SnaplumeWebExportTests_" + Guid.NewGuid().ToString("N"));

    static WebExportTests() => Loc.Initialize("fr");

    public WebExportTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    private static readonly VideoInfo FullHd = new(1920, 1080, 30, true, TimeSpan.FromMinutes(5));
    private const long TenMegabytes = 10 * SizeTargets.BytesPerMegabyte;

    // ---- Sizes ----

    [Theory]
    [InlineData(1920, 1080, 720, 1280, 720)]
    [InlineData(1080, 1920, 720, 720, 1280)]
    [InlineData(2560, 1440, 1080, 1920, 1080)]
    [InlineData(1365, 767, 480, 854, 480)]
    [InlineData(1280, 720, 1080, 1280, 720)] // never enlarged
    [InlineData(801, 601, 1080, 800, 600)]   // odd sizes made even
    public void FitKeepsTheShapeWithEvenSizes(int width, int height, int shortSide, int expectedWidth, int expectedHeight) =>
        Assert.Equal((expectedWidth, expectedHeight), WebExportPlanner.Fit(width, height, shortSide));

    [Fact]
    public void LadderGoesDownInSizeThenFrameRate()
    {
        var steps = WebExportPlanner.Ladder(new VideoInfo(2560, 1440, 60, true, TimeSpan.Zero)).ToList();
        Assert.Equal(new (int, int, double)[]
        {
            (2560, 1440, 60), (2560, 1440, 30), (1920, 1080, 30), (1280, 720, 30), (1280, 720, 24), (854, 480, 30), (854, 480, 24),
        }, steps);
    }

    // ---- Plans for a file size ----

    [Fact]
    public void DiscordMinuteOfFullHdGoesTo720pAt24()
    {
        var result = WebExportPlanner.PlanForTarget(WebFormat.Mp4H264, TenMegabytes, TimeSpan.FromSeconds(60), FullHd);
        var plan = Assert.IsType<WebEncodePlan>(result.Plan);
        // (10 MB × 8 × 0.95 − 96 kbit/s of AAC × 60 s) / 60 s
        Assert.Equal(1_170_666, plan.VideoBitrate, tolerance: 2);
        Assert.Equal(WebExportPlanner.AacBitrate, plan.AudioBitrate);
        Assert.Equal((1280, 720, 24.0), (plan.Width, plan.Height, plan.FramesPerSecond));
        Assert.True((plan.VideoBitrate + plan.AudioBitrate) * 60.0 / 8 <= TenMegabytes * WebExportPlanner.SizeMargin + 1);
        Assert.Null(plan.Crf);
    }

    [Fact]
    public void Av1NeedsFewerBitsPerPixelAndUsesOpus()
    {
        var plan = WebExportPlanner.PlanForTarget(WebFormat.Mp4Av1, TenMegabytes, TimeSpan.FromSeconds(60), FullHd).Plan!;
        Assert.Equal((1280, 720, 30.0), (plan.Width, plan.Height, plan.FramesPerSecond));
        Assert.Equal(WebExportPlanner.OpusLowBitrate, plan.AudioBitrate);
        var hevc = WebExportPlanner.PlanForTarget(WebFormat.Mp4Hevc, TenMegabytes, TimeSpan.FromSeconds(60), FullHd).Plan!;
        Assert.Equal(WebExportPlanner.AacBitrate, hevc.AudioBitrate);
    }

    [Fact]
    public void GenerousSizeKeepsTheSourceButNotAWastefulBitrate()
    {
        var sixty = new VideoInfo(1920, 1080, 60, true, TimeSpan.FromSeconds(30));
        var plan = WebExportPlanner.PlanForTarget(WebFormat.Mp4H264, 50 * SizeTargets.BytesPerMegabyte, TimeSpan.FromSeconds(30), sixty).Plan!;
        Assert.Equal((1920, 1080, 60.0), (plan.Width, plan.Height, plan.FramesPerSecond));

        var small = new VideoInfo(1280, 720, 30, false, TimeSpan.FromSeconds(5));
        var capped = WebExportPlanner.PlanForTarget(WebFormat.Mp4H264, 50 * SizeTargets.BytesPerMegabyte, TimeSpan.FromSeconds(5), small).Plan!;
        Assert.Equal(0, capped.AudioBitrate);
        Assert.InRange(capped.VideoBitrate, 1, (int)(1280 * 720 * 30 * 0.15) + 1);
    }

    [Fact]
    public void TooLongForTheSizeIsRefusedWithTheLongestDurationThatFits()
    {
        var result = WebExportPlanner.PlanForTarget(WebFormat.Mp4H264, TenMegabytes, TimeSpan.FromMinutes(30), FullHd);
        Assert.False(result.Fits);
        // 10 MB × 8 × 0.95 / (150 + 96 kbit/s)
        Assert.Equal(TimeSpan.FromSeconds(308), result.MaxDuration);
        // Exactly at that duration, 480p at the lowest bit rate fits.
        var limit = WebExportPlanner.PlanForTarget(WebFormat.Mp4H264, TenMegabytes, result.MaxDuration, FullHd).Plan!;
        Assert.Equal((854, 480, 24.0), (limit.Width, limit.Height, limit.FramesPerSecond));
        Assert.True(limit.VideoBitrate >= WebExportPlanner.MinVideoBitrate);
    }

    [Fact]
    public void ShrinkLowersTheBitrateInProportionThenGivesUp()
    {
        var plan = new WebEncodePlan(1280, 720, 30, 1_000_000, 96_000);
        var next = WebExportPlanner.Shrink(WebFormat.Mp4H264, plan, (long)(TenMegabytes * 1.2), TenMegabytes, TimeSpan.FromSeconds(60), FullHd)!;
        Assert.InRange(next.VideoBitrate, 600_000, 850_000);
        Assert.Equal(plan with { VideoBitrate = next.VideoBitrate }, next);
        var floor = plan with { VideoBitrate = WebExportPlanner.MinVideoBitrate };
        Assert.Null(WebExportPlanner.Shrink(WebFormat.Mp4H264, floor, TenMegabytes * 2, TenMegabytes, TimeSpan.FromSeconds(60), FullHd));
    }

    [Fact]
    public void WebPTargetLowersTheQualityThenThePicture()
    {
        var plan = WebExportPlanner.PlanForTarget(WebFormat.WebP, TenMegabytes, TimeSpan.FromSeconds(10), FullHd).Plan!;
        Assert.Equal(0, plan.AudioBitrate);
        Assert.Equal(70, plan.WebPQuality);
        var lower = WebExportPlanner.Shrink(WebFormat.WebP, plan, TenMegabytes * 2, TenMegabytes, TimeSpan.FromSeconds(10), FullHd)!;
        Assert.True(lower.WebPQuality < plan.WebPQuality);
        var floor = lower with { WebPQuality = 35 };
        var smaller = WebExportPlanner.Shrink(WebFormat.WebP, floor, TenMegabytes * 2, TenMegabytes, TimeSpan.FromSeconds(10), FullHd)!;
        Assert.True(smaller.Width * smaller.Height * smaller.FramesPerSecond < floor.Width * floor.Height * floor.FramesPerSecond);
    }

    [Fact]
    public void QualityPresetsUseCrfAndSmallerPicturesForLight()
    {
        var high = WebExportPlanner.PlanForQuality(WebFormat.Mp4Av1, WebQuality.High, new VideoInfo(2560, 1440, 60, true, TimeSpan.Zero));
        Assert.Equal((2560, 1440, 60.0, 0), (high.Width, high.Height, high.FramesPerSecond, high.VideoBitrate));
        Assert.Equal(30, high.Crf);
        var light = WebExportPlanner.PlanForQuality(WebFormat.Mp4Av1, WebQuality.Light, new VideoInfo(2560, 1440, 60, true, TimeSpan.Zero));
        Assert.Equal((1280, 720, 30.0), (light.Width, light.Height, light.FramesPerSecond));
        Assert.True(light.Crf > high.Crf);

        // The Windows encoder takes a bit rate: H.264 and HEVC plans carry both; HEVC needs fewer bits.
        var h264 = WebExportPlanner.PlanForQuality(WebFormat.Mp4H264, WebQuality.Medium, FullHd);
        var hevc = WebExportPlanner.PlanForQuality(WebFormat.Mp4Hevc, WebQuality.Medium, FullHd);
        Assert.True(h264.VideoBitrate > hevc.VideoBitrate && hevc.VideoBitrate > 0);
        Assert.NotNull(hevc.Crf);
        var webp = WebExportPlanner.PlanForQuality(WebFormat.WebP, WebQuality.Medium, FullHd);
        Assert.Equal((1280, 720, 15.0, 0), (webp.Width, webp.Height, webp.FramesPerSecond, webp.AudioBitrate));
    }

    // ---- ffmpeg command lines ----

    private static readonly WebEncodePlan TargetPlan = new(1280, 720, 24, 1_200_000, 64_000);

    [Fact]
    public void Vp9TwoPassesWriteStatisticsThenTheWebm()
    {
        var encoders = new FfmpegEncoderChoice("libvpx-vp9", "libopus");
        var first = FfmpegWebArguments.Encode(WebFormat.WebmVp9, TargetPlan, encoders, "in.mp4", "out.webm", TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(12), hasAudio: true, pass: 1, passLog: "/tmp/log");
        Assert.Equal(new[] { "-pass", "1", "-passlogfile", "/tmp/log" }, Slice(first, "-pass", 4));
        Assert.Equal(new[] { "-f", "null", "-" }, first.TakeLast(3));
        Assert.Contains("-an", first);
        Assert.DoesNotContain("0:a:0?", first);
        Assert.Equal(new[] { "-ss", "2", "-i", "in.mp4", "-t", "10" }, Slice(first, "-ss", 6));

        var second = FfmpegWebArguments.Encode(WebFormat.WebmVp9, TargetPlan, encoders, "in.mp4", "out.webm", TimeSpan.FromSeconds(2),
            TimeSpan.FromSeconds(12), hasAudio: true, pass: 2, passLog: "/tmp/log");
        Assert.Equal(new[] { "-c:a", "libopus", "-b:a", "64k" }, Slice(second, "-c:a", 4));
        Assert.Equal(new[] { "-b:v", "1200k" }, Slice(second, "-b:v", 2));
        Assert.Equal(new[] { "-f", "webm", "out.webm" }, second.TakeLast(3));
        Assert.Equal("scale=1280:720:flags=lanczos,setsar=1,fps=24,format=yuv420p", Slice(second, "-vf", 2)[1]);
    }

    [Fact]
    public void HevcIsTaggedHvc1AndLibx265PassesGoInItsOptions()
    {
        var args = FfmpegWebArguments.Encode(WebFormat.Mp4Hevc, TargetPlan with { AudioBitrate = 96_000 }, new FfmpegEncoderChoice("libx265", "aac"),
            "in.mp4", "out.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), hasAudio: true, pass: 2, passLog: @"C:\Temp\snap");
        Assert.Equal(new[] { "-tag:v", "hvc1" }, Slice(args, "-tag:v", 2));
        Assert.Equal(new[] { "-x265-params", @"pass=2:stats=C\:/Temp/snap.x265.log" }, Slice(args, "-x265-params", 2));
        Assert.DoesNotContain("-pass", args);
        Assert.Equal(new[] { "-movflags", "+faststart", "-f", "mp4", "out.mp4" }, args.TakeLast(5));
        Assert.Equal(@"C\:/a\'b\=c", FfmpegWebArguments.EscapeOptionValue(@"C:\a'b=c"));
    }

    [Fact]
    public void HardwareEncodersGetTheirOwnOptions()
    {
        var vaapi = FfmpegWebArguments.Encode(WebFormat.Mp4Hevc, TargetPlan, new FfmpegEncoderChoice("hevc_vaapi", "aac"), "in.mp4", "out.mp4",
            TimeSpan.Zero, TimeSpan.FromSeconds(10), hasAudio: false);
        Assert.True(vaapi.IndexOf("-vaapi_device") < vaapi.IndexOf("-i"));
        Assert.EndsWith("format=nv12,hwupload", Slice(vaapi, "-vf", 2)[1]);
        Assert.Contains("-an", vaapi);

        var nvenc = FfmpegWebArguments.Encode(WebFormat.Mp4Hevc, TargetPlan with { VideoBitrate = 0, Crf = 27 },
            new FfmpegEncoderChoice("hevc_nvenc", "aac"), "in.mp4", "out.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), hasAudio: true);
        Assert.Equal(new[] { "-cq", "27" }, Slice(nvenc, "-cq", 2));
        Assert.Contains("hvc1", nvenc);

        var probe = FfmpegWebArguments.ProbeEncoder("hevc_qsv");
        Assert.Equal(new[] { "-c:v", "hevc_qsv", "-f", "null", "-" }, probe.TakeLast(5));
        Assert.Contains("lavfi", probe);
    }

    [Fact]
    public void QualityModeAndWebP()
    {
        var av1 = FfmpegWebArguments.Encode(WebFormat.Mp4Av1, new WebEncodePlan(1920, 1080, 30, 0, 96_000, Crf: 38),
            new FfmpegEncoderChoice("libsvtav1", "libopus"), "in.mp4", "out.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(10), hasAudio: true);
        Assert.Equal(new[] { "-crf", "38" }, Slice(av1, "-crf", 2));
        Assert.DoesNotContain("-b:v", av1);

        var webp = FfmpegWebArguments.Encode(WebFormat.WebP, new WebEncodePlan(960, 540, 15, 0, 0, WebPQuality: 70),
            new FfmpegEncoderChoice("libwebp_anim", null), "in.mp4", "out.webp", TimeSpan.Zero, TimeSpan.FromSeconds(10), hasAudio: true);
        Assert.Equal(new[] { "-quality", "70" }, Slice(webp, "-quality", 2));
        Assert.Equal(new[] { "-loop", "0", "-f", "webp", "out.webp" }, webp.TakeLast(5));
        Assert.Equal("scale=960:540:flags=lanczos,setsar=1,fps=15", Slice(webp, "-vf", 2)[1]);
        Assert.Contains("-an", webp);
    }

    private static string[] Slice(List<string> args, string first, int count)
    {
        int index = args.IndexOf(first);
        Assert.True(index >= 0, $"{first} missing in {string.Join(' ', args)}");
        return args.Skip(index).Take(count).ToArray();
    }

    // ---- Encoders and availability ----

    private static HashSet<string> Set(params string[] names) => new(names, StringComparer.Ordinal);

    [Fact]
    public void EncodersAreChosenByPreference()
    {
        // Windows: the graphics card before libx265 (which only a GPL ffmpeg of PATH has).
        Assert.Equal("hevc_nvenc", FfmpegWebArguments.Choose(WebFormat.Mp4Hevc, Set("libx265", "hevc_nvenc", "aac"), windows: true)!.Video);
        // Linux: libx265 first, VA-API otherwise.
        Assert.Equal("libx265", FfmpegWebArguments.Choose(WebFormat.Mp4Hevc, Set("libx265", "hevc_vaapi", "aac"), windows: false)!.Video);
        Assert.Equal("hevc_vaapi", FfmpegWebArguments.Choose(WebFormat.Mp4Hevc, Set("hevc_vaapi", "aac"), windows: false)!.Video);
        // A build without SVT-AV1 still has libaom; without Opus, AV1 MP4 takes AAC.
        Assert.Equal(new FfmpegEncoderChoice("libaom-av1", "aac"), FfmpegWebArguments.Choose(WebFormat.Mp4Av1, Set("libaom-av1", "aac"), true));
        // WebM needs Opus or Vorbis.
        Assert.Null(FfmpegWebArguments.Choose(WebFormat.WebmVp9, Set("libvpx-vp9", "aac"), true));
        Assert.Equal(new FfmpegEncoderChoice("libwebp", null), FfmpegWebArguments.Choose(WebFormat.WebP, Set("libwebp"), true));
    }

    private static FfmpegInfo Ffmpeg(FfmpegSource source, params string[] encoders) => new("ffmpeg", source, "8.1", Set(encoders));

    [Fact]
    public void WindowsWithoutFfmpegOffersH264AndExplainsTheRest()
    {
        WebFormatAvailability Of(WebFormat f, bool hevc) =>
            WebVideoExport.Availability(f, c => c == VideoCodec.H264 || hevc, null, windows: true);
        Assert.Equal(WebEncoderRoute.System, Of(WebFormat.Mp4H264, false).Route);
        var hevc = Of(WebFormat.Mp4Hevc, false);
        Assert.False(hevc.IsAvailable);
        Assert.Equal(Loc.T("Player.Web.NoHevcWindows"), hevc.Reason);
        // With the HEVC Video Extensions (or a graphics card): the Windows encoder.
        Assert.Equal(WebEncoderRoute.System, Of(WebFormat.Mp4Hevc, true).Route);
        Assert.Equal(Loc.T("Player.Web.NeedsModule"), Of(WebFormat.Mp4Av1, false).Reason);
    }

    [Fact]
    public void FfmpegWithMissingEncodersGreysTheirFormats()
    {
        // Like Fedora's ffmpeg-free: no libx264, no libx265, no working hardware.
        var ffmpeg = Ffmpeg(FfmpegSource.System, "libopenh264", "aac", "libsvtav1", "libopus", "libwebp_anim");
        WebFormatAvailability Of(WebFormat f) => WebVideoExport.Availability(f, c => c == VideoCodec.H264, ffmpeg, windows: false);
        Assert.Equal(WebEncoderRoute.System, Of(WebFormat.Mp4H264).Route);
        Assert.Equal(Loc.T("Player.Web.NoHevcLinux"), Of(WebFormat.Mp4Hevc).Reason);
        Assert.Equal(new WebFormatAvailability(WebFormat.Mp4Av1, WebEncoderRoute.Ffmpeg, "", "libsvtav1"), Of(WebFormat.Mp4Av1));
        Assert.Equal(Loc.T("Player.Web.MissingEncoder", "libvpx-vp9"), Of(WebFormat.WebmVp9).Reason);
        Assert.True(Of(WebFormat.WebP).IsAvailable);
    }

    [Fact]
    public void WithoutFfmpegOnlyH264IsOfferedEvenWithTheWindowsHevcEncoder()
    {
        // Windows with the HEVC Video Extensions, no ffmpeg: H.265 waits for the module like the other formats.
        var offered = WebVideoExport.Offered(_ => true, null, windows: true);
        Assert.Equal(new[] { WebFormat.Mp4H264 }, offered.Select(a => a.Format));
        Assert.Equal(WebEncoderRoute.System, offered[0].Route);

        // With ffmpeg, every format; those its build lacks stay listed with the reason, HEVC still by Windows.
        var ffmpeg = Ffmpeg(FfmpegSource.Module, "libsvtav1", "libopus", "aac");
        var all = WebVideoExport.Offered(_ => true, ffmpeg, windows: true);
        Assert.Equal(WebFormats.All, all.Select(a => a.Format));
        Assert.Equal(WebEncoderRoute.System, all.Single(a => a.Format == WebFormat.Mp4Hevc).Route);
        Assert.Equal(Loc.T("Player.Web.MissingEncoder", "libvpx-vp9"), all.Single(a => a.Format == WebFormat.WebmVp9).Reason);

        // Linux without ffmpeg: H.264 itself cannot be made, and says why.
        var linux = Assert.Single(WebVideoExport.Offered(_ => false, null, windows: false));
        Assert.False(linux.IsAvailable);
    }

    // ---- ffmpeg detection ----

    [Fact]
    public async Task DetectionKeepsOnlyTheHardwareEncodersThatWork()
    {
        var module = Path.Combine(_folder, "bin", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        File.WriteAllText(module, "");
        var runner = new FakeRunner
        {
            Encoders = "Encoders:\n V..... = Video\n ------\n V....D libsvtav1  SVT-AV1\n V....D hevc_nvenc  NVIDIA\n V....D hevc_qsv  Intel\n A....D aac  AAC\n",
            // No NVIDIA card here.
            Fails = args => args.Contains("hevc_nvenc"),
        };
        var info = await FfmpegTools.DetectAsync(runner, module, null, windows: true);
        Assert.NotNull(info);
        Assert.Equal((module, FfmpegSource.Module, "n8.1.3-9-g29e619e767"), (info.Path, info.Source, info.Version));
        Assert.Equal(Set("libsvtav1", "hevc_qsv", "aac"), info.Encoders);
        Assert.Equal("hevc_qsv", FfmpegWebArguments.Choose(WebFormat.Mp4Hevc, info.Encoders, windows: true)!.Video);
    }

    [Fact]
    public async Task DetectionFallsBackToPathAndSkipsRelativeEntries()
    {
        var folder = Path.Combine(_folder, "tools");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "ffmpeg.exe"), "");
        Assert.Equal(Path.Combine(folder, "ffmpeg.exe"), FfmpegTools.FindInPath($"bin;{folder}", windows: true));
        Assert.Null(FfmpegTools.FindInPath("bin;.", windows: true));

        // The module is damaged (does not start): the one in PATH is used.
        var module = Path.Combine(_folder, "module", "ffmpeg.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(module)!);
        File.WriteAllText(module, "");
        var runner = new FakeRunner { Fails = _ => false, FailingExecutable = module };
        var info = await FfmpegTools.DetectAsync(runner, module, folder, windows: true);
        Assert.Equal((Path.Combine(folder, "ffmpeg.exe"), FfmpegSource.Path), (info!.Path, info.Source));
        Assert.Null(await FfmpegTools.DetectAsync(runner, null, null, windows: true));
    }

    [Fact]
    public void InputInfoIsReadFromFfmpegsErrorOutput()
    {
        const string text = """
            Input #0, mov,mp4,m4a,3gp,3g2,mj2, from 'video.mp4':
              Duration: 00:01:02.50, start: 0.000000, bitrate: 4612 kb/s
              Stream #0:0[0x1](und): Video: h264 (High) (avc1 / 0x31637661), yuv420p(tv, bt709, progressive), 1920x1080 [SAR 1:1 DAR 16:9], 4480 kb/s, 29.97 fps, 30 tbr, 90k tbn (default)
              Stream #0:1[0x2](und): Audio: aac (LC) (mp4a / 0x6134706D), 48000 Hz, stereo, fltp, 128 kb/s (default)
            """;
        var info = FfmpegTools.ParseInputInfo(text)!;
        Assert.Equal(new VideoInfo(1920, 1080, 29.97, true, TimeSpan.FromSeconds(62.5)), info);
        Assert.Null(FfmpegTools.ParseInputInfo("Stream #0:0: Audio: mp3, 44100 Hz"));
    }

    // ---- Export ----

    private string Source()
    {
        var source = Path.Combine(_folder, "video.mp4");
        File.WriteAllBytes(source, new byte[16]);
        return source;
    }

    private static WebExportSettings Target(WebFormat format, long bytes, double seconds = 60) =>
        new(TimeSpan.Zero, TimeSpan.FromSeconds(seconds), format, WebQuality.Medium, bytes, SizeTargets.Discord);

    [Fact]
    public async Task TooLargeOutputIsEncodedAgainAtALowerBitrate()
    {
        int calls = 0;
        var tools = new FakeVideoTools { TranscodedSize = _ => ++calls == 1 ? TenMegabytes + 500_000 : TenMegabytes - 100_000 };
        var source = Source();
        var destination = Path.Combine(_folder, "video.discord.mp4");
        await WebVideoExport.ExportAsync(tools, null, source, destination, Target(WebFormat.Mp4H264, TenMegabytes), FullHd, null, CancellationToken.None);
        Assert.Equal(2, tools.Transcodes.Count);
        Assert.True(tools.Transcodes[1].VideoBitrate < tools.Transcodes[0].VideoBitrate);
        Assert.All(tools.Transcodes, t => Assert.Equal((VideoCodec.H264, true), (t.Codec, t.SizeMatters)));
        Assert.Equal(TenMegabytes - 100_000, new FileInfo(destination).Length);
        Assert.Equal(new[] { source, destination }.Order(), Directory.GetFiles(_folder).Order());
    }

    [Fact]
    public async Task SizeNeverReachedFailsCleanly()
    {
        var tools = new FakeVideoTools { TranscodedSize = _ => TenMegabytes * 2 };
        var source = Source();
        var destination = Path.Combine(_folder, "video.discord.mp4");
        await Assert.ThrowsAsync<WebExportException>(() =>
            WebVideoExport.ExportAsync(tools, null, source, destination, Target(WebFormat.Mp4H264, TenMegabytes), FullHd, null, CancellationToken.None));
        Assert.Equal(WebVideoExport.MaxAttempts, tools.Transcodes.Count);
        Assert.Equal(new[] { source }, Directory.GetFiles(_folder));
    }

    [Fact]
    public async Task TooLongIsRefusedBeforeEncoding()
    {
        var tools = new FakeVideoTools();
        var source = Source();
        var error = await Assert.ThrowsAsync<WebExportException>(() => WebVideoExport.ExportAsync(tools, null, source,
            Path.Combine(_folder, "out.mp4"), Target(WebFormat.Mp4H264, TenMegabytes, seconds: 1800), FullHd, null, CancellationToken.None));
        Assert.Contains("5:08", error.Message);
        Assert.Empty(tools.Transcodes);
    }

    [Fact]
    public async Task FfmpegFormatsRunTwoPassesAndReportProgress()
    {
        var runner = new FakeRunner { OutputSize = 4_000_000 };
        var ffmpeg = Ffmpeg(FfmpegSource.Module, "libvpx-vp9", "libopus");
        var source = Source();
        var destination = Path.Combine(_folder, "video.discord.webm");
        var reports = new List<double>();
        await WebVideoExport.ExportAsync(new FakeVideoTools(), ffmpeg, source, destination, Target(WebFormat.WebmVp9, TenMegabytes), FullHd,
            new SyncProgress(reports.Add), CancellationToken.None, runner);
        Assert.Equal(2, runner.Calls.Count);
        Assert.Contains("1", runner.Calls[0].Arguments);
        Assert.Equal("null", runner.Calls[0].Arguments[^2]);
        Assert.Equal(4_000_000, new FileInfo(destination).Length);
        Assert.Contains(reports, r => r is > 0 and < 0.4);
        Assert.Equal(1, reports[^1]);
        // Nothing left of the passes nor of the temporary file.
        Assert.Equal(new[] { source, destination }.Order(), Directory.GetFiles(_folder).Order());
    }

    [Fact]
    public async Task CancelledExportLeavesNothing()
    {
        using var cancel = new CancellationTokenSource();
        var runner = new FakeRunner { OnRun = () => cancel.Cancel() };
        var source = Source();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WebVideoExport.ExportAsync(new FakeVideoTools(),
            Ffmpeg(FfmpegSource.Module, "libsvtav1", "libopus"), source, Path.Combine(_folder, "out.mp4"),
            new WebExportSettings(TimeSpan.Zero, TimeSpan.FromSeconds(10), WebFormat.Mp4Av1, WebQuality.Medium, null, SizeTargets.Off), FullHd, null,
            cancel.Token, runner));
        Assert.Equal(new[] { source }, Directory.GetFiles(_folder));
    }

    [Fact]
    public void FileNamesSayWhatTheExportIs()
    {
        WebExportSettings S(WebFormat f, long? bytes, string id) => new(TimeSpan.Zero, TimeSpan.FromSeconds(10), f, WebQuality.Medium, bytes, id);
        Assert.Equal("clip.discord", WebVideoExport.BaseName("/v/clip.mp4", S(WebFormat.Mp4H264, TenMegabytes, SizeTargets.Discord)));
        Assert.Equal("clip.compressed", WebVideoExport.BaseName("/v/clip.mp4", S(WebFormat.Mp4H264, null, SizeTargets.Off)));
        Assert.Equal("clip", WebVideoExport.BaseName("/v/clip.mp4", S(WebFormat.WebmVp9, null, SizeTargets.Off)));
        Assert.Equal("clip.20mb.hevc", WebVideoExport.BaseName("/v/clip.mp4", S(WebFormat.Mp4Hevc, 20 * SizeTargets.BytesPerMegabyte, SizeTargets.Custom)));
        Assert.Equal("clip.nitro.av1", WebVideoExport.BaseName("/v/clip.mp4", S(WebFormat.Mp4Av1, 50 * SizeTargets.BytesPerMegabyte, SizeTargets.Nitro)));
    }

    // ---- Form of the dialog ----

    private static WebExportForm Form(TimeSpan duration, params WebFormat[] unavailable) =>
        new(duration, null, null, FullHd with { Duration = duration },
            WebFormats.All.Select(f => unavailable.Contains(f)
                ? new WebFormatAvailability(f, WebEncoderRoute.Unavailable, "no " + f)
                : new WebFormatAvailability(f, WebEncoderRoute.System, "")).ToList());

    [Fact]
    public void FormChecksTheSizeTypedAndTheFormat()
    {
        var form = Form(TimeSpan.FromSeconds(60), WebFormat.Mp4Hevc);
        form.TargetId = SizeTargets.Custom;
        form.MegabytesText = "20.5";
        Assert.True(form.TryRead(out var settings, out _));
        Assert.Equal(20_500_000, settings.TargetBytes);
        Assert.NotEmpty(form.Summary());

        form.MegabytesText = "0.2";
        Assert.False(form.TryRead(out _, out var error));
        Assert.Equal(Loc.T("Player.Web.SizeError", SizeTargets.MinMegabytes, SizeTargets.MaxMegabytes), error);

        form.TargetId = SizeTargets.Off;
        form.Format = WebFormat.Mp4Hevc;
        Assert.False(form.TryRead(out _, out error));
        Assert.Equal("no Mp4Hevc", error);
        Assert.Equal(WebFormat.Mp4H264, form.Available(WebFormat.Mp4Hevc));
    }

    [Fact]
    public void FormRefusesAVideoTooLongForTheSize()
    {
        var form = Form(TimeSpan.FromMinutes(30));
        form.TargetId = SizeTargets.Discord;
        form.MegabytesText = "10";
        Assert.False(form.TryRead(out _, out var error));
        Assert.Contains("5:08", error);
        Assert.Equal("", form.Summary());
    }

    // ---- ffmpeg module ----

    [Fact]
    public void PinnedBuildIsADatedLgplReleaseOfBtbN()
    {
        var package = FfmpegModule.Package;
        Assert.True(FfmpegModule.IsAllowedDownload(package.Url));
        Assert.EndsWith("/" + package.FileName, package.Url.AbsoluteUri);
        Assert.Contains("/autobuild-", package.Url.AbsolutePath);
        Assert.DoesNotContain("/latest/", package.Url.AbsolutePath);
        Assert.Contains("-win64-lgpl", package.FileName);
        Assert.Matches("^[0-9a-f]{64}$", package.Sha256);
        Assert.InRange(package.Size, 10_000_000, 500_000_000);
    }

    [Theory]
    [InlineData("https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg.zip", true)]
    [InlineData("http://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-2026-09-30-13-08/ffmpeg.zip", false)]
    [InlineData("https://github.com/evil/FFmpeg-Builds/releases/download/x/ffmpeg.zip", false)]
    [InlineData("https://github.com/BtbN/FFmpeg-Builds/releases/download/x/../../y/ffmpeg.zip", false)]
    [InlineData("https://github.com:8443/BtbN/FFmpeg-Builds/releases/download/x/ffmpeg.zip", false)]
    [InlineData("https://example.com/BtbN/FFmpeg-Builds/releases/download/x/ffmpeg.zip", false)]
    public void ModuleDownloadsOnlyFromBtbNReleases(string url, bool allowed) =>
        Assert.Equal(allowed, FfmpegModule.IsAllowedDownload(new Uri(url)));

    [Fact]
    public void ModuleRedirectsStayOnGitHubStorage()
    {
        Assert.True(FfmpegModule.IsAllowedRedirect(new Uri("https://release-assets.githubusercontent.com/github-production-release-asset/1/2?sig=x")));
        Assert.False(FfmpegModule.IsAllowedRedirect(new Uri("https://evil.example/ffmpeg.zip")));
        Assert.False(FfmpegModule.IsAllowedRedirect(new Uri("http://objects.githubusercontent.com/x")));
    }

    private const string ModuleUrl = "https://github.com/BtbN/FFmpeg-Builds/releases/download/autobuild-test/ffmpeg-test-win64-lgpl.zip";
    private const string Storage = "https://release-assets.githubusercontent.com/github-production-release-asset/9/zip?sig=abc";

    /// <summary>An archive like BtbN's: one top folder, bin\, doc\, include\ and a licence.</summary>
    private static byte[] ModuleZip(params (string Name, string Content)[] extra)
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entries = new List<(string, string)>
            {
                ("ffmpeg-test/bin/ffmpeg.exe", "exe"), ("ffmpeg-test/bin/avcodec-62.dll", "dll"), ("ffmpeg-test/bin/ffplay.exe", "player"),
                ("ffmpeg-test/include/libavcodec/avcodec.h", "h"), ("ffmpeg-test/doc/ffmpeg.html", "doc"), ("ffmpeg-test/LICENSE.txt", "LGPL"),
            };
            entries.AddRange(extra);
            foreach (var (name, content) in entries)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }
        }
        return stream.ToArray();
    }

    private static PinnedPackage Pin(byte[] zip, string? sha = null) =>
        new("8.1.3", new Uri(ModuleUrl), "ffmpeg-test-win64-lgpl.zip", zip.Length, sha ?? Convert.ToHexStringLower(SHA256.HashData(zip)));

    [Fact]
    public async Task ModuleIsVerifiedThenOnlyItsProgramsAreUnzipped()
    {
        var zip = ModuleZip();
        var handler = new FakeHandler();
        handler.Redirect(ModuleUrl, Storage);
        handler.File(Storage, zip);
        var folder = Path.Combine(_folder, "modules", "ffmpeg");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "old.dll"), "old");
        long received = 0;
        await FfmpegModule.InstallAsync(new SyncProgress(r => received = (long)r), CancellationToken.None, Pin(zip), folder, handler);

        Assert.Equal(zip.Length, received);
        var files = Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/')).Order();
        Assert.Equal(new[] { "LICENSE.txt", "bin/avcodec-62.dll", "bin/ffmpeg.exe", "version.txt" }.Order(), files);
        Assert.Equal("8.1.3", FfmpegModule.InstalledVersion(folder));
        // The download folder is emptied.
        Assert.False(Directory.Exists(Path.Combine(_folder, "modules", "download")));

        FfmpegModule.Uninstall(folder);
        Assert.False(Directory.Exists(folder));
    }

    [Fact]
    public async Task ModuleWithAnotherChecksumIsRefusedAndDeleted()
    {
        var zip = ModuleZip();
        var handler = new FakeHandler();
        handler.File(ModuleUrl, zip);
        var folder = Path.Combine(_folder, "modules", "ffmpeg");
        await Assert.ThrowsAsync<UpdateVerificationException>(() =>
            FfmpegModule.InstallAsync(null, CancellationToken.None, Pin(zip, new string('0', 64)), folder, handler));
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.GetFiles(_folder, "*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task ModuleRedirectedElsewhereIsRefused()
    {
        var zip = ModuleZip();
        var handler = new FakeHandler();
        handler.Redirect(ModuleUrl, "https://evil.example/ffmpeg.zip");
        handler.File("https://evil.example/ffmpeg.zip", zip);
        await Assert.ThrowsAsync<InvalidDataException>(() =>
            FfmpegModule.InstallAsync(null, CancellationToken.None, Pin(zip), Path.Combine(_folder, "modules", "ffmpeg"), handler));
        Assert.DoesNotContain(handler.Requested, u => u.Host == "evil.example");
    }

    [Fact]
    public void ArchiveLeavingItsFolderIsRefused()
    {
        var zip = Path.Combine(_folder, "evil.zip");
        File.WriteAllBytes(zip, ModuleZip(("ffmpeg-test/bin/../../evil.dll", "x")));
        var folder = Path.Combine(_folder, "modules", "ffmpeg");
        Assert.Throws<InvalidDataException>(() => FfmpegModule.Unpack(zip, folder, "8.1.3"));
        Assert.False(Directory.Exists(folder));
        Assert.False(File.Exists(Path.Combine(_folder, "evil.dll")));

        File.WriteAllBytes(zip, ModuleZipWithout());
        Assert.Throws<InvalidDataException>(() => FfmpegModule.Unpack(zip, folder, "8.1.3"));
    }

    /// <summary>An archive without ffmpeg.exe.</summary>
    private static byte[] ModuleZipWithout()
    {
        var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            using var writer = new StreamWriter(zip.CreateEntry("ffmpeg-test/bin/avcodec-62.dll").Open());
            writer.Write("dll");
        }
        return stream.ToArray();
    }

    // ---- Fakes ----

    /// <summary>Answers from a table of addresses; anything else is a 404. Never reaches the network.</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Func<HttpResponseMessage>> _routes = new();
        public readonly List<Uri> Requested = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requested.Add(request.RequestUri!);
            return Task.FromResult(_routes.TryGetValue(request.RequestUri!.AbsoluteUri, out var answer)
                ? answer()
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        public void File(string url, byte[] content) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(content) };

        public void Redirect(string url, string location) =>
            _routes[url] = () => new HttpResponseMessage(HttpStatusCode.Found) { Headers = { Location = new Uri(location) } };
    }

    /// <summary>
    /// Plays ffmpeg: "-version" and "-encoders" answer with <see cref="Encoders"/>; an encoding writes
    /// <see cref="OutputSize"/> bytes to its output (the last argument) and reports its progress.
    /// </summary>
    private sealed class FakeRunner : IProcessRunner
    {
        public string Encoders { get; init; } = "------\n";
        public long OutputSize { get; init; } = 1000;
        public Func<IReadOnlyList<string>, bool> Fails { get; init; } = _ => false;
        public string? FailingExecutable { get; init; }
        public Action? OnRun { get; init; }
        public List<(string Executable, IReadOnlyList<string> Arguments)> Calls { get; } = new();

        public Task<ProcessOutcome> RunAsync(string executable, IReadOnlyList<string> arguments, Action<string>? outputLine, TimeSpan? timeout,
            CancellationToken cancel)
        {
            if (executable == FailingExecutable) return Task.FromResult(new ProcessOutcome(-1073741515, "", "DLL missing"));
            if (arguments.Contains("-version")) return Task.FromResult(new ProcessOutcome(0, "ffmpeg version n8.1.3-9-g29e619e767 Copyright (c) 2000-2026\n", ""));
            if (arguments.Contains("-encoders")) return Task.FromResult(new ProcessOutcome(0, Encoders, ""));
            if (Fails(arguments)) return Task.FromResult(new ProcessOutcome(1, "", "No device"));
            Calls.Add((executable, arguments));
            OnRun?.Invoke();
            cancel.ThrowIfCancellationRequested();
            outputLine?.Invoke("out_time_us=5000000");
            outputLine?.Invoke("progress=end");
            var output = arguments[^1];
            if (output != "-") System.IO.File.WriteAllBytes(output, new byte[OutputSize]);
            return Task.FromResult(new ProcessOutcome(0, "", ""));
        }
    }

    private sealed class SyncProgress(Action<double> report) : IProgress<double>, IProgress<long>
    {
        public void Report(double value) => report(value);
        public void Report(long value) => report(value);
    }
}
