using System.Windows;
using System.Windows.Media;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>A vector stamp drawn in its own coordinate box (Width × Height).</summary>
public sealed class StampDefinition
{
    private DrawingImage? _preview;

    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    /// <summary>The stamp in its own box, built once and frozen.</summary>
    public required Drawing Drawing { get; init; }

    /// <summary>Draws the stamp fitted (uniformly, centered) into the target rectangle.</summary>
    public void Render(DrawingContext dc, Rect target)
    {
        if (target.Width < 1 || target.Height < 1) return;
        double scale = Math.Min(target.Width / Width, target.Height / Height);
        double x = target.X + (target.Width - Width * scale) / 2;
        double y = target.Y + (target.Height - Height * scale) / 2;
        dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
        dc.DrawDrawing(Drawing);
        dc.Pop();
    }

    public DrawingImage Preview
    {
        get
        {
            if (_preview != null) return _preview;
            var group = new DrawingGroup();
            using (var dc = group.Open()) Render(dc, new Rect(0, 0, Width, Height));
            _preview = new DrawingImage(group);
            _preview.Freeze();
            return _preview;
        }
    }
}

/// <summary>
/// Built-in stamps (original vector drawings): classic symbols, ink text stamps and mouse helpers.
/// </summary>
public static class StampLibrary
{
    public const string Classic = "Classic";
    public const string TextStamps = "Text";
    public const string Mouse = "Mouse";

    public static readonly IReadOnlyList<string> Categories = new[] { Classic, TextStamps, Mouse };

    public static string CategoryName(string category) => Loc.T("Stamp.Category." + category);

    private static readonly Typeface Bold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
    private static readonly Typeface Black = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Black, FontStretches.Normal);
    private static readonly Typeface SemiBold = new(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    private static readonly Brush White = Solid("#FFFFFF");
    private static readonly Brush Ink = Solid("#212121");
    private static readonly Brush Green = Solid("#43A047");
    private static readonly Brush Red = Solid("#E53935");
    private static readonly Brush DarkRed = Solid("#B71C1C");
    private static readonly Brush Yellow = Solid("#FDD835");
    private static readonly Brush Orange = Solid("#FB8C00");
    private static readonly Brush Blue = Solid("#1E88E5");
    private static readonly Brush Grey = Solid("#9E9E9E");

    private static readonly Lazy<List<StampDefinition>> Stamps = new(Build);

    public static IReadOnlyList<StampDefinition> All => Stamps.Value;

    public static StampDefinition? Find(string id) => Stamps.Value.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// Names and the words drawn on stamps are in the interface language (built on first use, after
    /// <see cref="Loc"/> is initialized); the ids saved in annotation files never change.
    /// </summary>
    private static List<StampDefinition> Build()
    {
        var list = new List<StampDefinition>();
        // The drawing code runs here only: rendering a stamp just replays its frozen drawing.
        void Add(string category, string id, string name, Action<DrawingContext> draw, double width = 64, double height = 64) =>
            list.Add(new StampDefinition { Category = category, Id = id, Name = name, Width = width, Height = height, Drawing = Record(draw) });

        // ---- Classic ----
        Add(Classic, "ok-circle", Loc.T("Stamp.Name.OkCircle"), dc =>
        {
            dc.DrawEllipse(Green, null, new Point(32, 32), 29, 29);
            dc.DrawGeometry(null, Stroke(White, 7), Geo("M18,33 L28,43 L46,22"));
        });
        Add(Classic, "ok", Loc.T("Stamp.Name.Ok"), dc => dc.DrawGeometry(null, Stroke(Green, 11), Geo("M9,34 L25,50 L56,14")));
        Add(Classic, "ko-circle", Loc.T("Stamp.Name.KoCircle"), dc =>
        {
            dc.DrawEllipse(Red, null, new Point(32, 32), 29, 29);
            dc.DrawGeometry(null, Stroke(White, 7), Geo("M21,21 L43,43 M43,21 L21,43"));
        });
        Add(Classic, "ko", Loc.T("Stamp.Name.Ko"), dc => dc.DrawGeometry(null, Stroke(Red, 12), Geo("M13,13 L51,51 M51,13 L13,51")));
        Add(Classic, "question", Loc.T("Stamp.Name.Question"), dc => Bubble(dc, "?"));
        Add(Classic, "remark", Loc.T("Stamp.Name.Remark"), dc => Bubble(dc, "!"));
        Add(Classic, "alert", Loc.T("Stamp.Name.Alert"), dc =>
        {
            dc.DrawEllipse(Red, null, new Point(32, 32), 29, 29);
            Text(dc, "!", new Point(32, 32), 40, White, Black);
        });
        Add(Classic, "warning", Loc.T("Stamp.Name.Warning"), dc =>
        {
            dc.DrawGeometry(Yellow, Stroke(Solid("#F9A825"), 3), Geo("M32,6 L60,56 H4 Z"));
            Text(dc, "!", new Point(32, 39), 32, Ink, Black);
        });
        Add(Classic, "danger", Loc.T("Stamp.Name.Danger"), dc =>
        {
            dc.DrawGeometry(Red, Stroke(DarkRed, 3), Geo("M32,6 L60,56 H4 Z"));
            Text(dc, "!", new Point(32, 39), 32, White, Black);
        });
        Add(Classic, "info", Loc.T("Stamp.Name.Info"), dc =>
        {
            dc.DrawEllipse(Blue, null, new Point(32, 32), 29, 29);
            Text(dc, "i", new Point(32, 33), 40, White, Black);
        });
        Add(Classic, "forbidden", Loc.T("Stamp.Name.Forbidden"), dc =>
        {
            dc.DrawEllipse(White, Stroke(Red, 7), new Point(32, 32), 25, 25);
            dc.DrawGeometry(null, Stroke(Red, 7, PenLineCap.Flat), Geo("M15,15 L49,49"));
        });
        Add(Classic, "bug", Loc.T("Stamp.Name.Bug"), DrawBug);
        Add(Classic, "star", Loc.T("Stamp.Name.Star"), dc => dc.DrawGeometry(Yellow, Stroke(Solid("#F9A825"), 2.5), StarGeometry(new Point(32, 34), 30, 12.5, 5)));
        Add(Classic, "idea", Loc.T("Stamp.Name.Idea"), dc =>
        {
            dc.DrawGeometry(Yellow, Stroke(Solid("#F9A825"), 2.5), Geo("M32,4 A20,20 0 0 1 44,40 V46 H20 V40 A20,20 0 0 1 32,4 Z"));
            dc.DrawRoundedRectangle(Grey, null, new Rect(21, 48, 22, 6), 2, 2);
            dc.DrawRoundedRectangle(Grey, null, new Rect(24, 55, 16, 5), 2, 2);
            dc.DrawGeometry(null, Stroke(Solid("#F9A825"), 2.5), Geo("M27,40 V30 L32,25 L37,30 V40"));
        });
        Add(Classic, "lock", Loc.T("Stamp.Name.Lock"), dc =>
        {
            dc.DrawGeometry(null, Stroke(Grey, 7), Geo("M20,30 V20 A12,12 0 0 1 44,20 V30"));
            dc.DrawRoundedRectangle(Solid("#FFB300"), Stroke(Solid("#F57F17"), 2.5), new Rect(10, 28, 44, 32), 6, 6);
            dc.DrawEllipse(Ink, null, new Point(32, 41), 4.5, 4.5);
            dc.DrawRectangle(Ink, null, new Rect(30.5, 42, 3, 9));
        });
        var newWord = Loc.T("Stamp.Name.New");
        // Wider only when the translated word needs it, so the French stamp keeps its proportions.
        double newWidth = Math.Max(96, Math.Ceiling(Measure(newWord, 19, Bold).Width) + 20);
        Add(Classic, "new", newWord, dc =>
        {
            dc.DrawRoundedRectangle(Orange, null, new Rect(2, 2, newWidth - 4, 36), 8, 8);
            Text(dc, newWord, new Point(newWidth / 2, 20), 19, White, Bold);
        }, newWidth, 40);
        foreach (var (color, colorKey, brush) in new[] { ("green", "Green", Green), ("red", "Red", Red) })
        {
            foreach (var (direction, directionKey, angle) in new[] { ("left", "Left", 180.0), ("right", "Right", 0.0), ("up", "Up", -90.0), ("down", "Down", 90.0) })
            {
                // Stamp.Name.ArrowLeftGreen, Stamp.Name.ArrowUpRed...
                Add(Classic, $"arrow-{direction}-{color}", Loc.T($"Stamp.Name.Arrow{directionKey}{colorKey}"), dc =>
                {
                    dc.PushTransform(new RotateTransform(angle, 32, 32));
                    dc.DrawGeometry(brush, null, Geo("M5,23 H33 V8 L59,32 L33,56 V41 H5 Z"));
                    dc.Pop();
                });
            }
        }

        // ---- Text (ink stamps) ----
        // The ids come from the original French words and must stay as they are.
        foreach (var (id, key, hex) in new[]
                 {
                     ("text-approuvé", "Approved", "#2E7D32"), ("text-validé", "Validated", "#2E7D32"),
                     ("text-vrai", "True", "#2E7D32"), ("text-terminé", "Done", "#2E7D32"),
                     ("text-refusé", "Refused", "#C62828"), ("text-faux", "False", "#C62828"),
                     ("text-annulé", "Cancelled", "#C62828"), ("text-confidentiel", "Confidential", "#C62828"),
                     ("text-urgent", "Urgent", "#C62828"), ("text-important", "Important", "#EF6C00"),
                     ("text-à corriger", "ToFix", "#EF6C00"), ("text-brouillon", "Draft", "#616161"),
                     ("text-en cours", "InProgress", "#1565C0"), ("text-avant", "Before", "#1565C0"),
                     ("text-après", "After", "#1565C0"),
                 })
        {
            var text = Loc.T("Stamp.Text." + key);
            var size = Measure(text, 26, Black);
            double width = Math.Ceiling(size.Width) + 32, height = 52;
            Add(TextStamps, id, text, dc => InkStamp(dc, text, Solid(hex, 0xE6), width, height), width, height);
        }

        // ---- Mouse ----
        Add(Mouse, "cursor", Loc.T("Stamp.Name.Cursor"), dc => Cursor(dc, 18, 6));
        Add(Mouse, "cursor-click", Loc.T("Stamp.Name.CursorClick"), dc =>
        {
            Cursor(dc, 22, 12);
            ClickRays(dc, new Point(22, 12));
        });
        Add(Mouse, "cursor-ripple", Loc.T("Stamp.Name.CursorRipple"), dc =>
        {
            Ripple(dc, new Point(24, 16));
            Cursor(dc, 24, 16);
        });
        Add(Mouse, "hand", Loc.T("Stamp.Name.Hand"), dc => Hand(dc, 0, 0));
        Add(Mouse, "hand-click", Loc.T("Stamp.Name.HandClick"), dc =>
        {
            Hand(dc, 6, 8);
            ClickRays(dc, new Point(36, 14));
        });
        Add(Mouse, "hand-ripple", Loc.T("Stamp.Name.HandRipple"), dc =>
        {
            Ripple(dc, new Point(33, 12));
            Hand(dc, 3, 6);
        });
        Add(Mouse, "mouse", Loc.T("Stamp.Name.Mouse"), dc => MouseDevice(dc, null, null));
        Add(Mouse, "mouse-left", Loc.T("Stamp.Name.MouseLeft"), dc => MouseDevice(dc, true, "1×"));
        Add(Mouse, "mouse-left-double", Loc.T("Stamp.Name.MouseDouble"), dc => MouseDevice(dc, true, "2×"));
        Add(Mouse, "mouse-right", Loc.T("Stamp.Name.MouseRight"), dc => MouseDevice(dc, false, "1×"));
        foreach (var (id, key) in new[]
                 {
                     ("label-cliquez ici", "ClickHere"), ("label-double-clic", "DoubleClick"), ("label-clic gauche", "LeftClick"),
                     ("label-clic droit", "RightClick"), ("label-glisser-déposer", "DragDrop"),
                 })
        {
            var label = Loc.T("Stamp.Label." + key);
            var size = Measure(label, 14, SemiBold);
            double width = Math.Ceiling(size.Width) + 52, height = 56;
            Add(Mouse, id, label, dc =>
            {
                dc.DrawRoundedRectangle(Blue, Stroke(White, 2), new Rect(18, 24, width - 20, 28), 14, 14);
                Text(dc, label, new Point(18 + (width - 20) / 2, 38), 14, White, SemiBold);
                dc.PushTransform(new ScaleTransform(0.55, 0.55));
                Cursor(dc, 12, 6);
                dc.Pop();
            }, width, height);
        }
        return list;
    }

    // ---- Drawing helpers ----

    private static Drawing Record(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) draw(dc);
        group.Freeze();
        return group;
    }

    private static void Bubble(DrawingContext dc, string symbol)
    {
        dc.DrawGeometry(White, Stroke(Solid("#616161"), 2.5),
            Geo("M9,8 H55 A5,5 0 0 1 60,13 V41 A5,5 0 0 1 55,46 H29 L16,58 V46 H9 A5,5 0 0 1 4,41 V13 A5,5 0 0 1 9,8 Z"));
        Text(dc, symbol, new Point(32, 27), 30, Ink, Black);
    }

    private static void InkStamp(DrawingContext dc, string text, Brush ink, double width, double height)
    {
        dc.DrawRoundedRectangle(null, new Pen(ink, 3.5), new Rect(2, 2, width - 4, height - 4), 5, 5);
        dc.DrawRoundedRectangle(null, new Pen(ink, 1.5), new Rect(7.5, 7.5, width - 15, height - 15), 3, 3);
        Text(dc, text, new Point(width / 2, height / 2), 26, ink, Black);
    }

    private static void DrawBug(DrawingContext dc)
    {
        var legs = Stroke(DarkRed, 3.5);
        dc.DrawGeometry(null, legs, Geo("M20,31 L8,25 M19,41 L5,41 M21,51 L9,58 M44,31 L56,25 M45,41 L59,41 M43,51 L55,58 M28,12 L22,3 M36,12 L42,3"));
        dc.DrawEllipse(Red, null, new Point(32, 40), 14, 18);
        dc.DrawEllipse(DarkRed, null, new Point(32, 17), 8.5, 8);
        dc.DrawLine(new Pen(White, 2), new Point(32, 24), new Point(32, 57));
        foreach (var p in new[] { new Point(25, 33), new Point(39, 33), new Point(25, 46), new Point(39, 46) })
            dc.DrawEllipse(White, null, p, 2.6, 2.6);
    }

    /// <summary>Arrow pointer with its tip at (x, y).</summary>
    private static void Cursor(DrawingContext dc, double x, double y)
    {
        dc.PushTransform(new TranslateTransform(x - 16, y - 6));
        dc.DrawGeometry(White, new Pen(Ink, 2.5) { LineJoin = PenLineJoin.Round }, Geo("M16,6 V50 L27,40 L35,57 L43,53 L35,37 H50 Z"));
        dc.Pop();
    }

    /// <summary>Pointing hand, index fingertip near (27 + dx, 6 + dy).</summary>
    private static void Hand(DrawingContext dc, double dx, double dy)
    {
        dc.PushTransform(new TranslateTransform(dx, dy));
        dc.PushTransform(new ScaleTransform(0.92, 0.92, 32, 32));
        dc.DrawGeometry(White, new Pen(Ink, 2.5) { LineJoin = PenLineJoin.Round },
            Geo("M27,10 C27,5 33,5 33,10 V28 C33,24 39,24 39,28 V31 C39,27 45,27 45,31 V34 C45,30 51,30 51,34 V46 C51,54 46,60 38,60 H31 C26,60 23,57 20,53 L12,41 C10,38 13,34 17,36 L27,44 Z"));
        dc.DrawGeometry(null, new Pen(Ink, 1.5) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, Geo("M33,28 V38 M39,31 V38 M45,34 V39"));
        dc.Pop();
        dc.Pop();
    }

    private static void ClickRays(DrawingContext dc, Point tip)
    {
        var pen = Stroke(Orange, 3);
        foreach (var (dx, dy) in new[] { (-1.0, 0.0), (-0.75, -0.75), (0.0, -1.0), (0.75, -0.75), (-0.75, 0.75) })
            dc.DrawLine(pen, new Point(tip.X + dx * 6, tip.Y + dy * 6), new Point(tip.X + dx * 13, tip.Y + dy * 13));
    }

    private static void Ripple(DrawingContext dc, Point center)
    {
        for (int i = 0; i < 3; i++)
        {
            var color = Color.FromArgb((byte)(230 - i * 60), 0x1E, 0x88, 0xE5);
            dc.DrawEllipse(null, new Pen(new SolidColorBrush(color), 3), center, 6 + i * 6, 6 + i * 6);
        }
    }

    /// <param name="left">True: left button highlighted, false: right, null: none.</param>
    private static void MouseDevice(DrawingContext dc, bool? left, string? badge)
    {
        var outline = new Pen(Ink, 2.5);
        dc.DrawRoundedRectangle(White, null, new Rect(18, 6, 28, 54), 14, 14);
        if (left == true) dc.DrawGeometry(Blue, null, Geo("M32,6 A14,14 0 0 0 18,20 V26 H32 Z"));
        if (left == false) dc.DrawGeometry(Blue, null, Geo("M32,6 A14,14 0 0 1 46,20 V26 H32 Z"));
        dc.DrawGeometry(null, outline, Geo("M32,6 V26 M18,26 H46"));
        dc.DrawRoundedRectangle(Grey, null, new Rect(30, 12, 4, 9), 2, 2);
        dc.DrawRoundedRectangle(null, outline, new Rect(18, 6, 28, 54), 14, 14);
        if (badge != null)
        {
            var center = left == false ? new Point(53, 10) : new Point(11, 10);
            dc.DrawEllipse(Blue, new Pen(White, 2), center, 10, 10);
            Text(dc, badge, center, 10.5, White, Bold);
        }
    }

    private static Geometry StarGeometry(Point center, double outer, double inner, int points)
    {
        var figure = new PathFigure { IsClosed = true, IsFilled = true };
        for (int i = 0; i < points * 2; i++)
        {
            double r = i % 2 == 0 ? outer : inner;
            double a = -Math.PI / 2 + i * Math.PI / points;
            var p = new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
            if (i == 0) figure.StartPoint = p;
            else figure.Segments.Add(new LineSegment(p, true) { IsSmoothJoin = true });
        }
        var geometry = new PathGeometry(new[] { figure });
        geometry.Freeze();
        return geometry;
    }

    private static void Text(DrawingContext dc, string text, Point center, double size, Brush brush, Typeface typeface)
    {
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, typeface, size, brush, 1.0);
        dc.DrawText(formatted, new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2));
    }

    private static Size Measure(string text, double size, Typeface typeface)
    {
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, typeface, size, Brushes.Black, 1.0);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    private static Geometry Geo(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }

    private static Pen Stroke(Brush brush, double thickness, PenLineCap cap = PenLineCap.Round)
    {
        var pen = new Pen(brush, thickness) { StartLineCap = cap, EndLineCap = cap, LineJoin = PenLineJoin.Round };
        pen.Freeze();
        return pen;
    }

    private static Brush Solid(string hex, byte alpha = 0xFF)
    {
        var color = HexColor.Parse(hex);
        color.A = alpha;
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
