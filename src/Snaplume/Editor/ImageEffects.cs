using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;
using static Snaplume.Editor.Freezables;

namespace Snaplume.Editor;

/// <summary>Sides of the image cut with a torn-paper edge.</summary>
[Flags]
public enum TornEdges
{
    None = 0,
    Top = 1,
    Bottom = 2,
    Left = 4,
    Right = 8,
    All = Top | Bottom | Left | Right,
}

/// <summary>
/// Effects applied to the whole image (drop shadow, border, rounded corners, torn edges), saved with the
/// annotations and rendered around them. Immutable: a change is a new value, kept by the undo history.
/// The shadow adds margins around the image in the flattened output only; annotation coordinates stay
/// relative to the image. Saved images have no transparency, so the margins and the cut-off parts take
/// <see cref="Background"/>.
/// </summary>
public sealed record ImageEffects
{
    public const double MaxShadowBlur = 200;
    public const double MaxShadowOffset = 200;
    public const double MaxBorderWidth = 200;
    public const double MaxCornerRadius = 2000;
    public const double MinTornSize = 2;
    public const double MaxTornSize = 200;

    public static readonly ImageEffects None = new();

    public bool Shadow { get; init; }
    /// <summary>Blur radius of the shadow, in pixels.</summary>
    public double ShadowBlur { get; init; } = 18;
    public double ShadowOpacity { get; init; } = 0.4;
    public double ShadowOffsetX { get; init; } = 5;
    public double ShadowOffsetY { get; init; } = 5;

    public bool Border { get; init; }
    public Color BorderColor { get; init; } = Color.FromRgb(0x37, 0x41, 0x51);
    public double BorderWidth { get; init; } = 3;

    /// <summary>0 for square corners.</summary>
    public double CornerRadius { get; init; }

    public TornEdges TornEdges { get; init; }
    /// <summary>Depth of the teeth of the torn edges, in pixels.</summary>
    public double TornSize { get; init; } = 12;
    /// <summary>Draws the teeth: the same seed always gives the same edge.</summary>
    public int TornSeed { get; init; } = 1;

    /// <summary>Color of the margins and of the parts cut off (files have no transparency).</summary>
    public Color Background { get; init; } = Colors.White;

    [JsonIgnore]
    public bool IsEmpty => !Shadow && !Border && CornerRadius <= 0 && TornEdges == TornEdges.None;

    /// <summary>False when values read from a sidecar file cannot be used.</summary>
    public bool IsValid() =>
        InRange(ShadowBlur, 0, MaxShadowBlur) && InRange(ShadowOpacity, 0, 1)
        && InRange(ShadowOffsetX, -MaxShadowOffset, MaxShadowOffset) && InRange(ShadowOffsetY, -MaxShadowOffset, MaxShadowOffset)
        && InRange(BorderWidth, 0.5, MaxBorderWidth) && InRange(CornerRadius, 0, MaxCornerRadius)
        && ((int)TornEdges & ~(int)TornEdges.All) == 0 && InRange(TornSize, MinTornSize, MaxTornSize);

    private static bool InRange(double value, double min, double max) => double.IsFinite(value) && value >= min && value <= max;

    /// <summary>The effects of the image resized by <paramref name="factor"/>: sizes follow, within their limits.</summary>
    public ImageEffects Scaled(double factor)
    {
        if (!double.IsFinite(factor) || factor <= 0) return this;
        return this with
        {
            ShadowBlur = Math.Clamp(ShadowBlur * factor, 0, MaxShadowBlur),
            ShadowOffsetX = Math.Clamp(ShadowOffsetX * factor, -MaxShadowOffset, MaxShadowOffset),
            ShadowOffsetY = Math.Clamp(ShadowOffsetY * factor, -MaxShadowOffset, MaxShadowOffset),
            BorderWidth = Math.Clamp(BorderWidth * factor, 0.5, MaxBorderWidth),
            CornerRadius = Math.Clamp(CornerRadius * factor, 0, MaxCornerRadius),
            TornSize = Math.Clamp(TornSize * factor, MinTornSize, MaxTornSize),
        };
    }

    /// <summary>Margins the shadow adds around an image, in whole pixels.</summary>
    public Int32Thickness GetPadding()
    {
        if (!Shadow) return default;
        // A little beyond the blur radius: the tail of the blur stays inside the image.
        double extent = ShadowBlur * 1.3;
        return new Int32Thickness(
            (int)Math.Ceiling(Math.Max(0, extent - ShadowOffsetX)),
            (int)Math.Ceiling(Math.Max(0, extent - ShadowOffsetY)),
            (int)Math.Ceiling(Math.Max(0, extent + ShadowOffsetX)),
            (int)Math.Ceiling(Math.Max(0, extent + ShadowOffsetY)));
    }
}

public readonly record struct Int32Thickness(int Left, int Top, int Right, int Bottom)
{
    public Thickness ToThickness() => new(Left, Top, Right, Bottom);
}

/// <summary>
/// Everything needed to draw the effects of an image of a given size, computed once
/// (see <see cref="EffectsRenderer.For"/>). Coordinates are those of the image: the margins are negative.
/// </summary>
public sealed class EffectsLayout
{
    private readonly Brush _background;
    private readonly ShadowMask? _shadow;
    private readonly Pen? _borderPen;
    private readonly Rect _borderRect;

    internal EffectsLayout(ImageEffects effects, int width, int height)
    {
        Effects = effects;
        Width = width;
        Height = height;
        Padding = effects.GetPadding();
        OutlineGeometry = EffectsRenderer.Outline(effects, width, height);

        var background = effects.Background;
        _background = Frozen(new SolidColorBrush(Color.FromRgb(background.R, background.G, background.B)));

        if (effects.Shadow && effects.ShadowOpacity > 0)
            _shadow = EffectsRenderer.Shadow(effects, OutlineGeometry, width, height);

        if (effects.Border)
        {
            double border = Math.Min(effects.BorderWidth, Math.Min(width, height) / 2.0);
            var brush = Frozen(new SolidColorBrush(effects.BorderColor));
            // Around an outline the pen is centered on it and clipped to it: twice as wide.
            var outline = OutlineGeometry;
            _borderPen = Frozen(new Pen(brush, outline == null ? border : border * 2) { LineJoin = outline == null ? PenLineJoin.Miter : PenLineJoin.Round });
            _borderRect = new Rect(border / 2, border / 2, Math.Max(0, width - border), Math.Max(0, height - border));
        }
    }

    public ImageEffects Effects { get; }
    public int Width { get; }
    public int Height { get; }
    public Int32Thickness Padding { get; }
    public int OutputWidth => Width + Padding.Left + Padding.Right;
    public int OutputHeight => Height + Padding.Top + Padding.Bottom;

    /// <summary>Outline of the image (rounded corners, torn edges); null for a plain rectangle.</summary>
    public Geometry? OutlineGeometry { get; }

    /// <summary>Area of the output, margins included, in image coordinates.</summary>
    public Rect OutputArea => new(-Padding.Left, -Padding.Top, OutputWidth, OutputHeight);

    /// <summary>What is under the image: the background color and the shadow.</summary>
    public void DrawBackdrop(DrawingContext dc)
    {
        dc.DrawRectangle(_background, null, OutputArea);
        if (_shadow == null) return;
        // The opacity is applied here: changing it reuses the same blurred mask.
        var area = _shadow.Area;
        area.Offset(Effects.ShadowOffsetX, Effects.ShadowOffsetY);
        dc.PushOpacity(Effects.ShadowOpacity);
        dc.DrawImage(_shadow.Bitmap, area);
        dc.Pop();
    }

    /// <summary>Clips what follows (the image and its annotations) to <see cref="OutlineGeometry"/>; call <see cref="PopOutline"/> after.</summary>
    public void PushOutline(DrawingContext dc)
    {
        if (OutlineGeometry != null) dc.PushClip(OutlineGeometry);
    }

    public void PopOutline(DrawingContext dc)
    {
        if (OutlineGeometry != null) dc.Pop();
    }

    /// <summary>Border inside the outline of the image, drawn over the annotations.</summary>
    public void DrawBorder(DrawingContext dc)
    {
        if (_borderPen == null) return;
        if (OutlineGeometry == null)
        {
            dc.DrawRectangle(null, _borderPen, _borderRect);
            return;
        }
        dc.PushClip(OutlineGeometry);
        dc.DrawGeometry(null, _borderPen, OutlineGeometry);
        dc.Pop();
    }

    /// <summary>Draws the whole output: backdrop, then <paramref name="drawImage"/> clipped to the outline, then the border.</summary>
    public void Render(DrawingContext dc, Action<DrawingContext> drawImage)
    {
        dc.PushTransform(new TranslateTransform(Padding.Left, Padding.Top));
        DrawBackdrop(dc);
        PushOutline(dc);
        dc.PushClip(new RectangleGeometry(new Rect(0, 0, Width, Height)));
        drawImage(dc);
        dc.Pop();
        PopOutline(dc);
        DrawBorder(dc);
        dc.Pop();
    }
}

/// <summary>Blurred outline of the image, fully opaque, and where to draw it (before the offset of the shadow).</summary>
internal sealed record ShadowMask(BitmapSource Bitmap, Rect Area);

public static class EffectsRenderer
{
    /// <summary>
    /// Largest blurred mask, in pixels: a bigger image gets a mask of reduced resolution, drawn enlarged
    /// (a shadow has no detail to lose).
    /// </summary>
    private const double MaxMaskPixels = 2e6;
    /// <summary>At most about this many teeth along the longest side: more would only slow the drawing down.</summary>
    private const double MaxTeeth = 1500;

    // Kept until the effects or the size change. A few of each, so that two editors, or the preview and the
    // saved image, do not rebuild them in turn. Only the drawing parts are keyed: changing the opacity or the
    // offset of the shadow, or the border, reuses the outline and the blurred mask.
    private static readonly SmallCache<(ImageEffects Effects, int Width, int Height), EffectsLayout> Layouts = new(4);
    private static readonly SmallCache<OutlineKey, Geometry?> Outlines = new(4);
    private static readonly SmallCache<(OutlineKey Outline, double Blur), ShadowMask> Shadows = new(4);

    /// <summary>What the outline of the image depends on.</summary>
    private readonly record struct OutlineKey(int Width, int Height, double Radius, TornEdges Edges, double Tooth, int Seed);

    /// <summary>Layout of the effects for an image of this size, or null when there is no effect.</summary>
    /// <exception cref="InvalidDataException">The output, margins included, would be too big (see <see cref="ImageIO.CheckSize"/>).</exception>
    public static EffectsLayout? For(ImageEffects effects, int width, int height)
    {
        if (effects.IsEmpty || width < 1 || height < 1) return null;
        // Checked before anything is allocated.
        var padding = effects.GetPadding();
        ImageIO.CheckSize(width + padding.Left + padding.Right, height + padding.Top + padding.Bottom);
        return Layouts.GetOrAdd((effects, width, height), () => new EffectsLayout(effects, width, height));
    }

    private static OutlineKey KeyOf(ImageEffects effects, int width, int height)
    {
        double radius = Math.Min(effects.CornerRadius, Math.Min(width, height) / 2.0);
        double tooth = Math.Min(Math.Max(effects.TornSize, Math.Max(width, height) / MaxTeeth), Math.Min(width, height) / 4.0);
        var edges = tooth >= 1 ? effects.TornEdges & TornEdges.All : TornEdges.None;
        return edges == TornEdges.None
            ? new OutlineKey(width, height, Math.Max(0, radius), TornEdges.None, 0, 0)
            : new OutlineKey(width, height, Math.Max(0, radius), edges, tooth, effects.TornSeed);
    }

    /// <summary>Outline with the rounded corners and the torn edges, or null for a plain rectangle.</summary>
    internal static Geometry? Outline(ImageEffects effects, int width, int height)
    {
        var key = KeyOf(effects, width, height);
        return Outlines.GetOrAdd(key, () => BuildOutline(key));
    }

    private static Geometry? BuildOutline(OutlineKey key)
    {
        var bounds = new Rect(0, 0, key.Width, key.Height);
        if (key.Edges == TornEdges.None)
            return key.Radius > 0 ? Frozen(new RectangleGeometry(bounds, key.Radius, key.Radius)) : null;

        var torn = TornPolygon(key);
        if (key.Radius <= 0) return torn;
        // One intersection, flattened once: clipping with a polygon is faster than with a combined geometry.
        var rounded = new RectangleGeometry(bounds, key.Radius, key.Radius);
        return Frozen(new CombinedGeometry(GeometryCombineMode.Intersect, rounded, torn).GetFlattenedPathGeometry(0.25, ToleranceType.Absolute));
    }

    /// <summary>
    /// The image outline as one polygon, clockwise from the top-left: each side is a straight line or a jagged
    /// line, and consecutive sides are joined where they cross near their common corner.
    /// </summary>
    private static Geometry TornPolygon(OutlineKey key)
    {
        int width = key.Width, height = key.Height;
        var order = new[] { TornEdges.Top, TornEdges.Right, TornEdges.Bottom, TornEdges.Left };
        var sides = new List<Point>[4];
        for (int s = 0; s < 4; s++)
        {
            var edge = order[s];
            var line = key.Edges.HasFlag(edge) ? TornSide(edge, width, height, key.Tooth, key.Seed) : StraightSide(edge, width, height);
            // Bottom and left run backwards when going clockwise.
            if (edge is TornEdges.Bottom or TornEdges.Left) line.Reverse();
            sides[s] = line;
        }
        var corners = new[] { new Point(width, 0), new Point(width, height), new Point(0, height), new Point(0, 0) };
        double zone = key.Tooth + 2;

        // Corner s joins side s (ending) and side s + 1 (starting).
        var joins = new (int EndIndex, int StartIndex, Point At)[4];
        for (int s = 0; s < 4; s++)
            joins[s] = Join(sides[s], sides[(s + 1) % 4], corners[s], zone);

        var points = new List<Point>();
        for (int s = 0; s < 4; s++)
        {
            var (_, startIndex, startAt) = joins[(s + 3) % 4];
            var (endIndex, _, _) = joins[s];
            points.Add(startAt);
            for (int i = startIndex + 1; i <= endIndex && i < sides[s].Count; i++) points.Add(sides[s][i]);
        }

        var geometry = new StreamGeometry { FillRule = FillRule.Nonzero };
        using (var ctx = geometry.Open())
        {
            ctx.BeginFigure(points[0], isFilled: true, isClosed: true);
            ctx.PolyLineTo(points.Skip(1).ToList(), isStroked: false, isSmoothJoin: false);
        }
        geometry.Freeze();
        return geometry;
    }

    /// <summary>
    /// Where side <paramref name="a"/> (ending near <paramref name="corner"/>) crosses side <paramref name="b"/>
    /// (starting there): the first crossing along <paramref name="a"/>, looking only at the segments near the corner.
    /// Returns the last point of <paramref name="a"/> kept, the last point of <paramref name="b"/> skipped, and the crossing.
    /// </summary>
    private static (int EndIndex, int StartIndex, Point At) Join(List<Point> a, List<Point> b, Point corner, double zone)
    {
        bool Near(Point p) => Math.Abs(p.X - corner.X) <= zone && Math.Abs(p.Y - corner.Y) <= zone;

        int aFirst = a.Count - 2;
        while (aFirst > 0 && (Near(a[aFirst]) || Near(a[aFirst + 1]))) aFirst--;
        int bLast = 0;
        while (bLast < b.Count - 2 && (Near(b[bLast]) || Near(b[bLast + 1]))) bLast++;

        for (int i = Math.Max(0, aFirst); i < a.Count - 1; i++)
        {
            double bestT = double.MaxValue;
            int bestJ = -1;
            for (int j = 0; j <= bLast && j < b.Count - 1; j++)
            {
                if (Cross(a[i], a[i + 1], b[j], b[j + 1]) is double t && t < bestT)
                {
                    bestT = t;
                    bestJ = j;
                }
            }
            if (bestJ >= 0) return (i, bestJ, a[i] + (a[i + 1] - a[i]) * bestT);
        }
        // Not expected (the sides always cross near the corner): joined by a straight line.
        return (a.Count - 1, -1, a[^1]);
    }

    /// <summary>Position along p1-p2 (0 to 1) where it crosses q1-q2, or null.</summary>
    private static double? Cross(Point p1, Point p2, Point q1, Point q2)
    {
        var r = p2 - p1;
        var s = q2 - q1;
        double denominator = Vector.CrossProduct(r, s);
        if (Math.Abs(denominator) < 1e-12) return null;
        var qp = q1 - p1;
        double t = Vector.CrossProduct(qp, s) / denominator;
        double u = Vector.CrossProduct(qp, r) / denominator;
        return t >= 0 && t <= 1 && u >= 0 && u <= 1 ? t : null;
    }

    /// <summary>A side without teeth, slightly longer than the image so that it crosses its neighbors.</summary>
    private static List<Point> StraightSide(TornEdges edge, int width, int height)
    {
        double length = edge is TornEdges.Top or TornEdges.Bottom ? width : height;
        return new List<Point> { SidePoint(edge, -1, 0, width, height), SidePoint(edge, length + 1, 0, width, height) };
    }

    /// <summary>
    /// Jagged line running along one side, from one end to the other. Points come from a hash of the seed,
    /// the side and their index, so the edge never changes between renders.
    /// </summary>
    private static List<Point> TornSide(TornEdges edge, int width, int height, double tooth, int seed)
    {
        double length = edge is TornEdges.Top or TornEdges.Bottom ? width : height;
        var points = new List<Point>();
        double along = -1;
        for (int i = 0; ; i++)
        {
            // Alternating shallow and deep notches, irregularly spaced, like torn paper.
            double depth = tooth * (i % 2 == 0 ? 0.05 + 0.3 * Noise(seed, (int)edge, 2 * i) : 0.55 + 0.45 * Noise(seed, (int)edge, 2 * i));
            points.Add(SidePoint(edge, Math.Min(along, length + 1), depth, width, height));
            if (along >= length + 1) break;
            along += tooth * (0.45 + 0.6 * Noise(seed, (int)edge, 2 * i + 1));
        }
        return points;
    }

    /// <summary>Point at <paramref name="along"/> on the side, <paramref name="depth"/> pixels inside the image.</summary>
    private static Point SidePoint(TornEdges edge, double along, double depth, int width, int height) => edge switch
    {
        TornEdges.Top => new Point(along, depth),
        TornEdges.Bottom => new Point(along, height - depth),
        TornEdges.Left => new Point(depth, along),
        _ => new Point(width - depth, along),
    };

    /// <summary>Stable pseudo-random number in [0, 1) (SplitMix64), the same on every machine and version.</summary>
    internal static double Noise(int seed, int stream, int index)
    {
        ulong z = unchecked((ulong)(uint)seed * 0x9E3779B97F4A7C15UL ^ (ulong)(uint)stream * 0xBF58476D1CE4E5B9UL ^ (ulong)(uint)index * 0x94D049BB133111EBUL);
        z = unchecked((z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL);
        z = unchecked((z ^ (z >> 27)) * 0x94D049BB133111EBUL);
        z ^= z >> 31;
        return (z >> 11) * (1.0 / (1UL << 53));
    }

    /// <summary>The blurred mask of the shadow, shared by the layouts that differ only by opacity, offset or border.</summary>
    internal static ShadowMask Shadow(ImageEffects effects, Geometry? outline, int width, int height)
    {
        var key = (KeyOf(effects, width, height), effects.ShadowBlur);
        return Shadows.GetOrAdd(key, () => BuildShadow(effects.ShadowBlur, outline, width, height));
    }

    /// <summary>
    /// The outline of the image filled and blurred (three box blurs, close to a gaussian), at a resolution
    /// reduced for big blurs and big images, then drawn enlarged under the image. A plain rectangle is
    /// blurred as two lines (the blur of a rectangle is the product of the blurs of its sides).
    /// </summary>
    private static ShadowMask BuildShadow(double blur, Geometry? outline, int width, int height)
    {
        int extent = (int)Math.Ceiling(blur * 1.3);
        double area = (width + 2.0 * extent) * (height + 2.0 * extent);
        int factor = Math.Clamp((int)Math.Ceiling(Math.Max(blur / 6, Math.Sqrt(area / MaxMaskPixels))), 1, 64);
        int pad = extent + 2 * factor;
        int maskWidth = Math.Max(1, (int)Math.Ceiling((width + 2.0 * pad) / factor));
        int maskHeight = Math.Max(1, (int)Math.Ceiling((height + 2.0 * pad) / factor));
        int radius = (int)Math.Round(blur / 2 / factor);
        int stride = maskWidth * 4;
        var pixels = new byte[(long)stride * maskHeight];

        if (outline == null)
        {
            var columns = Coverage(maskWidth, pad, width, factor);
            var rows = Coverage(maskHeight, pad, height, factor);
            if (radius > 0)
            {
                BoxBlur(columns, radius);
                BoxBlur(rows, radius);
            }
            // Black shadow, premultiplied: only the alpha is set.
            for (int y = 0; y < maskHeight; y++)
            {
                double row = rows[y] * 255;
                if (row <= 0) continue;
                long line = (long)y * stride + 3;
                for (int x = 0; x < maskWidth; x++) pixels[line + x * 4] = (byte)Math.Round(columns[x] * row);
            }
        }
        else
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
            {
                dc.PushTransform(new ScaleTransform(1.0 / factor, 1.0 / factor));
                dc.PushTransform(new TranslateTransform(pad, pad));
                dc.DrawGeometry(Brushes.Black, null, outline);
                dc.Pop();
                dc.Pop();
            }
            var target = new RenderTargetBitmap(maskWidth, maskHeight, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);
            target.CopyPixels(pixels, stride, 0);

            var alpha = new byte[(long)maskWidth * maskHeight];
            for (long i = 0; i < alpha.LongLength; i++) alpha[i] = pixels[i * 4 + 3];
            if (radius > 0) BoxBlur(alpha, maskWidth, maskHeight, radius);
            Array.Clear(pixels);
            for (long i = 0; i < alpha.LongLength; i++) pixels[i * 4 + 3] = alpha[i];
        }

        var bitmap = BitmapSource.Create(maskWidth, maskHeight, 96, 96, PixelFormats.Pbgra32, null, pixels, stride);
        bitmap.Freeze();
        return new ShadowMask(bitmap, new Rect(-pad, -pad, maskWidth * factor, maskHeight * factor));
    }

    /// <summary>Share of each mask cell covered by the image (from <paramref name="start"/>, <paramref name="length"/> long).</summary>
    private static double[] Coverage(int cells, int start, int length, int factor)
    {
        var values = new double[cells];
        for (int i = 0; i < cells; i++)
        {
            double from = Math.Max(i * factor, start), to = Math.Min((i + 1) * factor, start + length);
            values[i] = Math.Max(0, to - from) / factor;
        }
        return values;
    }

    /// <summary>The three box blurs on one line; outside counts as empty.</summary>
    private static void BoxBlur(double[] values, int radius)
    {
        var temp = new double[values.Length];
        int window = 2 * radius + 1;
        for (int pass = 0; pass < 3; pass++)
        {
            double sum = 0;
            for (int k = 0; k <= Math.Min(radius, values.Length - 1); k++) sum += values[k];
            for (int k = 0; k < values.Length; k++)
            {
                temp[k] = sum / window;
                int add = k + radius + 1, remove = k - radius;
                if (add < values.Length) sum += values[add];
                if (remove >= 0) sum -= values[remove];
            }
            Array.Copy(temp, values, values.Length);
        }
    }

    private static void BoxBlur(byte[] values, int width, int height, int radius)
    {
        var temp = new byte[values.Length];
        for (int pass = 0; pass < 3; pass++)
        {
            BlurLines(values, temp, width, height, radius, horizontal: true);
            BlurLines(temp, values, width, height, radius, horizontal: false);
        }
    }

    /// <summary>Running-sum blur along rows or columns; outside the bitmap counts as empty.</summary>
    private static void BlurLines(byte[] source, byte[] target, int width, int height, int radius, bool horizontal)
    {
        int lines = horizontal ? height : width;
        int length = horizontal ? width : height;
        long step = horizontal ? 1 : width;
        int window = 2 * radius + 1;
        for (int line = 0; line < lines; line++)
        {
            long start = horizontal ? (long)line * width : line;
            int sum = 0;
            for (int k = 0; k <= Math.Min(radius, length - 1); k++) sum += source[start + k * step];
            for (int k = 0; k < length; k++)
            {
                target[start + k * step] = (byte)((sum + window / 2) / window);
                int add = k + radius + 1, remove = k - radius;
                if (add < length) sum += source[start + add * step];
                if (remove >= 0) sum -= source[start + remove * step];
            }
        }
    }

    /// <summary>The few last values used, most recent first.</summary>
    private sealed class SmallCache<TKey, TValue>(int capacity) where TKey : notnull
    {
        private readonly List<KeyValuePair<TKey, TValue>> _items = new();

        public TValue GetOrAdd(TKey key, Func<TValue> create)
        {
            lock (_items)
            {
                int index = _items.FindIndex(item => EqualityComparer<TKey>.Default.Equals(item.Key, key));
                if (index >= 0)
                {
                    var found = _items[index];
                    _items.RemoveAt(index);
                    _items.Insert(0, found);
                    return found.Value;
                }
            }
            // Built outside the lock; on failure (out of memory) nothing is kept.
            var value = create();
            lock (_items)
            {
                _items.Insert(0, new KeyValuePair<TKey, TValue>(key, value));
                if (_items.Count > capacity) _items.RemoveAt(_items.Count - 1);
            }
            return value;
        }
    }
}
