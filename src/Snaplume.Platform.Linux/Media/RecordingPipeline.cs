using System.Globalization;
using System.Text;
using Snaplume.Capturing;

namespace Snaplume.Platform.Linux.Media;

/// <summary>Where the pictures of a recording come from.</summary>
internal abstract record RecordingSource;

/// <summary>An area of the X11 screen (ximagesrc).</summary>
internal sealed record X11AreaSource(PixelRect Area, bool ShowPointer) : RecordingSource;

/// <summary>A ScreenCast portal stream (pipewiresrc), with the size the portal announced (logical pixels).</summary>
internal sealed record PipeWireSource(int FileDescriptor, uint Node, int Width, int Height) : RecordingSource;

/// <summary>Where the webcam picture goes, in pixels of the recording.</summary>
internal readonly record struct WebcamPlacement(string Device, int X, int Y, int Width, int Height);

/// <summary>
/// The gst-launch description of a recording. Only numbers, element names and Snaplume's own constants go into
/// the text; the output file name is set afterwards as a property of the "out" element, never parsed.
/// </summary>
internal static class RecordingPipeline
{
    public static readonly string[] VideoEncoders = { "x264enc", "openh264enc" };
    /// <summary>
    /// HEVC encoders by preference: x265 (software, predictable), then VA-API (new "va" plugin, then gstreamer-vaapi),
    /// which may be present without a working driver: a failed start falls back to H.264.
    /// </summary>
    public static readonly string[] HevcEncoders = { "x265enc", "vah265enc", "vaapih265enc" };
    public static readonly string[] AudioEncoders = { "avenc_aac", "fdkaacenc", "voaacenc", "opusenc" };

    public static string? Choose(IEnumerable<string> candidates, Func<string, bool> available) => candidates.FirstOrDefault(available);

    /// <summary>Encoders work on even sizes (4:2:0 chroma): an odd width or height loses its last pixel.</summary>
    public static PixelRect EvenArea(PixelRect area) =>
        area with { Width = Math.Max(2, area.Width & ~1), Height = Math.Max(2, area.Height & ~1) };

    /// <summary>Name of the videocrop element that zooms on clicks (its margins change while recording).</summary>
    public const string ZoomElement = "zoom";
    /// <summary>Name of the microphone source, whose device is set after parsing (the name comes from the sound server).</summary>
    public const string MicrophoneElement = "mic";
    /// <summary>Name of the mp4mux element, whose faststart-file is set after parsing.</summary>
    public const string MuxElement = "mux";

    /// <summary>
    /// Where mp4mux spools the samples until it writes the index first (faststart): beside the output file, never
    /// in the shared temporary folder (often a small tmpfs in RAM, where the spool is created readable by everyone).
    /// </summary>
    public static string FastStartFile(string outputPath)
    {
        var full = Path.GetFullPath(outputPath);
        return Path.Combine(Path.GetDirectoryName(full)!, "." + Path.GetFileName(full) + ".faststart");
    }

    /// <param name="outputWidth">Width of the recording, to place the webcam (0 when unknown).</param>
    /// <param name="clickZoom">
    /// X11 only: the screen goes through a videocrop and is scaled back to the size of the area, so cropping it
    /// while recording zooms in (the webcam, composited afterwards, is not zoomed).
    /// </param>
    /// <param name="videoEncoder">One of <see cref="VideoEncoders"/> (H.264) or <see cref="HevcEncoders"/>.</param>
    /// <param name="encoding">
    /// Quality and largest picture (Settings › Video); null for <see cref="RecordingEncoding.Default"/>. The codec is
    /// the one of <paramref name="videoEncoder"/>.
    /// </param>
    public static string Build(RecordingSource source, int framerate, string videoEncoder, string? audioEncoder,
        bool systemAudio, bool microphone, WebcamPlacement? webcam, int outputWidth, int outputHeight, bool clickZoom = false,
        RecordingEncoding? encoding = null)
    {
        encoding ??= RecordingEncoding.Default;
        var f = CultureInfo.InvariantCulture;
        framerate = Math.Clamp(framerate, 1, 120);
        var d = new StringBuilder();
        d.Append(f, $"mp4mux name={MuxElement} faststart=true ! filesink name=out ");

        string screen = source switch
        {
            X11AreaSource x => string.Create(f, $"ximagesrc use-damage=false show-pointer={(x.ShowPointer ? "true" : "false")} startx={x.Area.X} starty={x.Area.Y} endx={x.Area.Right - 1} endy={x.Area.Bottom - 1}"),
            PipeWireSource p => string.Create(f, $"pipewiresrc fd={p.FileDescriptor} path={p.Node} do-timestamp=true keepalive-time=1000"),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        // Screen frames arrive irregularly (only on change for PipeWire): videorate makes a constant frame rate.
        string screenChain = string.Create(f, $"{screen} ! videoconvert ! videorate ! video/x-raw,framerate={framerate}/1");
        if (clickZoom && source is X11AreaSource zoomed)
            // add-borders=false: a crop whose ratio is a pixel off is stretched, never framed with black.
            screenChain += string.Create(f, $" ! videocrop name={ZoomElement} ! videoscale add-borders=false ! video/x-raw,width={zoomed.Area.Width},height={zoomed.Area.Height},pixel-aspect-ratio=1/1");
        if (source is PipeWireSource { Width: > 0, Height: > 0 } pw && (webcam != null || pw.Width % 2 != 0 || pw.Height % 2 != 0))
            // The webcam position is computed on the size the portal announced, and the encoders need even sizes (a
            // window may have an odd one): the stream is scaled to it then. Otherwise it keeps its own size, which
            // is larger than the announced (logical) one on a HiDPI screen.
            screenChain += string.Create(f, $" ! videoscale ! video/x-raw,width={Math.Max(2, pw.Width & ~1)},height={Math.Max(2, pw.Height & ~1)}");
        // A quality preset may record a smaller picture than the area (same shape), scaled before the encoder.
        var (encodedWidth, encodedHeight) = outputWidth > 0 && outputHeight > 0 ? encoding.OutputSize(outputWidth, outputHeight) : (0, 0);
        string scale = encodedWidth > 0 && (encodedWidth != (outputWidth & ~1) || encodedHeight != (outputHeight & ~1))
            ? string.Create(f, $"videoscale add-borders=false ! video/x-raw,width={encodedWidth},height={encodedHeight},pixel-aspect-ratio=1/1 ! ")
            : "";
        if (scale.Length == 0) (encodedWidth, encodedHeight) = (outputWidth, outputHeight);
        // The VA-API encoders take NV12 from memory; the software ones I420.
        string toEncoder = $"{scale}videoconvert ! video/x-raw,format={(videoEncoder.StartsWith("va", StringComparison.Ordinal) ? "NV12" : "I420")}";
        string encoder = VideoEncoderChain(videoEncoder, encodedWidth, encodedHeight, framerate, encoding.Quality);

        if (webcam is { } cam)
        {
            d.Append(f, $"compositor name=comp background=black sink_0::zorder=0 sink_1::zorder=1 sink_1::xpos={cam.X} sink_1::ypos={cam.Y} sink_1::width={cam.Width} sink_1::height={cam.Height} ");
            d.Append(f, $"! {toEncoder} ! queue ! {encoder} ! queue ! mux. ");
            d.Append(f, $"{screenChain} ! queue ! comp.sink_0 ");
            d.Append(f, $"v4l2src device={cam.Device} ! decodebin ! videoconvert ! aspectratiocrop aspect-ratio=4/3 ! videoscale ! video/x-raw,width={cam.Width},height={cam.Height},pixel-aspect-ratio=1/1 ! videorate ! video/x-raw,framerate={framerate}/1 ! queue ! comp.sink_1 ");
        }
        else
        {
            d.Append(f, $"{screenChain} ! {toEncoder} ! queue ! {encoder} ! queue ! mux. ");
        }

        if (audioEncoder != null && (systemAudio || microphone))
        {
            string audioEncoderChain = AudioEncoderChain(audioEncoder);
            const string normalize = "audioconvert ! audioresample ! audio/x-raw,rate=48000,channels=2";
            // provide-clock=false: the pipeline keeps the system clock, which the screen source follows too.
            const string system = "pulsesrc device=@DEFAULT_MONITOR@ provide-clock=false do-timestamp=true";
            const string mic = "pulsesrc provide-clock=false do-timestamp=true name=" + MicrophoneElement;
            if (systemAudio && microphone)
            {
                d.Append(f, $"audiomixer name=mix ! audioconvert ! {audioEncoderChain} ! queue ! mux. ");
                d.Append(f, $"{system} ! {normalize} ! queue ! mix. ");
                d.Append(f, $"{mic} ! {normalize} ! queue ! mix. ");
            }
            else
            {
                d.Append(f, $"{(systemAudio ? system : mic)} ! {normalize} ! {audioEncoderChain} ! queue ! mux. ");
            }
        }
        return d.ToString().TrimEnd();
    }

    /// <param name="quality">1 to 100 (<see cref="RecordingEncoding.Quality"/>), 70 being the usual quality.</param>
    private static string VideoEncoderChain(string encoder, int width, int height, int framerate, int quality)
    {
        var f = CultureInfo.InvariantCulture;
        int gop = framerate * 2;
        // HEVC in MP4 tagged "hvc1" (parameter sets in the header): Apple devices and Safari play nothing else.
        const string hevcOut = "h265parse ! video/x-h265,stream-format=hvc1,alignment=au";
        int hevcQp = Quantizer(quality) + 3;
        return encoder switch
        {
            // Constant quality (CRF 21 at the usual quality), no B-frames: low latency and readable everywhere.
            "x264enc" => string.Create(f, $"x264enc speed-preset=veryfast tune=zerolatency pass=qual quantizer={Quantizer(quality)} key-int-max={gop} ! h264parse"),
            "openh264enc" => string.Create(f, $"openh264enc complexity=low rate-control=bitrate bitrate={Bitrate(width, height, framerate, quality)} gop-size={gop} ! h264parse"),
            // Constant quantizer: about the look of H.264 three steps lower, in a smaller file.
            "x265enc" => string.Create(f, $"x265enc speed-preset=superfast tune=zerolatency qp={hevcQp} key-int-max={gop} ! {hevcOut}"),
            "vah265enc" => string.Create(f, $"vah265enc rate-control=cqp qpi={hevcQp} qpp={hevcQp} qpb={hevcQp} b-frames=0 key-int-max={gop} ! {hevcOut}"),
            "vaapih265enc" => string.Create(f, $"vaapih265enc rate-control=cqp init-qp={hevcQp} keyframe-period={gop} max-bframes=0 ! {hevcOut}"),
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
        };
    }

    /// <summary>H.264 quantizer of a quality (1 to 100): 21 at the usual 70, one step per 4 points, within 12 to 40.</summary>
    internal static int Quantizer(int quality) =>
        Math.Clamp((int)Math.Round(21 + (RecordingEncoding.DefaultQuality - Math.Clamp(quality, 1, 100)) / 4.0, MidpointRounding.AwayFromZero), 12, 40);

    /// <summary>
    /// About 0.12 bit per pixel and frame at the usual quality (in proportion for the others), within 2 to 40 Mbit/s
    /// (openh264 has no constant-quality mode).
    /// </summary>
    internal static long Bitrate(int width, int height, int framerate, int quality = RecordingEncoding.DefaultQuality)
    {
        if (width <= 0 || height <= 0) (width, height) = (1920, 1080);
        double bitsPerPixel = 0.12 * Math.Clamp(quality, 1, 100) / RecordingEncoding.DefaultQuality;
        return Math.Clamp((long)(width * (double)height * framerate * bitsPerPixel), 2_000_000, 40_000_000);
    }

    private static string AudioEncoderChain(string encoder) => encoder switch
    {
        "avenc_aac" => "avenc_aac bitrate=160000 ! aacparse",
        "fdkaacenc" => "fdkaacenc bitrate=160000 ! aacparse",
        "voaacenc" => "voaacenc bitrate=160000 ! aacparse",
        "opusenc" => "opusenc bitrate=128000 ! opusparse",
        _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
    };

    /// <summary>
    /// The webcam picture in a corner: <paramref name="sizePercent"/> of the recording's width, 4:3, a margin of
    /// 2 % of the width; even numbers for the encoder.
    /// </summary>
    public static WebcamPlacement PlaceWebcam(string device, string corner, int sizePercent, int width, int height)
    {
        int w = Math.Max(32, width * Math.Clamp(sizePercent, 5, 60) / 100) & ~1;
        int h = Math.Max(24, w * 3 / 4) & ~1;
        if (h > height / 2) (h, w) = ((height / 2) & ~1, ((height / 2) * 4 / 3) & ~1);
        int margin = Math.Max(4, width / 50);
        bool right = corner is "TopRight" or "BottomRight";
        bool bottom = corner is "BottomLeft" or "BottomRight";
        int x = right ? width - w - margin : margin;
        int y = bottom ? height - h - margin : margin;
        return new WebcamPlacement(device, Math.Max(0, x), Math.Max(0, y), w, h);
    }
}
