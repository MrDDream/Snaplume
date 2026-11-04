using Avalonia;
using Avalonia.Controls;

namespace Snaplume.Ui;

/// <summary>
/// Keeps windows inside the screen they open on. Their default sizes suit a desktop monitor; on a small laptop screen
/// at 125-150 % (about 1280 × 680 points of work area) the top and bottom would be out of reach.
/// </summary>
internal static class WindowFit
{
    /// <summary>
    /// When <paramref name="window"/> opens larger than the work area of its screen, it is maximized
    /// (<paramref name="maximize"/>: editors and players, which use every pixel) or shrunk to the work area and
    /// centered on it; its minimum size is lowered too if needed.
    /// </summary>
    public static void FitToScreen(Window window, bool maximize)
    {
        window.Opened += (_, _) =>
        {
            if (window.WindowState != WindowState.Normal) return;
            var screen = window.Screens.ScreenFromWindow(window) ?? window.Screens.Primary;
            if (screen == null || screen.Scaling <= 0) return;
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            double maxWidth = area.Width / scale, maxHeight = area.Height / scale;
            if (window.Bounds.Width <= maxWidth && window.Bounds.Height <= maxHeight) return;

            window.MinWidth = Math.Min(window.MinWidth, maxWidth);
            window.MinHeight = Math.Min(window.MinHeight, maxHeight);
            if (maximize)
            {
                window.WindowState = WindowState.Maximized;
                return;
            }
            window.Width = Math.Min(window.Width, maxWidth);
            window.Height = Math.Min(window.Height, maxHeight);
            window.Position = new PixelPoint(
                area.X + Math.Max(0, (int)((area.Width - window.Width * scale) / 2)),
                area.Y + Math.Max(0, (int)((area.Height - window.Height * scale) / 2)));
        };
    }
}
