using System.Diagnostics;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Capturing;

/// <param name="Image">The stitched image (at least the first frame), opaque BGRA.</param>
/// <param name="NothingScrolled">
/// The area never scrolled although the user did not stop it: the window ignores the wheel, or runs
/// with higher rights (as administrator under Windows), which silently drops Snaplume's synthetic wheel.
/// </param>
public sealed record ScrollCaptureResult(PixelBuffer Image, bool NothingScrolled);

/// <summary>The small "Stop" bar shown during a scrolling capture (a window of the application, or a fake in the tests).</summary>
public interface IScrollCaptureBar
{
    /// <summary>Where the bar is on the screen (physical pixels), once shown: the mouse may cross it to reach "Stop".</summary>
    PixelRect? ScreenBounds { get; }

    /// <summary>Height of the image stitched so far.</summary>
    void SetProgress(int height);

    /// <summary>Scrolling waits while the mouse is away from its place (e.g. on its way to the Stop button).</summary>
    void SetPaused(bool paused);

    void Close();
}

/// <summary>
/// Delays of the scrolling capture. The defaults are the ones tuned in Snaplume 1.1.1 on real browsers; the tests
/// shorten them so that a whole simulated page scrolls in a fraction of a second.
/// </summary>
public sealed record ScrollCaptureTiming
{
    public static readonly ScrollCaptureTiming Default = new();

    /// <summary>Lets the selection overlay disappear, and the hover effects under the cursor appear, before the first frame.</summary>
    public int StartDelayMs { get; init; } = 250;
    /// <summary>Lets the application start scrolling after a wheel step, before the screen is watched.</summary>
    public int StepDelayMs { get; init; } = 120;
    /// <summary>
    /// The screen is grabbed at this interval until two grabs in a row are the same (see <see cref="ScrollingCapture.IsSettled"/>):
    /// browsers animate the scrolling (smooth scrolling) and paint the parts that come into view afterwards.
    /// </summary>
    public int SettlePollMs { get; init; } = 60;
    /// <summary>Longest wait for the screen to settle (an animation or a video on the page never does).</summary>
    public int SettleTimeoutMs { get; init; } = 1500;
    /// <summary>Second look when nothing moved: a slow application may still be scrolling.</summary>
    public int RetryDelayMs { get; init; } = 450;
    /// <summary>Interval at which the stop conditions (Esc, mouse, Stop button) are checked while waiting.</summary>
    public int PollMs { get; init; } = 25;
    /// <summary>Longest capture: a page that never ends (infinite scrolling) is cut there.</summary>
    public TimeSpan MaxDuration { get; init; } = TimeSpan.FromMinutes(3);
}

/// <summary>
/// Scrolling capture: turns the mouse wheel over an area of the screen, captures it after each step
/// and stitches the new rows (<see cref="ScrollStitcher"/>) into one tall image.
/// </summary>
/// <remarks>
/// Stops at the end of the content (a frame brings nothing new), when the frames cannot be matched,
/// at the maximum height, or when the user presses Esc, clicks "Stop" or moves the mouse out of the area.
/// Moving the mouse inside the area (or over the bar, to reach "Stop") only pauses the scrolling: the
/// wheel always turns under the cursor, so it must be back at its place.
/// Esc is only watched, not intercepted: the scrolled application receives it too (it may close a menu
/// or stop loading a page).
/// The mouse, the wheel and the screen go through the platform services (<see cref="IPointerControl"/>,
/// <see cref="IScreenGrabber"/>), so the whole logic runs the same under Windows, X11 and Wayland, and in the tests.
/// </remarks>
public sealed class ScrollingCapture
{
    /// <summary>Most steps of one capture (with <see cref="MaxNotches"/>, far more than the maximum height).</summary>
    private const int MaxSteps = 400;
    private const int MaxNotches = 10;
    /// <summary>Times a step that scrolled too far (frames no longer overlapping) is undone before giving up.</summary>
    private const int MaxRecoveries = 3;
    /// <summary>Aim for each step to scroll about this share of the area, so consecutive frames overlap a lot.</summary>
    private const double StepShare = 0.45;
    /// <summary>The mouse further than this from its place pauses the scrolling.</summary>
    private const int MouseTolerance = 16;
    /// <summary>Share of the rows that may still change in a settled screen (blinking caret, small animation).</summary>
    private const double SettledRowShare = 0.02;

    private enum MousePosition
    {
        /// <summary>Where the capture put it: the wheel scrolls the area.</summary>
        AtPoint,
        /// <summary>Moved, but still over the area or the bar: the scrolling waits.</summary>
        Away,
        /// <summary>Taken out of the area: the user wants the mouse back, the capture stops.</summary>
        Outside,
    }

    private readonly IPointerControl _pointer;
    private readonly Func<PixelRect, IScreenGrabber> _createGrabber;
    private readonly IReadOnlyList<PixelRect> _monitors;

    /// <param name="createGrabber">Grabs the area again and again (usually <see cref="IScreenCapture.CreateGrabber"/>).</param>
    /// <param name="monitors">Monitor bounds, absolute physical coordinates (to keep the wheel point on a screen).</param>
    public ScrollingCapture(IPointerControl pointer, Func<PixelRect, IScreenGrabber> createGrabber, IReadOnlyList<PixelRect> monitors)
    {
        _pointer = pointer;
        _createGrabber = createGrabber;
        _monitors = monitors;
    }

    public ScrollCaptureTiming Timing { get; init; } = ScrollCaptureTiming.Default;

    /// <summary>Shows the Stop bar next to the area (called once the first frame is taken); null: no bar.</summary>
    /// <remarks>The bar reports a click on Stop through the action it receives.</remarks>
    public Func<PixelRect, Action, IScrollCaptureBar?>? CreateBar { get; init; }

    /// <summary>Other reasons to stop, checked with Esc (Snaplume quitting, another selection opening...).</summary>
    public Func<bool>? ExternalStop { get; init; }

    /// <summary>Scrolls and captures the area.</summary>
    /// <param name="area">Absolute physical screen coordinates.</param>
    /// <param name="cancel">Stops the scrolling (e.g. Snaplume quits); the image captured so far is still returned.</param>
    /// <returns>Null if the system refused the control of the pointer (Wayland portal declined).</returns>
    /// <exception cref="Exception">Only if not even the first frame could be captured.</exception>
    public async Task<ScrollCaptureResult?> RunAsync(PixelRect area, CancellationToken cancel = default)
    {
        if (area.IsEmpty) throw new ArgumentException("Empty area", nameof(area));
        if (!await _pointer.PrepareAsync(cancel)) return null;
        try
        {
            return await CaptureAsync(area, cancel);
        }
        finally
        {
            _pointer.Release();
        }
    }

    private async Task<ScrollCaptureResult> CaptureAsync(PixelRect area, CancellationToken cancel)
    {
        int maxHeight = (int)Math.Clamp(ImageIO.MaxPixels / area.Width, area.Height, ScrollStitcher.DefaultMaxHeight);
        // The image must also fit in one array (PixelBuffer).
        maxHeight = (int)Math.Clamp(Math.Min(maxHeight, (long)int.MaxValue / 4 / area.Width), area.Height, maxHeight);
        var stitcher = new ScrollStitcher(area.Width, area.Height, maxHeight);
        bool stopped;
        using (var grabber = _createGrabber(area))
        {
            stopped = await ScrollAndStitchAsync(area, stitcher, grabber, cancel);
        }
        bool nothingScrolled = stitcher.FramesUsed <= 1 && !stopped;
        // Tens of megapixels: made into the image off the UI thread, without a second copy.
        var image = await Task.Run(() => ToImage(stitcher));
        return new ScrollCaptureResult(image, nothingScrolled);
    }

    /// <returns>True if the user (or the caller) stopped the capture.</returns>
    private async Task<bool> ScrollAndStitchAsync(PixelRect area, ScrollStitcher stitcher, IScreenGrabber grabber, CancellationToken cancel)
    {
        // Three frame buffers, reused: the stitcher keeps the last frame it used (held), the two others take
        // the grabs compared while the screen settles.
        int stride = area.Width * 4;
        var buffers = new[] { new byte[stride * area.Height], new byte[stride * area.Height], new byte[stride * area.Height] };
        byte[]? held = null;
        async Task<byte[]> GrabIntoAsync(byte[]? other)
        {
            var buffer = buffers.First(b => b != held && b != other);
            // Off the UI thread: a grab can take tens of milliseconds (a portal round trip under Wayland).
            await Task.Run(() => grabber.GrabAsync(buffer, CancellationToken.None));
            return buffer;
        }
        // The area once it stopped moving, or as it is after SettleTimeoutMs; null if a stop was requested.
        async Task<byte[]?> GrabSettledAsync(Func<bool> stopRequested)
        {
            var frame = await GrabIntoAsync(null);
            var watch = Stopwatch.StartNew();
            while (watch.ElapsedMilliseconds < Timing.SettleTimeoutMs)
            {
                if (!await WaitAsync(Timing.SettlePollMs, stopRequested)) return null;
                var before = frame;
                frame = await GrabIntoAsync(before);
                if (IsSettled(before, frame, stride, area.Height)) break;
            }
            return frame;
        }
        StitchStep Add(byte[] frame, int expected = 0)
        {
            var step = stitcher.Add(frame, expected);
            if (step.NewRows > 0) held = frame;
            return step;
        }

        bool hadOriginal = _pointer.TryGetPosition(out int originalX, out int originalY);
        var point = PickPoint(area, _monitors);
        IScrollCaptureBar? bar = null;
        bool stopClicked = false, stopped = false;
        MousePosition Mouse() => Locate(point, area, bar?.ScreenBounds);
        try
        {
            _pointer.ActivateWindowAt(point.X, point.Y);
            _pointer.SetPosition(point.X, point.Y);
            // The reference is where the cursor really is (the system may have kept it elsewhere).
            if (_pointer.TryGetPosition(out int actualX, out int actualY)) point = (actualX, actualY);
            if (Timing.StartDelayMs > 0) await Task.Delay(Timing.StartDelayMs, CancellationToken.None);
            Add(await GrabSettledAsync(() => cancel.IsCancellationRequested) ?? await GrabIntoAsync(null));

            try
            {
                bar = CreateBar?.Invoke(area, () => stopClicked = true);
            }
            catch (Exception ex)
            {
                // Esc and the mouse still stop the capture.
                ErrorLog.Write("Barre de la capture défilante", ex);
            }

            // Forget an Esc pressed before (e.g. during the selection).
            _pointer.EscapePressed();
            bool StopRequested()
            {
                if (stopClicked || cancel.IsCancellationRequested || ExternalStop?.Invoke() == true
                    || _pointer.EscapePressed() || Mouse() == MousePosition.Outside)
                    stopped = true;
                return stopped;
            }

            try
            {
                await ScrollAsync(area, Add, GrabSettledAsync, () => stitcher.Height, bar, StopRequested, () => Mouse() == MousePosition.AtPoint);
            }
            catch (Exception ex)
            {
                // Keep what was captured so far.
                ErrorLog.Write("Capture défilante", ex);
            }
        }
        finally
        {
            try
            {
                bar?.Close();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Barre de la capture défilante", ex);
            }
            // Put the mouse back, unless the user took it over meanwhile.
            if (hadOriginal && Mouse() == MousePosition.AtPoint) _pointer.SetPosition(originalX, originalY);
        }
        return stopped;
    }

    /// <param name="add">Stitches a frame (with the likely shift); <paramref name="grabSettled"/> captures one once the screen settled.</param>
    /// <param name="height">Height of the image stitched so far.</param>
    private async Task ScrollAsync(PixelRect area, Func<byte[], int, StitchStep> add, Func<Func<bool>, Task<byte[]?>> grabSettled,
        Func<int> height, IScrollCaptureBar? bar, Func<bool> stopRequested, Func<bool> mouseAtPoint)
    {
        var watch = Stopwatch.StartNew();
        // First step: one notch, to measure how many pixels a notch scrolls in this window.
        int notches = 1, recoveries = 0;
        double pixelsPerNotch = 0;
        for (int step = 0; step < MaxSteps && watch.Elapsed < Timing.MaxDuration; step++)
        {
            if (stopRequested()) return;
            if (!await WaitAtPointAsync(mouseAtPoint, bar, stopRequested)) return;
            if (!_pointer.Scroll(-notches))
            {
                ErrorLog.Write("Capture défilante", "Le système a refusé la molette (entrée bloquée ?)");
                return;
            }
            if (!await WaitAsync(Timing.StepDelayMs, stopRequested)) return;

            int shiftHint = (int)Math.Round(pixelsPerNotch * notches);
            if (await grabSettled(stopRequested) is not { } frame) return;
            var result = await Task.Run(() => add(frame, shiftHint));
            if (result.Outcome == StitchOutcome.NoChange)
            {
                // Slow repaint or late start of the scrolling: look once more before concluding it is the end.
                if (!await WaitAsync(Timing.RetryDelayMs, stopRequested)) return;
                if (await grabSettled(stopRequested) is not { } again) return;
                result = await Task.Run(() => add(again, shiftHint));
            }
            if (result.Outcome == StitchOutcome.NoMatch && notches > 1 && recoveries < MaxRecoveries)
            {
                // Scrolled too far for the frames to overlap: scroll back up and go on with smaller steps.
                recoveries++;
                if (!await WaitAtPointAsync(mouseAtPoint, bar, stopRequested)) return;
                if (!_pointer.Scroll(notches))
                {
                    // Not scrolled back: the next frame would not overlap the image either.
                    ErrorLog.Write("Capture défilante", "Le système a refusé la molette pour revenir en arrière (entrée bloquée ?)");
                    return;
                }
                notches = Math.Max(1, notches / 3);
                if (!await WaitAsync(Timing.StepDelayMs, stopRequested)) return;
                if (await grabSettled(stopRequested) is null) return;
                continue;
            }
            if (result.NewRows > 0) bar?.SetProgress(height());
            if (result.Outcome != StitchOutcome.Appended) return;

            // Scroll about the same share of the area at each step, whatever the wheel speed of the application,
            // but at most twice as many notches as the last step: a measure taken while the page still moved must
            // not send the next step past the area.
            pixelsPerNotch = Math.Max(1, result.Shift / (double)notches);
            int target = Math.Clamp((int)(area.Height * StepShare / pixelsPerNotch), 1, MaxNotches);
            notches = Math.Min(target, notches * 2);
        }
    }

    /// <summary>
    /// True when two grabs a few milliseconds apart are the same, but for a few rows (blinking caret, small
    /// animation): the scrolling animation is over and the page is painted.
    /// </summary>
    public static bool IsSettled(byte[] a, byte[] b, int stride, int height)
    {
        int allowed = (int)(height * SettledRowShare), changed = 0;
        for (int y = 0; y < height; y++)
        {
            if (!a.AsSpan(y * stride, stride).SequenceEqual(b.AsSpan(y * stride, stride)) && ++changed > allowed) return false;
        }
        return true;
    }

    /// <summary>Waits while the mouse is away from its place (scrolling paused), checking the stop conditions.</summary>
    /// <returns>False if a stop was requested meanwhile.</returns>
    private async Task<bool> WaitAtPointAsync(Func<bool> mouseAtPoint, IScrollCaptureBar? bar, Func<bool> stopRequested)
    {
        while (!mouseAtPoint())
        {
            bar?.SetPaused(true);
            await Task.Delay(Math.Max(1, Timing.PollMs));
            if (stopRequested()) return false;
        }
        bar?.SetPaused(false);
        return true;
    }

    /// <summary>Waits, checking the stop conditions.</summary>
    /// <returns>False if a stop was requested meanwhile.</returns>
    private async Task<bool> WaitAsync(int milliseconds, Func<bool> stopRequested)
    {
        int poll = Math.Max(1, Timing.PollMs);
        for (int waited = 0; waited < milliseconds; waited += poll)
        {
            await Task.Delay(poll);
            if (stopRequested()) return false;
        }
        return true;
    }

    /// <summary>Where the mouse is, compared with its place, the area and the Stop bar.</summary>
    private MousePosition Locate((int X, int Y) point, PixelRect area, PixelRect? bar)
    {
        // Unknown position (some Wayland sessions): the wheel is assumed to turn at its place.
        if (!_pointer.TryGetPosition(out int x, out int y)) return MousePosition.AtPoint;
        return Locate(x, y, point, area, bar);
    }

    private static MousePosition Locate(int x, int y, (int X, int Y) point, PixelRect area, PixelRect? bar)
    {
        if (Math.Abs(x - point.X) <= MouseTolerance && Math.Abs(y - point.Y) <= MouseTolerance) return MousePosition.AtPoint;
        // The area and the bar, with the gap between them: the way to the Stop button.
        var zone = bar is PixelRect b ? area.Union(b) : area;
        zone = PixelRect.FromLTRB(zone.X - MouseTolerance, zone.Y - MouseTolerance, zone.Right + MouseTolerance, zone.Bottom + MouseTolerance);
        return zone.Contains(x, y) ? MousePosition.Away : MousePosition.Outside;
    }

    /// <summary>
    /// Point where the wheel is turned: centered, in the upper part of the area (hover effects under the
    /// cursor then stay out of the rows appended from the bottom of each frame; still below a window's
    /// title and tool bars). An area spanning monitors of different sizes may have that point in a dead
    /// zone between them: the part of the area on the monitor that shows most of it is used instead.
    /// </summary>
    public static (int X, int Y) PickPoint(PixelRect area, IReadOnlyList<PixelRect> monitors)
    {
        static (int X, int Y) UpperCenter(PixelRect r) => (r.X + r.Width / 2, r.Y + (int)(r.Height * 0.4));
        var preferred = UpperCenter(area);
        PixelRect? largest = null;
        foreach (var bounds in monitors)
        {
            if (bounds.Contains(preferred.X, preferred.Y)) return preferred;
            var part = bounds.Intersect(area);
            if (!part.IsEmpty && (largest is not PixelRect l || part.Area > l.Area)) largest = part;
        }
        return largest is PixelRect visible ? UpperCenter(visible) : preferred;
    }

    /// <summary>
    /// The stitched pixels, taken straight from the stitcher's buffer when it has the exact size. A larger buffer
    /// (it grows ahead) is copied once: code elsewhere (clipboard, encoders) may read <see cref="PixelBuffer.Pixels"/>
    /// to its end.
    /// </summary>
    private static PixelBuffer ToImage(ScrollStitcher stitcher)
    {
        ImageIO.CheckSize(stitcher.Width, stitcher.Height);
        var pixels = stitcher.Complete();
        var array = pixels.Array!;
        if (array.Length != pixels.Count) array = pixels.AsSpan().ToArray();
        return new PixelBuffer(stitcher.Width, stitcher.Height, array);
    }
}
