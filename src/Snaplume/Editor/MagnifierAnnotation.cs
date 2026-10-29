using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Snaplume.Editor.Freezables;

namespace Snaplume.Editor;

/// <summary>Outline of a magnifier: the source area and its lens have the same shape.</summary>
public enum MagnifierShape { Circle, RoundedRect }

/// <summary>
/// Magnified copy of an area of the screenshot (<see cref="TwoPointAnnotation.Start"/> to
/// <see cref="TwoPointAnnotation.End"/>), shown in a lens placed elsewhere. The lens is the source
/// area enlarged <see cref="Zoom"/> times around <see cref="LensCenter"/>. It shows what is below it:
/// the base image and the annotations drawn before it (an opaque box hiding a secret hides it in the lens
/// too), except the other magnifiers; and every pixelated area of the document, wherever it is in the order,
/// so that a pixelated secret never shows up enlarged.
/// </summary>
public sealed class MagnifierAnnotation : TwoPointAnnotation
{
    public const double MinZoom = 1;
    public const double MaxZoom = 8;
    /// <summary>Zoom range reached by dragging the lens handle.</summary>
    public const double MinHandleZoom = 1.25;
    public const double MaxHandleZoom = 6;

    /// <summary>Handle that resizes the lens (after the 4 corners of the source area).</summary>
    public const int LensHandle = 4;

    private enum Part { All, Source, Lens }

    private Point? _anchor;
    private Part _moving;
    private Pen? _thinPen;

    public Point LensCenter { get; set; }
    public double Zoom { get; set; } = 2;
    /// <summary>Stored as "Shape", the name of the first versions.</summary>
    [JsonPropertyName("Shape")]
    public MagnifierShape LensShape { get; set; }
    /// <summary>Thin line from the source area to the lens.</summary>
    public bool Connector { get; set; } = true;

    public override EditorTool Kind => EditorTool.Magnifier;

    public override bool IsValid() =>
        base.IsValid() && IsValidPoint(LensCenter) && double.IsFinite(Zoom) && Zoom >= MinZoom && Zoom <= MaxZoom
        && Enum.IsDefined(LensShape);

    /// <summary>The lens: the source area enlarged around <see cref="LensCenter"/>.</summary>
    public Rect GetLensRect()
    {
        var source = GetRect();
        double w = source.Width * Zoom, h = source.Height * Zoom;
        return new Rect(LensCenter.X - w / 2, LensCenter.Y - h / 2, w, h);
    }

    private double ThinThickness => Math.Max(1, Thickness / 2);

    private Pen ThinPen()
    {
        var brush = MakeBrush();
        if (_thinPen == null || _thinPen.Thickness != ThinThickness || _thinPen.Brush != brush)
            _thinPen = Frozen(new Pen(brush, ThinThickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        return _thinPen;
    }

    /// <param name="scale">1 for the source area, <see cref="Zoom"/> for the lens: both get the same rounded corners, enlarged.</param>
    private Geometry Outline(Rect r, double scale)
    {
        if (LensShape == MagnifierShape.Circle) return new EllipseGeometry(r);
        var source = GetRect();
        double lensSide = Math.Min(source.Width, source.Height) * Zoom;
        double radius = Math.Min(lensSide / 2, 6 + lensSide * 0.08) / Zoom * scale;
        return new RectangleGeometry(r, radius, radius);
    }

    /// <summary>
    /// Places the lens next to the source area: to its right, else left, below or above,
    /// the first place where it fits inside <paramref name="area"/> (the image).
    /// </summary>
    public void PlaceLens(Rect area)
    {
        var source = GetRect();
        double w = source.Width * Zoom, h = source.Height * Zoom;
        double gap = Math.Max(12, Thickness * 3);
        var center = new Point(source.X + source.Width / 2, source.Y + source.Height / 2);
        var candidates = new[]
        {
            new Point(source.Right + gap + w / 2, center.Y),
            new Point(source.Left - gap - w / 2, center.Y),
            new Point(center.X, source.Bottom + gap + h / 2),
            new Point(center.X, source.Top - gap - h / 2),
        };
        if (area.IsEmpty)
        {
            LensCenter = candidates[0];
            return;
        }
        // Where the lens fits; else where most of it is visible.
        Point best = candidates[0];
        double bestVisible = -1;
        foreach (var c in candidates)
        {
            var lens = new Rect(c.X - w / 2, c.Y - h / 2, w, h);
            var visible = Rect.Intersect(lens, area);
            double amount = visible.IsEmpty ? 0 : visible.Width * visible.Height;
            if (area.Contains(lens))
            {
                best = c;
                break;
            }
            if (amount > bestVisible)
            {
                bestVisible = amount;
                best = c;
            }
        }
        // Kept inside the image as far as its size allows.
        double x = w >= area.Width ? area.X + area.Width / 2 : Math.Clamp(best.X, area.Left + w / 2, area.Right - w / 2);
        double y = h >= area.Height ? area.Y + area.Height / 2 : Math.Clamp(best.Y, area.Top + h / 2, area.Bottom - h / 2);
        LensCenter = new Point(x, y);
    }

    /// <summary>A circle magnifier needs a square area: the smallest one around the current area, same center.</summary>
    public void MakeSquare()
    {
        if (LensShape != MagnifierShape.Circle) return;
        var r = GetRect();
        if (Math.Abs(r.Width - r.Height) < 1e-6) return;
        double side = Math.Max(r.Width, r.Height);
        var center = new Point(r.X + r.Width / 2, r.Y + r.Height / 2);
        Start = new Point(center.X - side / 2, center.Y - side / 2);
        End = new Point(center.X + side / 2, center.Y + side / 2);
    }

    /// <summary>Alone (preset previews): the lens shows the base image only.</summary>
    public override void Render(DrawingContext dc, BitmapSource baseImage) =>
        Render(dc, baseImage, new Annotation[] { this }, 0);

    /// <summary>Draws the magnifier, number <paramref name="index"/> of <paramref name="annotations"/> (the document).</summary>
    /// <param name="skip">Annotation left out of the lens too (text being edited).</param>
    public void Render(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations, int index, Annotation? skip = null)
    {
        var source = GetRect();
        var lens = GetLensRect();
        var thin = ThinPen();
        var sourceOutline = Outline(source, 1);

        if (Connector && ConnectorPoints() is { } line) dc.DrawLine(thin, line.From, line.To);

        if (source.Width >= 1 && source.Height >= 1)
        {
            var lensOutline = Outline(lens, Zoom);
            dc.PushClip(lensOutline);
            // Under the parts of the area outside the image.
            dc.DrawRectangle(Brushes.White, null, lens);
            if (Content(baseImage, annotations, index, skip, source) is { } content)
            {
                // The area enlarged into the lens.
                dc.PushTransform(new MatrixTransform(Zoom, 0, 0, Zoom, lens.X - source.X * Zoom, lens.Y - source.Y * Zoom));
                dc.DrawDrawing(content);
                dc.Pop();
            }
            dc.Pop();
            dc.DrawGeometry(null, MakePen(), lensOutline);
        }
        dc.DrawGeometry(null, thin, sourceOutline);
    }

    /// <summary>
    /// What the lens shows, in image coordinates, clipped to the area (and to the image): the base image, the
    /// annotations below this one except the magnifiers, then every pixelated area. Null outside the image.
    /// </summary>
    private static DrawingGroup? Content(BitmapSource baseImage, IReadOnlyList<Annotation> annotations, int index, Annotation? skip, Rect source)
    {
        var image = new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight);
        // A pixel more around the area: the edges of the lens are smoothed from their neighbors.
        var area = source;
        area.Inflate(1, 1);
        area.Intersect(image);
        if (area.IsEmpty || area.Width <= 0 || area.Height <= 0) return null;

        var group = new DrawingGroup();
        RenderOptions.SetBitmapScalingMode(group, BitmapScalingMode.HighQuality);
        using (var g = group.Open())
        {
            g.PushClip(new RectangleGeometry(area));
            g.DrawImage(baseImage, image);
            AnnotationRenderer.Render(g, baseImage, annotations, 0, Math.Clamp(index, 0, annotations.Count), skip, withMagnifiers: false);
            // Every pixelated area is drawn last, those above the magnifier too: a pixelated secret never shows up enlarged.
            for (int i = 0; i < annotations.Count; i++)
            {
                if (annotations[i] is PixelateAnnotation pixelate && !ReferenceEquals(pixelate, skip)) pixelate.Render(g, baseImage);
            }
            g.Pop();
        }
        if (group.CanFreeze) group.Freeze();
        return group;
    }

    /// <summary>Ends of the connector, on the outlines of the area and of the lens; null when they overlap.</summary>
    private (Point From, Point To)? ConnectorPoints()
    {
        var source = GetRect();
        var lens = GetLensRect();
        var sourceCenter = new Point(source.X + source.Width / 2, source.Y + source.Height / 2);
        var v = LensCenter - sourceCenter;
        double distance = v.Length;
        if (distance < 1e-6) return null;
        v /= distance;
        double fromEdge = EdgeDistance(source, v), toEdge = EdgeDistance(lens, v);
        if (fromEdge + toEdge >= distance) return null;
        return (sourceCenter + v * fromEdge, LensCenter - v * toEdge);
    }

    /// <summary>Distance from the center of the outline to its edge in the direction <paramref name="v"/> (unit vector).</summary>
    private double EdgeDistance(Rect r, Vector v)
    {
        double a = Math.Max(r.Width / 2, 1e-6), b = Math.Max(r.Height / 2, 1e-6);
        if (LensShape == MagnifierShape.Circle) return 1 / Math.Sqrt(v.X * v.X / (a * a) + v.Y * v.Y / (b * b));
        double tx = Math.Abs(v.X) < 1e-9 ? double.MaxValue : a / Math.Abs(v.X);
        double ty = Math.Abs(v.Y) < 1e-9 ? double.MaxValue : b / Math.Abs(v.Y);
        return Math.Min(tx, ty);
    }

    public override Rect GetBounds()
    {
        var bounds = Inflate(GetRect(), ThinThickness / 2);
        bounds.Union(Inflate(GetLensRect(), Thickness / 2));
        return bounds;
    }

    public override bool HitTest(Point p, double tolerance) =>
        InLens(p, tolerance) || Inflate(GetRect(), ThinThickness / 2 + tolerance).Contains(p)
        || (Connector && ConnectorPoints() is { } line && DistanceToSegment(p, line.From, line.To) <= ThinThickness / 2 + tolerance);

    private bool InLens(Point p, double tolerance)
    {
        var lens = Inflate(GetLensRect(), Thickness / 2 + tolerance);
        if (!lens.Contains(p)) return false;
        if (LensShape != MagnifierShape.Circle) return true;
        double a = Math.Max(lens.Width / 2, 1), b = Math.Max(lens.Height / 2, 1);
        double dx = (p.X - LensCenter.X) / a, dy = (p.Y - LensCenter.Y) / b;
        return dx * dx + dy * dy <= 1;
    }

    /// <summary>Dragging the lens moves the lens only, dragging the area moves the area only.</summary>
    public override void BeginMove(Point p) =>
        _moving = InLens(p, 0) ? Part.Lens : Inflate(GetRect(), ThinThickness).Contains(p) ? Part.Source : Part.All;

    public override void Translate(Vector offset)
    {
        if (_moving != Part.Lens) base.Translate(offset);
        if (_moving != Part.Source) LensCenter += offset;
    }

    public override void Scale(double sx, double sy)
    {
        LensCenter = Scaled(LensCenter, sx, sy);
        base.Scale(sx, sy);
    }

    /// <summary>The 4 corners of the source area, then the lens handle (see <see cref="LensHandle"/>).</summary>
    public override IReadOnlyList<Point> GetHandles()
    {
        var r = GetRect();
        var lens = GetLensRect();
        var lensHandle = LensShape == MagnifierShape.Circle
            ? LensCenter + new Vector(lens.Width / 2, lens.Height / 2) / Math.Sqrt(2)
            : lens.BottomRight;
        return new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft, lensHandle };
    }

    public override void BeginHandleDrag(int index) =>
        _anchor = index < LensHandle ? GetHandles()[(index + 2) % 4] : null;

    public override void EndHandleDrag()
    {
        _anchor = null;
        _moving = Part.All;
    }

    public override void MoveHandle(int index, Point position)
    {
        var r = GetRect();
        if (index == LensHandle)
        {
            // The lens handle changes the zoom; the lens stays centered where it is.
            double w = Math.Max(r.Width, 1), h = Math.Max(r.Height, 1);
            // Circle: the handle is on the outline at 45°, Zoom × √(w² + h²) / (2√2) from the center.
            double zoom = LensShape == MagnifierShape.Circle
                ? 2 * Math.Sqrt(2) * (position - LensCenter).Length / Math.Sqrt(w * w + h * h)
                : Math.Max(2 * Math.Abs(position.X - LensCenter.X) / w, 2 * Math.Abs(position.Y - LensCenter.Y) / h);
            Zoom = Math.Clamp(zoom, MinHandleZoom, MaxHandleZoom);
            return;
        }
        var anchor = _anchor ?? GetHandles()[(index + 2) % 4];
        var v = position - anchor;
        if (LensShape == MagnifierShape.Circle)
        {
            // A circle keeps a square area.
            double side = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
            v = new Vector(v.X < 0 ? -side : side, v.Y < 0 ? -side : side);
        }
        Start = anchor;
        End = anchor + v;
    }

    public override Annotation Clone()
    {
        // Nothing else to reset: the lens keeps no copy of the image, the undo history holds no pixels.
        var copy = (MagnifierAnnotation)base.Clone();
        copy._anchor = null;
        copy._moving = Part.All;
        return copy;
    }
}
