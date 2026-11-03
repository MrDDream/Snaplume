using Snaplume.Capturing;

namespace Snaplume.Tests;

/// <summary>The area of a video before recording: standard sizes, resizing and moving within the monitors.</summary>
public class RecordingAreaTests
{
    // A 1600 × 900 primary monitor, and a 1920 × 1080 one on its right.
    private static readonly PixelRect[] Monitors = { new(0, 0, 1600, 900), new(1600, 0, 1920, 1080) };

    [Fact]
    public void Presets_that_do_not_fit_are_left_out()
    {
        Assert.Equal(new[] { (640, 480), (854, 480), (1280, 720) }, RecordingArea.PresetsFor(Monitors[0]));
        Assert.Equal(4, RecordingArea.PresetsFor(Monitors[1]).Count);
    }

    [Fact]
    public void Resizing_keeps_the_center_and_stays_on_the_monitor()
    {
        var area = new PixelRect(500, 300, 200, 100);
        Assert.Equal(new PixelRect(280, 110, 640, 480), RecordingArea.Resize(area, 640, 480, Monitors));

        // Near the top-left corner: pushed inside.
        Assert.Equal(new PixelRect(0, 0, 1280, 720), RecordingArea.Resize(new PixelRect(10, 10, 100, 100), 1280, 720, Monitors));

        // Larger than the monitor: as large as the monitor.
        Assert.Equal(new PixelRect(0, 0, 1600, 900), RecordingArea.Resize(area, 1920, 1080, Monitors));

        // On the second monitor, even sizes.
        var right = RecordingArea.Resize(new PixelRect(3000, 900, 400, 100), 855, 481, Monitors);
        Assert.Equal(new PixelRect(1600 + 1920 - 854, 1080 - 480, 854, 480), right);
    }

    [Fact]
    public void Moving_stays_within_the_monitor_under_the_center()
    {
        var area = new PixelRect(100, 100, 640, 480);
        Assert.Equal(new PixelRect(150, 80, 640, 480), RecordingArea.Move(area, 50, -20, Monitors));
        // Pushed against the edges.
        Assert.Equal(new PixelRect(0, 0, 640, 480), RecordingArea.Move(area, -500, -500, Monitors));
        // Its center now on the second monitor: kept inside that one.
        Assert.Equal(new PixelRect(1600, 600, 640, 480), RecordingArea.Move(area, 1300, 900, Monitors));
    }

    [Fact]
    public void An_area_across_monitors_moves_within_the_desktop()
    {
        var wide = new PixelRect(1000, 0, 2000, 800);
        Assert.Equal(new PixelRect(1520, 0, 2000, 800), RecordingArea.Move(wide, 600, -50, Monitors));
    }
}
