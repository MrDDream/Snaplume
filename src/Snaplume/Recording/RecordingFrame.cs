using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Snaplume.Capturing;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Recording;

/// <summary>
/// Dashed red frame drawn just outside the recorded area. Click-through and
/// excluded from the recording.
/// </summary>
public sealed class RecordingFrame : Window
{
    /// <summary>Physical pixels between the recorded area and the frame.</summary>
    private const int Gap = 4;

    public RecordingFrame(PixelRect area)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        FlowDirection = FlowDirection.LeftToRight; // placed in screen pixels, never mirrored
        Left = 0;
        Top = 0;
        Width = 10;
        Height = 10;
        Content = new Rectangle
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
            StrokeThickness = 2,
            StrokeDashArray = new DoubleCollection { 4, 2 },
        };

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // The frame may span monitors: keep its physical size when the DPI changes.
            IgnoreDpiChanges(this);
            AddExtendedStyle(hwnd, WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ExcludeFromCapture(hwnd);
            SetWindowPos(hwnd, HWND_TOPMOST, area.X - Gap, area.Y - Gap, area.Width + 2 * Gap, area.Height + 2 * Gap,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        };
    }
}
