using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;
using Snaplume.Capturing;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>
/// Dashed red frame drawn just outside the recorded area. Click-through, and excluded from the recording when the
/// system allows it (elsewhere it stays outside the area, so it is not recorded either).
/// </summary>
public sealed class RecordingFrame : Window
{
    /// <summary>Physical pixels between the recorded area and the frame.</summary>
    private const int Gap = 4;

    public RecordingFrame(PixelRect area)
    {
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        IsHitTestVisible = false;
        WindowStartupLocation = WindowStartupLocation.Manual;
        FlowDirection = FlowDirection.LeftToRight; // placed in screen pixels, never mirrored
        Title = "Snaplume";
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            StrokeThickness = 2,
            StrokeDashArray = new Avalonia.Collections.AvaloniaList<double> { 4, 2 },
        };

        var bounds = new PixelRect(area.X - Gap, area.Y - Gap, area.Width + 2 * Gap, area.Height + 2 * Gap);
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = 10;
        Height = 10;
        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this, clickThrough: true);
            // Sized in pixels from the scaling of the monitor it opened on.
            double scaling = RenderScaling > 0 ? RenderScaling : 1;
            Width = bounds.Width / scaling;
            Height = bounds.Height / scaling;
            Position = new PixelPoint(bounds.X, bounds.Y);
        };
    }
}
