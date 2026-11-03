namespace Snaplume.Capturing;

/// <summary>
/// The area of a video before the recording starts: standard sizes, resizing around its center and moving it,
/// always within the monitors. Physical pixels, sizes kept even (H.264).
/// </summary>
public static class RecordingArea
{
    /// <summary>Video sizes offered besides the selected area (4:3, 16:9 at 480, 720 and 1080 lines).</summary>
    public static readonly IReadOnlyList<(int Width, int Height)> Presets = new[]
    {
        (640, 480),
        (854, 480),
        (1280, 720),
        (1920, 1080),
    };

    /// <summary>The presets that fit on the monitor.</summary>
    public static IReadOnlyList<(int Width, int Height)> PresetsFor(PixelRect monitor) =>
        Presets.Where(p => p.Width <= monitor.Width && p.Height <= monitor.Height).ToList();

    /// <summary>The monitor holding the center of the area (the nearest one in a dead zone).</summary>
    public static PixelRect MonitorOf(PixelRect area, IReadOnlyList<PixelRect> monitors) =>
        CaptureGeometry.MonitorAt(monitors, area.X + area.Width / 2, area.Y + area.Height / 2);

    /// <summary>
    /// The area with a new size, centered on the same point, moved inside its monitor where it would cross an edge;
    /// never larger than the monitor.
    /// </summary>
    public static PixelRect Resize(PixelRect area, int width, int height, IReadOnlyList<PixelRect> monitors)
    {
        var monitor = MonitorOf(area, monitors);
        width = Math.Max(2, Math.Min(width, monitor.Width) & ~1);
        height = Math.Max(2, Math.Min(height, monitor.Height) & ~1);
        int cx = area.X + area.Width / 2, cy = area.Y + area.Height / 2;
        return ClampInto(new PixelRect(cx - width / 2, cy - height / 2, width, height), monitor);
    }

    /// <summary>
    /// The area moved by (<paramref name="dx"/>, <paramref name="dy"/>), kept inside the monitor under its new center;
    /// an area larger than that monitor (selected across several) is kept inside the whole desktop.
    /// </summary>
    public static PixelRect Move(PixelRect area, int dx, int dy, IReadOnlyList<PixelRect> monitors)
    {
        var moved = area.Offset(dx, dy);
        if (monitors.Count == 0) return moved;
        var monitor = MonitorOf(moved, monitors);
        if (area.Width <= monitor.Width && area.Height <= monitor.Height) return ClampInto(moved, monitor);
        var desktop = monitors.Skip(1).Aggregate(monitors[0], (all, m) => all.Union(m));
        return ClampInto(moved, desktop);
    }

    /// <summary>Moves the area inside the bounds (its top-left corner wins when it is larger).</summary>
    public static PixelRect ClampInto(PixelRect area, PixelRect bounds) => area with
    {
        X = Math.Max(bounds.X, Math.Min(area.X, bounds.Right - area.Width)),
        Y = Math.Max(bounds.Y, Math.Min(area.Y, bounds.Bottom - area.Height)),
    };
}
