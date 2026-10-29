using System.Windows;
using System.Windows.Interop;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Capturing;

/// <summary>Placement of the small bars shown next to an area of the screen (scrolling capture, video recording).</summary>
internal static class FloatingBar
{
    /// <summary>Physical pixels between the area and the bar.</summary>
    private const int Gap = 12;

    /// <summary>
    /// Shows the bar topmost, without activating it: centered under the area, or above it, or inside its
    /// bottom edge when there is no room; always within the work area of the monitor at the bottom of the area.
    /// Call once the bar has its size (Loaded).
    /// </summary>
    /// <param name="area">Absolute physical screen coordinates.</param>
    /// <returns>Where the bar was placed (physical pixels), or null if its size could not be read.</returns>
    public static PixelRect? PlaceNearArea(Window bar, PixelRect area)
    {
        var hwnd = new WindowInteropHelper(bar).Handle;
        if (!GetWindowRect(hwnd, out var r)) return null;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => PixelRect.From(s.Bounds).Contains(area.X + area.Width / 2, area.Bottom - 1)) ?? screens[0];
        var bounds = PixelRect.From(screen.WorkingArea);

        int x = Math.Clamp(area.X + (area.Width - width) / 2, bounds.X, Math.Max(bounds.X, bounds.Right - width));
        int y = area.Bottom + Gap;
        if (y + height > bounds.Bottom) y = area.Y - Gap - height;
        if (y < bounds.Y) y = Math.Max(bounds.Y, Math.Min(area.Bottom, bounds.Bottom) - Gap - height);
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
        return new PixelRect(x, y, width, height);
    }
}
