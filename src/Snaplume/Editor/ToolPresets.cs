using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>Saved drawing style of a tool, offered when its button is clicked in the tool rail.</summary>
public sealed record ToolPreset(EditorTool Tool, DrawingStyle Style)
{
    private const double PreviewWidth = 140;
    private const double PreviewHeight = 56;

    [JsonIgnore]
    public string Description => Tool switch
    {
        EditorTool.Text => Loc.T(Style.Fill ? "Preset.Desc.TextOnWhite" : "Preset.Desc.Text", Style.FontSize),
        EditorTool.Step => Loc.T("Preset.Desc.Step", Style.Thickness),
        EditorTool.Highlight => Loc.T("Preset.Desc.Highlight"),
        EditorTool.Arrow when Style.Heads == ArrowHeads.Both => Loc.T("Preset.Desc.DoubleArrow", Style.Thickness),
        EditorTool.Arrow when Style.Heads == ArrowHeads.None => Loc.T("Preset.Desc.NoHead", Style.Thickness),
        EditorTool.Rectangle or EditorTool.Ellipse when Style.Fill => Loc.T("Preset.Desc.Filled"),
        _ => Loc.T("Preset.Desc.Thickness", Style.Thickness),
    };

    /// <summary>The preset drawn on a small sample, at the size it will have on the image.</summary>
    public ImageSource CreatePreview()
    {
        var area = new Rect(0, 0, PreviewWidth, PreviewHeight);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            dc.DrawRectangle(Brushes.Transparent, null, area);
            dc.PushClip(new RectangleGeometry(area));
            if (Tool == EditorTool.Highlight)
            {
                // Fake lines of text under the highlighter.
                var ink = new SolidColorBrush(Color.FromRgb(0x9C, 0xA3, 0xAF));
                dc.DrawRoundedRectangle(ink, null, new Rect(18, 20, 104, 6), 3, 3);
                dc.DrawRoundedRectangle(ink, null, new Rect(18, 32, 72, 6), 3, 3);
            }
            // Bullets are big: shown at 75 % so the pin and the tail fit.
            if (Tool == EditorTool.Step) dc.PushTransform(new ScaleTransform(0.75, 0.75, PreviewWidth / 2, PreviewHeight / 2));
            CreateSample().Render(dc, Placeholder);
            if (Tool == EditorTool.Step) dc.Pop();
            dc.Pop();
        }
        group.Freeze();
        var image = new DrawingImage(group);
        image.Freeze();
        return image;
    }

    private Annotation CreateSample()
    {
        Annotation sample = Tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation { Start = new(14, 28), End = new(126, 28) },
            EditorTool.Line => new LineAnnotation { Start = new(14, 28), End = new(126, 28) },
            EditorTool.Rectangle => new RectangleAnnotation { Start = new(30, 12), End = new(110, 44) },
            EditorTool.Ellipse => new EllipseAnnotation { Start = new(30, 10), End = new(110, 46) },
            EditorTool.Highlight => new HighlightAnnotation { Start = new(12, 14), End = new(128, 44) },
            EditorTool.Step => new StepAnnotation
            {
                Number = 1,
                Center = Style.Shape switch { StepShape.Leader => new(96, 28), StepShape.Tail => new(86, 28), StepShape.Pin => new(70, 20), _ => new(70, 28) },
                Target = Style.Shape switch { StepShape.Leader => new Point(26, 28), StepShape.Tail => new Point(20, 28), _ => null },
            },
            EditorTool.Text => new TextAnnotation { Text = Loc.T("Preset.SampleText") },
            _ => new FreehandAnnotation { Points = Wave() },
        };
        Style.ApplyTo(sample);
        if (sample is TextAnnotation text)
        {
            text.FontSize = Math.Min(text.FontSize, 28);
            var size = text.GetBounds().Size;
            text.Position = new(PreviewWidth / 2 - size.Width / 2, PreviewHeight / 2 - size.Height / 2);
        }
        return sample;
    }

    private static List<Point> Wave() =>
        Enumerable.Range(0, 41).Select(i => new Point(16 + i * 2.7, 28 + 11 * Math.Sin(i / 40.0 * 2 * Math.PI))).ToList();

    private static readonly BitmapSource Placeholder =
        BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);
}

/// <summary>Presets of every tool, stored in presets.json next to the settings.</summary>
public static class ToolPresets
{
    private static readonly EditorTool[] SupportedTools =
    {
        EditorTool.Arrow, EditorTool.Line, EditorTool.Rectangle, EditorTool.Ellipse, EditorTool.Pen,
        EditorTool.Text, EditorTool.Step, EditorTool.Highlight,
    };

    /// <summary>One entry of presets.json, in the flat format of the first versions (still read by them).</summary>
    private sealed class StoredPreset
    {
        public EditorTool Tool { get; set; }
        public Color Color { get; set; } = DrawingStyle.Default.Color;
        public double Thickness { get; set; } = DrawingStyle.Default.Thickness;
        public bool Filled { get; set; }
        public double FontSize { get; set; } = DrawingStyle.Default.FontSize;
        public ArrowHeads Heads { get; set; }
        public StepShape Shape { get; set; }
        public bool Letters { get; set; }
    }

    private static List<ToolPreset>? _all;

    private static string FilePath => Path.Combine(AppSettings.SettingsFolder, "presets.json");

    private static List<ToolPreset> All => _all ??= Load();

    public static bool Supports(EditorTool tool) => SupportedTools.Contains(tool);

    public static IReadOnlyList<ToolPreset> For(EditorTool tool) => All.Where(p => p.Tool == tool).ToList();

    /// <summary>Adds the preset unless an identical one exists. Returns false if it was already there.</summary>
    public static bool Add(ToolPreset preset)
    {
        if (All.Contains(preset)) return false;
        All.Add(preset);
        Save();
        return true;
    }

    public static void Remove(ToolPreset preset)
    {
        All.Remove(preset);
        Save();
    }

    public static void Reset(EditorTool tool)
    {
        All.RemoveAll(p => p.Tool == tool);
        All.AddRange(Defaults().Where(p => p.Tool == tool));
        Save();
    }

    private static List<ToolPreset> Load()
    {
        if (!File.Exists(FilePath)) return Defaults();
        try
        {
            var stored = JsonSerializer.Deserialize<List<StoredPreset?>>(File.ReadAllText(FilePath), AnnotationJson.Options)
                         ?? throw new JsonException("Liste de préréglages attendue");
            // Entries that cannot be used are dropped, the others kept.
            return stored.Select(ToPreset).OfType<ToolPreset>().ToList();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecture des préréglages", ex);
            // Kept aside rather than overwritten by the next save.
            try { File.Move(FilePath, FilePath + ".bak", overwrite: true); }
            catch (Exception moveEx) { ErrorLog.Write("Sauvegarde des préréglages illisibles", moveEx); }
            return Defaults();
        }
    }

    private static ToolPreset? ToPreset(StoredPreset? s)
    {
        if (s == null || !Enum.IsDefined(s.Tool) || !SupportedTools.Contains(s.Tool)
            || !Enum.IsDefined(s.Heads) || !Enum.IsDefined(s.Shape)
            || !(s.Thickness >= 1 && s.Thickness <= 100) || !(s.FontSize >= 6 && s.FontSize <= 400))
            return null;
        return new ToolPreset(s.Tool, new DrawingStyle(s.Color, s.Thickness, s.Filled, s.FontSize, s.Heads, s.Shape, s.Letters));
    }

    private static StoredPreset ToStored(ToolPreset p) => new()
    {
        Tool = p.Tool,
        Color = p.Style.Color,
        Thickness = p.Style.Thickness,
        Filled = p.Style.Fill,
        FontSize = p.Style.FontSize,
        Heads = p.Style.Heads,
        Shape = p.Style.Shape,
        Letters = p.Style.Letters,
    };

    private static void Save()
    {
        try
        {
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(All.Select(ToStored).ToList(), AnnotationJson.Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement des préréglages", ex);
        }
    }

    private static Color Hex(string hex) => (Color)ColorConverter.ConvertFromString(hex);

    private static ToolPreset P(EditorTool tool, Color color, double thickness, bool filled = false, double fontSize = 24,
        ArrowHeads heads = ArrowHeads.End, StepShape shape = StepShape.Circle, bool letters = false) =>
        new(tool, new DrawingStyle(color, thickness, filled, fontSize, heads, shape, letters));

    private static List<ToolPreset> Defaults()
    {
        Color red = Hex("#E53935"), pink = Hex("#E91E63"), orange = Hex("#FB8C00"), yellow = Hex("#FDD835"),
              green = Hex("#43A047"), blue = Hex("#1E88E5"), black = Hex("#212121"), white = Colors.White,
              lime = Hex("#8BC34A"), cyan = Hex("#03A9F4"), gray = Hex("#9E9E9E");
        return new()
        {
            P(EditorTool.Arrow, red, 4),
            P(EditorTool.Arrow, pink, 4),
            P(EditorTool.Arrow, lime, 4),
            P(EditorTool.Arrow, cyan, 4),
            P(EditorTool.Arrow, red, 2),
            P(EditorTool.Arrow, red, 3, heads: ArrowHeads.Both),
            P(EditorTool.Arrow, gray, 3, heads: ArrowHeads.None),
            P(EditorTool.Arrow, red, 8),

            P(EditorTool.Line, red, 4),
            P(EditorTool.Line, blue, 3),
            P(EditorTool.Line, black, 2),
            P(EditorTool.Line, yellow, 8),

            P(EditorTool.Rectangle, red, 3),
            P(EditorTool.Rectangle, blue, 3),
            P(EditorTool.Rectangle, green, 3),
            P(EditorTool.Rectangle, black, 2),
            P(EditorTool.Rectangle, yellow, 3, filled: true),
            P(EditorTool.Rectangle, white, 3, filled: true),

            P(EditorTool.Ellipse, red, 3),
            P(EditorTool.Ellipse, blue, 3),
            P(EditorTool.Ellipse, green, 3),
            P(EditorTool.Ellipse, orange, 6),

            P(EditorTool.Pen, red, 4),
            P(EditorTool.Pen, blue, 4),
            P(EditorTool.Pen, black, 2),
            P(EditorTool.Pen, yellow, 12),

            P(EditorTool.Text, red, 4, fontSize: 24),
            P(EditorTool.Text, black, 4, fontSize: 20),
            P(EditorTool.Text, white, 4, fontSize: 24),
            P(EditorTool.Text, red, 4, filled: true, fontSize: 20),
            P(EditorTool.Text, blue, 4, filled: true, fontSize: 32),

            P(EditorTool.Step, red, 3, shape: StepShape.Leader),
            P(EditorTool.Step, pink, 3, shape: StepShape.Leader),
            P(EditorTool.Step, lime, 3, shape: StepShape.Tail),
            P(EditorTool.Step, Hex("#0288D1"), 3, shape: StepShape.Pin, letters: true),
            P(EditorTool.Step, orange, 3, shape: StepShape.Dark),
            P(EditorTool.Step, red, 4),

            P(EditorTool.Highlight, yellow, 4),
            P(EditorTool.Highlight, Hex("#66BB6A"), 4),
            P(EditorTool.Highlight, Hex("#F06292"), 4),
            P(EditorTool.Highlight, Hex("#4FC3F7"), 4),
        };
    }
}
