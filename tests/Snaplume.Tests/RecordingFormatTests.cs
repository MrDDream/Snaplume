using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Platform.Linux.Media;
using Snaplume.Player;
using Snaplume.Tests.Fakes;

namespace Snaplume.Tests;

/// <summary>
/// Format and compression of the recordings (Settings › Video): recorded directly or converted afterwards, the
/// settings, the conversion of a finished recording (names, replacement, failures), the recorders' encodings.
/// </summary>
public class RecordingFormatTests
{
    static RecordingFormatTests() => Loc.Initialize("fr");

    private static readonly VideoInfo Recording = new(1920, 1080, 30, true, TimeSpan.FromSeconds(20));

    private static RecordingFormatChoice Choice(WebFormat format, string compression, double megabytes = 25, bool keep = false) =>
        new(format, compression, megabytes, keep);

    // ---- When to convert ----

    [Fact]
    public void The_defaults_record_mp4_h264_as_before_without_conversion()
    {
        var settings = new AppSettings();
        Assert.Equal("mp4", settings.VideoFormat);
        Assert.Equal(RecordingCompression.None, settings.VideoCompression);
        Assert.False(settings.VideoKeepOriginal);
        bool asked = false;
        var plan = RecordingPlanner.Plan(RecordingFormatChoice.From(settings), 60, _ =>
        {
            asked = true;
            return true;
        });
        Assert.Equal(RecordingRoute.Direct, plan.Route);
        Assert.Null(plan.Conversion);
        Assert.Equal(RecordingEncoding.Default, plan.Encoding);
        Assert.Equal(60, plan.Framerate);
        Assert.False(asked); // H.264 needs no question to the recorder
    }

    [Fact]
    public void Quality_presets_of_h264_go_to_the_recorder()
    {
        var high = RecordingPlanner.Plan(Choice(WebFormat.Mp4H264, RecordingCompression.High), 60, _ => false);
        Assert.Equal(new RecordingEncoding(VideoCodec.H264, 62, 0), high.Encoding);
        Assert.Equal(60, high.Framerate);
        var medium = RecordingPlanner.Plan(Choice(WebFormat.Mp4H264, RecordingCompression.Medium), 60, _ => false);
        Assert.Equal(new RecordingEncoding(VideoCodec.H264, 50, 1080), medium.Encoding);
        Assert.Equal(30, medium.Framerate);
        var light = RecordingPlanner.Plan(Choice(WebFormat.Mp4H264, RecordingCompression.Light), 24, _ => false);
        Assert.Equal(new RecordingEncoding(VideoCodec.H264, 40, 720), light.Encoding);
        Assert.Equal(24, light.Framerate);
        Assert.All(new[] { high, medium, light }, p => Assert.Null(p.Conversion));
    }

    [Fact]
    public void Hevc_is_recorded_directly_when_the_recorder_has_an_encoder_else_converted()
    {
        var direct = RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, RecordingCompression.Light), 30, c => c == VideoCodec.Hevc);
        Assert.Equal(RecordingRoute.Direct, direct.Route);
        Assert.Equal(new RecordingEncoding(VideoCodec.Hevc, 40, 720), direct.Encoding);
        Assert.Null(direct.Conversion);

        var converted = RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, RecordingCompression.None), 30, _ => false);
        Assert.Equal(RecordingRoute.NoLiveEncoder, converted.Route);
        Assert.Equal(new RecordingEncoding(VideoCodec.H264, RecordingPlanner.IntermediateQuality), converted.Encoding);
        Assert.Equal(new RecordingConversionTarget(WebFormat.Mp4Hevc, WebQuality.Original, null, SizeTargets.Off), converted.Conversion);
    }

    [Fact]
    public void Av1_and_webm_are_always_converted_with_the_preset()
    {
        foreach (var format in new[] { WebFormat.Mp4Av1, WebFormat.WebmVp9 })
        {
            var plan = RecordingPlanner.Plan(Choice(format, RecordingCompression.Medium), 60, _ => true);
            Assert.Equal(RecordingRoute.NotLiveEncodable, plan.Route);
            Assert.Equal(VideoCodec.H264, plan.Encoding.Codec);
            Assert.Equal(0, plan.Encoding.MaxShortSide); // the conversion scales
            Assert.Equal(60, plan.Framerate);
            Assert.Equal(new RecordingConversionTarget(format, WebQuality.Medium, null, SizeTargets.Off), plan.Conversion);
        }
        var none = RecordingPlanner.Plan(Choice(WebFormat.WebmVp9, RecordingCompression.None), 30, _ => true);
        Assert.Equal(WebQuality.Original, none.Conversion!.Quality);
    }

    [Fact]
    public void A_file_size_is_always_a_conversion_even_in_h264()
    {
        var discord = RecordingPlanner.Plan(Choice(WebFormat.Mp4H264, SizeTargets.Discord), 30, _ => true);
        Assert.Equal(RecordingRoute.SizeTarget, discord.Route);
        Assert.Equal(10_000_000, discord.Conversion!.TargetBytes);
        Assert.Equal(SizeTargets.Discord, discord.Conversion.TargetId);
        Assert.Equal(WebFormat.Mp4H264, discord.Conversion.Format);

        var custom = RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, SizeTargets.Custom, 33.5), 30, _ => true);
        Assert.Equal(RecordingRoute.SizeTarget, custom.Route);
        Assert.Equal(33_500_000, custom.Conversion!.TargetBytes);
        Assert.Equal(VideoCodec.H264, custom.Encoding.Codec);
    }

    [Fact]
    public void A_recorder_falling_back_to_h264_gets_its_recording_converted_to_hevc()
    {
        var plan = RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, RecordingCompression.High), 30, _ => true);
        Assert.Null(RecordingPlanner.AfterRecording(plan, VideoCodec.Hevc));
        Assert.Equal(new RecordingConversionTarget(WebFormat.Mp4Hevc, WebQuality.Original, null, SizeTargets.Off),
            RecordingPlanner.AfterRecording(plan, VideoCodec.H264));
        // A planned conversion stays the one planned.
        var webm = RecordingPlanner.Plan(Choice(WebFormat.WebmVp9, RecordingCompression.Light), 30, _ => true);
        Assert.Same(webm.Conversion, RecordingPlanner.AfterRecording(webm, VideoCodec.H264));
    }

    [Fact]
    public void The_settings_say_which_case_applies()
    {
        Assert.Equal("Enregistré directement en MP4 (HEVC / H.265), sans conversion.",
            RecordingPlanner.Describe(RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, RecordingCompression.None), 30, _ => true), WebFormat.Mp4Hevc));
        Assert.Contains("MP4 (AV1) ne peut pas être encodé en direct",
            RecordingPlanner.Describe(RecordingPlanner.Plan(Choice(WebFormat.Mp4Av1, RecordingCompression.None), 30, _ => true), WebFormat.Mp4Av1));
        Assert.Contains("pas d'encodeur MP4 (HEVC / H.265)",
            RecordingPlanner.Describe(RecordingPlanner.Plan(Choice(WebFormat.Mp4Hevc, RecordingCompression.None), 30, _ => false), WebFormat.Mp4Hevc));
        Assert.Contains("taille maximale",
            RecordingPlanner.Describe(RecordingPlanner.Plan(Choice(WebFormat.Mp4H264, SizeTargets.Email), 30, _ => true), WebFormat.Mp4H264));
    }

    // ---- Settings ----

    [Fact]
    public void Unknown_values_fall_back_to_no_conversion()
    {
        var settings = new AppSettings
        {
            VideoFormat = "webp", VideoCompression = "huge", VideoCompressionMegabytes = double.NaN, WebExportQuality = "original",
        };
        settings.Sanitize();
        Assert.Equal("mp4", settings.VideoFormat); // animated WebP is not a recording format
        Assert.Equal(RecordingCompression.None, settings.VideoCompression);
        Assert.Equal(25, settings.VideoCompressionMegabytes);
        Assert.Equal("medium", settings.WebExportQuality); // "original" is not a choice of the export dialog

        var kept = new AppSettings { VideoFormat = "av1", VideoCompression = "nitro", VideoCompressionMegabytes = 99_999 };
        kept.Sanitize();
        Assert.Equal("av1", kept.VideoFormat);
        Assert.Equal("nitro", kept.VideoCompression);
        Assert.Equal(SizeTargets.MaxMegabytes, kept.VideoCompressionMegabytes);
    }

    [Fact]
    public void An_old_settings_file_keeps_recording_as_before()
    {
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"VideoFramerate": 60, "WebExportFormat": "webm"}""")!;
        old.Sanitize();
        Assert.Equal("mp4", old.VideoFormat);
        Assert.Equal(RecordingCompression.None, old.VideoCompression);
        Assert.Equal(RecordingRoute.Direct, RecordingPlanner.Plan(RecordingFormatChoice.From(old), old.VideoFramerate, _ => true).Route);
    }

    [Fact]
    public void The_export_dialog_opens_with_the_format_of_the_recordings()
    {
        var settings = new AppSettings { WebExportTarget = SizeTargets.Discord };
        RecordingFormats.ApplyToExportDialog(Choice(WebFormat.Mp4Av1, RecordingCompression.Light), settings);
        Assert.Equal(("av1", "light", SizeTargets.Off), (settings.WebExportFormat, settings.WebExportQuality, settings.WebExportTarget));

        RecordingFormats.ApplyToExportDialog(Choice(WebFormat.WebmVp9, SizeTargets.Custom, 33), settings);
        Assert.Equal(("webm", SizeTargets.Custom, 33.0), (settings.WebExportFormat, settings.WebExportTarget, settings.WebExportMegabytes));

        // No compression is the best preset of the dialog.
        RecordingFormats.ApplyToExportDialog(Choice(WebFormat.Mp4Hevc, RecordingCompression.None), settings);
        Assert.Equal(("hevc", "high", SizeTargets.Off), (settings.WebExportFormat, settings.WebExportQuality, settings.WebExportTarget));
    }

    [Fact]
    public void Animated_webp_is_not_offered_for_recordings()
    {
        var ffmpeg = new FfmpegInfo("ffmpeg", FfmpegSource.Path, "7.1", new HashSet<string> { "libsvtav1", "libopus", "libvpx-vp9", "libwebp_anim", "aac" });
        var offered = RecordingFormats.Offered(c => c == VideoCodec.H264, ffmpeg, windows: true);
        Assert.Equal(new[] { WebFormat.Mp4H264, WebFormat.Mp4Hevc, WebFormat.Mp4Av1, WebFormat.WebmVp9 }, offered.Select(f => f.Format));
        Assert.Single(RecordingFormats.Offered(_ => true, null, windows: true));
    }

    [Fact]
    public void Recording_formats_are_read_like_the_export_ones_without_webp()
    {
        foreach (var format in RecordingFormats.All) Assert.Equal(format, RecordingFormats.FromId(WebFormats.Id(format)));
        Assert.Equal(WebFormat.Mp4H264, RecordingFormats.FromId(WebFormats.Id(WebFormat.WebP)));
        Assert.Equal(WebFormat.Mp4H264, RecordingFormats.FromId("unknown"));
        Assert.Equal(WebFormat.Mp4H264, RecordingFormats.FromId(null));
    }

    [Fact]
    public void The_original_quality_keeps_size_and_frame_rate()
    {
        var source = new VideoInfo(2560, 1440, 60, true, TimeSpan.FromSeconds(10));
        var plan = WebExportPlanner.PlanForQuality(WebFormat.Mp4Av1, WebQuality.Original, source);
        Assert.Equal((2560, 1440, 60.0), (plan.Width, plan.Height, plan.FramesPerSecond));
        Assert.True(plan.Crf < WebExportPlanner.PlanForQuality(WebFormat.Mp4Av1, WebQuality.High, source).Crf);
        Assert.Equal("original", WebFormats.QualityId(WebQuality.Original));
        Assert.Equal(WebQuality.Original, WebFormats.QualityFromId("original"));
    }

    // ---- Conversion of a finished recording ----

    /// <summary>Writes the destination like an encoder, or fails / waits as asked.</summary>
    private sealed class FakeEncoder
    {
        public int Calls { get; private set; }
        public WebExportSettings? Settings { get; private set; }
        public Exception? Failure { get; init; }
        public bool WaitForCancel { get; init; }

        public async Task Encode(string source, string destination, WebExportSettings settings, VideoInfo info, IProgress<double>? progress,
            CancellationToken cancel)
        {
            Calls++;
            Settings = settings;
            Assert.True(File.Exists(source));
            if (WaitForCancel) await Task.Delay(Timeout.Infinite, cancel);
            if (Failure != null) throw Failure;
            await File.WriteAllBytesAsync(destination, new byte[] { 1, 2, 3 }, cancel);
            progress?.Report(1);
        }
    }

    private static string Recorded(TempFolder temp)
    {
        var path = temp.File("Snaplume video 2026-11-03.mp4");
        File.WriteAllBytes(path, new byte[] { 9, 9, 9, 9 });
        return path;
    }

    private static Func<string, CancellationToken, Task<VideoInfo?>> Probe(VideoInfo? info) => (_, _) => Task.FromResult(info);

    private static readonly RecordingConversionTarget Webm = new(WebFormat.WebmVp9, WebQuality.Medium, null, SizeTargets.Off);
    private static readonly RecordingConversionTarget Hevc = new(WebFormat.Mp4Hevc, WebQuality.Original, null, SizeTargets.Off);

    [Fact]
    public async Task The_converted_file_replaces_the_recording()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var recorded = Recorded(temp);
        var encoder = new FakeEncoder();
        var result = await RecordingConversion.RunAsync(recorded, folder, "Video_1", Webm, keepOriginal: false, Probe(Recording), encoder.Encode, null,
            CancellationToken.None);
        Assert.True(result.Converted);
        Assert.Equal(Path.Combine(folder, "Video_1.webm"), result.Path);
        Assert.Null(result.Original);
        Assert.False(File.Exists(recorded));
        Assert.Equal(new[] { "Video_1.webm" }, Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.Equal(new WebExportSettings(TimeSpan.Zero, Recording.Duration, WebFormat.WebmVp9, WebQuality.Medium, null, SizeTargets.Off), encoder.Settings);
    }

    [Fact]
    public async Task An_mp4_conversion_takes_the_name_of_the_recording()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var target = new RecordingConversionTarget(WebFormat.Mp4H264, WebQuality.High, 10_000_000, SizeTargets.Discord);
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", target, keepOriginal: false, Probe(Recording),
            new FakeEncoder().Encode, null, CancellationToken.None);
        Assert.Equal(Path.Combine(folder, "Video_1.mp4"), result.Path);
    }

    [Fact]
    public async Task The_original_is_kept_on_request_under_its_name()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var recorded = Recorded(temp);
        var result = await RecordingConversion.RunAsync(recorded, folder, "Video_1", Hevc, keepOriginal: true, Probe(Recording), new FakeEncoder().Encode,
            null, CancellationToken.None);
        Assert.True(result.Converted);
        Assert.Equal(Path.Combine(folder, "Video_1.hevc.mp4"), result.Path);
        Assert.Equal(Path.Combine(folder, "Video_1.mp4"), result.Original);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, File.ReadAllBytes(result.Original!));
        Assert.False(File.Exists(recorded));
    }

    [Fact]
    public async Task A_kept_original_that_cannot_be_moved_leaves_the_conversion_done()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var recorded = Recorded(temp);
        var encoder = new FakeEncoder();
        // The original vanishes once encoded (antivirus, disk removed...): its move fails.
        var result = await RecordingConversion.RunAsync(recorded, folder, "Video_1", Hevc, keepOriginal: true, Probe(Recording),
            async (source, destination, settings, info, progress, cancel) =>
            {
                await encoder.Encode(source, destination, settings, info, progress, cancel);
                File.Delete(source);
            }, null, CancellationToken.None);
        Assert.True(result.Converted);
        Assert.Equal(Path.Combine(folder, "Video_1.hevc.mp4"), result.Path);
        Assert.Null(result.Original);
        Assert.Equal(recorded, result.OriginalLeftAt);
        Assert.Equal(1, encoder.Calls);
        Assert.Equal(new[] { "Video_1.hevc.mp4" }, Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task Names_already_taken_are_never_overwritten()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "Video_1.webm"), "older");
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", Webm, keepOriginal: false, Probe(Recording),
            new FakeEncoder().Encode, null, CancellationToken.None);
        Assert.NotEqual(Path.Combine(folder, "Video_1.webm"), result.Path);
        Assert.Equal("older", File.ReadAllText(Path.Combine(folder, "Video_1.webm")));
    }

    [Fact]
    public async Task A_failed_conversion_keeps_the_original_and_says_so()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var recorded = Recorded(temp);
        var result = await RecordingConversion.RunAsync(recorded, folder, "Video_1", Webm, keepOriginal: false, Probe(Recording),
            new FakeEncoder { Failure = new InvalidOperationException("ffmpeg (1): boom") }.Encode, null, CancellationToken.None);
        Assert.False(result.Converted);
        Assert.Equal(Loc.T("Video.ConvertFailed"), result.Problem);
        Assert.Equal(Path.Combine(folder, "Video_1.mp4"), result.Path);
        Assert.Equal(result.Path, result.Original);
        Assert.Equal(new byte[] { 9, 9, 9, 9 }, File.ReadAllBytes(result.Path));
        Assert.Equal(new[] { "Video_1.mp4" }, Directory.GetFiles(folder).Select(Path.GetFileName));
    }

    [Fact]
    public async Task A_format_not_available_here_keeps_the_original_with_the_reason()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", Webm, keepOriginal: false, Probe(Recording),
            new FakeEncoder { Failure = new WebExportException(Loc.T("Player.Web.NeedsModule")) }.Encode, null, CancellationToken.None);
        Assert.Equal(Loc.T("Player.Web.NeedsModule"), result.Problem);
        Assert.True(File.Exists(Path.Combine(folder, "Video_1.mp4")));
    }

    [Fact]
    public async Task A_recording_too_long_for_the_size_is_kept_with_the_longest_duration()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var encoder = new FakeEncoder();
        var target = new RecordingConversionTarget(WebFormat.Mp4H264, WebQuality.High, 10_000_000, SizeTargets.Discord);
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", target, keepOriginal: false,
            Probe(Recording with { Duration = TimeSpan.FromMinutes(30) }), encoder.Encode, null, CancellationToken.None);
        Assert.Equal(0, encoder.Calls);
        Assert.Contains("5:08", result.Problem);
        Assert.True(File.Exists(Path.Combine(folder, "Video_1.mp4")));
    }

    [Fact]
    public async Task An_unreadable_recording_is_kept()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var encoder = new FakeEncoder();
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", Webm, keepOriginal: false, Probe(null), encoder.Encode, null,
            CancellationToken.None);
        Assert.Equal(0, encoder.Calls);
        Assert.Equal(Loc.T("Video.ConvertFailed"), result.Problem);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task A_cancelled_conversion_keeps_the_original()
    {
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        using var cancel = new CancellationTokenSource();
        var running = RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", Webm, keepOriginal: false, Probe(Recording),
            new FakeEncoder { WaitForCancel = true }.Encode, null, cancel.Token);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        cancel.Cancel();
        var result = await running;
        Assert.True(result.Cancelled);
        Assert.Null(result.Problem);
        Assert.Equal(Path.Combine(folder, "Video_1.mp4"), result.Path);
        Assert.True(File.Exists(result.Path));
    }

    [Fact]
    public async Task The_real_export_writes_the_converted_recording()
    {
        // The system's H.264 encoder (a fake one): a 10 MB target for a 20 s recording.
        using var temp = new TempFolder();
        var folder = Path.Combine(temp.Path, "workspace");
        var tools = new FakeVideoTools { TranscodedSize = _ => 4_000_000 };
        var target = new RecordingConversionTarget(WebFormat.Mp4H264, WebQuality.High, 10_000_000, SizeTargets.Discord);
        var result = await RecordingConversion.RunAsync(Recorded(temp), folder, "Video_1", target, keepOriginal: true, Probe(Recording),
            (source, destination, settings, info, progress, cancel) => WebVideoExport.ExportAsync(tools, null, source, destination, settings, info, progress, cancel),
            null, CancellationToken.None);
        Assert.True(result.Converted);
        Assert.Equal(Path.Combine(folder, "Video_1.discord.mp4"), result.Path);
        Assert.Equal(4_000_000, new FileInfo(result.Path).Length);
        Assert.Equal(Path.Combine(folder, "Video_1.mp4"), result.Original);
        Assert.Single(tools.Transcodes);
        Assert.Equal(2, Directory.GetFiles(folder).Length); // no temporary file left
    }

    // ---- Encodings of the recorders ----

    [Fact]
    public void The_output_size_keeps_the_shape_and_never_enlarges()
    {
        Assert.Equal((1920, 1080), RecordingEncoding.Default.OutputSize(1921, 1081));
        Assert.Equal((1280, 720), new RecordingEncoding(MaxShortSide: 720).OutputSize(2560, 1440));
        Assert.Equal((720, 1280), new RecordingEncoding(MaxShortSide: 720).OutputSize(1080, 1920));
        Assert.Equal((800, 600), new RecordingEncoding(MaxShortSide: 1080).OutputSize(800, 600));
    }

    [Fact]
    public void The_linux_pipeline_is_unchanged_by_default()
    {
        var source = new X11AreaSource(new PixelRect(0, 0, 1920, 1080), true);
        var plain = RecordingPipeline.Build(source, 30, "x264enc", "avenc_aac", true, false, null, 1920, 1080);
        Assert.Equal(plain, RecordingPipeline.Build(source, 30, "x264enc", "avenc_aac", true, false, null, 1920, 1080, encoding: RecordingEncoding.Default));
        Assert.Contains("videorate ! video/x-raw,framerate=30/1 ! videoconvert ! video/x-raw,format=I420 ! queue ! x264enc speed-preset=veryfast tune=zerolatency pass=qual quantizer=21 key-int-max=60 ! h264parse", plain);
        Assert.DoesNotContain("videoscale", plain);
    }

    [Fact]
    public void The_linux_pipeline_records_hevc_scaled_with_the_preset()
    {
        var source = new X11AreaSource(new PixelRect(0, 0, 1920, 1080), true);
        var hevc = RecordingPipeline.Build(source, 30, "x265enc", "avenc_aac", true, false, null, 1920, 1080,
            encoding: new RecordingEncoding(VideoCodec.Hevc, 40, 720));
        Assert.Contains("videoscale add-borders=false ! video/x-raw,width=1280,height=720,pixel-aspect-ratio=1/1 ! videoconvert ! video/x-raw,format=I420", hevc);
        Assert.Contains("x265enc speed-preset=superfast tune=zerolatency qp=32 key-int-max=60 ! h265parse ! video/x-h265,stream-format=hvc1,alignment=au ! queue ! mux.", hevc);

        var va = RecordingPipeline.Build(source, 30, "vah265enc", null, false, false, null, 1920, 1080, encoding: new RecordingEncoding(VideoCodec.Hevc));
        Assert.Contains("videoconvert ! video/x-raw,format=NV12 ! queue ! vah265enc rate-control=cqp qpi=24 qpp=24 qpb=24 b-frames=0 key-int-max=60", va);
        var vaapi = RecordingPipeline.Build(source, 60, "vaapih265enc", null, false, false, null, 1920, 1080, encoding: new RecordingEncoding(VideoCodec.Hevc));
        Assert.Contains("vaapih265enc rate-control=cqp init-qp=24 keyframe-period=120 max-bframes=0 ! h265parse", vaapi);
    }

    [Fact]
    public void The_linux_webcam_pipeline_is_scaled_after_the_compositor()
    {
        var placement = RecordingPipeline.PlaceWebcam("/dev/video0", "BottomRight", 20, 1920, 1080);
        var description = RecordingPipeline.Build(new X11AreaSource(new PixelRect(0, 0, 1920, 1080), true), 30, "x264enc", null, false, false,
            placement, 1920, 1080, encoding: new RecordingEncoding(VideoCodec.H264, 50, 720));
        Assert.Contains("compositor name=comp", description);
        Assert.Contains("! videoscale add-borders=false ! video/x-raw,width=1280,height=720,pixel-aspect-ratio=1/1 ! videoconvert ! video/x-raw,format=I420 ! queue ! x264enc", description);
        Assert.Contains("quantizer=26", description);
    }

    [Fact]
    public void Linux_quality_follows_the_recorder_scale()
    {
        Assert.Equal(21, RecordingPipeline.Quantizer(RecordingEncoding.DefaultQuality));
        Assert.Equal(23, RecordingPipeline.Quantizer(62));
        Assert.Equal(26, RecordingPipeline.Quantizer(50));
        Assert.Equal(29, RecordingPipeline.Quantizer(40));
        Assert.Equal(19, RecordingPipeline.Quantizer(RecordingPlanner.IntermediateQuality));
        Assert.Equal(RecordingPipeline.Bitrate(1920, 1080, 30), RecordingPipeline.Bitrate(1920, 1080, 30, RecordingEncoding.DefaultQuality));
        Assert.True(RecordingPipeline.Bitrate(1920, 1080, 60, 40) < RecordingPipeline.Bitrate(1920, 1080, 60));
    }

    // ---- ffmpeg module card ----

    [Theory]
    [InlineData("n8.1.3-9-g29e619e767-20260930", "8.1.3")]
    [InlineData("n8.1.3-9-g29e619e767", "8.1.3")]
    [InlineData("7.1.1", "7.1.1")]
    [InlineData("6.1.1-3ubuntu5", "6.1.1")]
    [InlineData("N-121000-g1234abcdef-20260101", "N-121000")]
    [InlineData("2024-03-01-git-abcdef", "2024-03-01-git-abcdef")]
    [InlineData("", "")]
    public void Ffmpeg_versions_are_shortened(string version, string expected) => Assert.Equal(expected, FfmpegTools.ShortVersion(version));

    [Fact]
    public void The_module_size_counts_every_file()
    {
        using var temp = new TempFolder();
        Directory.CreateDirectory(Path.Combine(temp.Path, "bin"));
        File.WriteAllBytes(Path.Combine(temp.Path, "bin", "ffmpeg.exe"), new byte[1000]);
        File.WriteAllBytes(Path.Combine(temp.Path, "LICENSE.txt"), new byte[24]);
        Assert.Equal(1024, FfmpegModule.InstalledBytes(temp.Path));
        Assert.Equal(0, FfmpegModule.InstalledBytes(Path.Combine(temp.Path, "missing")));
    }
}
