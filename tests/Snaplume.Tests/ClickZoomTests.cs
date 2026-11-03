using Snaplume.Capturing;
using Snaplume.Recording;

namespace Snaplume.Tests;

/// <summary>Zoom on clicks: timing, easing and geometry of the view, and the driver that feeds the recorder.</summary>
public class ClickZoomTests
{
    private static TimeSpan Ms(double milliseconds) => TimeSpan.FromMilliseconds(milliseconds);

    [Fact]
    public void Idle_zoom_shows_the_whole_area()
    {
        var zoom = new ClickZoom(1280, 720);
        Assert.Equal(new ZoomView(0, 0, 1280, 720), zoom.ViewAt(Ms(0)));
        Assert.Equal(new ZoomView(0, 0, 1280, 720), zoom.ViewAt(Ms(5000)));
        Assert.True(zoom.IsIdleAt(Ms(0)));
    }

    [Fact]
    public void A_click_zooms_in_holds_then_zooms_out()
    {
        var zoom = new ClickZoom(1280, 720);
        Assert.True(zoom.Click(640, 360, Ms(1000)));
        Assert.False(zoom.IsIdleAt(Ms(1000)));

        // Halfway through the zoom in: halfway between the whole area and the target (ease in-out is symmetric).
        var half = zoom.ViewAt(Ms(1150));
        Assert.Equal(960, half.Width, 6);
        Assert.Equal(540, half.Height, 6);

        // Zoomed ×2 around the click, for the whole hold.
        var zoomed = new ZoomView(320, 180, 640, 360);
        Assert.Equal(zoomed, zoom.ViewAt(Ms(1300)));
        Assert.Equal(zoomed, zoom.ViewAt(Ms(2799)));

        // The zoom out starts 300 ms + 1.5 s after the click and lasts 300 ms.
        var outward = zoom.ViewAt(Ms(2950));
        Assert.True(outward.Width > 640 && outward.Width < 1280);
        Assert.Equal(new ZoomView(0, 0, 1280, 720), zoom.ViewAt(Ms(3100)));
        Assert.True(zoom.IsIdleAt(Ms(3100)));
        Assert.False(zoom.IsIdleAt(Ms(3099)));
    }

    [Fact]
    public void The_view_keeps_the_ratio_and_grows_smoothly()
    {
        var zoom = new ClickZoom(1920, 1080);
        zoom.Click(100, 100, Ms(0));
        double previous = 1920;
        for (int t = 0; t <= 300; t += 10)
        {
            var view = zoom.ViewAt(Ms(t));
            Assert.Equal(1920.0 / 1080, view.Width / view.Height, 6);
            Assert.True(view.Width <= previous + 1e-9);
            previous = view.Width;
            Assert.True(view.X >= 0 && view.Y >= 0 && view.Right <= 1920 + 1e-9 && view.Bottom <= 1080 + 1e-9);
        }
    }

    [Fact]
    public void Clicks_near_an_edge_keep_the_view_inside()
    {
        var zoom = new ClickZoom(1280, 720);
        Assert.Equal(new ZoomView(0, 0, 640, 360), zoom.Target(5, 5));
        Assert.Equal(new ZoomView(640, 360, 640, 360), zoom.Target(1279, 719));
        Assert.Equal(new ZoomView(640, 100, 640, 360), zoom.Target(1200, 280));
    }

    [Fact]
    public void Clicks_outside_the_area_are_ignored()
    {
        var zoom = new ClickZoom(800, 600);
        Assert.False(zoom.Click(-1, 10, Ms(0)));
        Assert.False(zoom.Click(800, 10, Ms(0)));
        Assert.False(zoom.Click(10, 600, Ms(0)));
        Assert.True(zoom.IsIdleAt(Ms(0)));
    }

    [Fact]
    public void A_new_click_while_zoomed_moves_the_view_and_extends_the_hold()
    {
        var zoom = new ClickZoom(1280, 720);
        zoom.Click(320, 180, Ms(0));
        Assert.Equal(new ZoomView(0, 0, 640, 360), zoom.ViewAt(Ms(1000)));

        zoom.Click(960, 540, Ms(1000));
        // Moving from the first point to the second one, still zoomed.
        var moving = zoom.ViewAt(Ms(1150));
        Assert.Equal(640, moving.Width, 6);
        Assert.Equal(320, moving.X, 6);
        Assert.Equal(new ZoomView(640, 360, 640, 360), zoom.ViewAt(Ms(1300)));
        // The first click would have released at 1800 ms: the second keeps it zoomed until 2800 ms.
        Assert.Equal(new ZoomView(640, 360, 640, 360), zoom.ViewAt(Ms(2700)));
        Assert.True(zoom.IsIdleAt(Ms(3100)));
    }

    [Fact]
    public void A_click_during_the_zoom_out_zooms_in_again_from_where_it_is()
    {
        var zoom = new ClickZoom(1280, 720);
        zoom.Click(640, 360, Ms(0));
        var during = zoom.ViewAt(Ms(1950));
        zoom.Click(640, 360, Ms(1950));
        // No jump: the view starts from where the zoom out was.
        Assert.Equal(during, zoom.ViewAt(Ms(1950)));
        Assert.Equal(new ZoomView(320, 180, 640, 360), zoom.ViewAt(Ms(2250)));
    }

    [Fact]
    public void Easing_starts_and_ends_slowly()
    {
        Assert.Equal(0, ClickZoom.Ease(0));
        Assert.Equal(0.5, ClickZoom.Ease(0.5), 9);
        Assert.Equal(1, ClickZoom.Ease(1));
        Assert.True(ClickZoom.Ease(0.1) < 0.1);
        Assert.True(ClickZoom.Ease(0.9) > 0.9);
    }

    [Fact]
    public void Views_become_even_pixels_inside_the_area()
    {
        Assert.Equal(new PixelRect(320, 180, 640, 360), new ZoomView(320.2, 179.6, 640.4, 359.8).ToPixels(1280, 720));
        var odd = new ZoomView(1000.7, 500.3, 279.3, 219.9).ToPixels(1280, 720);
        Assert.Equal(0, odd.Width % 2);
        Assert.Equal(0, odd.Height % 2);
        Assert.True(odd.Right <= 1280 && odd.Bottom <= 720);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), new ZoomView(-0.4, 0.2, 1280.3, 720.1).ToPixels(1280, 720));
    }

    [Fact]
    public void Driver_feeds_the_recorder_and_stops_when_zoomed_out()
    {
        var time = new ManualTime();
        var views = new List<PixelRect>();
        using var driver = new ClickZoomDriver(new PixelRect(100, 50, 1280, 720), v =>
        {
            views.Add(v);
            return true;
        }, time: time);

        driver.OnClick(50, 50); // outside the area
        Assert.False(driver.IsRunning);
        Assert.False(time.Timer!.Running);

        driver.OnClick(100 + 640, 50 + 360);
        Assert.True(driver.IsRunning);
        Assert.True(time.Timer.Running);
        Assert.Equal(TimeSpan.FromSeconds(1.0 / 30), time.Timer.Period);

        for (int t = 0; t <= 2200; t += 33)
        {
            time.Now = Ms(t);
            driver.Tick();
        }
        Assert.Contains(new PixelRect(320, 180, 640, 360), views);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), views[^1]);
        Assert.Equal(new PixelRect(0, 0, 1280, 720), driver.Applied);
        Assert.False(driver.IsRunning);
        Assert.False(time.Timer.Running);
        // Only changes are given to the recorder.
        for (int i = 1; i < views.Count; i++) Assert.NotEqual(views[i - 1], views[i]);
    }

    [Fact]
    public void Driver_ignores_clicks_while_paused_and_retries_a_refused_view()
    {
        var time = new ManualTime();
        bool accept = false;
        var views = new List<PixelRect>();
        using var driver = new ClickZoomDriver(new PixelRect(0, 0, 800, 600), v =>
        {
            if (!accept) return false;
            views.Add(v);
            return true;
        }, time: time);

        driver.Paused = true;
        driver.OnClick(400, 300);
        Assert.False(driver.IsRunning);

        driver.Paused = false;
        driver.OnClick(400, 300);
        time.Now = Ms(300);
        driver.Tick();
        Assert.Empty(views);
        Assert.Equal(new PixelRect(0, 0, 800, 600), driver.Applied);
        // The recorder takes it again: the same view is given once more.
        accept = true;
        driver.Tick();
        Assert.Equal(new PixelRect(200, 150, 400, 300), Assert.Single(views));
    }

    [Fact]
    public void Driver_turns_off_when_the_recorder_fails()
    {
        var time = new ManualTime();
        int calls = 0;
        using var driver = new ClickZoomDriver(new PixelRect(0, 0, 800, 600), _ =>
        {
            calls++;
            throw new InvalidOperationException("refused");
        }, time: time);
        driver.OnClick(400, 300);
        time.Now = Ms(100);
        driver.Tick();
        time.Now = Ms(200);
        driver.Tick();
        Assert.Equal(1, calls);
        Assert.False(driver.IsRunning);
        driver.OnClick(400, 300);
        Assert.False(driver.IsRunning);
    }

    /// <summary>A clock moved by the test, and a timer that never fires by itself (the test calls Tick).</summary>
    private sealed class ManualTime : TimeProvider
    {
        public TimeSpan Now { get; set; }
        public ManualTimer? Timer { get; private set; }

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Now.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualTimer();
            Timer.Change(dueTime, period);
            return Timer;
        }
    }

    private sealed class ManualTimer : ITimer
    {
        public bool Running { get; private set; }
        public TimeSpan Period { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Running = dueTime != Timeout.InfiniteTimeSpan;
            Period = period;
            return true;
        }

        public void Dispose() => Running = false;
        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
