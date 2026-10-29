using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;
using static Snaplume.Native.NativeMethods;
using WinForms = System.Windows.Forms;

namespace Snaplume.Capturing;

/// <param name="Image">The stitched image (at least the first frame), 96 DPI Bgra32, frozen.</param>
/// <param name="NothingScrolled">
/// The area never scrolled although the user did not stop it: the window ignores the wheel, or runs
/// with higher rights (as administrator), which silently drops Snaplume's synthetic wheel.
/// </param>
public sealed record ScrollCaptureResult(BitmapSource Image, bool NothingScrolled);

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
/// </remarks>
public static class ScrollingCapture
{
    /// <summary>Lets the selection overlay disappear, and the hover effects under the cursor appear, before the first frame.</summary>
    private const int StartDelayMs = 250;
    /// <summary>Repaint time after a wheel step (smooth scrolling animations included).</summary>
    private const int StepDelayMs = 220;
    /// <summary>Second look when nothing moved: a slow application may still be scrolling.</summary>
    private const int RetryDelayMs = 450;
    private const int PollMs = 25;
    private const int MaxSteps = 400;
    private const int MaxNotches = 10;
    /// <summary>Aim for each step to scroll about this share of the area, so consecutive frames overlap a lot.</summary>
    private const double StepShare = 0.45;
    /// <summary>The mouse further than this from its place pauses the scrolling.</summary>
    private const int MouseTolerance = 16;
    private static readonly TimeSpan MaxDuration = TimeSpan.FromMinutes(3);

    private enum MousePosition
    {
        /// <summary>Where the capture put it: the wheel scrolls the area.</summary>
        AtPoint,
        /// <summary>Moved, but still over the area or the bar: the scrolling waits.</summary>
        Away,
        /// <summary>Taken out of the area: the user wants the mouse back, the capture stops.</summary>
        Outside,
    }

    /// <summary>Scrolls and captures the area.</summary>
    /// <param name="area">Absolute physical screen coordinates.</param>
    /// <param name="cancel">Stops the scrolling (e.g. Snaplume quits); the image captured so far is still returned.</param>
    /// <exception cref="Exception">Only if not even the first frame could be captured.</exception>
    public static async Task<ScrollCaptureResult> RunAsync(PixelRect area, CancellationToken cancel = default)
    {
        int maxHeight = (int)Math.Clamp(ImageIO.MaxPixels / area.Width, area.Height, ScrollStitcher.DefaultMaxHeight);
        var stitcher = new ScrollStitcher(area.Width, area.Height, maxHeight);
        using var grabber = new ScreenGrabber(area);
        // Two frame buffers, reused: the stitcher keeps the last frame it used, the next one goes to the other.
        var buffers = new[] { new byte[area.Width * 4 * area.Height], new byte[area.Width * 4 * area.Height] };
        int nextBuffer = 0;
        byte[] Grab()
        {
            grabber.Grab();
            var buffer = buffers[nextBuffer];
            grabber.CopyTo(buffer);
            return buffer;
        }
        StitchStep Add(byte[] frame, int expected = 0)
        {
            var step = stitcher.Add(frame, expected);
            if (step.NewRows > 0) nextBuffer ^= 1;
            return step;
        }

        GetCursorPos(out var original);
        var point = PickPoint(area);
        ScrollStopBar? bar = null;
        bool stopClicked = false, stopped = false;
        MousePosition Mouse() => Locate(point, area, bar?.ScreenBounds);
        try
        {
            ActivateWindowAt(point);
            SetCursorPos(point.X, point.Y);
            // The reference is where the cursor really is (Windows may have kept it elsewhere).
            if (GetCursorPos(out var actual)) point = actual;
            await Task.Delay(StartDelayMs);
            Add(Grab());

            try
            {
                bar = new ScrollStopBar(area);
                bar.StopClicked += () => stopClicked = true;
                bar.Show();
            }
            catch (Exception ex)
            {
                // Esc and the mouse still stop the capture.
                ErrorLog.Write("Barre de la capture défilante", ex);
            }

            // Forget an Esc pressed before (e.g. during the selection).
            GetAsyncKeyState(VK_ESCAPE);
            bool StopRequested()
            {
                // A selection opening (another feature) must not have the wheel turning under it.
                if (stopClicked || cancel.IsCancellationRequested || App.Instance.IsExiting || RegionSelectorWindow.IsOpen
                    || EscapePressed() || Mouse() == MousePosition.Outside)
                    stopped = true;
                return stopped;
            }

            try
            {
                await ScrollAsync(area, Add, Grab, () => stitcher.Height, bar, StopRequested, () => Mouse() == MousePosition.AtPoint);
            }
            catch (Exception ex)
            {
                // Keep what was captured so far.
                ErrorLog.Write("Capture défilante", ex);
            }
        }
        finally
        {
            bar?.Close();
            // Put the mouse back, unless the user took it over meanwhile.
            if (Mouse() == MousePosition.AtPoint) SetCursorPos(original.X, original.Y);
        }
        bool nothingScrolled = stitcher.FramesUsed <= 1 && !stopped;
        Array.Clear(buffers); // frees the frames before the image is built
        grabber.Dispose();
        // Tens of megapixels: copied into the image off the UI thread.
        var image = await Task.Run(() => ToBitmap(stitcher));
        return new ScrollCaptureResult(image, nothingScrolled);
    }

    /// <param name="add">Stitches a frame (with the likely shift); <paramref name="grab"/> captures one.</param>
    /// <param name="height">Height of the image stitched so far.</param>
    private static async Task ScrollAsync(PixelRect area, Func<byte[], int, StitchStep> add, Func<byte[]> grab, Func<int> height,
        ScrollStopBar? bar, Func<bool> stopRequested, Func<bool> mouseAtPoint)
    {
        var watch = Stopwatch.StartNew();
        // First step: one notch, to measure how many pixels a notch scrolls in this window.
        int notches = 1, expected = 0;
        for (int step = 0; step < MaxSteps && watch.Elapsed < MaxDuration; step++)
        {
            if (stopRequested()) return;
            if (!await WaitAtPointAsync(mouseAtPoint, bar, stopRequested)) return;
            if (!SendWheel(-notches))
            {
                ErrorLog.Write("Capture défilante", "SendInput a refusé la molette (entrée bloquée ?)");
                return;
            }
            if (!await WaitAsync(StepDelayMs, stopRequested)) return;

            int shiftHint = expected;
            var result = await Task.Run(() => add(grab(), shiftHint));
            if (result.Outcome == StitchOutcome.NoChange)
            {
                // Slow repaint or long smooth scrolling: look once more before concluding it is the end.
                if (!await WaitAsync(RetryDelayMs, stopRequested)) return;
                result = await Task.Run(() => add(grab(), shiftHint));
            }
            if (result.NewRows > 0) bar?.SetProgress(height());
            if (result.Outcome != StitchOutcome.Appended) return;

            // Scroll about the same share of the area at each step, whatever the wheel speed of the application.
            double pixelsPerNotch = Math.Max(1, result.Shift / (double)notches);
            notches = Math.Clamp((int)(area.Height * StepShare / pixelsPerNotch), 1, MaxNotches);
            expected = (int)Math.Round(pixelsPerNotch * notches);
        }
    }

    /// <summary>Waits while the mouse is away from its place (scrolling paused), checking the stop conditions.</summary>
    /// <returns>False if a stop was requested meanwhile.</returns>
    private static async Task<bool> WaitAtPointAsync(Func<bool> mouseAtPoint, ScrollStopBar? bar, Func<bool> stopRequested)
    {
        while (!mouseAtPoint())
        {
            bar?.SetPaused(true);
            await Task.Delay(PollMs);
            if (stopRequested()) return false;
        }
        bar?.SetPaused(false);
        return true;
    }

    /// <summary>Waits, checking the stop conditions.</summary>
    /// <returns>False if a stop was requested meanwhile.</returns>
    private static async Task<bool> WaitAsync(int milliseconds, Func<bool> stopRequested)
    {
        for (int waited = 0; waited < milliseconds; waited += PollMs)
        {
            await Task.Delay(PollMs);
            if (stopRequested()) return false;
        }
        return true;
    }

    private static bool EscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0;

    /// <summary>Where the mouse is, compared with its place, the area and the Stop bar.</summary>
    private static MousePosition Locate(POINT point, PixelRect area, PixelRect? bar)
    {
        if (!GetCursorPos(out var p)) return MousePosition.AtPoint;
        if (Math.Abs(p.X - point.X) <= MouseTolerance && Math.Abs(p.Y - point.Y) <= MouseTolerance) return MousePosition.AtPoint;
        // The area and the bar, with the gap between them: the way to the Stop button.
        var zone = bar is PixelRect b ? area.Union(b) : area;
        zone = PixelRect.FromLTRB(zone.X - MouseTolerance, zone.Y - MouseTolerance, zone.Right + MouseTolerance, zone.Bottom + MouseTolerance);
        return zone.Contains(p.X, p.Y) ? MousePosition.Away : MousePosition.Outside;
    }

    /// <summary>
    /// Point where the wheel is turned: centered, in the upper part of the area (hover effects under the
    /// cursor then stay out of the rows appended from the bottom of each frame; still below a window's
    /// title and tool bars). An area spanning monitors of different sizes may have that point in a dead
    /// zone between them: the part of the area on the monitor that shows most of it is used instead.
    /// </summary>
    private static POINT PickPoint(PixelRect area)
    {
        static POINT UpperCenter(PixelRect r) => new() { X = r.X + r.Width / 2, Y = r.Y + (int)(r.Height * 0.4) };
        var preferred = UpperCenter(area);
        PixelRect? largest = null;
        foreach (var screen in WinForms.Screen.AllScreens)
        {
            var bounds = PixelRect.From(screen.Bounds);
            if (bounds.Contains(preferred.X, preferred.Y)) return preferred;
            var part = bounds.Intersect(area);
            if (!part.IsEmpty && (largest is not PixelRect l || part.Area > l.Area)) largest = part;
        }
        return largest is PixelRect visible ? UpperCenter(visible) : preferred;
    }

    /// <summary>
    /// Brings the window under the point to the front: applications that only scroll their active
    /// window (or a system set not to scroll inactive windows) then receive the wheel.
    /// </summary>
    private static void ActivateWindowAt(POINT point)
    {
        try
        {
            var root = GetAncestor(WindowFromPoint(point), GA_ROOT);
            if (root != IntPtr.Zero && root != GetForegroundWindow()) SetForegroundWindow(root);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture défilante", ex);
        }
    }

    /// <summary>The stitched pixels, copied once straight from the stitcher's buffer into a frozen image.</summary>
    private static BitmapSource ToBitmap(ScrollStitcher stitcher)
    {
        ImageIO.CheckSize(stitcher.Width, stitcher.Height);
        var pixels = stitcher.Complete();
        var handle = GCHandle.Alloc(pixels.Array!, GCHandleType.Pinned);
        try
        {
            var image = BitmapSource.Create(stitcher.Width, stitcher.Height, 96, 96, PixelFormats.Bgra32, null,
                handle.AddrOfPinnedObject(), pixels.Count, stitcher.Width * 4);
            image.Freeze();
            return image;
        }
        finally
        {
            handle.Free();
        }
    }
}
