using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using Snaplume.Capturing;
using Snaplume.Core;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>
/// Dashed red frame drawn just outside the recorded area. Click-through, and excluded from the recording when the
/// system allows it (elsewhere it stays outside the area, so it is not recorded either).
/// </summary>
/// <remarks>
/// Before the recording starts, the area can be moved: a small grip at the top of the frame (a window of its own, the
/// frame itself lets every click through to the desktop) is dragged, and the area follows, kept within the monitors.
/// </remarks>
public sealed class RecordingFrame : Window
{
    /// <summary>Physical pixels between the recorded area and the frame.</summary>
    private const int Gap = 4;

    private readonly IReadOnlyList<PixelRect> _monitors;
    private FrameGrip? _grip;
    private PixelRect _area;

    /// <summary>The user moved the area with the grip (absolute physical pixels).</summary>
    public event Action<PixelRect>? AreaChanged;

    /// <param name="area">Recorded area, absolute physical pixels.</param>
    /// <param name="monitors">Bounds of the monitors: the area can be moved within them; null for a fixed frame.</param>
    public RecordingFrame(PixelRect area, IReadOnlyList<PixelRect>? monitors = null)
    {
        _area = area;
        _monitors = monitors ?? Array.Empty<PixelRect>();
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

        Position = new PixelPoint(area.X - Gap, area.Y - Gap);
        Width = 10;
        Height = 10;
        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this, clickThrough: true);
            Place();
            if (monitors != null)
            {
                _grip = new FrameGrip(this);
                _grip.Show();
            }
        };
        // Sized in pixels from the scaling of the monitor it is on: moved to another one, it is sized again.
        ScalingChanged += (_, _) => Place();
        Closed += (_, _) => EndMoving();
    }

    /// <summary>Recorded area, absolute physical pixels.</summary>
    public PixelRect Area => _area;

    /// <summary>The grip is shown: the area can still be moved.</summary>
    public bool IsMovable => _grip != null;

    /// <summary>Moves or resizes the frame to a new area (another size chosen in the video options).</summary>
    public void SetArea(PixelRect area)
    {
        if (area == _area) return;
        _area = area;
        if (IsVisible) Place();
    }

    /// <summary>The recording starts: the area no longer moves, the grip goes away.</summary>
    public void EndMoving()
    {
        var grip = _grip;
        _grip = null;
        grip?.Close();
    }

    private void Place()
    {
        var bounds = new PixelRect(_area.X - Gap, _area.Y - Gap, _area.Width + 2 * Gap, _area.Height + 2 * Gap);
        double scaling = RenderScaling > 0 ? RenderScaling : 1;
        Position = new PixelPoint(bounds.X, bounds.Y);
        Width = bounds.Width / scaling;
        Height = bounds.Height / scaling;
        _grip?.Place();
    }

    /// <summary>Called by the grip while it is dragged: the area moved by (dx, dy) from where the drag started.</summary>
    private void DragTo(PixelRect start, int dx, int dy)
    {
        var moved = RecordingArea.Move(start, dx, dy, _monitors);
        if (moved == _area) return;
        _area = moved;
        Place();
        AreaChanged?.Invoke(moved);
    }

    /// <summary>The handle that moves the area, centered just inside the top edge of the frame.</summary>
    private sealed class FrameGrip : Window
    {
        private readonly RecordingFrame _frame;
        private PixelPoint? _dragStart;
        private PixelRect _dragArea;

        public FrameGrip(RecordingFrame frame)
        {
            _frame = frame;
            WindowDecorations = WindowDecorations.None;
            TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
            Background = Brushes.Transparent;
            CanResize = false;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            SizeToContent = SizeToContent.WidthAndHeight;
            WindowStartupLocation = WindowStartupLocation.Manual;
            FlowDirection = FlowDirection.LeftToRight;
            Title = "Snaplume";
            Position = new PixelPoint(frame._area.X, frame._area.Y);
            Cursor = new Cursor(StandardCursorType.SizeAll);
            ToolTip.SetTip(this, Loc.T("Recording.MoveAreaTip"));
            Content = new Border
            {
                Background = new SolidColorBrush(Color.FromArgb(0xF0, 0x1F, 0x1F, 0x1F)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(6),
                Padding = new Thickness(10, 2),
                Child = new Avalonia.Controls.Shapes.Path
                {
                    Data = (Geometry?)Application.Current?.FindResource("IconMove"),
                    Stroke = Brushes.White,
                    StrokeThickness = 1.8,
                    StrokeLineCap = PenLineCap.Round,
                    StrokeJoin = PenLineJoin.Round,
                    Width = 19.2,
                    Height = 19.2,
                    Stretch = Stretch.None,
                    RenderTransformOrigin = new RelativePoint(0, 0, RelativeUnit.Relative),
                    RenderTransform = new ScaleTransform(0.8, 0.8),
                },
            };
            Opened += (_, _) =>
            {
                FloatingBar.ApplyNativeStyles(this);
                Place();
            };
            SizeChanged += (_, _) => Place();
        }

        public void Place()
        {
            if (!IsVisible) return;
            double scaling = RenderScaling > 0 ? RenderScaling : 1;
            int width = (int)Math.Ceiling(ClientSize.Width * scaling), height = (int)Math.Ceiling(ClientSize.Height * scaling);
            // Inside the top edge: the bar may be above the area, and the grip is gone before the recording starts.
            var area = _frame._area;
            Position = new PixelPoint(area.X + (area.Width - width) / 2, area.Y + Gap);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            _dragStart = this.PointToScreen(e.GetPosition(this));
            _dragArea = _frame._area;
            e.Pointer.Capture(this);
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (_dragStart is not PixelPoint start) return;
            // Screen pixels: the grip itself moves under the pointer.
            var now = this.PointToScreen(e.GetPosition(this));
            _frame.DragTo(_dragArea, now.X - start.X, now.Y - start.Y);
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            _dragStart = null;
            e.Pointer.Capture(null);
        }

        protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
        {
            base.OnPointerCaptureLost(e);
            _dragStart = null;
        }
    }
}
