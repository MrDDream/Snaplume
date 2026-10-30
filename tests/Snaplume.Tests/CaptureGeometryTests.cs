using Snaplume.Capturing;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Tests;

/// <summary>Pixel arithmetic of the capture interface and the area selection, without a screen.</summary>
public class CaptureGeometryTests
{
    // Two monitors: a 1920 × 1080 primary, and a 2560 × 1440 one on its left, lower (desktop image starts at x = -2560).
    private static readonly PixelRect Desktop = new(-2560, 0, 4480, 1440);
    private static readonly PixelRect[] RelativeMonitors = { new(2560, 0, 1920, 1080), new(0, 0, 2560, 1440) };

    [Fact]
    public void MonitorAtPicksTheNearestInDeadZones()
    {
        Assert.Equal(RelativeMonitors[1], CaptureGeometry.MonitorAt(RelativeMonitors, 10, 10));
        Assert.Equal(RelativeMonitors[0], CaptureGeometry.MonitorAt(RelativeMonitors, 3000, 500));
        // Below the primary monitor (dead zone): the primary is nearer than the left one.
        Assert.Equal(RelativeMonitors[0], CaptureGeometry.MonitorAt(RelativeMonitors, 4000, 1300));
    }

    [Fact]
    public void ScreenAndDesktopCoordinates()
    {
        var relative = new PixelRect(2600, 100, 200, 100);
        var screen = CaptureGeometry.ToScreen(relative, Desktop);
        Assert.Equal(new PixelRect(40, 100, 200, 100), screen);
        Assert.Equal(relative, CaptureGeometry.ToDesktop(screen, Desktop));
        // Partly outside the desktop: cut.
        Assert.Equal(new PixelRect(0, 0, 50, 50), CaptureGeometry.ToDesktop(new PixelRect(-2610, -50, 100, 100), Desktop));
        Assert.True(CaptureGeometry.ToDesktop(new PixelRect(5000, 0, 10, 10), Desktop).IsEmpty);
    }

    [Fact]
    public void EvenSizeForVideo()
    {
        Assert.Equal(new PixelRect(3, 5, 100, 50), CaptureGeometry.EvenSize(new PixelRect(3, 5, 101, 51)));
    }

    [Fact]
    public void BarGoesUnderAboveOrInsideTheArea()
    {
        var screens = new[] { (new PixelRect(0, 0, 1920, 1080), new PixelRect(0, 0, 1920, 1040)) };
        // Room below: centered under the area.
        var below = CaptureGeometry.PlaceNearArea(200, 40, new PixelRect(100, 100, 400, 300), screens);
        Assert.Equal(new PixelRect(200, 412, 200, 40), below);
        // No room below (task bar): above.
        var above = CaptureGeometry.PlaceNearArea(200, 40, new PixelRect(100, 600, 400, 420), screens);
        Assert.Equal(588 - 40, above.Y);
        // Full screen: inside the bottom edge of the work area.
        var inside = CaptureGeometry.PlaceNearArea(200, 40, new PixelRect(0, 0, 1920, 1080), screens);
        Assert.Equal(1040 - 12 - 40, inside.Y);
        Assert.Equal(860, inside.X);
        // Area at the right edge: the bar stays on the screen.
        var right = CaptureGeometry.PlaceNearArea(200, 40, new PixelRect(1850, 100, 60, 60), screens);
        Assert.Equal(1720, right.X);
    }

    [Fact]
    public void MagnifierReadsAroundThePointAndGrayOutside()
    {
        var image = new PixelBuffer(10, 10);
        for (int y = 0; y < 10; y++)
            for (int x = 0; x < 10; x++)
            {
                int i = (y * 10 + x) * 4;
                image.Pixels[i] = (byte)x;
                image.Pixels[i + 1] = (byte)y;
                image.Pixels[i + 2] = 200;
                image.Pixels[i + 3] = 0; // undefined alpha of a screen copy
            }
        var cells = CaptureGeometry.MagnifierPixels(image, 0, 0, 5);
        // Center cell (2, 2) is pixel (0, 0); cell (0, 0) is outside: dark gray.
        int center = (2 * 5 + 2) * 4;
        Assert.Equal(new byte[] { 0, 0, 200, 255 }, cells[center..(center + 4)]);
        Assert.Equal(new byte[] { 40, 40, 40, 255 }, cells[0..4]);
        int last = (4 * 5 + 4) * 4; // pixel (2, 2)
        Assert.Equal(new byte[] { 2, 2, 200, 255 }, cells[last..(last + 4)]);

        Assert.Equal("#C80302", CaptureGeometry.HexColor(image, 2, 3));
    }

    [Fact]
    public void SelectionDragsAnArea()
    {
        var model = new SelectionModel(4480, 1440, RelativeMonitors, EmptyWindowSnapshot.Instance);
        PixelRect? done = null;
        model.Completed += r => done = r;
        model.MoveTo(100, 100);
        model.Press();
        model.MoveTo(101, 101); // under the drag threshold: still a click
        Assert.False(model.IsDragging);
        model.MoveTo(300, 50);
        Assert.True(model.IsDragging);
        Assert.Equal(new PixelRect(100, 50, 201, 51), model.Focus);
        model.Release();
        Assert.Equal(new PixelRect(100, 50, 201, 51), done);
    }

    [Fact]
    public void SelectionClicksAWindowAndWalksToItsParents()
    {
        var windows = new FakeWindows(new PixelRect(110, 110, 50, 20), new PixelRect(100, 100, 400, 300), RelativeMonitors[1]);
        var model = new SelectionModel(4480, 1440, RelativeMonitors, windows);
        PixelRect? done = null;
        model.Completed += r => done = r;
        model.MoveTo(120, 115);
        Assert.Equal(new PixelRect(110, 110, 50, 20), model.Hover);
        Assert.True(model.Wheel(+1));
        Assert.Equal(new PixelRect(100, 100, 400, 300), model.Hover);
        model.Wheel(+1);
        model.Wheel(+1); // stays on the largest
        Assert.Equal(RelativeMonitors[1], model.Hover);
        model.Wheel(-1);
        model.Press();
        model.Release();
        Assert.Equal(new PixelRect(100, 100, 400, 300), done);
    }

    [Fact]
    public void SelectionKeyboard()
    {
        var model = new SelectionModel(4480, 1440, RelativeMonitors, EmptyWindowSnapshot.Instance);
        PixelRect? done = null;
        bool cancelled = false;
        model.Completed += r => done = r;
        model.Cancelled += () => cancelled = true;
        model.MoveTo(3000, 500);
        model.Key(SelectionKey.Right, large: true);
        model.Key(SelectionKey.Up);
        Assert.Equal((3010, 499), (model.X, model.Y));
        // Enter without a highlighted window does nothing.
        Assert.False(model.Key(SelectionKey.Enter));
        model.Key(SelectionKey.F);
        Assert.Equal(RelativeMonitors[0], done);
        // Once completed, nothing else is reported.
        model.Key(SelectionKey.Escape);
        Assert.False(cancelled);

        var other = new SelectionModel(4480, 1440, RelativeMonitors, EmptyWindowSnapshot.Instance);
        PixelRect? all = null;
        other.Completed += r => all = r;
        other.Key(SelectionKey.A);
        Assert.Equal(new PixelRect(0, 0, 4480, 1440), all);

        var third = new SelectionModel(100, 100, Array.Empty<PixelRect>(), EmptyWindowSnapshot.Instance);
        bool thirdCancelled = false;
        third.Cancelled += () => thirdCancelled = true;
        third.MoveTo(500, -5); // clamped to the image
        Assert.Equal((99, 0), (third.X, third.Y));
        third.RightClick();
        Assert.True(thirdCancelled);
    }

    [Fact]
    public void RightClickDropsTheDragFirst()
    {
        var model = new SelectionModel(1000, 1000, Array.Empty<PixelRect>(), EmptyWindowSnapshot.Instance);
        bool cancelled = false;
        model.Cancelled += () => cancelled = true;
        model.MoveTo(10, 10);
        model.Press();
        model.MoveTo(200, 200);
        model.RightClick();
        Assert.False(model.IsDragging);
        Assert.False(cancelled);
        model.RightClick();
        Assert.True(cancelled);
    }

    private sealed class FakeWindows : IWindowSnapshot
    {
        private readonly PixelRect[] _nested;

        /// <param name="nested">Smallest first, each one inside the next.</param>
        public FakeWindows(params PixelRect[] nested) => _nested = nested;

        public IReadOnlyList<PixelRect> FindAt(int x, int y) => _nested.Where(r => r.Contains(x, y)).ToList();
    }
}
