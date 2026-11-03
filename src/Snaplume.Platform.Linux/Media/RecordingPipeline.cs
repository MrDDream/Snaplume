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
    public static readonly string[] AudioEncoders = { "avenc_aac", "fdkaacenc", "voaacenc", "opusenc" };

    public static string? Choose(IEnumerable<string> candidates, Func<string, bool> available) => candidates.FirstOrDefault(available);

    /// <summary>Encoders work on even sizes (4:2:0 chroma): an odd width or height loses its last pixel.</summary>
    public static PixelRect EvenArea(PixelRect area) =>
        area with { Width = Math.Max(2, area.Width & ~1), Height = Math.Max(2, area.Height & ~1) };

    /// <summary>Name of the videocrop element that zooms on clicks (its margins change while recording).</summary>
    public const string ZoomElement = "zoom";
    /// <summary>Name of the microphone source, whose device is set after parsing (the name comes from the sound server).</summary>
    public const string MicrophoneElement = "mic";

    /// <param name="outputWidth">Width of the recording, to place the webcam (0 when unknown).</param>
    /// <param name="clickZoom">
    /// X11 only: the screen goes through a videocrop and is scaled back to the size of the area, so cropping it
    /// while recording zooms in (the webcam, composited afterwards, is not zoomed).
    /// </param>
    public static string Build(RecordingSource source, int framerate, string videoEncoder, string? audioEncoder,
        bool systemAudio, bool microphone, WebcamPlacement? webcam, int outputWidth, int outputHeight, bool clickZoom = false)
    {
        var f = CultureInfo.InvariantCulture;
        framerate = Math.Clamp(framerate, 1, 120);
        var d = new StringBuilder();
        d.Append(f, $"mp4mux name=mux faststart=true ! filesink name=out ");

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
        if (source is PipeWireSource { Width: > 0, Height: > 0 } pw && webcam != null)
            // The webcam position is computed on the size the portal announced: the stream is scaled to it.
            screenChain += string.Create(f, $" ! videoscale ! video/x-raw,width={pw.Width & ~1},height={pw.Height & ~1}");
        string encoder = VideoEncoderChain(videoEncoder, outputWidth, outputHeight, framerate);

        if (webcam is { } cam)
        {
            d.Append(f, $"compositor name=comp background=black sink_0::zorder=0 sink_1::zorder=1 sink_1::xpos={cam.X} sink_1::ypos={cam.Y} sink_1::width={cam.Width} sink_1::height={cam.Height} ");
            d.Append(f, $"! videoconvert ! video/x-raw,format=I420 ! queue ! {encoder} ! queue ! mux. ");
            d.Append(f, $"{screenChain} ! queue ! comp.sink_0 ");
            d.Append(f, $"v4l2src device={cam.Device} ! decodebin ! videoconvert ! aspectratiocrop aspect-ratio=4/3 ! videoscale ! video/x-raw,width={cam.Width},height={cam.Height},pixel-aspect-ratio=1/1 ! videorate ! video/x-raw,framerate={framerate}/1 ! queue ! comp.sink_1 ");
        }
        else
        {
            d.Append(f, $"{screenChain} ! videoconvert ! video/x-raw,format=I420 ! queue ! {encoder} ! queue ! mux. ");
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

    private static string VideoEncoderChain(string encoder, int width, int height, int framerate)
    {
        var f = CultureInfo.InvariantCulture;
        return encoder switch
        {
            // Constant quality (CRF 21), no B-frames: low latency and readable everywhere.
            "x264enc" => string.Create(f, $"x264enc speed-preset=veryfast tune=zerolatency pass=qual quantizer=21 key-int-max={framerate * 2} ! h264parse"),
            "openh264enc" => string.Create(f, $"openh264enc complexity=low rate-control=bitrate bitrate={Bitrate(width, height, framerate)} gop-size={framerate * 2} ! h264parse"),
            _ => throw new ArgumentOutOfRangeException(nameof(encoder), encoder, null),
        };
    }

    /// <summary>About 0.12 bit per pixel and frame, within 2 to 40 Mbit/s (openh264 has no constant-quality mode).</summary>
    internal static long Bitrate(int width, int height, int framerate)
    {
        if (width <= 0 || height <= 0) (width, height) = (1920, 1080);
        return Math.Clamp((long)(width * (double)height * framerate * 0.12), 2_000_000, 40_000_000);
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
