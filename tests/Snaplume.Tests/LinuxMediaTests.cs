using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Platform.Linux.Media;
using Snaplume.Platform.Linux.Ocr;

namespace Snaplume.Tests;

/// <summary>Pure logic of the Linux media services: GStreamer pipelines, ffmpeg command lines, Tesseract, webcams.</summary>
public class LinuxMediaTests
{
    static LinuxMediaTests() => Loc.Initialize("fr");

    [Fact]
    public void X11_pipeline_records_an_even_area_with_sound()
    {
        var area = RecordingPipeline.EvenArea(new PixelRect(10, 20, 801, 601));
        Assert.Equal(new PixelRect(10, 20, 800, 600), area);
        var description = RecordingPipeline.Build(new X11AreaSource(area, true), 30, "x264enc", "avenc_aac", true, true, null, 800, 600);
        Assert.Contains("ximagesrc use-damage=false show-pointer=true startx=10 starty=20 endx=809 endy=619", description);
        Assert.Contains("video/x-raw,framerate=30/1", description);
        Assert.Contains("x264enc speed-preset=veryfast tune=zerolatency", description);
        Assert.Contains("mp4mux name=mux faststart=true ! filesink name=out", description);
        Assert.Contains("audiomixer name=mix", description);
        Assert.Contains("pulsesrc device=@DEFAULT_MONITOR@", description);
        Assert.Contains("avenc_aac bitrate=160000 ! aacparse", description);
        Assert.DoesNotContain("location", description); // the file name is never parsed
        Assert.DoesNotContain("compositor", description);
    }

    [Fact]
    public void PipeWire_pipeline_uses_the_portal_descriptor()
    {
        var description = RecordingPipeline.Build(new PipeWireSource(42, 57, 1920, 1080), 60, "openh264enc", null, true, false, null, 1920, 1080);
        Assert.StartsWith("mp4mux", description);
        Assert.Contains("pipewiresrc fd=42 path=57 do-timestamp=true", description);
        Assert.Contains("openh264enc", description);
        Assert.Contains("framerate=60/1", description);
        Assert.DoesNotContain("pulsesrc", description); // no audio encoder: no sound rather than a failure
    }

    [Fact]
    public void Single_audio_source_needs_no_mixer()
    {
        var description = RecordingPipeline.Build(new X11AreaSource(new PixelRect(0, 0, 100, 100), false), 30, "x264enc", "opusenc", false, true, null, 100, 100);
        Assert.DoesNotContain("audiomixer", description);
        Assert.DoesNotContain("@DEFAULT_MONITOR@", description);
        Assert.Contains("pulsesrc provide-clock=false", description);
        Assert.Contains("show-pointer=false", description);
    }

    [Fact]
    public void Webcam_is_composited_in_its_corner()
    {
        var placement = RecordingPipeline.PlaceWebcam("/dev/video0", "BottomRight", 20, 1920, 1080);
        Assert.Equal(384, placement.Width);
        Assert.Equal(288, placement.Height);
        Assert.Equal(1920 - 384 - 38, placement.X);
        Assert.Equal(1080 - 288 - 38, placement.Y);
        Assert.Equal(0, placement.Width % 2);

        var topLeft = RecordingPipeline.PlaceWebcam("/dev/video2", "TopLeft", 20, 1920, 1080);
        Assert.Equal((38, 38), (topLeft.X, topLeft.Y));

        var description = RecordingPipeline.Build(new X11AreaSource(new PixelRect(0, 0, 1920, 1080), true), 30, "x264enc", null, false, false,
            placement, 1920, 1080);
        Assert.Contains("compositor name=comp", description);
        Assert.Contains($"sink_1::xpos={placement.X} sink_1::ypos={placement.Y}", description);
        Assert.Contains("v4l2src device=/dev/video0 ! decodebin", description);
        Assert.Contains("comp.sink_0", description);
    }

    [Fact]
    public void Webcam_never_exceeds_half_the_height()
    {
        var placement = RecordingPipeline.PlaceWebcam("/dev/video0", "TopRight", 60, 1000, 200);
        Assert.True(placement.Height <= 100);
        Assert.True(placement.X + placement.Width <= 1000);
    }

    [Fact]
    public void Encoders_are_chosen_in_order()
    {
        Assert.Equal("openh264enc", RecordingPipeline.Choose(RecordingPipeline.VideoEncoders, e => e == "openh264enc"));
        Assert.Equal("x264enc", RecordingPipeline.Choose(RecordingPipeline.VideoEncoders, _ => true));
        Assert.Null(RecordingPipeline.Choose(RecordingPipeline.AudioEncoders, _ => false));
        Assert.InRange(RecordingPipeline.Bitrate(1920, 1080, 30), 2_000_000, 40_000_000);
    }

    [Fact]
    public void Clip_export_reencodes_the_exact_range()
    {
        var args = FfmpegArguments.ExportClip("/v/in put.mp4", "/v/out.mp4", TimeSpan.FromSeconds(1.5), TimeSpan.FromSeconds(4), "libx264", true);
        Assert.Equal("-ss", args[args.IndexOf("-i") - 2]);
        Assert.Equal("1.5", args[args.IndexOf("-i") - 1]);
        Assert.Equal("/v/in put.mp4", args[args.IndexOf("-i") + 1]);
        Assert.Equal("2.5", args[args.IndexOf("-t") + 1]);
        Assert.Contains("libx264", args);
        Assert.Contains("aac", args);
        Assert.Equal("/v/out.mp4", args[^1]);
        Assert.Contains("pipe:1", args);

        var silent = FfmpegArguments.ExportClip("a.mp4", "b.mp4", TimeSpan.Zero, TimeSpan.FromSeconds(1), "libopenh264", false);
        Assert.Contains("-an", silent);
        Assert.Contains("libopenh264", silent);
    }

    [Fact]
    public void Ffmpeg_output_is_parsed()
    {
        const string encoders = "Encoders:\n V..... = Video\n ------\n V....D libx264              libx264 H.264\n A....D aac                  AAC\n V....D libopenh264          OpenH264\n";
        var parsed = FfmpegArguments.ParseEncoders(encoders);
        Assert.Contains("libx264", parsed);
        Assert.Contains("aac", parsed);
        Assert.DoesNotContain("=", parsed);

        var duration = TimeSpan.FromSeconds(10);
        Assert.Equal(0.25, FfmpegArguments.ParseProgress("out_time_us=2500000", duration));
        Assert.Equal(0.5, FfmpegArguments.ParseProgress("out_time_ms=5000000", duration));
        Assert.Equal(1, FfmpegArguments.ParseProgress("progress=end", duration));
        Assert.Null(FfmpegArguments.ParseProgress("frame=12", duration));
        Assert.Null(FfmpegArguments.ParseProgress("out_time_us=N/A", duration));
    }

    [Fact]
    public void Evenly_spaced_frames_use_one_run()
    {
        var even = Enumerable.Range(0, 10).Select(i => TimeSpan.FromMilliseconds(500 + i * 100)).ToList();
        Assert.Equal(TimeSpan.FromMilliseconds(100), FfmpegArguments.RegularInterval(even));
        var uneven = new[] { TimeSpan.Zero, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(3) };
        Assert.Null(FfmpegArguments.RegularInterval(uneven));
        Assert.Null(FfmpegArguments.RegularInterval(new[] { TimeSpan.Zero }));

        var args = FfmpegArguments.RawFrames("/v.mp4", TimeSpan.FromSeconds(0.5), TimeSpan.FromMilliseconds(100), 10, 320, 180);
        Assert.Contains("fps=fps=10:round=near,scale=320:180:flags=bicubic", args);
        Assert.Equal("10", args[args.IndexOf("-frames:v") + 1]);
        Assert.Equal("bgra", args[args.IndexOf("-pix_fmt") + 1]);

        var thumb = FfmpegArguments.Thumbnail("/v.mp4", TimeSpan.FromSeconds(1), 256);
        Assert.Contains("png", thumb);
        Assert.Contains(thumb, a => a.Contains("min(256,iw)"));
    }

    [Fact]
    public void Tesseract_languages_are_mapped()
    {
        const string output = "List of available languages in \"/usr/share/tesseract-ocr/5/tessdata/\" (5):\neng\nfra\nosd\nchi_sim\nxyz\n";
        var languages = TesseractOcr.ParseLanguages(output);
        Assert.Equal(4, languages.Count);
        Assert.DoesNotContain(languages, l => l.Code == "osd");
        Assert.Contains(languages, l => l.Code == "fra" && l.Language == new OcrLanguage("fr-FR", "Français"));
        Assert.Contains(languages, l => l.Code == "xyz" && l.Language.Tag == "xyz");

        Assert.Equal("fra", TesseractOcr.ChooseDefault(languages, "fr")!.Value.Code);
        Assert.Equal("eng", TesseractOcr.ChooseDefault(languages, "de")!.Value.Code);
        Assert.Null(TesseractOcr.ChooseDefault(Array.Empty<(string, OcrLanguage)>(), "fr"));
        Assert.Equal("Bonjour\nle monde", TesseractOcr.CleanText("Bonjour  \r\n\nle monde\n\f"));
    }

    [Fact]
    public void Webcams_are_listed_from_sysfs()
    {
        var root = Directory.CreateTempSubdirectory("snaplume-v4l-");
        try
        {
            void Node(string name, string label, string? index)
            {
                var folder = Directory.CreateDirectory(Path.Combine(root.FullName, name)).FullName;
                File.WriteAllText(Path.Combine(folder, "name"), label + "\n");
                if (index != null) File.WriteAllText(Path.Combine(folder, "index"), index + "\n");
            }
            Node("video0", "Integrated Camera", "0");
            Node("video1", "Integrated Camera", "1"); // metadata node
            Node("video10", "USB Camera", null);
            Node("vbi0", "Teletext", null);
            var cameras = Webcams.List(root.FullName, "/dev");
            Assert.Equal(new[] { "/dev/video0", "/dev/video10" }, cameras.Select(c => c.Id));
            Assert.Equal("Integrated Camera", cameras[0].Name);
            Assert.Equal("/dev/video10", Webcams.Resolve(cameras, "/dev/video10")!.Id);
            Assert.Equal("/dev/video0", Webcams.Resolve(cameras, "")!.Id);
            Assert.Null(Webcams.Resolve(cameras, "/dev/video5"));
            Assert.Empty(Webcams.List(Path.Combine(root.FullName, "missing"), "/dev"));
        }
        finally
        {
            root.Delete(true);
        }
    }
}
