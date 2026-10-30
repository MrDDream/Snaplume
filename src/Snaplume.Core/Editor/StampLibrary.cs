using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>A vector stamp drawn in its own coordinate box (Width × Height).</summary>
public sealed class StampDefinition
{
    public required string Id { get; init; }
    public required string Category { get; init; }
    public required string Name { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    /// <summary>Draws the stamp in its own box; geometries, pens and texts are built once and replayed.</summary>
    public required Action<DrawingContext> Draw { get; init; }

    /// <summary>Draws the stamp fitted (uniformly, centered) into the target rectangle.</summary>
    public void Render(DrawingContext dc, Rect target)
    {
        if (target.Width < 1 || target.Height < 1) return;
        double scale = Math.Min(target.Width / Width, target.Height / Height);
        double x = target.X + (target.Width - Width * scale) / 2;
        double y = target.Y + (target.Height - Height * scale) / 2;
        using (dc.PushTransform(new Matrix(scale, 0, 0, scale, x, y))) Draw(dc);
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

    private static readonly Typeface Bold = new(EditorFonts.Family, FontStyle.Normal, FontWeight.Bold);
    private static readonly Typeface Black = new(EditorFonts.Family, FontStyle.Normal, FontWeight.Black);
    private static readonly Typeface SemiBold = new(EditorFonts.Family, FontStyle.Normal, FontWeight.SemiBold);

    private static readonly IImmutableBrush White = Solid("#FFFFFF");
    private static readonly IImmutableBrush Ink = Solid("#212121");
    private static readonly IImmutableBrush Green = Solid("#43A047");
    private static readonly IImmutableBrush Red = Solid("#E53935");
    private static readonly IImmutableBrush DarkRed = Solid("#B71C1C");
    private static readonly IImmutableBrush Yellow = Solid("#FDD835");
    private static readonly IImmutableBrush Orange = Solid("#FB8C00");
    private static readonly IImmutableBrush Blue = Solid("#1E88E5");
    private static readonly IImmutableBrush Grey = Solid("#9E9E9E");

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
        // The geometries and pens are made here, once: the drawing actions only replay them.
        void Add(string category, string id, string name, Action<DrawingContext> draw, double width = 64, double height = 64) =>
            list.Add(new StampDefinition { Category = category, Id = id, Name = name, Width = width, Height = height, Draw = draw });

        // ---- Classic ----
        {
            var check = Geo("M18,33 L28,43 L46,22");
            var pen = Stroke(White, 7);
            Add(Classic, "ok-circle", Loc.T("Stamp.Name.OkCircle"), dc =>
            {
                dc.DrawEllipse(Green, null, new Point(32, 32), 29, 29);
                dc.DrawGeometry(null, pen, check);
            });
        }
        {
            var check = Geo("M9,34 L25,50 L56,14");
            var pen = Stroke(Green, 11);
            Add(Classic, "ok", Loc.T("Stamp.Name.Ok"), dc => dc.DrawGeometry(null, pen, check));
        }
        {
            var cross = Geo("M21,21 L43,43 M43,21 L21,43");
            var pen = Stroke(White, 7);
            Add(Classic, "ko-circle", Loc.T("Stamp.Name.KoCircle"), dc =>
            {
                dc.DrawEllipse(Red, null, new Point(32, 32), 29, 29);
                dc.DrawGeometry(null, pen, cross);
            });
        }
        {
            var cross = Geo("M13,13 L51,51 M51,13 L13,51");
            var pen = Stroke(Red, 12);
            Add(Classic, "ko", Loc.T("Stamp.Name.Ko"), dc => dc.DrawGeometry(null, pen, cross));
        }
        Add(Classic, "question", Loc.T("Stamp.Name.Question"), Bubble("?"));
        Add(Classic, "remark", Loc.T("Stamp.Name.Remark"), Bubble("!"));
        {
            var mark = Text("!", new Point(32, 32), 40, White, Black);
            Add(Classic, "alert", Loc.T("Stamp.Name.Alert"), dc =>
            {
                dc.DrawEllipse(Red, null, new Point(32, 32), 29, 29);
                mark(dc);
            });
        }
        {
            var triangle = Geo("M32,6 L60,56 H4 Z");
            var pen = Stroke(Solid("#F9A825"), 3);
            var mark = Text("!", new Point(32, 39), 32, Ink, Black);
            Add(Classic, "warning", Loc.T("Stamp.Name.Warning"), dc =>
            {
                dc.DrawGeometry(Yellow, pen, triangle);
                mark(dc);
            });
        }
        {
            var triangle = Geo("M32,6 L60,56 H4 Z");
            var pen = Stroke(DarkRed, 3);
            var mark = Text("!", new Point(32, 39), 32, White, Black);
            Add(Classic, "danger", Loc.T("Stamp.Name.Danger"), dc =>
            {
                dc.DrawGeometry(Red, pen, triangle);
                mark(dc);
            });
        }
        {
            var mark = Text("i", new Point(32, 33), 40, White, Black);
            Add(Classic, "info", Loc.T("Stamp.Name.Info"), dc =>
            {
                dc.DrawEllipse(Blue, null, new Point(32, 32), 29, 29);
                mark(dc);
            });
        }
        {
            var ring = Stroke(Red, 7);
            var bar = Stroke(Red, 7, PenLineCap.Flat);
            var line = Geo("M15,15 L49,49");
            Add(Classic, "forbidden", Loc.T("Stamp.Name.Forbidden"), dc =>
            {
                dc.DrawEllipse(White, ring, new Point(32, 32), 25, 25);
                dc.DrawGeometry(null, bar, line);
            });
        }
        Add(Classic, "bug", Loc.T("Stamp.Name.Bug"), Bug());
        {
            var star = StarGeometry(new Point(32, 34), 30, 12.5, 5);
            var pen = Stroke(Solid("#F9A825"), 2.5);
            Add(Classic, "star", Loc.T("Stamp.Name.Star"), dc => dc.DrawGeometry(Yellow, pen, star));
        }
        {
            var bulb = Geo("M32,4 A20,20 0 0 1 44,40 V46 H20 V40 A20,20 0 0 1 32,4 Z");
            var filament = Geo("M27,40 V30 L32,25 L37,30 V40");
            var pen = Stroke(Solid("#F9A825"), 2.5);
            Add(Classic, "idea", Loc.T("Stamp.Name.Idea"), dc =>
            {
                dc.DrawGeometry(Yellow, pen, bulb);
                dc.DrawRectangle(Grey, null, new Rect(21, 48, 22, 6), 2, 2);
                dc.DrawRectangle(Grey, null, new Rect(24, 55, 16, 5), 2, 2);
                dc.DrawGeometry(null, pen, filament);
            });
        }
        {
            var shackle = Geo("M20,30 V20 A12,12 0 0 1 44,20 V30");
            var shacklePen = Stroke(Grey, 7);
            var bodyPen = Stroke(Solid("#F57F17"), 2.5);
            var body = Solid("#FFB300");
            Add(Classic, "lock", Loc.T("Stamp.Name.Lock"), dc =>
            {
                dc.DrawGeometry(null, shacklePen, shackle);
                dc.DrawRectangle(body, bodyPen, new Rect(10, 28, 44, 32), 6, 6);
                dc.DrawEllipse(Ink, null, new Point(32, 41), 4.5, 4.5);
                dc.DrawRectangle(Ink, null, new Rect(30.5, 42, 3, 9));
            });
        }
        {
            var newWord = Loc.T("Stamp.Name.New");
            // Wider only when the translated word needs it, so the French stamp keeps its proportions.
            double newWidth = Math.Max(96, Math.Ceiling(Measure(newWord, 19, Bold).Width) + 20);
            var word = Text(newWord, new Point(newWidth / 2, 20), 19, White, Bold);
            Add(Classic, "new", newWord, dc =>
            {
                dc.DrawRectangle(Orange, null, new Rect(2, 2, newWidth - 4, 36), 8, 8);
                word(dc);
            }, newWidth, 40);
        }
        var arrow = Geo("M5,23 H33 V8 L59,32 L33,56 V41 H5 Z");
        foreach (var (color, colorKey, brush) in new[] { ("green", "Green", Green), ("red", "Red", Red) })
        {
            foreach (var (direction, directionKey, angle) in new[] { ("left", "Left", 180.0), ("right", "Right", 0.0), ("up", "Up", -90.0), ("down", "Down", 90.0) })
            {
                var rotation = RotateAround(angle, 32, 32);
                // Stamp.Name.ArrowLeftGreen, Stamp.Name.ArrowUpRed...
                Add(Classic, $"arrow-{direction}-{color}", Loc.T($"Stamp.Name.Arrow{directionKey}{colorKey}"), dc =>
                {
                    using (dc.PushTransform(rotation)) dc.DrawGeometry(brush, null, arrow);
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
            Add(TextStamps, id, text, InkStamp(text, Solid(hex, 0xE6), width, height), width, height);
        }

        // ---- Mouse ----
        var cursor = Cursor();
        var hand = Hand();
        Add(Mouse, "cursor", Loc.T("Stamp.Name.Cursor"), dc => cursor(dc, 18, 6));
        {
            var rays = ClickRays(new Point(22, 12));
            Add(Mouse, "cursor-click", Loc.T("Stamp.Name.CursorClick"), dc =>
            {
                cursor(dc, 22, 12);
                rays(dc);
            });
        }
        {
            var ripple = Ripple(new Point(24, 16));
            Add(Mouse, "cursor-ripple", Loc.T("Stamp.Name.CursorRipple"), dc =>
            {
                ripple(dc);
                cursor(dc, 24, 16);
            });
        }
        Add(Mouse, "hand", Loc.T("Stamp.Name.Hand"), dc => hand(dc, 0, 0));
        {
            var rays = ClickRays(new Point(36, 14));
            Add(Mouse, "hand-click", Loc.T("Stamp.Name.HandClick"), dc =>
            {
                hand(dc, 6, 8);
                rays(dc);
            });
        }
        {
            var ripple = Ripple(new Point(33, 12));
            Add(Mouse, "hand-ripple", Loc.T("Stamp.Name.HandRipple"), dc =>
            {
                ripple(dc);
                hand(dc, 3, 6);
            });
        }
        Add(Mouse, "mouse", Loc.T("Stamp.Name.Mouse"), MouseDevice(null, null));
        Add(Mouse, "mouse-left", Loc.T("Stamp.Name.MouseLeft"), MouseDevice(true, "1×"));
        Add(Mouse, "mouse-left-double", Loc.T("Stamp.Name.MouseDouble"), MouseDevice(true, "2×"));
        Add(Mouse, "mouse-right", Loc.T("Stamp.Name.MouseRight"), MouseDevice(false, "1×"));
        var smallCursor = Matrix.CreateScale(0.55, 0.55);
        var labelPen = Stroke(White, 2);
        foreach (var (id, key) in new[]
                 {
                     ("label-cliquez ici", "ClickHere"), ("label-double-clic", "DoubleClick"), ("label-clic gauche", "LeftClick"),
                     ("label-clic droit", "RightClick"), ("label-glisser-déposer", "DragDrop"),
                 })
        {
            var label = Loc.T("Stamp.Label." + key);
            var size = Measure(label, 14, SemiBold);
            double width = Math.Ceiling(size.Width) + 52, height = 56;
            var text = Text(label, new Point(18 + (width - 20) / 2, 38), 14, White, SemiBold);
            Add(Mouse, id, label, dc =>
            {
                dc.DrawRectangle(Blue, labelPen, new Rect(18, 24, width - 20, 28), 14, 14);
                text(dc);
                using (dc.PushTransform(smallCursor)) cursor(dc, 12, 6);
            }, width, height);
        }
        return list;
    }

    // ---- Drawing helpers: each one prepares its geometries and returns what replays them ----

    private static Action<DrawingContext> Bubble(string symbol)
    {
        var outline = Geo("M9,8 H55 A5,5 0 0 1 60,13 V41 A5,5 0 0 1 55,46 H29 L16,58 V46 H9 A5,5 0 0 1 4,41 V13 A5,5 0 0 1 9,8 Z");
        var pen = Stroke(Solid("#616161"), 2.5);
        var mark = Text(symbol, new Point(32, 27), 30, Ink, Black);
        return dc =>
        {
            dc.DrawGeometry(White, pen, outline);
            mark(dc);
        };
    }

    private static Action<DrawingContext> InkStamp(string text, IImmutableBrush ink, double width, double height)
    {
        var outer = new ImmutablePen(ink, 3.5);
        var inner = new ImmutablePen(ink, 1.5);
        var word = Text(text, new Point(width / 2, height / 2), 26, ink, Black);
        return dc =>
        {
            dc.DrawRectangle(null, outer, new Rect(2, 2, width - 4, height - 4), 5, 5);
            dc.DrawRectangle(null, inner, new Rect(7.5, 7.5, width - 15, height - 15), 3, 3);
            word(dc);
        };
    }

    private static Action<DrawingContext> Bug()
    {
        var legs = Stroke(DarkRed, 3.5);
        var legLines = Geo("M20,31 L8,25 M19,41 L5,41 M21,51 L9,58 M44,31 L56,25 M45,41 L59,41 M43,51 L55,58 M28,12 L22,3 M36,12 L42,3");
        var spine = new ImmutablePen(White, 2);
        return dc =>
        {
            dc.DrawGeometry(null, legs, legLines);
            dc.DrawEllipse(Red, null, new Point(32, 40), 14, 18);
            dc.DrawEllipse(DarkRed, null, new Point(32, 17), 8.5, 8);
            dc.DrawLine(spine, new Point(32, 24), new Point(32, 57));
            foreach (var p in new[] { new Point(25, 33), new Point(39, 33), new Point(25, 46), new Point(39, 46) })
                dc.DrawEllipse(White, null, p, 2.6, 2.6);
        };
    }

    /// <summary>Arrow pointer drawn with its tip at (x, y).</summary>
    private static Action<DrawingContext, double, double> Cursor()
    {
        var shape = Geo("M16,6 V50 L27,40 L35,57 L43,53 L35,37 H50 Z");
        var pen = new ImmutablePen(Ink, 2.5, null, PenLineCap.Flat, PenLineJoin.Round);
        return (dc, x, y) =>
        {
            using (dc.PushTransform(Matrix.CreateTranslation(x - 16, y - 6))) dc.DrawGeometry(White, pen, shape);
        };
    }

    /// <summary>Pointing hand drawn with its index fingertip near (27 + dx, 6 + dy).</summary>
    private static Action<DrawingContext, double, double> Hand()
    {
        var shape = Geo("M27,10 C27,5 33,5 33,10 V28 C33,24 39,24 39,28 V31 C39,27 45,27 45,31 V34 C45,30 51,30 51,34 V46 C51,54 46,60 38,60 H31 C26,60 23,57 20,53 L12,41 C10,38 13,34 17,36 L27,44 Z");
        var fingers = Geo("M33,28 V38 M39,31 V38 M45,34 V39");
        var outline = new ImmutablePen(Ink, 2.5, null, PenLineCap.Flat, PenLineJoin.Round);
        var thin = new ImmutablePen(Ink, 1.5, null, PenLineCap.Round);
        var scale = ScaleAround(0.92, 32, 32);
        return (dc, dx, dy) =>
        {
            using (dc.PushTransform(scale * Matrix.CreateTranslation(dx, dy)))
            {
                dc.DrawGeometry(White, outline, shape);
                dc.DrawGeometry(null, thin, fingers);
            }
        };
    }

    private static Action<DrawingContext> ClickRays(Point tip)
    {
        var pen = Stroke(Orange, 3);
        var rays = new[] { (-1.0, 0.0), (-0.75, -0.75), (0.0, -1.0), (0.75, -0.75), (-0.75, 0.75) }
            .Select(d => (new Point(tip.X + d.Item1 * 6, tip.Y + d.Item2 * 6), new Point(tip.X + d.Item1 * 13, tip.Y + d.Item2 * 13)))
            .ToArray();
        return dc =>
        {
            foreach (var (from, to) in rays) dc.DrawLine(pen, from, to);
        };
    }

    private static Action<DrawingContext> Ripple(Point center)
    {
        var pens = Enumerable.Range(0, 3)
            .Select(i => new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb((byte)(230 - i * 60), 0x1E, 0x88, 0xE5)), 3))
            .ToArray();
        return dc =>
        {
            for (int i = 0; i < pens.Length; i++) dc.DrawEllipse(null, pens[i], center, 6 + i * 6, 6 + i * 6);
        };
    }

    /// <param name="left">True: left button highlighted, false: right, null: none.</param>
    private static Action<DrawingContext> MouseDevice(bool? left, string? badge)
    {
        var outline = new ImmutablePen(Ink, 2.5);
        var leftButton = Geo("M32,6 A14,14 0 0 0 18,20 V26 H32 Z");
        var rightButton = Geo("M32,6 A14,14 0 0 1 46,20 V26 H32 Z");
        var lines = Geo("M32,6 V26 M18,26 H46");
        var badgePen = new ImmutablePen(White, 2);
        var center = left == false ? new Point(53, 10) : new Point(11, 10);
        var badgeText = badge == null ? null : Text(badge, center, 10.5, White, Bold);
        return dc =>
        {
            dc.DrawRectangle(White, null, new Rect(18, 6, 28, 54), 14, 14);
            if (left == true) dc.DrawGeometry(Blue, null, leftButton);
            if (left == false) dc.DrawGeometry(Blue, null, rightButton);
            dc.DrawGeometry(null, outline, lines);
            dc.DrawRectangle(Grey, null, new Rect(30, 12, 4, 9), 2, 2);
            dc.DrawRectangle(null, outline, new Rect(18, 6, 28, 54), 14, 14);
            if (badgeText != null)
            {
                dc.DrawEllipse(Blue, badgePen, center, 10, 10);
                badgeText(dc);
            }
        };
    }

    private static Geometry StarGeometry(Point center, double outer, double inner, int points)
    {
        var geometry = new StreamGeometry();
        using (var ctx = geometry.Open())
        {
            for (int i = 0; i < points * 2; i++)
            {
                double r = i % 2 == 0 ? outer : inner;
                double a = -Math.PI / 2 + i * Math.PI / points;
                var p = new Point(center.X + r * Math.Cos(a), center.Y + r * Math.Sin(a));
                if (i == 0) ctx.BeginFigure(p, true);
                else ctx.LineTo(p);
            }
            ctx.EndFigure(true);
        }
        return geometry;
    }

    /// <summary>A text centered on a point, laid out once.</summary>
    private static Action<DrawingContext> Text(string text, Point center, double size, IBrush brush, Typeface typeface)
    {
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, typeface, size, brush);
        var origin = new Point(center.X - formatted.Width / 2, center.Y - formatted.Height / 2);
        return dc => dc.DrawText(formatted, origin);
    }

    private static Size Measure(string text, double size, Typeface typeface)
    {
        var formatted = new FormattedText(text, Loc.Culture, FlowDirection.LeftToRight, typeface, size, Brushes.Black);
        return new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
    }

    private static Matrix RotateAround(double degrees, double x, double y) =>
        Matrix.CreateTranslation(-x, -y) * Matrix.CreateRotation(degrees * Math.PI / 180) * Matrix.CreateTranslation(x, y);

    private static Matrix ScaleAround(double scale, double x, double y) =>
        Matrix.CreateTranslation(-x, -y) * Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(x, y);

    private static Geometry Geo(string data) => Geometry.Parse(data);

    private static IPen Stroke(IImmutableBrush brush, double thickness, PenLineCap cap = PenLineCap.Round) =>
        new ImmutablePen(brush, thickness, null, cap, PenLineJoin.Round);

    private static IImmutableBrush Solid(string hex, byte alpha = 0xFF)
    {
        var color = HexColor.Parse(hex);
        return new ImmutableSolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }
}
