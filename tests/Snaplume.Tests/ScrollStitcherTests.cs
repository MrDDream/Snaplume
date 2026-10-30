
using Snaplume.Capturing;

namespace Snaplume.Tests;

/// <summary>
/// Browser-like scrolling, simulated: a tall page seen through a window with a sticky header and a scrollbar
/// whose thumb moves. The page "scrolls" by whole notches (100 px each), but a smooth-scrolling animation means a
/// frame taken too early shows only part of the move. Replays the old and the new step logic of ScrollingCapture.
/// </summary>
public class ScrollStitcherTests
{
    private const int W = 400, H = 300, Header = 40, ScrollbarW = 12, PageH = 3000, PixelsPerNotch = 100;
    const double StepShare = 0.45;
    const int MaxNotches = 10;

    static byte[] page = BuildPage();

    static byte[] BuildPage()
    {
        var rnd = new Random(7);
        var p = new byte[W * 4 * PageH];
        for (int y = 0; y < PageH; y++)
        {
            bool textLine = (y / 6) % 3 != 0; // lines of "text" separated by blank rows
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                byte v = textLine && x > 20 && x < W - 40 && rnd.Next(4) == 0 ? (byte)rnd.Next(0, 120) : (byte)250;
                p[i] = v; p[i + 1] = v; p[i + 2] = (byte)(v ^ (y & 7)); p[i + 3] = 255;
            }
        }
        return p;
    }

    /// <summary>The window when the page is scrolled by <paramref name="offset"/> pixels.</summary>
    static byte[] Frame(int offset)
    {
        var f = new byte[W * 4 * H];
        for (int y = 0; y < H; y++)
        {
            for (int x = 0; x < W; x++)
            {
                int i = (y * W + x) * 4;
                if (y < Header) { f[i] = 40; f[i + 1] = 90; f[i + 2] = (byte)(x % 200); f[i + 3] = 255; continue; } // sticky header
                if (x >= W - ScrollbarW)
                {
                    int thumbTop = Header + (int)((H - Header - 40) * (offset / (double)(PageH - H)));
                    byte v = y >= thumbTop && y < thumbTop + 40 ? (byte)120 : (byte)230;
                    f[i] = v; f[i + 1] = v; f[i + 2] = v; f[i + 3] = 255; continue;
                }
                int src = ((offset + y) * W + x) * 4;
                f[i] = page[src]; f[i + 1] = page[src + 1]; f[i + 2] = page[src + 2]; f[i + 3] = 255;
            }
        }
        return f;
    }

    /// <summary>Replays a capture; the first measured step only shows <paramref name="firstVisibleShare"/> of its move (animation).</summary>
    static (int Height, int Steps, string Stop) Replay(bool newLogic, double firstVisibleShare)
    {
        var stitcher = new ScrollStitcher(W, H);
        int offset = 0, max = PageH - H;
        stitcher.Add(Frame(0));
        int notches = 1, recoveries = 0;
        double ppn = 0;
        for (int step = 0; step < 100; step++)
        {
            int target = Math.Min(max, offset + notches * PixelsPerNotch);
            // Old logic: grabbed during the animation on the first step. New logic: waits for the page to settle.
            int shown = !newLogic && step == 0 ? offset + (int)((target - offset) * firstVisibleShare) : target;
            offset = target;
            int hint = (int)Math.Round(ppn * notches);
            var r = stitcher.Add(Frame(shown), hint);
            if (!newLogic && step == 0) offset = shown; // the rest of the animation goes on, but the old logic measured this
            if (r.Outcome == StitchOutcome.NoChange) return (stitcher.Height, step, "end");
            if (r.Outcome == StitchOutcome.NoMatch)
            {
                if (newLogic && notches > 1 && recoveries < 3)
                {
                    recoveries++;
                    offset -= notches * PixelsPerNotch;
                    notches = Math.Max(1, notches / 3);
                    continue;
                }
                return (stitcher.Height, step, "NoMatch");
            }
            if (r.Outcome != StitchOutcome.Appended) return (stitcher.Height, step, r.Outcome.ToString());
            ppn = Math.Max(1, r.Shift / (double)notches);
            int wanted = Math.Clamp((int)(H * StepShare / ppn), 1, MaxNotches);
            notches = newLogic ? Math.Min(wanted, notches * 2) : wanted;
        }
        return (stitcher.Height, 100, "steps");
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(0.5)]
    [InlineData(0.25)]
    public void Whole_page_stitched_even_when_the_first_frame_is_taken_during_the_animation(double firstVisibleShare)
    {
        var result = Replay(newLogic: true, firstVisibleShare);
        Assert.Equal((PageH, "end"), (result.Height, result.Stop));
    }

    [Fact]
    public void Old_step_logic_stopped_early_when_the_first_frame_was_taken_too_soon() =>
        Assert.Equal("NoMatch", Replay(newLogic: false, 0.25).Stop);
}
