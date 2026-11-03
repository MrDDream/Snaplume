using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Snaplume.Core;

namespace Snaplume.Capturing;

/// <summary>
/// Draws one monitor of the area selection over the frozen desktop image: dimming, hovered window / control,
/// rubber band, size label, crosshair, magnifier (pixel coordinates and #RRGGBB color) and help.
/// Everything is drawn in desktop-image pixels, scaled to the window: the monitors of one selection may have
/// different scalings, the pixels stay exact.
/// </summary>
internal sealed class SelectionOverlay : Control
{
    private const int MagnifierCells = 15;
    private const double MagnifierCellDip = 8;

    private static readonly IBrush DimBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x78, 0, 0, 0));
    private static readonly IBrush HoverBrush = new ImmutableSolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35));
    private static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6));
    private static readonly IBrush LabelBrush = new ImmutableSolidColorBrush(Color.FromArgb(0xD9, 0x11, 0x18, 0x27));
    private static readonly IBrush CrosshairBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF));

    private readonly RegionSelectorWindow.Session _session;
    private readonly PixelRect _monitor;

    /// <summary>Pens whose width depends on the zoom, rebuilt only when it changes.</summary>
    private double _penUnit = double.NaN;
    private IPen _gridPen = null!, _cellPen = null!, _boxPen = null!, _hoverPen = null!, _selectionPen = null!, _crosshairPen = null!;
    private bool _rightDown;
    /// <summary>The help line, laid out for <see cref="_helpUnit"/>.</summary>
    private FormattedText? _helpText;
    private double _helpUnit;

    /// <param name="monitor">The monitor this overlay shows, relative to the desktop image.</param>
    public SelectionOverlay(RegionSelectorWindow.Session session, PixelRect monitor)
    {
        _session = session;
        _monitor = monitor;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        // Pixels of the frozen screen stay sharp, whatever the scaling.
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    private SelectionModel Model => _session.Model;

    /// <summary>The pointer in desktop-image pixels: exact on every monitor, whatever its scaling.</summary>
    private void TrackPointer(PointerEventArgs e)
    {
        var screen = this.PointToScreen(e.GetPosition(this));
        Model.MoveTo(screen.X - _session.Desktop.Bounds.X, screen.Y - _session.Desktop.Bounds.Y);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        TrackPointer(e);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        TrackPointer(e);
        var point = e.GetCurrentPoint(this);
        if (point.Properties.IsLeftButtonPressed)
        {
            Model.Press();
            // Keeps receiving the moves when the drag crosses onto another monitor's window.
            e.Pointer.Capture(this);
        }
        else if (point.Properties.IsRightButtonPressed)
        {
            _rightDown = true;
        }
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        TrackPointer(e);
        if (e.InitialPressMouseButton == MouseButton.Left)
        {
            e.Pointer.Capture(null);
            Model.Release();
        }
        else if (e.InitialPressMouseButton == MouseButton.Right && _rightDown)
        {
            _rightDown = false;
            Model.RightClick();
        }
        e.Handled = true;
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Model.Wheel(Math.Sign(e.Delta.Y))) e.Handled = true;
    }

    public override void Render(DrawingContext context)
    {
        var size = Bounds.Size;
        // Hit testing needs a painted surface: clicks inside the undimmed area must not be lost.
        context.FillRectangle(Brushes.Transparent, new Rect(size));
        if (size.Width <= 0 || size.Height <= 0) return;

        double scaleX = size.Width / _monitor.Width, scaleY = size.Height / _monitor.Height;
        double u = 1 / scaleX; // desktop pixels per DIP, for interface sizes
        UpdatePens(u);
        // From desktop-image pixels to this window: this monitor's part at the origin, then scaled.
        using var transform = context.PushTransform(Matrix.CreateTranslation(-_monitor.X, -_monitor.Y) * Matrix.CreateScale(scaleX, scaleY));

        var monitorRect = _monitor.ToRect();
        if (_session.Bitmap is { } bitmap) context.DrawImage(bitmap, monitorRect, monitorRect);
        if (!Model.HasPosition) return;

        var focus = Model.Focus;
        DrawDim(context, monitorRect, focus);
        // The crosshair, the magnifier and the help follow the mouse: drawn by the window of its monitor only.
        bool underCursor = Model.MonitorAt(Model.X, Model.Y) == _monitor;
        if (underCursor && !Model.IsDragging) DrawCrosshair(context);

        if (focus is PixelRect f)
        {
            var pen = Model.IsDragging ? _selectionPen : _hoverPen;
            double t = pen.Thickness;
            var r = f.ToRect();
            if (r.Width > t && r.Height > t) r = r.Deflate(t / 2);
            context.DrawRectangle(null, pen, r);
            DrawSizeLabel(context, f, u);
        }

        if (!underCursor) return;
        DrawMagnifier(context, u);
        DrawHelp(context, u);
    }

    private void UpdatePens(double u)
    {
        if (u == _penUnit) return;
        _penUnit = u;
        double t = Math.Max(1, Math.Round(2 * u));
        _hoverPen = new ImmutablePen(HoverBrush.ToImmutable(), t);
        _selectionPen = new ImmutablePen(SelectionBrush.ToImmutable(), t);
        _gridPen = new ImmutablePen(CrosshairBrush.ToImmutable(), u);
        _cellPen = new ImmutablePen(Brushes.White.ToImmutable(), u);
        _boxPen = new ImmutablePen(Brushes.White.ToImmutable(), 1.5 * u);
        _crosshairPen = new ImmutablePen(CrosshairBrush.ToImmutable(), u);
    }

    /// <summary>Dims this monitor except the focused area (four bands around it).</summary>
    private static void DrawDim(DrawingContext context, Rect monitor, PixelRect? focus)
    {
        if (focus is not PixelRect f)
        {
            context.FillRectangle(DimBrush, monitor);
            return;
        }
        var hole = f.ToRect().Intersect(monitor);
        if (hole.Width <= 0 || hole.Height <= 0)
        {
            context.FillRectangle(DimBrush, monitor);
            return;
        }
        context.FillRectangle(DimBrush, new Rect(monitor.X, monitor.Y, monitor.Width, hole.Y - monitor.Y));
        context.FillRectangle(DimBrush, new Rect(monitor.X, hole.Bottom, monitor.Width, monitor.Bottom - hole.Bottom));
        context.FillRectangle(DimBrush, new Rect(monitor.X, hole.Y, hole.X - monitor.X, hole.Height));
        context.FillRectangle(DimBrush, new Rect(hole.Right, hole.Y, monitor.Right - hole.Right, hole.Height));
    }

    private void DrawCrosshair(DrawingContext context)
    {
        var mon = Model.MonitorAt(Model.X, Model.Y);
        context.DrawLine(_crosshairPen, new Point(mon.X, Model.Y + 0.5), new Point(mon.Right, Model.Y + 0.5));
        context.DrawLine(_crosshairPen, new Point(Model.X + 0.5, mon.Y), new Point(Model.X + 0.5, mon.Bottom));
    }

    private void DrawSizeLabel(DrawingContext context, PixelRect r, double u)
    {
        var text = MakeText(FormattableString.Invariant($"{r.Width} × {r.Height}"), 12 * u);
        double pad = 4 * u;
        var size = new Size(text.Width + 2 * pad, text.Height + 2 * pad);
        var mon = Model.MonitorAt(r.X, r.Y);
        double y = r.Y - size.Height - 4 * u;
        if (y < mon.Y) y = r.Y + 4 * u;
        double x = Math.Max(mon.X, Math.Min(r.X, mon.Right - size.Width));
        DrawTag(context, text, new Rect(new Point(x, y), size), pad, u);
    }

    private void DrawMagnifier(DrawingContext context, double u)
    {
        double cell = MagnifierCellDip * u;
        double size = MagnifierCells * cell;
        var image = _session.Desktop.Image;
        var cells = CaptureGeometry.MagnifierPixels(image, Model.X, Model.Y, MagnifierCells);
        int half = MagnifierCells / 2;
        int center = (half * MagnifierCells + half) * 4;
        var color = Color.FromRgb(cells[center + 2], cells[center + 1], cells[center]);
        var hex = CaptureGeometry.HexColor(image, Model.X, Model.Y);
        var infoText = MakeText(
            FormattableString.Invariant($"{_session.Desktop.Bounds.X + Model.X}, {_session.Desktop.Bounds.Y + Model.Y}\n{hex}"), 11 * u);
        double infoHeight = infoText.Height + 8 * u;

        var mon = Model.MonitorAt(Model.X, Model.Y);
        double gap = 24 * u;
        double x = Model.X + gap, y = Model.Y + gap;
        if (x + size > mon.Right) x = Model.X - gap - size;
        if (y + size + infoHeight > mon.Bottom) y = Model.Y - gap - size - infoHeight;

        var box = new Rect(x, y, size, size);
        context.FillRectangle(Brushes.Black, box.Inflate(2 * u));
        for (int row = 0; row < MagnifierCells; row++)
        {
            for (int column = 0; column < MagnifierCells; column++)
            {
                int i = (row * MagnifierCells + column) * 4;
                var brush = new ImmutableSolidColorBrush(Color.FromRgb(cells[i + 2], cells[i + 1], cells[i]));
                // A hair larger than the cell: no seams between cells at fractional scalings.
                context.FillRectangle(brush, new Rect(x + column * cell, y + row * cell, cell + 0.5 * u, cell + 0.5 * u));
            }
        }

        context.DrawLine(_gridPen, new Point(x, y + (half + 0.5) * cell), new Point(x + size, y + (half + 0.5) * cell));
        context.DrawLine(_gridPen, new Point(x + (half + 0.5) * cell, y), new Point(x + (half + 0.5) * cell, y + size));
        context.DrawRectangle(null, _cellPen, new Rect(x + half * cell, y + half * cell, cell, cell));
        context.DrawRectangle(null, _boxPen, box);

        var info = new Rect(x, y + size + 2 * u, size, infoHeight);
        context.FillRectangle(LabelBrush, info);
        double swatch = 12 * u;
        context.DrawRectangle(new ImmutableSolidColorBrush(color), _cellPen,
            new Rect(info.Right - swatch - 6 * u, info.Bottom - swatch - 6 * u, swatch, swatch));
        context.DrawText(infoText, new Point(info.X + 6 * u, info.Y + 4 * u));
    }

    private void DrawHelp(DrawingContext context, double u)
    {
        // The same text at each mouse move: laid out again only when the scaling changes.
        if (_helpText == null || _helpUnit != u)
        {
            _helpText = MakeText((_session.Title != null ? _session.Title + "   •   " : "") + Loc.T("Selection.Help"), 12 * u);
            _helpUnit = u;
        }
        var text = _helpText;
        double pad = 8 * u;
        var mon = Model.MonitorAt(Model.X, Model.Y);
        var size = new Size(text.Width + 2 * pad, text.Height + 2 * pad);
        if (size.Width > mon.Width - 20 * u) return;
        var rect = new Rect(mon.X + (mon.Width - size.Width) / 2, mon.Y + 16 * u, size.Width, size.Height);
        if (rect.Contains(new Point(Model.X, Model.Y)) || (Model.Focus is PixelRect f && Model.IsDragging && rect.Intersects(f.ToRect())))
        {
            rect = rect.WithY(mon.Bottom - size.Height - 16 * u);
        }
        DrawTag(context, text, rect, pad, u);
    }

    private static void DrawTag(DrawingContext context, FormattedText text, Rect rect, double pad, double u)
    {
        context.DrawRectangle(LabelBrush, null, rect, 4 * u, 4 * u);
        context.DrawText(text, new Point(rect.X + pad, rect.Y + pad));
    }

    private static FormattedText MakeText(string text, double size) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, Typeface.Default, size, Brushes.White);
}
