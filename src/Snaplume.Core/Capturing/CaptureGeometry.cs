using System.Globalization;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Capturing;

/// <summary>
/// Pixel arithmetic of the capture interface (monitors, bars placed next to an area, magnifier), kept free of
/// windows so that it can be tested without a screen. Every rectangle is in physical pixels.
/// </summary>
public static class CaptureGeometry
{
    /// <summary>Physical pixels between an area and the bar placed next to it.</summary>
    public const int BarGap = 12;

    /// <summary>
    /// Monitor containing the point. In the dead zones between monitors of different sizes, the nearest one;
    /// with no monitor at all, a large rectangle at the origin.
    /// </summary>
    public static PixelRect MonitorAt(IReadOnlyList<PixelRect> monitors, int x, int y)
    {
        if (monitors.Count == 0) return new PixelRect(0, 0, 1 << 16, 1 << 16);
        var best = monitors[0];
        long bestDistance = long.MaxValue;
        foreach (var monitor in monitors)
        {
            long distance = monitor.DistanceSquared(x, y);
            if (distance == 0) return monitor;
            if (distance < bestDistance)
            {
                best = monitor;
                bestDistance = distance;
            }
        }
        return best;
    }

    /// <summary>
    /// The monitor under the mouse (in the dead zones between monitors, the nearest one, see <see cref="MonitorAt"/>);
    /// the primary monitor when the mouse position is unknown, else the first one; null without any monitor.
    /// </summary>
    /// <param name="cursor">Mouse position, in the coordinates of the monitor bounds; null when unknown.</param>
    public static MonitorInfo? MonitorUnderCursor(IReadOnlyList<MonitorInfo> monitors, (int X, int Y)? cursor)
    {
        if (monitors.Count == 0) return null;
        if (cursor is (int x, int y))
        {
            var bounds = MonitorAt(monitors.Select(m => m.Bounds).ToList(), x, y);
            return monitors.First(m => m.Bounds == bounds);
        }
        return monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors[0];
    }

    /// <summary>
    /// Where a small bar goes next to an area: centered under it, or above it, or inside its bottom edge when
    /// there is no room; always within the work area of the monitor at the bottom of the area.
    /// </summary>
    /// <param name="area">Absolute physical coordinates.</param>
    /// <param name="screens">Bounds and work area (without the task bar) of each monitor; the first one is the fallback.</param>
    public static PixelRect PlaceNearArea(int width, int height, PixelRect area, IReadOnlyList<(PixelRect Bounds, PixelRect WorkArea)> screens)
    {
        var bounds = screens.Count == 0
            ? area
            : screens.FirstOrDefault(s => s.Bounds.Contains(area.X + area.Width / 2, area.Bottom - 1), screens[0]).WorkArea;

        int x = Math.Clamp(area.X + (area.Width - width) / 2, bounds.X, Math.Max(bounds.X, bounds.Right - width));
        int y = area.Bottom + BarGap;
        if (y + height > bounds.Bottom) y = area.Y - BarGap - height;
        if (y < bounds.Y) y = Math.Max(bounds.Y, Math.Min(area.Bottom, bounds.Bottom) - BarGap - height);
        return new PixelRect(x, y, width, height);
    }

    /// <summary>
    /// The <paramref name="cells"/> × <paramref name="cells"/> pixels around a point, as opaque BGRA; outside the
    /// image they are dark gray. Read on demand from the desktop image (the magnifier keeps no copy of it).
    /// </summary>
    public static byte[] MagnifierPixels(PixelBuffer image, int cx, int cy, int cells)
    {
        int half = cells / 2;
        var buffer = new byte[cells * cells * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = buffer[i + 1] = buffer[i + 2] = 40;
            buffer[i + 3] = 255;
        }
        var area = new PixelRect(cx - half, cy - half, cells, cells).Intersect(new PixelRect(0, 0, image.Width, image.Height));
        if (area.IsEmpty) return buffer;
        for (int row = 0; row < area.Height; row++)
        {
            int source = (area.Y + row) * image.Stride + area.X * 4;
            int target = ((area.Y - (cy - half) + row) * cells + (area.X - (cx - half))) * 4;
            Buffer.BlockCopy(image.Pixels, source, buffer, target, area.Width * 4);
        }
        // Screen copies may leave the 4th byte undefined: keep the magnifier opaque.
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
        return buffer;
    }

    /// <summary>Color of a pixel as #RRGGBB (what designers paste elsewhere).</summary>
    public static string HexColor(PixelBuffer image, int x, int y)
    {
        if (x < 0 || y < 0 || x >= image.Width || y >= image.Height) return "#000000";
        int i = y * image.Stride + x * 4;
        return string.Create(CultureInfo.InvariantCulture, $"#{image.Pixels[i + 2]:X2}{image.Pixels[i + 1]:X2}{image.Pixels[i]:X2}");
    }

    /// <summary>H.264 needs even dimensions: the area loses its last odd column and row.</summary>
    public static PixelRect EvenSize(PixelRect area) => area with { Width = area.Width & ~1, Height = area.Height & ~1 };

    /// <summary>
    /// Converts an area of the desktop image back to the screen: <paramref name="desktop"/> is where the image starts
    /// (physical pixels, possibly negative left of the primary monitor).
    /// </summary>
    public static PixelRect ToScreen(PixelRect relative, PixelRect desktop) => relative.Offset(desktop.X, desktop.Y);

    /// <summary>Converts a screen area into the desktop image, cut to the image; empty if outside.</summary>
    public static PixelRect ToDesktop(PixelRect absolute, PixelRect desktop) =>
        absolute.Offset(-desktop.X, -desktop.Y).Intersect(new PixelRect(0, 0, desktop.Width, desktop.Height));
}
