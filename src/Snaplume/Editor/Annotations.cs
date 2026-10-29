using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Snaplume.Editor.Freezables;

namespace Snaplume.Editor;

/// <summary>
/// Vector annotation drawn over the base image. Coordinates are image pixels.
/// Annotations stay editable: they are saved next to the capture (see <see cref="EditorDocument"/>).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(ArrowAnnotation), "arrow")]
[JsonDerivedType(typeof(LineAnnotation), "line")]
[JsonDerivedType(typeof(RectangleAnnotation), "rectangle")]
[JsonDerivedType(typeof(EllipseAnnotation), "ellipse")]
[JsonDerivedType(typeof(HighlightAnnotation), "highlight")]
[JsonDerivedType(typeof(PixelateAnnotation), "pixelate")]
[JsonDerivedType(typeof(SpotlightAnnotation), "spotlight")]
[JsonDerivedType(typeof(TextAnnotation), "text")]
[JsonDerivedType(typeof(StepAnnotation), "step")]
[JsonDerivedType(typeof(FreehandAnnotation), "freehand")]
[JsonDerivedType(typeof(StampAnnotation), "stamp")]
[JsonDerivedType(typeof(MagnifierAnnotation), "magnifier")]
public abstract class Annotation
{
    /// <summary>Coordinates beyond this are rejected when reading a sidecar file.</summary>
    protected const double MaxCoordinate = 1e6;
    public const double MinThickness = 1;
    public const double MaxThickness = 500;

    // Frozen brush and pen reused while the color and thickness do not change.
    private SolidColorBrush? _brush;
    private Pen? _pen;

    public Color Color { get; set; } = Colors.Red;
    public double Thickness { get; set; } = 4;

    /// <summary>Tool that draws this kind of annotation (not stored, see <see cref="AnnotationJson"/>).</summary>
    public abstract EditorTool Kind { get; }

    public abstract void Render(DrawingContext dc, BitmapSource baseImage);

    public abstract Rect GetBounds();

    public virtual bool HitTest(Point p, double tolerance) => Inflate(GetBounds(), tolerance).Contains(p);

    public abstract void Translate(Vector offset);

    /// <summary>Follows an image resize: positions scale per axis, sizes by the mean factor.</summary>
    public virtual void Scale(double sx, double sy) =>
        Thickness = Math.Clamp(Thickness * (sx + sy) / 2, MinThickness, MaxThickness);

    protected static Point Scaled(Point p, double sx, double sy) => new(p.X * sx, p.Y * sy);

    /// <summary>
    /// False when the values read from a sidecar file cannot be used (missing, not finite, absurd sizes,
    /// unknown enum values): such an annotation is dropped instead of breaking the editor.
    /// </summary>
    public virtual bool IsValid() =>
        double.IsFinite(Thickness) && Thickness >= 0.5 && Thickness <= MaxThickness;

    protected static bool IsValidPoint(Point p) =>
        double.IsFinite(p.X) && double.IsFinite(p.Y) && Math.Abs(p.X) <= MaxCoordinate && Math.Abs(p.Y) <= MaxCoordinate;

    /// <summary>Resize handles; empty when the annotation can only be moved.</summary>
    public virtual IReadOnlyList<Point> GetHandles() => Array.Empty<Point>();

    /// <summary>
    /// Called when the user starts dragging the annotation itself at <paramref name="p"/>, before the
    /// <see cref="Translate"/> calls; <see cref="EndHandleDrag"/> ends it. An annotation made of separate
    /// parts may then move only the part under the mouse.
    /// </summary>
    public virtual void BeginMove(Point p)
    {
    }

    /// <summary>Called when the user starts dragging a handle, before the <see cref="MoveHandle"/> calls.</summary>
    public virtual void BeginHandleDrag(int index)
    {
    }

    public virtual void EndHandleDrag()
    {
    }

    public virtual void MoveHandle(int index, Point position)
    {
    }

    public virtual Annotation Clone() => (Annotation)MemberwiseClone();

    protected SolidColorBrush MakeBrush(byte? alpha = null)
    {
        var color = alpha is byte a ? Color.FromArgb(a, Color.R, Color.G, Color.B) : Color;
        if (_brush == null || _brush.Color != color) _brush = Frozen(new SolidColorBrush(color));
        return _brush;
    }

    protected Pen MakePen(double? thickness = null)
    {
        double t = thickness ?? Thickness;
        if (_pen == null || _pen.Thickness != t || ((SolidColorBrush)_pen.Brush).Color != Color)
        {
            _pen = Frozen(new Pen(MakeBrush(), t)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
                LineJoin = PenLineJoin.Round,
            });
        }
        return _pen;
    }

    protected static bool IsLight(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B > 160;

    protected static Rect Inflate(Rect r, double amount)
    {
        r.Inflate(amount, amount);
        return r;
    }

    protected static double DistanceToSegment(Point p, Point a, Point b)
    {
        var ab = b - a;
        double lengthSquared = ab.LengthSquared;
        if (lengthSquared < 1e-9) return (p - a).Length;
        double t = Math.Clamp(Vector.Multiply(p - a, ab) / lengthSquared, 0, 1);
        return (p - (a + t * ab)).Length;
    }
}

/// <summary>Annotation defined by two points (line, arrow) or a box between them.</summary>
public abstract class TwoPointAnnotation : Annotation
{
    public Point Start { get; set; }
    public Point End { get; set; }

    public Rect GetRect() => new(Start, End);

    public override Rect GetBounds() => GetRect();

    public override bool IsValid() => base.IsValid() && IsValidPoint(Start) && IsValidPoint(End);

    public override void Translate(Vector offset)
    {
        Start += offset;
        End += offset;
    }

    public override void Scale(double sx, double sy)
    {
        Start = Scaled(Start, sx, sy);
        End = Scaled(End, sx, sy);
        base.Scale(sx, sy);
    }
}

public class LineAnnotation : TwoPointAnnotation
{
    public override EditorTool Kind => EditorTool.Line;

    public override void Render(DrawingContext dc, BitmapSource baseImage) => dc.DrawLine(MakePen(), Start, End);

    public override Rect GetBounds() => Inflate(GetRect(), Thickness / 2);

    public override bool HitTest(Point p, double tolerance) =>
        DistanceToSegment(p, Start, End) <= Thickness / 2 + tolerance;

    public override IReadOnlyList<Point> GetHandles() => new[] { Start, End };

    public override void MoveHandle(int index, Point position)
    {
        if (index == 0) Start = position;
        else End = position;
    }
}

public enum ArrowHeads { End, Both, None }

public sealed class ArrowAnnotation : LineAnnotation
{
    private Pen? _headPen;

    public ArrowHeads Heads { get; set; }

    public override EditorTool Kind => EditorTool.Arrow;

    public override bool IsValid() => base.IsValid() && Enum.IsDefined(Heads);

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        if (Heads == ArrowHeads.None)
        {
            base.Render(dc, baseImage);
            return;
        }
        var v = End - Start;
        double length = v.Length;
        if (length < 1) return;
        v /= length;

        bool both = Heads == ArrowHeads.Both;
        double headLength = Math.Min(HeadLength, both ? length / 2 : length);
        // Shaft stops inside the heads so its round caps do not stick out of the tips.
        double inset = headLength - Math.Min(headLength * 0.5, Thickness);
        dc.DrawLine(MakePen(), both ? Start + v * inset : Start, End - v * inset);
        DrawHead(dc, End, v, headLength);
        if (both) DrawHead(dc, Start, -v, headLength);
    }

    private void DrawHead(DrawingContext dc, Point tip, Vector direction, double headLength)
    {
        var normal = new Vector(-direction.Y, direction.X);
        var headBase = tip - direction * headLength;
        double headHalfWidth = headLength * 0.6;
        var head = new StreamGeometry();
        using (var ctx = head.Open())
        {
            ctx.BeginFigure(tip, isFilled: true, isClosed: true);
            ctx.LineTo(headBase + normal * headHalfWidth, true, true);
            ctx.LineTo(headBase - normal * headHalfWidth, true, true);
        }
        head.Freeze();
        var brush = MakeBrush();
        // Thin outline with round joins softens the tips.
        if (_headPen == null || _headPen.Brush != brush) _headPen = Frozen(new Pen(brush, 1) { LineJoin = PenLineJoin.Round });
        dc.DrawGeometry(brush, _headPen, head);
    }

    public override Rect GetBounds() => Inflate(GetRect(), HeadLength * 0.6);

    private double HeadLength => Math.Max(16, Thickness * 5);
}

/// <summary>Box shape with 4 corner handles.</summary>
public abstract class BoxAnnotation : TwoPointAnnotation
{
    /// <summary>Corner that stays in place while a handle is dragged, even past it.</summary>
    private Point? _anchor;

    public override IReadOnlyList<Point> GetHandles()
    {
        var r = GetRect();
        return new[] { r.TopLeft, r.TopRight, r.BottomRight, r.BottomLeft };
    }

    public override void BeginHandleDrag(int index) => _anchor = GetHandles()[(index + 2) % 4];

    public override void EndHandleDrag() => _anchor = null;

    public override Annotation Clone()
    {
        var copy = (BoxAnnotation)base.Clone();
        copy._anchor = null;
        return copy;
    }

    /// <summary>Opposite corner of the dragged handle.</summary>
    protected Point Anchor(int index) => _anchor ?? GetHandles()[(index + 2) % 4];

    public override void MoveHandle(int index, Point position)
    {
        Start = Anchor(index);
        End = position;
    }
}

public sealed class RectangleAnnotation : BoxAnnotation
{
    public bool Filled { get; set; }

    public override EditorTool Kind => EditorTool.Rectangle;

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        var r = GetRect();
        if (Filled)
        {
            dc.DrawRectangle(MakeBrush(), null, r);
        }
        else
        {
            if (r.Width > Thickness && r.Height > Thickness) r.Inflate(-Thickness / 2, -Thickness / 2);
            dc.DrawRectangle(null, MakePen(), r);
        }
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var r = GetRect();
        if (!Inflate(r, tolerance).Contains(p)) return false;
        if (Filled) return true;
        var inner = Inflate(r, -(Thickness + tolerance));
        return inner.IsEmpty || !inner.Contains(p);
    }
}

public sealed class EllipseAnnotation : BoxAnnotation
{
    public bool Filled { get; set; }

    public override EditorTool Kind => EditorTool.Ellipse;

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        var r = GetRect();
        double rx = r.Width / 2, ry = r.Height / 2;
        if (Filled) dc.DrawEllipse(MakeBrush(), null, Center(r), rx, ry);
        else dc.DrawEllipse(null, MakePen(), Center(r), Math.Max(0, rx - Thickness / 2), Math.Max(0, ry - Thickness / 2));
    }

    public override bool HitTest(Point p, double tolerance)
    {
        var r = GetRect();
        // The slack below is relative to the smaller radius: a flat ellipse would otherwise catch far clicks.
        if (!Inflate(r, Thickness + tolerance).Contains(p)) return false;
        double rx = Math.Max(r.Width / 2, 1), ry = Math.Max(r.Height / 2, 1);
        var c = Center(r);
        double d = Math.Sqrt(Math.Pow((p.X - c.X) / rx, 2) + Math.Pow((p.Y - c.Y) / ry, 2));
        double slack = (Thickness + tolerance) / Math.Min(rx, ry);
        return Filled ? d <= 1 + slack : Math.Abs(d - 1) <= slack;
    }

    private static Point Center(Rect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
}

/// <summary>Translucent marker over an area.</summary>
public sealed class HighlightAnnotation : BoxAnnotation
{
    public override EditorTool Kind => EditorTool.Highlight;

    public override void Render(DrawingContext dc, BitmapSource baseImage) =>
        dc.DrawRectangle(MakeBrush(0x66), null, GetRect());
}

/// <summary>Hides confidential information by pixelating the base image.</summary>
public sealed class PixelateAnnotation : BoxAnnotation
{
    /// <summary>Whole base images pixelated, per block size (see <see cref="PixelatedImage"/>).</summary>
    private static readonly ConditionalWeakTable<BitmapSource, Dictionary<int, BitmapSource>> Pixelated = new();

    /// <summary>Big enough blocks: small ones leave text guessable.</summary>
    public int BlockSize => Math.Max(8, (int)Math.Round(Thickness * 2.5));

    public override EditorTool Kind => EditorTool.Pixelate;

    /// <summary>Area covered, in whole pixels of the image.</summary>
    public Int32Rect GetPixelArea(BitmapSource baseImage)
    {
        var r = GetRect();
        int x0 = Math.Clamp((int)Math.Floor(r.X), 0, baseImage.PixelWidth);
        int y0 = Math.Clamp((int)Math.Floor(r.Y), 0, baseImage.PixelHeight);
        int x1 = Math.Clamp((int)Math.Ceiling(r.Right), 0, baseImage.PixelWidth);
        int y1 = Math.Clamp((int)Math.Ceiling(r.Bottom), 0, baseImage.PixelHeight);
        return new Int32Rect(x0, y0, Math.Max(0, x1 - x0), Math.Max(0, y1 - y0));
    }

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        var area = GetPixelArea(baseImage);
        if (area.Width < 1 || area.Height < 1) return;
        dc.PushClip(new RectangleGeometry(new Rect(area.X, area.Y, area.Width, area.Height)));
        dc.DrawImage(PixelatedImage(baseImage, BlockSize), new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight));
        dc.Pop();
    }

    /// <summary>
    /// The whole image pixelated on a grid anchored at its corner, computed once per image and block size:
    /// moving or resizing the area only changes the clip, so the blocks never shimmer, and pixelating
    /// an already pixelated area gives the same pixels.
    /// </summary>
    internal static BitmapSource PixelatedImage(BitmapSource image, int block)
    {
        var sizes = Pixelated.GetValue(image, _ => new Dictionary<int, BitmapSource>());
        if (!sizes.TryGetValue(block, out var result))
        {
            result = Pixelate(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelHeight), block);
            sizes[block] = result;
        }
        return result;
    }

    /// <param name="source">Bgra32 image.</param>
    private static BitmapSource Pixelate(BitmapSource source, Int32Rect area, int block)
    {
        int stride = area.Width * 4;
        var buffer = new byte[(long)stride * area.Height];
        source.CopyPixels(area, buffer, stride, 0);

        for (int by = 0; by < area.Height; by += block)
        {
            int bh = Math.Min(block, area.Height - by);
            for (int bx = 0; bx < area.Width; bx += block)
            {
                int bw = Math.Min(block, area.Width - bx);
                long b = 0, g = 0, r = 0;
                for (int y = by; y < by + bh; y++)
                {
                    for (int x = bx; x < bx + bw; x++)
                    {
                        long i = (long)y * stride + x * 4;
                        b += buffer[i];
                        g += buffer[i + 1];
                        r += buffer[i + 2];
                    }
                }
                int n = bw * bh;
                byte bb = (byte)(b / n), gg = (byte)(g / n), rr = (byte)(r / n);
                for (int y = by; y < by + bh; y++)
                {
                    for (int x = bx; x < bx + bw; x++)
                    {
                        long i = (long)y * stride + x * 4;
                        buffer[i] = bb;
                        buffer[i + 1] = gg;
                        buffer[i + 2] = rr;
                        buffer[i + 3] = 255;
                    }
                }
            }
        }

        var result = BitmapSource.Create(area.Width, area.Height, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        result.Freeze();
        return result;
    }
}

/// <summary>Darkens everything except the area, to draw attention to it.</summary>
public sealed class SpotlightAnnotation : BoxAnnotation
{
    private static readonly Brush Shade = Frozen(new SolidColorBrush(Color.FromArgb(0x99, 0, 0, 0)));

    public override EditorTool Kind => EditorTool.Spotlight;

    /// <summary>
    /// Alone; with others, <see cref="AnnotationRenderer"/> draws them together with <see cref="DrawShade"/>
    /// so that overlapping shades do not darken twice.
    /// </summary>
    public override void Render(DrawingContext dc, BitmapSource baseImage) =>
        DrawShade(dc, new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight), new[] { this });

    /// <summary>One shade over the image minus the union of the spotlight areas.</summary>
    public static void DrawShade(DrawingContext dc, Rect image, IEnumerable<SpotlightAnnotation> spotlights)
    {
        var holes = new GeometryGroup { FillRule = FillRule.Nonzero };
        foreach (var spotlight in spotlights) holes.Children.Add(new RectangleGeometry(spotlight.GetRect(), 6, 6));
        var shade = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(image), holes);
        shade.Freeze();
        dc.DrawGeometry(Shade, null, shade);
    }
}

public sealed class TextAnnotation : Annotation
{
    public const double Pad = 6;
    public const double MinFontSize = 6;
    public const double MaxFontSize = 2000;
    public static readonly FontFamily Font = new("Segoe UI");

    // Layout cache, rebuilt when the text, its size or its color change.
    private FormattedText? _format;
    private string? _formatText;
    private double _formatSize;
    private Color _formatColor;
    private Geometry? _geometry;
    private Pen? _halo;

    public Point Position { get; set; }
    public string Text { get; set; } = "";
    public double FontSize { get; set; } = 24;
    /// <summary>White box with a colored border behind the text.</summary>
    public bool HasBackground { get; set; }

    public override EditorTool Kind => EditorTool.Text;

    public override bool IsValid() =>
        base.IsValid() && Text != null && Text.Length <= 100_000 && IsValidPoint(Position)
        && double.IsFinite(FontSize) && FontSize >= 1 && FontSize <= MaxFontSize;

    private FormattedText Format()
    {
        if (_format == null || _formatText != Text || _formatSize != FontSize || _formatColor != Color)
        {
            _format = new FormattedText(Text.Length == 0 ? " " : Text, CultureInfo.CurrentUICulture, DirectionOf(Text),
                new Typeface(Font, FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal),
                FontSize, MakeBrush(), 1.0);
            _formatText = Text;
            _formatSize = FontSize;
            _formatColor = Color;
            _geometry = null;
            _halo = null;
        }
        return _format;
    }

    /// <summary>
    /// Direction of a text, from its first letter with a strong direction (Arabic, Hebrew... read right to left),
    /// whatever the language of the interface. Left to right without any letter.
    /// </summary>
    public static FlowDirection DirectionOf(string? text)
    {
        if (string.IsNullOrEmpty(text)) return FlowDirection.LeftToRight;
        foreach (var rune in text.EnumerateRunes())
        {
            if (IsRightToLeft(rune.Value)) return FlowDirection.RightToLeft;
            if (Rune.IsLetter(rune)) return FlowDirection.LeftToRight;
        }
        return FlowDirection.LeftToRight;
    }

    /// <summary>Blocks of the right-to-left scripts (Hebrew, Arabic, Syriac, Thaana, N'Ko... and their presentation forms).</summary>
    private static bool IsRightToLeft(int c) =>
        (c >= 0x0590 && c <= 0x08FF) || (c >= 0xFB1D && c <= 0xFDFF) || (c >= 0xFE70 && c <= 0xFEFF)
        || (c >= 0x10800 && c <= 0x10FFF) || (c >= 0x1E800 && c <= 0x1EFFF);

    /// <summary>Where the text is drawn in the box: a right-to-left text starts at its right edge.</summary>
    private Point TextOrigin(FormattedText text) =>
        text.FlowDirection == FlowDirection.RightToLeft
            ? new Point(Pad + text.WidthIncludingTrailingWhitespace, Pad)
            : new Point(Pad, Pad);

    public override Rect GetBounds()
    {
        var text = Format();
        return new Rect(Position.X, Position.Y, text.WidthIncludingTrailingWhitespace + 2 * Pad, text.Height + 2 * Pad);
    }

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        var text = Format();
        if (HasBackground)
        {
            dc.DrawRoundedRectangle(Brushes.White, MakePen(Math.Max(1.5, Thickness / 2)), GetBounds(), 4, 4);
            dc.DrawText(text, Position + (Vector)TextOrigin(text));
        }
        else
        {
            // Contrasting halo keeps the text readable on any screenshot.
            _geometry ??= Frozen(text.BuildGeometry(TextOrigin(text)));
            _halo ??= Frozen(new Pen(IsLight(Color) ? Brushes.Black : Brushes.White, Math.Max(2, FontSize / 7)) { LineJoin = PenLineJoin.Round });
            dc.PushTransform(new TranslateTransform(Position.X, Position.Y));
            dc.DrawGeometry(null, _halo, _geometry);
            dc.DrawGeometry(MakeBrush(), null, _geometry);
            dc.Pop();
        }
    }

    public override void Translate(Vector offset) => Position += offset;

    public override void Scale(double sx, double sy)
    {
        Position = Scaled(Position, sx, sy);
        FontSize = Math.Clamp(FontSize * (sx + sy) / 2, MinFontSize, MaxFontSize);
        base.Scale(sx, sy);
    }
}

/// <summary>Look of a numbered bullet.</summary>
public enum StepShape
{
    Circle,
    /// <summary>Circle linked by a line to a dot on the target.</summary>
    Leader,
    /// <summary>Speech-bubble circle whose tail points at the target.</summary>
    Tail,
    /// <summary>Map pin pointing down.</summary>
    Pin,
    /// <summary>Dark circle with the number in the color.</summary>
    Dark,
}

/// <summary>Numbered bullet: each click adds the next number (or letter).</summary>
public sealed class StepAnnotation : Annotation
{
    public const int MaxNumber = 100_000;

    private static readonly Brush DarkFill = Frozen(new SolidColorBrush(Color.FromArgb(0xE6, 0x26, 0x26, 0x2B)));

    private Pen? _outline;

    public Point Center { get; set; }
    public int Number { get; set; } = 1;
    public StepShape Shape { get; set; }
    /// <summary>A, B, C... instead of 1, 2, 3.</summary>
    public bool Letters { get; set; }
    /// <summary>Point shown by the leader or the tail.</summary>
    public Point? Target { get; set; }

    public override EditorTool Kind => EditorTool.Step;

    private double Radius => 10 + Thickness * 2.5;

    private bool HasTarget => Shape is StepShape.Leader or StepShape.Tail;

    public override bool IsValid() =>
        base.IsValid() && Number >= 1 && Number <= MaxNumber && Enum.IsDefined(Shape)
        && IsValidPoint(Center) && (Target is not Point t || IsValidPoint(t));

    /// <summary>
    /// Gives a target to the shapes that need one, to the left of the bullet,
    /// or to its right when that would leave <paramref name="area"/> (the image).
    /// </summary>
    public void EnsureTarget(Rect? area = null)
    {
        if (!HasTarget || Target != null) return;
        double distance = Radius * 2.6;
        var target = Center + new Vector(-distance, 0);
        if (area is Rect a && !a.IsEmpty)
        {
            if (target.X < a.Left) target = Center + new Vector(distance, 0);
            target = new Point(Math.Clamp(target.X, a.Left, a.Right), Math.Clamp(target.Y, a.Top, a.Bottom));
        }
        Target = target;
    }

    [JsonIgnore]
    public string Label => Letters ? ToLetters(Number) : Number.ToString(CultureInfo.InvariantCulture);

    private static string ToLetters(int n)
    {
        var s = "";
        for (n = Math.Max(1, n); n > 0; n = (n - 1) / 26) s = (char)('A' + (n - 1) % 26) + s;
        return s;
    }

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        double r = Radius;
        bool light = IsLight(Color);
        var contrast = light ? Brushes.Black : Brushes.White;
        double outlineWidth = Math.Max(1.5, r * 0.12);
        if (_outline == null || _outline.Brush != contrast || _outline.Thickness != outlineWidth)
            _outline = Frozen(new Pen(contrast, outlineWidth) { LineJoin = PenLineJoin.Round });
        var fill = MakeBrush();
        Brush textBrush = contrast;
        Point labelCenter = Center;

        switch (Shape)
        {
            case StepShape.Leader when Target is Point target:
                dc.DrawLine(MakePen(Math.Max(2, r * 0.16)), target, Center);
                dc.DrawEllipse(fill, null, target, r * 0.28, r * 0.28);
                dc.DrawEllipse(fill, _outline, Center, r, r);
                break;
            case StepShape.Tail when Target is Point target:
                dc.DrawGeometry(fill, _outline, Teardrop(Center, target - Center, r, r * 2));
                break;
            case StepShape.Pin:
                dc.DrawGeometry(fill, _outline, Teardrop(Center, new Vector(0, 1), r, r * 2.1));
                break;
            case StepShape.Dark:
                dc.DrawEllipse(DarkFill, null, Center, r, r);
                textBrush = fill;
                break;
            default:
                dc.DrawEllipse(fill, _outline, Center, r, r);
                break;
        }

        var label = Label;
        var text = new FormattedText(label, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, new Typeface(TextAnnotation.Font, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
            r * (label.Length >= 2 ? 0.95 : 1.15), textBrush, 1.0);
        dc.DrawText(text, new Point(labelCenter.X - text.Width / 2, labelCenter.Y - text.Height / 2));
    }

    /// <summary>Circle of radius r prolonged by a point at distance tip from its center.</summary>
    private static Geometry Teardrop(Point center, Vector direction, double r, double tip)
    {
        if (direction.Length < 1e-6) direction = new Vector(0, 1);
        direction.Normalize();
        tip = Math.Max(tip, r * 1.3);
        var apex = center + direction * tip;
        // Tangent points of the lines from the apex to the circle.
        double angle = Math.Acos(r / tip);
        var a = Rotate(direction, angle) * r;
        var b = Rotate(direction, -angle) * r;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(apex, isFilled: true, isClosed: true);
            ctx.LineTo(center + a, true, true);
            ctx.ArcTo(center + b, new Size(r, r), 0, isLargeArc: true, SweepDirection.Clockwise, true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Vector Rotate(Vector v, double angle) =>
        new(v.X * Math.Cos(angle) - v.Y * Math.Sin(angle), v.X * Math.Sin(angle) + v.Y * Math.Cos(angle));

    public override Rect GetBounds()
    {
        double r = Radius;
        var bounds = new Rect(Center.X - r, Center.Y - r, 2 * r, 2 * r);
        if (Shape == StepShape.Pin) bounds.Union(Center + new Vector(0, r * 2.1));
        if (HasTarget && Target is Point target) bounds.Union(Shape == StepShape.Tail ? Center + Clamp(target - Center, r * 2) : target);
        return Inflate(bounds, r * 0.3);
    }

    private static Vector Clamp(Vector v, double length)
    {
        if (v.Length < 1e-6) return new Vector(0, length);
        v.Normalize();
        return v * length;
    }

    public override bool HitTest(Point p, double tolerance) =>
        (p - Center).Length <= Radius + tolerance
        || (Shape == StepShape.Leader && Target is Point t && DistanceToSegment(p, t, Center) <= Radius * 0.3 + tolerance)
        || (Shape is StepShape.Tail or StepShape.Pin && GetBounds().Contains(p) && (p - Center).Length <= Radius * 2.2 + tolerance);

    /// <summary>The target of a leader or a tail can be dragged.</summary>
    public override IReadOnlyList<Point> GetHandles() =>
        HasTarget && Target is Point t ? new[] { Shape == StepShape.Tail ? Center + Clamp(t - Center, Radius * 2) : t } : Array.Empty<Point>();

    public override void MoveHandle(int index, Point position) => Target = position;

    public override void Translate(Vector offset)
    {
        Center += offset;
        if (Target is Point t) Target = t + offset;
    }

    public override void Scale(double sx, double sy)
    {
        Center = Scaled(Center, sx, sy);
        if (Target is Point t) Target = Scaled(t, sx, sy);
        base.Scale(sx, sy);
    }
}

public sealed class FreehandAnnotation : Annotation
{
    public const int MaxPoints = 200_000;

    public List<Point> Points { get; set; } = new();

    public override EditorTool Kind => EditorTool.Pen;

    public override bool IsValid() =>
        base.IsValid() && Points != null && Points.Count <= MaxPoints && Points.TrueForAll(IsValidPoint);

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        if (Points.Count < 2) return;
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(Points[0], isFilled: false, isClosed: false);
            for (int i = 1; i < Points.Count; i++) ctx.LineTo(Points[i], isStroked: true, isSmoothJoin: true);
        }
        geometry.Freeze();
        dc.DrawGeometry(null, MakePen(), geometry);
    }

    public override Rect GetBounds()
    {
        if (Points.Count == 0) return Rect.Empty;
        var r = new Rect(Points[0], Points[0]);
        foreach (var p in Points) r.Union(p);
        return Inflate(r, Thickness / 2);
    }

    public override bool HitTest(Point p, double tolerance)
    {
        for (int i = 1; i < Points.Count; i++)
            if (DistanceToSegment(p, Points[i - 1], Points[i]) <= Thickness / 2 + tolerance) return true;
        return false;
    }

    public override void Translate(Vector offset)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] += offset;
    }

    public override void Scale(double sx, double sy)
    {
        for (int i = 0; i < Points.Count; i++) Points[i] = Scaled(Points[i], sx, sy);
        base.Scale(sx, sy);
    }

    public override Annotation Clone()
    {
        var copy = (FreehandAnnotation)MemberwiseClone();
        copy.Points = new List<Point>(Points);
        return copy;
    }
}

/// <summary>Vector stamp from <see cref="StampLibrary"/>, resizable while keeping its proportions.</summary>
public sealed class StampAnnotation : BoxAnnotation
{
    private static readonly Pen MissingPen = Frozen(new Pen(Brushes.Gray, 1) { DashStyle = DashStyles.Dash });

    public string StampId { get; set; } = "";

    public override EditorTool Kind => EditorTool.Stamp;

    public override bool IsValid() => base.IsValid() && StampId != null && StampId.Length <= 200;

    public override void Render(DrawingContext dc, BitmapSource baseImage)
    {
        if (StampLibrary.Find(StampId) is { } stamp) stamp.Render(dc, GetRect());
        else dc.DrawRectangle(null, MissingPen, GetRect());
    }

    public override void MoveHandle(int index, Point position)
    {
        var anchor = Anchor(index);
        var rect = GetRect();
        double ratio = rect.Width > 0 && rect.Height > 0 ? rect.Width / rect.Height : 1;
        var v = position - anchor;
        // Keep the aspect ratio: follow the dominant axis.
        double width = Math.Max(Math.Abs(v.X), Math.Abs(v.Y) * ratio);
        width = Math.Max(width, 8);
        Start = anchor;
        End = new Point(anchor.X + (v.X < 0 ? -width : width), anchor.Y + (v.Y < 0 ? -width / ratio : width / ratio));
    }
}
