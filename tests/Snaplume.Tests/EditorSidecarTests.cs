using System.Globalization;
using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>Annotation files: those written by Snaplume 1.1.1 are read, and what is written reads back the same.</summary>
public class EditorSidecarTests
{
    /// <summary>Every kind of annotation as Snaplume 1.1.1 writes them (System.Text.Json, enums as names, "#AARRGGBB", [x, y]).</summary>
    private const string Annotations111 = """
        [
          { "type": "arrow", "Heads": "Both", "Start": [10, 20], "End": [110.5, 60.25], "Color": "#FFE53935", "Thickness": 4 },
          { "type": "line", "Start": [5, 5], "End": [50, 5], "Color": "#FF1E88E5", "Thickness": 2 },
          { "type": "rectangle", "Filled": true, "Start": [30, 40], "End": [1, 2], "Color": "#80FFFFFF", "Thickness": 3 },
          { "type": "ellipse", "Filled": false, "Start": [1, 2], "End": [30, 40], "Color": "#FF000000", "Thickness": 3 },
          { "type": "highlight", "Start": [0, 0], "End": [20, 10], "Color": "#FFFDD835", "Thickness": 4 },
          { "type": "pixelate", "BlockSize": 10, "Start": [0, 0], "End": [20, 10], "Color": "#FFFF0000", "Thickness": 4 },
          { "type": "spotlight", "Start": [0, 0], "End": [20, 10], "Color": "#FFFF0000", "Thickness": 4 },
          { "type": "text", "Position": [12, 14], "Text": "Bonjour\nمرحبا", "FontSize": 32, "HasBackground": true, "Color": "#FF212121", "Thickness": 4 },
          { "type": "step", "Center": [40, 40], "Number": 3, "Shape": "Leader", "Letters": true, "Target": [10, 40], "Color": "#FFE53935", "Thickness": 3 },
          { "type": "step", "Center": [60, 40], "Number": 4, "Shape": "Circle", "Letters": false, "Target": null, "Color": "#FFE53935", "Thickness": 3 },
          { "type": "freehand", "Points": [[1, 1], [2, 3], [4, 5]], "Color": "#FF43A047", "Thickness": 6 },
          { "type": "stamp", "StampId": "text-approuvé", "Start": [10, 10], "End": [90, 62], "Color": "#FFFF0000", "Thickness": 4 },
          { "type": "magnifier", "LensCenter": [80, 40], "Zoom": 2.5, "Shape": "RoundedRect", "Connector": false, "Start": [1, 1], "End": [21, 21], "Color": "#FF000000", "Thickness": 2 },
          { "type": "arrow", "Heads": 2, "Start": [0, 0], "End": [9, 9], "Color": "#FF000000", "Thickness": 1 }
        ]
        """;

    /// <summary>Entries 1.1.1 itself would drop when reading (see Annotation.IsValid), mixed with a good one.</summary>
    private const string InvalidEntries = """
        [
          null,
          { "type": "arrow", "Heads": 7, "Start": [0, 0], "End": [9, 9], "Color": "#FF000000", "Thickness": 1 },
          { "type": "line", "Start": [0, 0], "End": [9, 9], "Color": "#FF000000", "Thickness": 0 },
          { "type": "text", "Position": [0, 0], "Text": "x", "FontSize": 5000, "Color": "#FF000000", "Thickness": 4 },
          { "type": "line", "Start": [0, 1e300], "End": [9, 9], "Color": "#FF000000", "Thickness": 2 },
          { "type": "magnifier", "LensCenter": [80, 40], "Zoom": 50, "Shape": "Circle", "Start": [1, 1], "End": [21, 21], "Color": "#FF000000", "Thickness": 2 },
          { "type": "line", "Start": [1, 2], "End": [3, 4], "Color": "#FF00FF00", "Thickness": 2 }
        ]
        """;

    private const string Effects111 = """
        { "Shadow": true, "ShadowBlur": 18, "ShadowOpacity": 0.4, "ShadowOffsetX": 5, "ShadowOffsetY": 5, "Border": true,
          "BorderColor": "#FF374151", "BorderWidth": 3, "CornerRadius": 12, "TornEdges": "Top, Bottom", "TornSize": 12,
          "TornSeed": 7, "Background": "#FFF3F4F6" }
        """;

    /// <summary>A capture with its base image and a sidecar file in the 1.1.1 layout; returns the capture path.</summary>
    private static string WriteCapture(TempFolder folder, string annotationsJson, string? effectsJson = null, int version = 1,
        Func<string, string>? tamper = null)
    {
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(120, 80, Colors.SteelBlue), path);
        var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
        Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
        ImageIO.Save(Solid(120, 80, Colors.White), basePath);
        var file = new FileInfo(path);
        var json = $$"""
            {
              "Version": {{version}},
              "FileLength": {{file.Length}},
              "FileWriteTimeUtc": "{{file.LastWriteTimeUtc.ToString("O", CultureInfo.InvariantCulture)}}",
              "BaseFileLength": {{new FileInfo(basePath).Length}},
              {{(effectsJson == null ? "" : $"\"Effects\": {effectsJson},")}}
              "Annotations": {{annotationsJson}}
            }
            """;
        File.WriteAllText(jsonPath, tamper?.Invoke(json) ?? json);
        return path;
    }

    [Theory]
    [InlineData("#FF112233", 0xFF, 0x11, 0x22, 0x33)]
    [InlineData("#80aBcDeF", 0x80, 0xAB, 0xCD, 0xEF)]
    [InlineData("#112233", 0xFF, 0x11, 0x22, 0x33)]
    public void HexColor_reads_only_hexadecimal_colors(string text, byte a, byte r, byte g, byte b)
    {
        Assert.True(HexColor.TryParse(text, out var color));
        Assert.Equal(Color.FromArgb(a, r, g, b), color);
        Assert.Equal(text.Length == 9 ? text.ToUpperInvariant() : "#FF" + text[1..].ToUpperInvariant(), HexColor.Format(color));
    }

    [Theory]
    [InlineData("ContextColor file://server/share/profile.icc 1,0,0,0")]
    [InlineData("Red")]
    [InlineData("sc#1,0,0,1")]
    [InlineData("#12345")]
    [InlineData("#GG112233")]
    [InlineData(" #FF112233")]
    [InlineData("")]
    public void HexColor_refuses_everything_else(string text) => Assert.False(HexColor.TryParse(text, out _));

    [Fact]
    public void Sidecar_of_version_1_1_1_is_read_with_every_kind()
    {
        using var folder = new TempFolder();
        var document = EditorDocument.Load(WriteCapture(folder, Annotations111));
        var a = document.Annotations;
        Assert.Equal(14, a.Count);
        // The base image of the sidecar is the one edited, not the flattened capture.
        Assert.Equal(Colors.White, At(document.BaseImage.Pixels, 10, 10));

        var arrow = Assert.IsType<ArrowAnnotation>(a[0]);
        Assert.Equal((ArrowHeads.Both, new Point(10, 20), new Point(110.5, 60.25), Color.FromRgb(0xE5, 0x39, 0x35), 4.0),
            (arrow.Heads, arrow.Start, arrow.End, arrow.Color, arrow.Thickness));
        Assert.Equal(EditorTool.Line, Assert.IsType<LineAnnotation>(a[1]).Kind);
        var rectangle = Assert.IsType<RectangleAnnotation>(a[2]);
        Assert.True(rectangle.Filled);
        Assert.Equal(new Rect(1, 2, 29, 38), rectangle.GetRect());
        Assert.Equal(Color.FromArgb(0x80, 0xFF, 0xFF, 0xFF), rectangle.Color);
        Assert.False(Assert.IsType<EllipseAnnotation>(a[3]).Filled);
        Assert.IsType<HighlightAnnotation>(a[4]);
        Assert.Equal(10, Assert.IsType<PixelateAnnotation>(a[5]).BlockSize);
        Assert.IsType<SpotlightAnnotation>(a[6]);
        var text = Assert.IsType<TextAnnotation>(a[7]);
        Assert.Equal(("Bonjour\nمرحبا", 32.0, true, new Point(12, 14)), (text.Text, text.FontSize, text.HasBackground, text.Position));
        var step = Assert.IsType<StepAnnotation>(a[8]);
        Assert.Equal((3, StepShape.Leader, true, (Point?)new Point(10, 40), "C"), (step.Number, step.Shape, step.Letters, step.Target, step.Label));
        Assert.Null(Assert.IsType<StepAnnotation>(a[9]).Target);
        Assert.Equal(new[] { new Point(1, 1), new Point(2, 3), new Point(4, 5) }, Assert.IsType<FreehandAnnotation>(a[10]).Points);
        Assert.Equal("text-approuvé", Assert.IsType<StampAnnotation>(a[11]).StampId);
        var magnifier = Assert.IsType<MagnifierAnnotation>(a[12]);
        Assert.Equal((new Point(80, 40), 2.5, MagnifierShape.RoundedRect, false), (magnifier.LensCenter, magnifier.Zoom, magnifier.LensShape, magnifier.Connector));
        // Enums written as numbers by the first versions.
        Assert.Equal(ArrowHeads.None, Assert.IsType<ArrowAnnotation>(a[13]).Heads);
        Assert.Equal(ImageEffects.None, document.Effects);
        Assert.False(document.IsDirty);
    }

    [Fact]
    public void Sidecar_of_version_2_reads_the_effects()
    {
        using var folder = new TempFolder();
        var document = EditorDocument.Load(WriteCapture(folder, "[]", Effects111, version: 2));
        var effects = document.Effects;
        Assert.True(effects.Shadow && effects.Border);
        Assert.Equal(TornEdges.Top | TornEdges.Bottom, effects.TornEdges);
        Assert.Equal((12.0, 7, Color.FromRgb(0xF3, 0xF4, 0xF6), Color.FromRgb(0x37, 0x41, 0x51)),
            (effects.CornerRadius, effects.TornSeed, effects.Background, effects.BorderColor));
    }

    [Fact]
    public void Unusable_entries_are_dropped_and_the_others_kept()
    {
        using var folder = new TempFolder();
        var document = EditorDocument.Load(WriteCapture(folder, InvalidEntries));
        var line = Assert.IsType<LineAnnotation>(Assert.Single(document.Annotations));
        Assert.Equal(Color.FromRgb(0, 0xFF, 0), line.Color);
        // Effects out of range are dropped, the annotations kept.
        var withBadEffects = EditorDocument.Load(WriteCapture(folder, "[]", Effects111.Replace("\"ShadowOpacity\": 0.4", "\"ShadowOpacity\": 4"), version: 2));
        Assert.Equal(ImageEffects.None, withBadEffects.Effects);
    }

    public static TheoryData<string, string> Mismatches => new()
    {
        { "general color syntax", "\"#FF1E88E5\"|\"ContextColor file://server/p.icc 1,0,0,1\"" },
        { "newer version", "\"Version\": 1|\"Version\": 3" },
        { "capture length", "\"FileLength\": |\"FileLength\": 1" },
        { "capture date", "\"FileWriteTimeUtc\": \"2|\"FileWriteTimeUtc\": \"1" },
        { "base length", "\"BaseFileLength\": |\"BaseFileLength\": 9" },
        { "unknown type", "\"type\": \"line\"|\"type\": \"hexagon\"" },
    };

    /// <summary>A sidecar that does not belong to the capture, or cannot be trusted, is ignored as a whole.</summary>
    [Theory]
    [MemberData(nameof(Mismatches))]
    public void Sidecar_that_does_not_match_is_ignored(string reason, string replacement)
    {
        var parts = replacement.Split('|');
        using var folder = new TempFolder();
        var path = WriteCapture(folder, Annotations111, tamper: json =>
        {
            Assert.Contains(parts[0], json);
            return json.Replace(parts[0], parts[1]);
        });
        var document = EditorDocument.Load(path);
        Assert.True(document.Annotations.Count == 0, reason);
        // The capture itself is edited.
        Assert.Equal(Colors.SteelBlue, At(document.BaseImage.Pixels, 10, 10));
    }

    public static TheoryData<string, int> TooMuch => new()
    {
        { """{ "type": "line", "Start": [1, 2], "End": [3, 4], "Color": "#FF00FF00", "Thickness": 2 }""", 10_001 },
        { """{ "type": "magnifier", "LensCenter": [80, 40], "Zoom": 2, "Shape": "Circle", "Start": [1, 1], "End": [21, 21], "Color": "#FF000000", "Thickness": 2 }""", 101 },
        { $$"""{ "type": "text", "Position": [0, 0], "Text": "{{new string('x', 100_000)}}", "FontSize": 20, "Color": "#FF000000", "Thickness": 4 }""", 11 },
    };

    [Theory]
    [MemberData(nameof(TooMuch))]
    public void Sidecar_with_more_than_the_editor_makes_is_ignored(string entry, int count)
    {
        using var folder = new TempFolder();
        var path = WriteCapture(folder, "[" + string.Join(",", Enumerable.Repeat(entry, count)) + "]");
        var document = EditorDocument.Load(path);
        Assert.Empty(document.Annotations);
        // The capture itself is opened.
        Assert.Equal(Colors.SteelBlue, At(document.BaseImage.Pixels, 10, 10));

        // One less is accepted.
        var limit = JsonSerializer.Deserialize<List<Annotation?>>("[" + string.Join(",", Enumerable.Repeat(entry, count - 1)) + "]", AnnotationJson.Options)!;
        Assert.Null(EditorDocument.TooMuchToEdit(limit));
    }

    [AvaloniaFact]
    public void Save_failing_on_the_capture_keeps_the_base_and_the_annotation_file()
    {
        using var folder = new TempFolder();
        var path = WriteCapture(folder, Annotations111);
        var document = EditorDocument.Load(path);
        var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
        document.Crop(new PixelRect(0, 0, 60, 40));
        document.Save();
        Assert.Equal((60, 40), (ImageIO.Load(basePath).Width, ImageIO.Load(basePath).Height));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(basePath)!, "*.tmp"));
        var reloaded = EditorDocument.Load(path);
        Assert.Equal((60, 40), (reloaded.BaseImage.Width, reloaded.BaseImage.Height));
        Assert.NotEmpty(reloaded.Annotations);

        // The capture cannot be written (locked by another program): the base and the annotation file stay as they were.
        var baseBefore = File.ReadAllBytes(basePath);
        var jsonBefore = File.ReadAllBytes(jsonPath);
        var locked = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        try
        {
            reloaded.Crop(new PixelRect(0, 0, 30, 20));
            if (OperatingSystem.IsWindows()) Assert.ThrowsAny<Exception>(() => reloaded.Save());
        }
        finally
        {
            locked.Dispose();
        }
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(baseBefore, File.ReadAllBytes(basePath));
            Assert.Equal(jsonBefore, File.ReadAllBytes(jsonPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(basePath)!, "*.tmp"));
        }
    }

    [AvaloniaFact]
    public void A_save_written_in_the_background_leaves_the_later_changes_unsaved()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(120, 80, Colors.SteelBlue), path);
        var document = EditorDocument.Load(path);
        var before = document.BeginChange();
        document.Annotations.Add(new RectangleAnnotation { Start = new(10, 10), End = new(60, 40), Color = Colors.Red, Filled = true });
        document.CommitChange(before);

        // Read now, written later (another thread in the editor), while the editing goes on.
        var job = document.PrepareSave();
        var more = document.BeginChange();
        document.Annotations.Add(new LineAnnotation { Start = new(1, 1), End = new(50, 50) });
        document.CommitChange(more);
        job.Write();
        document.CompleteSave(job);

        Assert.True(document.IsDirty);
        Assert.True(Near(At(ImageIO.Load(path), 30, 20), Colors.Red));
        Assert.Single(EditorDocument.Load(path).Annotations);
        // Back to what was saved: clean.
        document.Undo();
        Assert.False(document.IsDirty);
    }

    [AvaloniaFact]
    public async Task Resize_in_the_background_is_one_step_and_dropped_when_the_image_changed()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(120, 80, Colors.SteelBlue), path);
        var document = EditorDocument.Load(path);
        var rectangle = new RectangleAnnotation { Start = new(10, 10), End = new(60, 40) };
        document.Annotations.Add(rectangle);
        int changes = 0;
        document.Changed += () => changes++;

        Assert.True(await document.ResizeAsync(60, 40));
        Assert.Equal((60, 40), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(new Point(30, 20), rectangle.End);
        Assert.Equal(1, changes);
        Assert.False(await document.ResizeAsync(60, 40));

        // Undone while the pixels are being resampled: the resize is abandoned.
        var resize = document.ResizeAsync(240, 160);
        document.Undo();
        Assert.False(await resize);
        Assert.Equal((120, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.False(document.CanUndo);
    }

    [Fact]
    public void Orphans_and_stale_temporary_files_are_cleaned_once_per_folder()
    {
        using var folder = new TempFolder();
        var path = WriteCapture(folder, Annotations111);
        var sidecars = Path.GetDirectoryName(SidecarFiles.PathsOf(path).JsonPath)!;
        var stale = Path.Combine(sidecars, "capture.png.base.png.tmp");
        var recent = Path.Combine(sidecars, "capture.png.json.tmp");
        var orphan = Path.Combine(sidecars, "gone.png.json");
        File.WriteAllText(stale, "");
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddHours(-2));
        File.WriteAllText(recent, "");
        File.WriteAllText(orphan, "{}");

        SidecarFiles.CleanOrphans(folder.Path);
        Assert.False(File.Exists(stale));
        Assert.True(File.Exists(recent));
        Assert.False(File.Exists(orphan));

        // Once per session: a second call does not enumerate the folder again.
        File.WriteAllText(orphan, "{}");
        SidecarFiles.CleanOrphans(folder.Path);
        Assert.True(File.Exists(orphan));
    }

    [Fact]
    public void Annotations_written_then_read_are_the_same()
    {
        var original = JsonSerializer.Deserialize<List<Annotation>>(Annotations111, AnnotationJson.Options)!;
        var written = JsonSerializer.Serialize(original, AnnotationJson.Options);
        // Same names as 1.1.1: discriminators, properties, enums as names, colors and points.
        Assert.Contains("\"type\": \"magnifier\"", written);
        Assert.Contains("\"Shape\": \"RoundedRect\"", written);
        Assert.Contains("\"Color\": \"#80FFFFFF\"", written);
        Assert.Contains("\"Heads\": \"Both\"", written);
        Assert.Contains("\"BlockSize\": 10", written);
        Assert.DoesNotContain("\"Kind\"", written);
        Assert.DoesNotContain("\"Label\"", written);
        Assert.DoesNotContain("\"LensShape\"", written);
        using (var parsed = JsonDocument.Parse(written))
        {
            var end = parsed.RootElement[0].GetProperty("End");
            Assert.Equal(JsonValueKind.Array, end.ValueKind);
            Assert.Equal(110.5, end[0].GetDouble());
        }
        var again = JsonSerializer.Deserialize<List<Annotation>>(written, AnnotationJson.Options)!;
        Assert.Equal(written, JsonSerializer.Serialize(again, AnnotationJson.Options));
        Assert.Equal(original.Select(a => a.GetType()), again.Select(a => a.GetType()));
    }

    [Fact]
    public void Effects_written_then_read_are_the_same()
    {
        var effects = JsonSerializer.Deserialize<ImageEffects>(Effects111, AnnotationJson.Options)!;
        var written = JsonSerializer.Serialize(effects, AnnotationJson.Options);
        Assert.Contains("\"TornEdges\": \"Top, Bottom\"", written);
        Assert.DoesNotContain("IsEmpty", written);
        Assert.Equal(effects, JsonSerializer.Deserialize<ImageEffects>(written, AnnotationJson.Options));
    }

    [AvaloniaFact]
    public void Presets_file_of_1_1_1_is_read_and_completed()
    {
        using var folder = new TempFolder();
        var previous = ToolPresets.FilePath;
        try
        {
            ToolPresets.FilePath = folder.File("presets.json");
            File.WriteAllText(ToolPresets.FilePath, """
                [
                  { "Tool": "Arrow", "Color": "#FF123456", "Thickness": 5, "Filled": false, "FontSize": 24, "Heads": "Both", "Shape": "Circle", "Letters": false },
                  { "Tool": 9, "Color": "#FFE53935", "Thickness": 3, "Filled": false, "FontSize": 24, "Heads": 0, "Shape": 1, "Letters": true },
                  { "Tool": "Crop", "Color": "#FFE53935", "Thickness": 3 },
                  { "Tool": "Line", "Color": "#FFE53935", "Thickness": 300 }
                ]
                """);
            ToolPresets.Reload();
            var arrow = Assert.Single(ToolPresets.For(EditorTool.Arrow));
            Assert.Equal(new DrawingStyle(Color.FromRgb(0x12, 0x34, 0x56), 5, false, 24, ArrowHeads.Both, StepShape.Circle, false), arrow.Style);
            Assert.Equal(StepShape.Leader, Assert.Single(ToolPresets.For(EditorTool.Step)).Style.Shape);
            Assert.Empty(ToolPresets.For(EditorTool.Line));
            // The magnifier came after this file: its default presets are added.
            Assert.Equal(3, ToolPresets.For(EditorTool.Magnifier).Count);

            Assert.True(ToolPresets.Add(arrow with { Style = arrow.Style with { Thickness = 8 } }));
            Assert.False(ToolPresets.Add(arrow));
            ToolPresets.Reload();
            Assert.Equal(2, ToolPresets.For(EditorTool.Arrow).Count);
            ToolPresets.Reset(EditorTool.Arrow);
            Assert.Equal(8, ToolPresets.For(EditorTool.Arrow).Count);
        }
        finally
        {
            ToolPresets.FilePath = previous;
            ToolPresets.Reload();
        }
    }
}
