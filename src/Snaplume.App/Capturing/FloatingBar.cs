using Avalonia;
using Avalonia.Controls;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Capturing;

/// <summary>Native behavior and placement of the small windows shown next to an area (scrolling capture, video recording).</summary>
internal static class FloatingBar
{
    /// <summary>
    /// Gives the window what Avalonia does not offer, once it has a native handle: out of the screenshots and videos
    /// (when the system allows it), never activated and out of the task bar, and optionally click-through.
    /// Failures are only logged: the window works without them.
    /// </summary>
    /// <param name="activatable">
    /// The window may take the keyboard focus (the video options: Escape, drop-down lists); it then keeps its usual
    /// native style, out of the task bar through <see cref="Window.ShowInTaskbar"/>.
    /// </param>
    public static void ApplyNativeStyles(Window window, bool clickThrough = false, bool activatable = false)
    {
        if (!AppServices.HasPlatform) return;
        try
        {
            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle == IntPtr.Zero) return;
            var platform = AppServices.Platform;
            if (!activatable) platform.NativeWindows.MakeToolWindow(handle);
            if (platform.Has(PlatformFeatures.ExcludeFromCapture)) platform.NativeWindows.ExcludeFromCapture(handle);
            if (clickThrough) platform.NativeWindows.MakeClickThrough(handle);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Fenêtre flottante", ex);
        }
    }

    /// <summary>
    /// Moves the bar next to the area: centered under it, or above it, or inside its bottom edge when there is no
    /// room; always within the work area of the monitor at the bottom of the area. Call once the bar has its size.
    /// </summary>
    /// <param name="area">Absolute physical screen coordinates.</param>
    /// <returns>Where the bar was placed (physical pixels).</returns>
    public static PixelRect PlaceNearArea(Window bar, PixelRect area)
    {
        double scaling = bar.RenderScaling;
        int width = (int)Math.Ceiling(bar.ClientSize.Width * scaling), height = (int)Math.Ceiling(bar.ClientSize.Height * scaling);
        var screens = bar.Screens.All
            .OrderByDescending(s => s.IsPrimary) // primary first: the fallback
            .Select(s => (Bounds: FromAvalonia(s.Bounds), WorkArea: FromAvalonia(s.WorkingArea)))
            .ToList();
        var placed = CaptureGeometry.PlaceNearArea(width, height, area, screens);
        bar.Position = new PixelPoint(placed.X, placed.Y);
        return placed;
    }

    public static PixelRect FromAvalonia(Avalonia.PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}
