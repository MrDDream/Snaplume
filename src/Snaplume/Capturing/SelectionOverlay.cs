using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Capturing;

/// <summary>
/// Draws the region selection UI over the frozen screenshot: dimming, hovered
/// window/control highlight, rubber band, size label, magnifier and help.
/// All drawing happens in screenshot pixel space.
/// </summary>
public sealed class SelectionOverlay : FrameworkElement
{
    private const int MagnifierCells = 15;
    private const double MagnifierCellDip = 8;

    private static readonly Brush DimBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x78, 0, 0, 0)));
    private static readonly Brush HoverBrush = Frozen(new SolidColorBrush(Color.FromRgb(0xE5, 0x39, 0x35)));
    private static readonly Brush SelectionBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x29, 0xB6, 0xF6)));
    private static readonly Brush LabelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xD9, 0x11, 0x18, 0x27)));
    private static readonly Brush CrosshairBrush = Frozen(new SolidColorBrush(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF)));
    private static readonly Typeface UiFont = new("Segoe UI");
    private static readonly Pen CrosshairPen = Frozen(new Pen(CrosshairBrush, 1));

    private readonly ScreenSnapshot _snap;

    /// <summary>Pens whose width depends on the zoom, rebuilt only when it changes.</summary>
    private double _penUnit = double.NaN;
    private Pen _gridPen = null!, _cellPen = null!, _boxPen = null!, _hoverPen = null!, _selectionPen = null!;

    private int _mx = -1, _my = -1;
    private bool _down, _dragging;
    private int _sx, _sy;
    private IReadOnlyList<PixelRect> _candidates = Array.Empty<PixelRect>();
    private int _level;

    public event Action<PixelRect>? Completed;
    public event Action? Cancelled;

    private readonly string? _title;

    /// <param name="title">Optional prefix of the help line, e.g. "Vidéo".</param>
    public SelectionOverlay(ScreenSnapshot snap, string? title = null)
    {
        _snap = snap;
        _title = title;

        Focusable = true;
        FocusVisualStyle = null;
        Cursor = Cursors.Cross;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SizeChanged += (_, _) => InvalidateVisual();
    }

    private int W => _snap.Image.PixelWidth;
    private int H => _snap.Image.PixelHeight;
    private PixelRect? Hover => _candidates.Count == 0 ? null : _candidates[Math.Clamp(_level, 0, _candidates.Count - 1)];

    /// <summary>Reads the real cursor position: exact in physical pixels whatever the DPI.</summary>
    public void UpdateMouse()
    {
        var (x, y) = _snap.CursorPosition();
        x = Math.Clamp(x, 0, W - 1);
        y = Math.Clamp(y, 0, H - 1);
        if (x == _mx && y == _my && _candidates.Count > 0) return;
        _mx = x;
        _my = y;
        if (_down && !_dragging && (Math.Abs(_mx - _sx) >= 3 || Math.Abs(_my - _sy) >= 3)) _dragging = true;
        if (!_down) _candidates = _snap.Windows.FindAt(_mx, _my);
        InvalidateVisual();
    }

    protected override void OnMouseMove(MouseEventArgs e) => UpdateMouse();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        UpdateMouse();
        _down = true;
        _sx = _mx;
        _sy = _my;
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (!_down) return;
        UpdateMouse();
        _down = false;
        ReleaseMouseCapture();
        e.Handled = true;

        if (_dragging)
        {
            _dragging = false;
            var rect = PixelRect.FromCorners(_sx, _sy, _mx, _my);
            if (rect.Width >= 2 && rect.Height >= 2) Completed?.Invoke(rect);
            else InvalidateVisual();
        }
        else if (Hover is PixelRect hover)
        {
            Completed?.Invoke(hover);
        }
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_down)
        {
            _down = _dragging = false;
            ReleaseMouseCapture();
            _candidates = _snap.Windows.FindAt(_mx, _my);
            InvalidateVisual();
        }
        else
        {
            Cancelled?.Invoke();
        }
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_down || _candidates.Count == 0) return;
        _level = Math.Clamp(Math.Clamp(_level, 0, _candidates.Count - 1) + (e.Delta > 0 ? 1 : -1), 0, _candidates.Count - 1);
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        int step = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift) ? 10 : 1;
        switch (e.Key)
        {
            case Key.Escape: Cancelled?.Invoke(); break;
            case Key.Enter when Hover is PixelRect hover: Completed?.Invoke(hover); break;
            case Key.F: Completed?.Invoke(_snap.MonitorAt(_mx, _my)); break;
            case Key.A: Completed?.Invoke(new PixelRect(0, 0, W, H)); break;
            case Key.Left: MoveCursor(-step, 0); break;
            case Key.Right: MoveCursor(step, 0); break;
            case Key.Up: MoveCursor(0, -step); break;
            case Key.Down: MoveCursor(0, step); break;
            default: return;
        }
        e.Handled = true;
    }

    private void MoveCursor(int dx, int dy)
    {
        GetCursorPos(out var p);
        SetCursorPos(p.X + dx, p.Y + dy);
        UpdateMouse();
    }

    protected override void OnRender(DrawingContext dc)
    {
        // WPF only routes mouse input to painted pixels: paint the whole surface (transparent)
        // so that clicks inside the undimmed area are not lost.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (ActualWidth <= 0 || _mx < 0) return;

        double scale = ActualWidth / W; // DIP per screenshot pixel
        double u = 1 / scale;           // screenshot pixels per DIP, for UI sizes
        UpdatePens(u);
        dc.PushTransform(new ScaleTransform(scale, ActualHeight / H));

        PixelRect? focus = _dragging ? PixelRect.FromCorners(_sx, _sy, _mx, _my) : Hover;

        var dim = new GeometryGroup { FillRule = FillRule.EvenOdd };
        dim.Children.Add(new RectangleGeometry(new Rect(0, 0, W, H)));
        if (focus is PixelRect f) dim.Children.Add(new RectangleGeometry(f.ToRect()));
        dc.DrawGeometry(DimBrush, null, dim);

        if (!_dragging) DrawCrosshair(dc);

        if (focus is PixelRect fr)
        {
            var pen = _dragging ? _selectionPen : _hoverPen;
            double t = pen.Thickness;
            var r = fr.ToRect();
            if (r.Width > t && r.Height > t) r.Inflate(-t / 2, -t / 2);
            dc.DrawRectangle(null, pen, r);
            DrawSizeLabel(dc, fr, u);
        }

        DrawMagnifier(dc, u);
        DrawHelp(dc, u);
        dc.Pop();
    }

    private void UpdatePens(double u)
    {
        if (u == _penUnit) return;
        _penUnit = u;
        double t = Math.Max(1, Math.Round(2 * u));
        _hoverPen = Frozen(new Pen(HoverBrush, t));
        _selectionPen = Frozen(new Pen(SelectionBrush, t));
        _gridPen = Frozen(new Pen(CrosshairBrush, u));
        _cellPen = Frozen(new Pen(Brushes.White, u));
        _boxPen = Frozen(new Pen(Brushes.White, 1.5 * u));
    }

    private void DrawCrosshair(DrawingContext dc)
    {
        var mon = _snap.MonitorAt(_mx, _my);
        dc.DrawLine(CrosshairPen, new Point(mon.X, _my + 0.5), new Point(mon.Right, _my + 0.5));
        dc.DrawLine(CrosshairPen, new Point(_mx + 0.5, mon.Y), new Point(_mx + 0.5, mon.Bottom));
    }

    private void DrawSizeLabel(DrawingContext dc, PixelRect r, double u)
    {
        var text = MakeText(FormattableString.Invariant($"{r.Width} × {r.Height}"), 12 * u, Brushes.White);
        double pad = 4 * u;
        var size = new Size(text.Width + 2 * pad, text.Height + 2 * pad);
        var mon = _snap.MonitorAt(r.X, r.Y);
        double y = r.Y - size.Height - 4 * u;
        if (y < mon.Y) y = r.Y + 4 * u;
        double x = Math.Max(mon.X, Math.Min(r.X, mon.Right - size.Width));
        DrawTag(dc, text, new Rect(new Point(x, y), size), pad, u);
    }

    private void DrawMagnifier(DrawingContext dc, double u)
    {
        double cell = MagnifierCellDip * u;
        double size = MagnifierCells * cell;
        var zoom = ReadPixels(_mx, _my);
        var color = PixelColor(zoom, MagnifierCells / 2, MagnifierCells / 2);
        var infoText = MakeText(
            FormattableString.Invariant($"{_snap.Bounds.X + _mx}, {_snap.Bounds.Y + _my}\n#{color.R:X2}{color.G:X2}{color.B:X2}"),
            11 * u, Brushes.White);
        double infoHeight = infoText.Height + 8 * u;

        var mon = _snap.MonitorAt(_mx, _my);
        double gap = 24 * u;
        double x = _mx + gap, y = _my + gap;
        if (x + size > mon.Right) x = _mx - gap - size;
        if (y + size + infoHeight > mon.Bottom) y = _my - gap - size - infoHeight;

        var box = new Rect(x, y, size, size);
        dc.DrawRectangle(Brushes.Black, null, Inflated(box, 2 * u));
        dc.DrawImage(BitmapSource.Create(MagnifierCells, MagnifierCells, 96, 96, PixelFormats.Bgr32, null, zoom, MagnifierCells * 4), box);

        int half = MagnifierCells / 2;
        dc.DrawLine(_gridPen, new Point(x, y + (half + 0.5) * cell), new Point(x + size, y + (half + 0.5) * cell));
        dc.DrawLine(_gridPen, new Point(x + (half + 0.5) * cell, y), new Point(x + (half + 0.5) * cell, y + size));
        dc.DrawRectangle(null, _cellPen, new Rect(x + half * cell, y + half * cell, cell, cell));
        dc.DrawRectangle(null, _boxPen, box);

        var info = new Rect(x, y + size + 2 * u, size, infoHeight);
        dc.DrawRectangle(LabelBrush, null, info);
        double swatch = 12 * u;
        dc.DrawRectangle(new SolidColorBrush(color), _cellPen,
            new Rect(info.Right - swatch - 6 * u, info.Bottom - swatch - 6 * u, swatch, swatch));
        dc.DrawText(infoText, new Point(info.X + 6 * u, info.Y + 4 * u));
    }

    private void DrawHelp(DrawingContext dc, double u)
    {
        var text = MakeText(
            (_title != null ? _title + "   •   " : "") + Loc.T("Selection.Help"),
            12 * u, Brushes.White);
        double pad = 8 * u;
        var mon = _snap.MonitorAt(_mx, _my);
        var size = new Size(text.Width + 2 * pad, text.Height + 2 * pad);
        if (size.Width > mon.Width - 20 * u) return;
        var rect = new Rect(mon.X + (mon.Width - size.Width) / 2, mon.Y + 16 * u, size.Width, size.Height);
        if (rect.Contains(new Point(_mx, _my)) || (_dragging && rect.IntersectsWith(PixelRect.FromCorners(_sx, _sy, _mx, _my).ToRect())))
        {
            rect.Y = mon.Bottom - size.Height - 16 * u;
        }
        DrawTag(dc, text, rect, pad, u);
    }

    /// <summary>
    /// The MagnifierCells × MagnifierCells pixels around a point, as Bgr32, read from the screenshot
    /// on demand (no full copy of it is kept); outside the screen they are dark gray.
    /// </summary>
    private byte[] ReadPixels(int cx, int cy)
    {
        const int n = MagnifierCells;
        int half = n / 2;
        var buffer = new byte[n * n * 4];
        for (int i = 0; i < buffer.Length; i += 4)
        {
            buffer[i] = buffer[i + 1] = buffer[i + 2] = 40;
            buffer[i + 3] = 255;
        }
        var area = new PixelRect(cx - half, cy - half, n, n).Intersect(new PixelRect(0, 0, W, H));
        if (area.IsEmpty) return buffer;

        int stride = area.Width * 4;
        var pixels = new byte[stride * area.Height];
        _snap.Image.CopyPixels(area.ToInt32Rect(), pixels, stride, 0);
        for (int row = 0; row < area.Height; row++)
        {
            int target = ((area.Y - (cy - half) + row) * n + (area.X - (cx - half))) * 4;
            Buffer.BlockCopy(pixels, row * stride, buffer, target, stride);
        }
        // Bgr32 leaves the fourth byte undefined: keep the magnifier opaque.
        for (int i = 3; i < buffer.Length; i += 4) buffer[i] = 255;
        return buffer;
    }

    private static Color PixelColor(byte[] bgr32, int x, int y)
    {
        int i = (y * MagnifierCells + x) * 4;
        return Color.FromRgb(bgr32[i + 2], bgr32[i + 1], bgr32[i]);
    }

    private void DrawTag(DrawingContext dc, FormattedText text, Rect rect, double pad, double u)
    {
        dc.DrawRoundedRectangle(LabelBrush, null, rect, 4 * u, 4 * u);
        dc.DrawText(text, new Point(rect.X + pad, rect.Y + pad));
    }

    private FormattedText MakeText(string text, double size, Brush brush) =>
        new(text, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, UiFont, size, brush,
            VisualTreeHelper.GetDpi(this).PixelsPerDip);

    private static Rect Inflated(Rect r, double d)
    {
        r.Inflate(d, d);
        return r;
    }

    private static T Frozen<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
