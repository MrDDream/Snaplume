namespace Snaplume.Player;

/// <summary>What the GIF export makes of a video.</summary>
/// <param name="Start">Beginning of the part of the video turned into a GIF.</param>
/// <param name="End">End of that part (clamped to the length of the video).</param>
/// <param name="FramesPerSecond">Pictures per second of the GIF (5–30).</param>
/// <param name="Width">Size of the GIF, in pixels.</param>
/// <param name="Speed">Playback speed: 2 plays the video twice as fast (half the duration).</param>
/// <param name="Loop">The GIF plays again and again.</param>
/// <param name="Dither">Floyd–Steinberg dithering (smoother gradients, bigger file).</param>
public sealed record GifExportSettings(TimeSpan Start, TimeSpan End, int FramesPerSecond, int Width, int Height,
    double Speed, bool Loop, bool Dither)
{
    public const int MinFramesPerSecond = 5;
    public const int MaxFramesPerSecond = 30;
    /// <summary>Longest GIF accepted (memory and time stay reasonable).</summary>
    public const int MaxFrames = 3000;

    /// <summary>Times (from the start of the video) of the pictures of the GIF.</summary>
    public IReadOnlyList<TimeSpan> FrameTimes()
    {
        double step = Speed / FramesPerSecond; // seconds of video between two pictures
        var times = new TimeSpan[Math.Min(FrameCount, MaxFrames)];
        for (int i = 0; i < times.Length; i++) times[i] = Start + TimeSpan.FromSeconds(i * step);
        return times;
    }

    /// <summary>Number of pictures, without building the list.</summary>
    public int FrameCount => Math.Max(1, (int)Math.Ceiling(Math.Max(0, (End - Start).TotalSeconds) / (Speed / FramesPerSecond) - 1e-6));
}
