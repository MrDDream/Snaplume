using System.Globalization;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;
using Snaplume.Recording;

namespace Snaplume.Player;

/// <summary>
/// Animated GIF made from part of a video: frames read by the platform (<see cref="IVideoTools.ReadFramesAsync"/>),
/// encoded by <see cref="GifEncoder"/>. Nothing leaves the computer; the source file is only read.
/// </summary>
public static class VideoGifExport
{
    /// <summary>The last picture of a video is a little before its end.</summary>
    private static readonly TimeSpan EndMargin = TimeSpan.FromMilliseconds(20);

    /// <summary>
    /// Writes the GIF to <paramref name="destination"/> (a new file: never overwrites, deleted again if the export
    /// fails or is cancelled). Runs on the thread pool.
    /// </summary>
    /// <param name="duration">Length of the video (frame times are kept inside it); zero if unknown.</param>
    /// <param name="progress">Receives 0 to 1.</param>
    /// <exception cref="OperationCanceledException">Cancelled through <paramref name="cancel"/>.</exception>
    public static Task ExportAsync(IVideoTools tools, string source, string destination, GifExportSettings settings, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancel) =>
        Task.Run(() => WriteAsync(tools, source, destination, settings, duration, progress, cancel), cancel);

    /// <summary>Times of the GIF's pictures, kept inside the video.</summary>
    public static IReadOnlyList<TimeSpan> FrameTimes(GifExportSettings settings, TimeSpan duration)
    {
        var last = duration > EndMargin ? duration - EndMargin : TimeSpan.MaxValue;
        return settings.FrameTimes().Select(t => t > last ? last : t < TimeSpan.Zero ? TimeSpan.Zero : t).ToList();
    }

    private static async Task WriteAsync(IVideoTools tools, string source, string destination, GifExportSettings settings, TimeSpan duration,
        IProgress<double>? progress, CancellationToken cancel)
    {
        CheckDestination(source, destination);
        var times = FrameTimes(settings, duration);
        var delays = GifEncoder.FrameDelays(times.Count, settings.FramesPerSecond);
        int width = settings.Width, height = settings.Height;

        var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
        try
        {
            using (var encoder = new GifEncoder(output, width, height, new GifEncoderOptions { LoopForever = settings.Loop, Dither = settings.Dither }))
            {
                int index = 0;
                await foreach (var frame in tools.ReadFramesAsync(source, times, width, height, cancel).WithCancellation(cancel))
                {
                    cancel.ThrowIfCancellationRequested();
                    if (index >= times.Count) break;
                    // The platform should scale; a frame of another size is scaled here rather than refused.
                    var picture = frame.Width == width && frame.Height == height ? frame : ImageIO.Resize(frame, width, height);
                    encoder.AddFrame(picture.Pixels.AsSpan(0, picture.Stride * height), picture.Stride, delays[index]);
                    index++;
                    progress?.Report((double)index / times.Count);
                }
                if (index != times.Count) throw new InvalidOperationException($"Missing video frames ({index} of {times.Count}).");
                encoder.Finish();
            }
            output.Dispose();
        }
        catch
        {
            output.Dispose();
            await DeleteQuietlyAsync(destination);
            throw;
        }
    }

    /// <summary>The export never writes over the video, nor over another file.</summary>
    public static void CheckDestination(string source, string destination)
    {
        if (string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("The destination is the source file.", nameof(destination));
        if (File.Exists(destination)) throw new IOException($"{destination} already exists.");
    }

    /// <summary>
    /// Removes an unfinished output (another process may still hold it for a moment); the last failure is logged
    /// when it is still there after a second.
    /// </summary>
    public static async Task DeleteQuietlyAsync(string path)
    {
        const int attempts = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
                return;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (attempt == attempts)
                {
                    ErrorLog.Write("Suppression d'un fichier inachevé", ex);
                    return;
                }
                await Task.Delay(200);
            }
        }
    }
}

/// <summary>
/// The fields of the GIF export dialog, checked and turned into <see cref="GifExportSettings"/>: kept out of the
/// window so that the rules (limits, height from the width, selection or whole video) can be tested.
/// </summary>
public sealed class GifExportForm
{
    /// <summary>Narrowest GIF accepted.</summary>
    public const int SmallestWidth = 16;
    /// <summary>Width proposed first (at most the video's).</summary>
    public const int DefaultWidth = 800;

    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    public GifExportForm(TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, int videoWidth, int videoHeight)
    {
        Duration = duration;
        HasSelection = inPoint != null || outPoint != null;
        In = inPoint ?? TimeSpan.Zero;
        Out = outPoint ?? duration;
        VideoWidth = Math.Max(1, videoWidth);
        VideoHeight = Math.Max(1, videoHeight);
        WidthText = Math.Min(DefaultWidth, VideoWidth).ToString(Loc.Culture);
        UseSelection = HasSelection;
    }

    public TimeSpan Duration { get; }
    public bool HasSelection { get; }
    public TimeSpan In { get; }
    public TimeSpan Out { get; }
    public int VideoWidth { get; }
    public int VideoHeight { get; }

    public bool UseSelection { get; set; }
    public string FramesPerSecondText { get; set; } = "12";
    public string WidthText { get; set; }
    public double Speed { get; set; } = 1;
    public bool Loop { get; set; } = true;
    public bool Dither { get; set; }

    /// <summary>
    /// Widest GIF accepted: the video's width, and a GIF at most <see cref="GifEncoder.MaxSide"/> pixels on each side
    /// (the height follows the width).
    /// </summary>
    public int LargestWidth =>
        (int)Math.Min(Math.Min(VideoWidth, GifEncoder.MaxSide), Math.Floor((GifEncoder.MaxSide + 0.5) * VideoWidth / VideoHeight));

    /// <summary>Settings typed, or the reason (ready to show) why they are not acceptable.</summary>
    public bool TryRead(out GifExportSettings settings, out string error)
    {
        settings = null!;
        error = "";
        if (!int.TryParse(FramesPerSecondText.Trim(), NumberStyles.Integer, Loc.Culture, out var fps) ||
            fps < GifExportSettings.MinFramesPerSecond || fps > GifExportSettings.MaxFramesPerSecond)
        {
            error = Loc.T("Player.Gif.FpsError", GifExportSettings.MinFramesPerSecond, GifExportSettings.MaxFramesPerSecond);
            return false;
        }
        int widest = LargestWidth;
        if (!int.TryParse(WidthText.Trim(), NumberStyles.Integer, Loc.Culture, out var width) || width < SmallestWidth || width > widest)
        {
            error = Loc.T("Player.Gif.WidthError", SmallestWidth, widest);
            return false;
        }
        int height = Math.Max(1, (int)Math.Round((double)width * VideoHeight / VideoWidth));
        if (height > GifEncoder.MaxSide)
        {
            // A very tall video (only reached when it is taller than a GIF can be).
            error = Loc.T("Player.Gif.WidthError", SmallestWidth, widest);
            return false;
        }
        bool selection = UseSelection && HasSelection;
        var start = selection ? In : TimeSpan.Zero;
        var end = selection ? Out : Duration;
        settings = new GifExportSettings(start, end, fps, width, height, double.IsFinite(Speed) && Speed > 0 ? Speed : 1, Loop, Dither);
        if (settings.FrameCount > GifExportSettings.MaxFrames)
        {
            error = Loc.T("Player.Gif.TooLong", GifExportSettings.MaxFrames);
            return false;
        }
        return true;
    }
}
