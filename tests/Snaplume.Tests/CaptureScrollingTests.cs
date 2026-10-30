using Snaplume.Capturing;
using Snaplume.Tests.Fakes;

namespace Snaplume.Tests;

/// <summary>
/// The scrolling capture end to end, on a simulated page (<see cref="FakeScrollingPage"/>): wheel, grabs, settling,
/// stitching, stop conditions and the mouse put back. Delays are shortened; the rules are the ones of Snaplume 1.1.1.
/// </summary>
public class CaptureScrollingTests
{
    private static readonly PixelRect Area = new(100, 50, 300, 300);
    private static readonly PixelRect[] Monitors = { new(0, 0, 1920, 1080) };

    private static readonly ScrollCaptureTiming Fast = new()
    {
        StartDelayMs = 0,
        StepDelayMs = 10,
        SettlePollMs = 20,
        SettleTimeoutMs = 1000,
        RetryDelayMs = 60,
        PollMs = 5,
        MaxDuration = TimeSpan.FromSeconds(60),
    };

    private static ScrollingCapture Capture(FakeScrollingPage page, List<FakeScrollBar>? bars = null, Func<bool>? externalStop = null) =>
        new(page, page.CreateGrabber, Monitors)
        {
            Timing = Fast,
            ExternalStop = externalStop,
            CreateBar = (area, stop) =>
            {
                var bar = new FakeScrollBar(stop) { ScreenBounds = new PixelRect(area.X, area.Bottom + 12, 200, 40) };
                bars?.Add(bar);
                return bar;
            },
        };

    private static void AssertWholePage(FakeScrollingPage page, ScrollCaptureResult result)
    {
        Assert.Equal(Area.Width, result.Image.Width);
        Assert.Equal(page.PageHeight, result.Image.Height);
        Assert.Equal(result.Image.Stride * result.Image.Height, result.Image.Pixels.Length);
        Assert.True(page.Page.AsSpan().SequenceEqual(result.Image.Pixels), "stitched pixels differ from the page");
    }

    [Fact]
    public async Task WholePageIsStitched()
    {
        var page = new FakeScrollingPage(Area, 2400);
        var bars = new List<FakeScrollBar>();
        var result = await Capture(page, bars).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        AssertWholePage(page, result);
        Assert.False(result.NothingScrolled);
        Assert.True(page.Prepared);
        Assert.True(page.Released);
        // Steps grow from one notch to about 45 % of the area (3 notches of 40 px here), never more than doubling.
        Assert.Equal(-1, page.ScrollLog[0]);
        Assert.All(page.ScrollLog.Zip(page.ScrollLog.Skip(1)), p => Assert.True(Math.Abs(p.Second) <= 2 * Math.Abs(p.First)));
        // (The last step, at the end of the page, follows a short move: it may take more notches, and finds nothing new.)
        Assert.True(page.ScrollLog.SkipLast(1).Max(Math.Abs) <= 3);
        // The bar showed the progress and was closed; the mouse is back where it was.
        var bar = Assert.Single(bars);
        Assert.True(bar.Closed);
        Assert.Equal(page.PageHeight, bar.Progress[^1]);
        Assert.True(page.TryGetPosition(out int x, out int y));
        Assert.Equal(page.StartPosition, (x, y));
    }

    [Fact]
    public async Task SmoothScrollingIsWaitedFor()
    {
        // Each wheel step is animated over 90 ms: frames taken during the animation would be stitched at the wrong place.
        var page = new FakeScrollingPage(Area, 1800) { AnimationMs = 90 };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        AssertWholePage(page, result);
        Assert.False(result.NothingScrolled);
    }

    [Fact]
    public async Task OverScrollIsUndoneAndCaptureGoesOn()
    {
        // The first step of 3 notches (and the step back) jumps three times further: past the viewport.
        var page = new FakeScrollingPage(Area, 2000) { BurstNotches = 3, BurstCount = 2, AnimationMs = 30 };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.Equal(0, page.BurstCount);
        // The recovery scrolled back up by the same number of notches.
        int back = page.ScrollLog.FindIndex(n => n > 0);
        Assert.True(back > 0, "no step back");
        Assert.Equal(-page.ScrollLog[back - 1], page.ScrollLog[back]);
        Assert.Equal(1, Math.Abs(page.ScrollLog[back + 1]));
        AssertWholePage(page, result);
    }

    [Fact]
    public async Task EscapeStopsAndKeepsWhatWasCaptured()
    {
        var page = new FakeScrollingPage(Area, 3000) { EscapeAfterScrolls = 4 };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.NothingScrolled);
        Assert.InRange(page.Scrolls, 4, 5);
        Assert.True(result.Image.Height > Area.Height);
        Assert.True(result.Image.Height < page.PageHeight);
        // What was kept is the top of the page.
        Assert.True(page.Page.AsSpan(0, result.Image.Pixels.Length).SequenceEqual(result.Image.Pixels));
        Assert.True(page.Released);
    }

    [Fact]
    public async Task MouseTakenOutOfTheAreaStops()
    {
        var page = new FakeScrollingPage(Area, 3000) { LeaveAfterScrolls = 2 };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.False(result.NothingScrolled);
        Assert.InRange(page.Scrolls, 2, 3);
    }

    [Fact]
    public async Task StopButtonAndExternalStop()
    {
        var bars = new List<FakeScrollBar>();
        var page = new FakeScrollingPage(Area, 3000);
        var capture = Capture(page, bars, externalStop: () => page.Scrolls >= 3);
        var result = await capture.RunAsync(Area, TestContext.Current.CancellationToken);
        Assert.NotNull(result);
        Assert.InRange(page.Scrolls, 3, 4);

        var page2 = new FakeScrollingPage(Area, 3000);
        var bars2 = new List<FakeScrollBar>();
        var capture2 = new ScrollingCapture(page2, page2.CreateGrabber, Monitors)
        {
            Timing = Fast,
            CreateBar = (area, stop) =>
            {
                var bar = new FakeScrollBar(stop);
                bars2.Add(bar);
                stop(); // clicked at once
                return bar;
            },
        };
        var result2 = await capture2.RunAsync(Area, TestContext.Current.CancellationToken);
        Assert.NotNull(result2);
        Assert.Equal(0, page2.Scrolls);
        Assert.Equal(Area.Height, result2.Image.Height);
        Assert.False(result2.NothingScrolled); // the user stopped it: not a window ignoring the wheel
    }

    [Fact]
    public async Task WindowIgnoringTheWheelIsReported()
    {
        var page = new FakeScrollingPage(Area, 3000) { IgnoresWheel = true };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.NotNull(result);
        Assert.True(result.NothingScrolled);
        Assert.Equal(Area.Height, result.Image.Height);
    }

    [Fact]
    public async Task RefusedPointerPermissionCapturesNothing()
    {
        var page = new FakeScrollingPage(Area, 3000) { Grant = false };
        var result = await Capture(page).RunAsync(Area, TestContext.Current.CancellationToken);

        Assert.Null(result);
        Assert.Equal(0, page.Grabs);
        Assert.Equal(0, page.Scrolls);
    }

    [Fact]
    public async Task CancellationKeepsTheImage()
    {
        var page = new FakeScrollingPage(Area, 3000);
        using var cancel = new CancellationTokenSource();
        var capture = new ScrollingCapture(page, page.CreateGrabber, Monitors)
        {
            Timing = Fast,
            ExternalStop = () =>
            {
                if (page.Scrolls >= 2) cancel.Cancel();
                return false;
            },
        };
        var result = await capture.RunAsync(Area, cancel.Token);
        Assert.NotNull(result);
        Assert.True(result.Image.Height >= Area.Height);
        Assert.True(page.Released);
    }

    [Fact]
    public void WheelPointIsOnAMonitor()
    {
        // Upper center of the area.
        Assert.Equal((250, 170), ScrollingCapture.PickPoint(Area, Monitors));
        // An area across two monitors of different heights whose upper center falls in the dead zone below the small one:
        // the point goes to the part on the monitor showing most of the area.
        var monitors = new[] { new PixelRect(0, 0, 1000, 400), new PixelRect(1000, 0, 1000, 1000) };
        var area = new PixelRect(400, 600, 1400, 300); // its upper center (1100, 720) is on the big monitor
        Assert.Equal((1100, 720), ScrollingCapture.PickPoint(area, monitors));
        var area2 = new PixelRect(0, 300, 1200, 600); // upper center (600, 540): dead zone
        var point = ScrollingCapture.PickPoint(area2, monitors);
        Assert.True(monitors[1].Contains(point.X, point.Y));
    }

    [Fact]
    public void SettledAllowsAFewChangedRows()
    {
        int stride = 40, height = 100;
        var a = new byte[stride * height];
        var b = (byte[])a.Clone();
        b[5 * stride] = 1;
        b[50 * stride] = 1; // 2 rows of 100: within 2 %
        Assert.True(ScrollingCapture.IsSettled(a, b, stride, height));
        b[70 * stride] = 1;
        Assert.False(ScrollingCapture.IsSettled(a, b, stride, height));
    }
}
