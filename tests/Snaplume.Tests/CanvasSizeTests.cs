using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>Space added around the image without stretching it, like "Canvas size" in Paint (see EditorDocument.ExtendCanvas).</summary>
public class CanvasSizeTests
{
    private static EditorDocument Load(TempFolder folder, PixelBuffer image)
    {
        var path = folder.File("capture.png");
        ImageIO.Save(image, path);
        return EditorDocument.Load(path);
    }

    /// <summary>The rows of <paramref name="inner"/> are found unchanged in <paramref name="outer"/> at (<paramref name="x"/>, <paramref name="y"/>).</summary>
    private static void AssertPixelsAt(PixelBuffer outer, PixelBuffer inner, int x, int y)
    {
        for (int row = 0; row < inner.Height; row++)
        {
            var expected = inner.Pixels.AsSpan(row * inner.Stride, inner.Stride);
            var actual = outer.Pixels.AsSpan((y + row) * outer.Stride + x * 4, inner.Stride);
            Assert.True(expected.SequenceEqual(actual), $"Row {row} differs");
        }
    }

    [AvaloniaFact]
    public void Extending_keeps_the_pixels_fills_the_new_parts_and_moves_the_annotations()
    {
        using var folder = new TempFolder();
        var original = Noise(100, 80);
        var document = Load(folder, original);
        var arrow = new ArrowAnnotation { Start = new(10, 10), End = new(50, 40) };
        document.Annotations.Add(arrow);
        long version = document.Version;
        int changes = 0;
        document.Changed += () => changes++;

        document.ExtendCanvas(10, 20, 30, 40, Colors.White);
        Assert.Equal((140, 140), (document.BaseImage.Width, document.BaseImage.Height));
        AssertPixelsAt(document.BaseImage.Pixels, original, 10, 20);
        foreach (var (x, y) in new[] { (0, 0), (5, 100), (139, 0), (139, 139), (50, 139), (115, 50) })
            Assert.Equal(Colors.White, At(document.BaseImage.Pixels, x, y));
        Assert.Equal(new Point(20, 30), ((ArrowAnnotation)document.Annotations[0]).Start);
        Assert.Equal(1, changes);
        Assert.True(document.IsDirty);

        // One undo step, and back.
        document.Undo();
        Assert.Equal((100, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(new Point(10, 10), ((ArrowAnnotation)document.Annotations[0]).Start);
        Assert.Equal(version, document.Version);
        Assert.False(document.CanUndo);
        document.Redo();
        Assert.Equal((140, 140), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(new Point(20, 30), ((ArrowAnnotation)document.Annotations[0]).Start);

        // No margin: nothing changes, no undo step.
        version = document.Version;
        document.ExtendCanvas(0, 0, 0, 0, Colors.White);
        Assert.Equal(version, document.Version);
    }

    [AvaloniaFact]
    public void Transparent_and_colored_backgrounds_fill_the_new_parts_and_survive_the_save()
    {
        using var folder = new TempFolder();
        var document = Load(folder, Solid(60, 40, Colors.SteelBlue));
        document.ExtendCanvas(0, 0, 20, 10, Colors.Transparent);
        Assert.Equal(0, At(document.BaseImage.Pixels, 70, 20).A);
        Assert.Equal(0, At(document.BaseImage.Pixels, 30, 45).A);
        Assert.Equal(Colors.SteelBlue, At(document.BaseImage.Pixels, 59, 39));
        document.ExtendCanvas(5, 0, 0, 0, Colors.Black);
        Assert.Equal(Colors.Black, At(document.BaseImage.Pixels, 2, 10));
        Assert.Equal(0, At(document.BaseImage.Pixels, 75, 20).A);

        // The effects take the new size; the annotations and the base image stay editable after a save.
        document.Annotations.Add(new RectangleAnnotation { Start = new(62, 42), End = new(80, 48), Color = Colors.Red, Filled = true });
        document.SetEffects(ImageEffects.None with { Shadow = true });
        var padding = document.Effects.GetPadding();
        Assert.Equal(new Size(85 + padding.Left + padding.Right, 50 + padding.Top + padding.Bottom), document.OutputSize);
        document.Save();
        var flattened = ImageIO.Load(document.FilePath);
        Assert.Equal((int)document.OutputSize.Width, flattened.Width);
        Assert.True(Near(At(flattened, padding.Left + 70, padding.Top + 45), Colors.Red));

        var reloaded = EditorDocument.Load(document.FilePath);
        Assert.Equal((85, 50), (reloaded.BaseImage.Width, reloaded.BaseImage.Height));
        Assert.Equal(0, At(reloaded.BaseImage.Pixels, 75, 20).A);
        Assert.Single(reloaded.Annotations);
        Assert.True(reloaded.Effects.Shadow);
    }

    [AvaloniaFact]
    public void Too_large_a_canvas_is_refused_and_leaves_the_document_unchanged()
    {
        using var folder = new TempFolder();
        var document = Load(folder, Solid(100, 80, Colors.SteelBlue));
        // Wider than the editor allows.
        Assert.Throws<InvalidDataException>(() => document.ExtendCanvas(0, 0, EditorDocument.MaxDimension, 0, Colors.White));
        // Too many pixels: 20 100 × 20 080.
        var error = Assert.Throws<InvalidDataException>(() => document.ExtendCanvas(0, 0, 20_000, 20_000, Colors.White));
        Assert.Equal(Loc.T("App.ImageTooLarge", 20_100, 20_080), error.Message);
        // Within the limit alone, beyond it with the margins of the shadow.
        document.SetEffects(ImageEffects.None with { Shadow = true });
        long versionWithEffects = document.Version;
        Assert.Throws<InvalidDataException>(() => document.ExtendCanvas(0, 0, 15_700, 15_720, Colors.White));
        Assert.Equal(versionWithEffects, document.Version);
        Assert.Equal((100, 80), (document.BaseImage.Width, document.BaseImage.Height));

        // A canvas that would leave out the whole image is not a canvas size.
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ExtendCanvas(-100, 0, 50, 0, Colors.White));
        Assert.Throws<ArgumentOutOfRangeException>(() => document.ExtendCanvas(0, 0, 0, -80, Colors.White));
    }

    [AvaloniaFact]
    public void A_smaller_canvas_cuts_the_image_and_drops_what_is_left_outside()
    {
        using var folder = new TempFolder();
        var original = Noise(100, 80);
        var document = Load(folder, original);
        document.Annotations.Add(new RectangleAnnotation { Start = new(10, 10), End = new(30, 30) });
        document.Annotations.Add(new LineAnnotation { Start = new(80, 60), End = new(95, 75) });
        document.ExtendCanvas(0, 0, -40, -30, Colors.White);
        Assert.Equal((60, 50), (document.BaseImage.Width, document.BaseImage.Height));
        AssertPixelsAt(original, document.BaseImage.Pixels, 0, 0);
        Assert.IsType<RectangleAnnotation>(Assert.Single(document.Annotations));
        document.Undo();
        Assert.Equal(2, document.Annotations.Count);
    }

    [Theory]
    [InlineData(CanvasAnchor.Center, 200, 120, 50, 20, 50, 20)]
    [InlineData(CanvasAnchor.Center, 201, 121, 50, 20, 51, 21)]
    [InlineData(CanvasAnchor.TopLeft, 200, 120, 0, 0, 100, 40)]
    [InlineData(CanvasAnchor.Top, 200, 120, 50, 0, 50, 40)]
    [InlineData(CanvasAnchor.Right, 200, 120, 100, 20, 0, 20)]
    [InlineData(CanvasAnchor.BottomRight, 200, 120, 100, 40, 0, 0)]
    [InlineData(CanvasAnchor.BottomLeft, 200, 120, 0, 40, 100, 0)]
    [InlineData(CanvasAnchor.Center, 95, 80, -2, 0, -3, 0)]
    public void Margins_put_the_difference_opposite_the_anchor(CanvasAnchor anchor, int newWidth, int newHeight, int left, int top, int right, int bottom)
    {
        Assert.Equal((left, top, right, bottom), CanvasSize.Margins(100, 80, newWidth, newHeight, anchor));
    }

    [Fact]
    public void The_background_setting_defaults_to_white_and_is_kept()
    {
        Assert.Equal(Colors.White, CanvasSize.BackgroundFrom(new AppSettings().EditorCanvasBackground));
        Assert.Equal(Colors.White, CanvasSize.BackgroundFrom(null));
        // Older settings files do not have it.
        var old = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{"JpegQuality": 80}""")!;
        old.Sanitize();
        Assert.Equal("#FFFFFFFF", old.EditorCanvasBackground);

        var settings = new AppSettings { EditorCanvasBackground = "#00FFFFFF" };
        settings.Sanitize();
        Assert.Equal(Colors.Transparent, CanvasSize.BackgroundFrom(settings.EditorCanvasBackground));
        settings.EditorCanvasBackground = "blue";
        settings.Sanitize();
        Assert.Equal("#FFFFFFFF", settings.EditorCanvasBackground);
        settings.EditorCanvasBackground = "#ff336699";
        settings.Sanitize();
        Assert.Equal("#FF336699", settings.EditorCanvasBackground);
    }
}
