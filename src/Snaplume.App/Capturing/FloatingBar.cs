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
    /// room; always within the work area of the monitor at the bottom of the area. Call once the bar has its size
    /// (<see cref="KeepNearArea"/> does it at the right times).
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

    /// <summary>
    /// Keeps the bar next to the area (<see cref="PlaceNearArea"/>): placed when it opens, when its size changes (first
    /// layout, other texts) and when its scaling changes. A bar first placed with the scaling of another monitor (mixed
    /// DPI) gets its physical size, and so its place, right once it is on the monitor of the area.
    /// </summary>
    /// <param name="area">The area, read at each placement (it may move): absolute physical screen coordinates.</param>
    /// <param name="enabled">False while the bar must stay where it is (e.g. dragged elsewhere by the user); null: always.</param>
    /// <param name="placed">Told where the bar went (physical pixels).</param>
    public static void KeepNearArea(Window bar, Func<PixelRect> area, Func<bool>? enabled = null, Action<PixelRect>? placed = null)
    {
        void Place()
        {
            if (!bar.IsVisible || enabled?.Invoke() == false) return;
            var bounds = PlaceNearArea(bar, area());
            placed?.Invoke(bounds);
        }

        bar.Opened += (_, _) => Place();
        bar.SizeChanged += (_, _) => Place();
        bar.ScalingChanged += (_, _) => Place();
    }

    private static PixelRect FromAvalonia(Avalonia.PixelRect r) => new(r.X, r.Y, r.Width, r.Height);
}
