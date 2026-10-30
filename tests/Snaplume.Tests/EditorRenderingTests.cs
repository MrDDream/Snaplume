using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>Annotations and effects drawn by Avalonia (Skia, headless), as on screen and in the saved image.</summary>
public class EditorRenderingTests
{
    private static readonly Color Red = Color.FromRgb(0xE5, 0x39, 0x35);
    private static readonly Color Blue = Color.FromRgb(0x1E, 0x88, 0xE5);

    /// <summary>A white capture of 200 × 150 with one annotation, flattened.</summary>
    private static PixelBuffer FlattenOne(Annotation annotation, int width = 200, int height = 150)
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(width, height, Colors.White), path);
        var document = EditorDocument.Load(path);
        document.Annotations.Add(annotation);
        return document.Flatten();
    }

    public static TheoryData<string> Kinds => new()
    {
        "arrow", "line", "rectangle", "filled-rectangle", "ellipse", "highlight", "spotlight", "text", "text-background",
        "step", "step-leader", "step-tail", "step-pin", "step-dark", "freehand", "stamp", "magnifier",
    };

    /// <summary>Each kind draws something where it is, and nothing far from it (except the spotlight shade).</summary>
    [AvaloniaTheory]
    [MemberData(nameof(Kinds))]
    public void Each_annotation_kind_draws_where_expected(string kind)
    {
        Annotation annotation = kind switch
        {
            "arrow" => new ArrowAnnotation { Start = new(20, 75), End = new(120, 75), Color = Red },
            "line" => new LineAnnotation { Start = new(20, 75), End = new(120, 75), Color = Red },
            "rectangle" => new RectangleAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red },
            "filled-rectangle" => new RectangleAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red, Filled = true },
            "ellipse" => new EllipseAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red },
            "highlight" => new HighlightAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red },
            "spotlight" => new SpotlightAnnotation { Start = new(20, 40), End = new(120, 110) },
            "text" => new TextAnnotation { Position = new(20, 50), Text = "Snaplume", Color = Red, FontSize = 24 },
            "text-background" => new TextAnnotation { Position = new(20, 50), Text = "Snaplume", Color = Red, FontSize = 24, HasBackground = true },
            "step" => new StepAnnotation { Center = new(70, 75), Color = Red },
            "step-leader" => new StepAnnotation { Center = new(70, 75), Color = Red, Shape = StepShape.Leader, Target = new(30, 75) },
            "step-tail" => new StepAnnotation { Center = new(70, 75), Color = Red, Shape = StepShape.Tail, Target = new(30, 75) },
            "step-pin" => new StepAnnotation { Center = new(70, 60), Color = Red, Shape = StepShape.Pin },
            "step-dark" => new StepAnnotation { Center = new(70, 75), Color = Red, Shape = StepShape.Dark },
            "freehand" => new FreehandAnnotation { Points = { new(20, 75), new(70, 60), new(120, 75) }, Color = Red },
            "stamp" => new StampAnnotation { StampId = "ok-circle", Start = new(20, 40), End = new(84, 104) },
            "magnifier" => new MagnifierAnnotation { Start = new(20, 50), End = new(60, 90), LensCenter = new(140, 70), Color = Red },
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        var image = FlattenOne(annotation);
        Assert.Equal((200, 150), (image.Width, image.Height));

        var bounds = annotation.GetBounds();
        var inside = new PixelRect((int)bounds.X, (int)bounds.Y, (int)Math.Ceiling(bounds.Width), (int)Math.Ceiling(bounds.Height));
        Assert.True(CountDifferent(image, inside, Colors.White) > 20, $"{kind}: nothing drawn in {inside}");
        // A corner far from every annotation stays white, except under the spotlight shade.
        var far = At(image, 195, 145);
        if (kind == "spotlight")
        {
            Assert.True(far.R < 200, "the spotlight darkens the rest of the image");
            Assert.True(Near(At(image, 70, 75), Colors.White), "the spotlight area stays clear");
        }
        else
        {
            Assert.True(Near(far, Colors.White), $"{kind}: {far} drawn far away");
        }
    }

    [AvaloniaFact]
    public void Filled_rectangle_has_its_color_inside_and_outline_only_on_its_edge()
    {
        var filled = FlattenOne(new RectangleAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red, Filled = true });
        Assert.True(Near(At(filled, 70, 75), Red));
        var outline = FlattenOne(new RectangleAnnotation { Start = new(20, 40), End = new(120, 110), Color = Red, Thickness = 4 });
        Assert.True(Near(At(outline, 70, 75), Colors.White));
        Assert.True(Near(At(outline, 22, 75), Red));
    }

    [AvaloniaFact]
    public void Text_left_to_right_and_right_to_left_stay_in_their_box()
    {
        foreach (var text in new[] { "Snaplume", "مرحبا بالعالم" })
        {
            var annotation = new TextAnnotation { Position = new(30, 40), Text = text, Color = Red, FontSize = 24 };
            var image = FlattenOne(annotation, 300, 150);
            var bounds = annotation.GetBounds();
            Assert.Equal(TextAnnotation.DirectionOf(text) == FlowDirection.RightToLeft, text != "Snaplume");
            // Everything drawn (the text and its halo) is inside the box of the annotation.
            int drawn = CountDifferent(image, new PixelRect(0, 0, 300, 150), Colors.White);
            int inBox = CountDifferent(image, new PixelRect((int)bounds.X - 2, (int)bounds.Y - 2, (int)bounds.Width + 4, (int)bounds.Height + 4), Colors.White);
            Assert.True(drawn > 50, text);
            Assert.Equal(drawn, inBox);
        }
    }

    [AvaloniaFact]
    public void Pixelate_hides_the_source_with_uniform_blocks()
    {
        using var folder = new TempFolder();
        var path = folder.File("secret.png");
        var secret = Noise(160, 120);
        ImageIO.Save(secret, path);
        var document = EditorDocument.Load(path);
        var pixelate = new PixelateAnnotation { Start = new(16, 16), End = new(96, 80), Thickness = 4 };
        document.Annotations.Add(pixelate);
        var image = document.Flatten();

        int block = pixelate.BlockSize;
        Assert.Equal(10, block);
        // Inside: each block of the grid anchored at the corner of the image has one color.
        for (int by = 20; by < 80; by += block)
        for (int bx = 20; bx < 90; bx += block)
        {
            var first = At(image, bx, by);
            for (int y = by; y < Math.Min(by + block, 80); y++)
            for (int x = bx; x < Math.Min(bx + block, 96); x++)
                Assert.Equal(first, At(image, x, y));
        }
        // The source pixels are gone, the rest of the image is untouched.
        int same = 0;
        for (int y = 20; y < 80; y++)
        for (int x = 20; x < 90; x++)
            if (At(image, x, y) == At(secret, x, y)) same++;
        Assert.True(same < 30, $"{same} pixels of the secret left");
        Assert.Equal(At(secret, 120, 100), At(image, 120, 100));
        Assert.Equal(At(secret, 5, 5), At(image, 5, 5));
    }

    [AvaloniaFact]
    public void Magnifier_never_shows_a_pixelated_secret_enlarged()
    {
        using var folder = new TempFolder();
        var path = folder.File("secret.png");
        var secret = Noise(200, 150, seed: 2);
        ImageIO.Save(secret, path);
        var document = EditorDocument.Load(path);
        // The magnifier is below the pixelated area in the order: the lens shows it pixelated all the same.
        document.Annotations.Add(new MagnifierAnnotation
        {
            Start = new(20, 20), End = new(60, 60), LensCenter = new(140, 80), Zoom = 2, LensShape = MagnifierShape.RoundedRect,
            Connector = false,
        });
        document.Annotations.Add(new PixelateAnnotation { Start = new(0, 0), End = new(80, 80), Thickness = 8 });
        var image = document.Flatten();
        // Lens from (100, 40) to (180, 120): a block of 20 px enlarged twice gives runs of 40 px of one color.
        var row = Enumerable.Range(110, 60).Select(x => At(image, x, 70)).ToList();
        int changes = row.Zip(row.Skip(1)).Count(p => !Near(p.First, p.Second, 2));
        Assert.True(changes <= 4, $"{changes} color changes along the lens: the secret shows");
    }

    [AvaloniaFact]
    public void Effects_grow_the_output_by_the_shadow_margins()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(120, 80, Blue), path);
        var document = EditorDocument.Load(path);
        var effects = ImageEffects.None with { Shadow = true, ShadowBlur = 10, ShadowOffsetX = 4, ShadowOffsetY = 6, Background = Colors.White };
        document.SetEffects(effects);
        var padding = effects.GetPadding();
        Assert.Equal(new Int32Thickness(9, 7, 17, 19), padding);
        var image = document.Flatten();
        Assert.Equal((120 + 9 + 17, 80 + 7 + 19), (image.Width, image.Height));
        Assert.Equal(new Size(146, 106), document.OutputSize);
        // The image sits inside the margins, the far corner is the background, the shadow darkens below right.
        Assert.True(Near(At(image, 9 + 60, 7 + 40), Blue));
        Assert.True(Near(At(image, 0, 0), Colors.White, 2));
        var shadow = At(image, 9 + 60, 7 + 80 + 4);
        Assert.True(shadow.R < 230 && shadow.R > 60, $"shadow {shadow}");
    }

    [AvaloniaFact]
    public void Border_corners_and_torn_edges_cut_the_image()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(200, 120, Blue), path);
        var document = EditorDocument.Load(path);
        document.SetEffects(ImageEffects.None with { Border = true, BorderColor = Red, BorderWidth = 4 });
        var bordered = document.Flatten();
        Assert.Equal((200, 120), (bordered.Width, bordered.Height));
        Assert.True(Near(At(bordered, 1, 60), Red));
        Assert.True(Near(At(bordered, 100, 60), Blue));

        document.SetEffects(ImageEffects.None with { CornerRadius = 30, Background = Colors.Black });
        var rounded = document.Flatten();
        Assert.True(Near(At(rounded, 1, 1), Colors.Black));
        Assert.True(Near(At(rounded, 100, 60), Blue));

        document.SetEffects(ImageEffects.None with { TornEdges = TornEdges.Bottom, TornSize = 12, CornerRadius = 10, Background = Colors.White });
        var torn = document.Flatten();
        int cut = Enumerable.Range(0, 200).Count(x => Near(At(torn, x, 117), Colors.White));
        Assert.True(cut > 40, $"{cut} pixels cut along the torn bottom edge");
        Assert.Equal(0, Enumerable.Range(20, 160).Count(x => Near(At(torn, x, 2), Colors.White)));
        Assert.True(Near(At(torn, 1, 1), Colors.White), "rounded corner with torn edges");
    }

    [AvaloniaFact]
    public void Insert_grows_the_capture_shifts_annotations_and_undoes_in_one_step()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(100, 80, Blue), path);
        var document = EditorDocument.Load(path);
        var arrow = new ArrowAnnotation { Start = new(10, 10), End = new(50, 40) };
        document.Annotations.Add(arrow);
        document.PushUndo();
        long version = document.Version;

        // Inside: same size, the inserted pixels replace the capture's.
        document.InsertImage(Solid(20, 20, Red), new Rect(10, 10, 20, 20), Colors.White);
        Assert.Equal((100, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(Red, At(document.BaseImage.Pixels, 20, 20));
        Assert.Equal(Blue, At(document.BaseImage.Pixels, 50, 50));

        // Past the right edge: the capture grows, the new part takes the background.
        document.InsertImage(Solid(50, 30, Red), new Rect(90, 0, 50, 30), Colors.White);
        Assert.Equal((140, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(Red, At(document.BaseImage.Pixels, 120, 10));
        Assert.Equal(Colors.White, At(document.BaseImage.Pixels, 120, 60));
        Assert.Equal(new Point(10, 10), ((ArrowAnnotation)document.Annotations[0]).Start);

        // Past the left and top edges: the annotations follow the image.
        document.InsertImage(Solid(30, 30, Red), new Rect(-20, -10, 30, 30), Colors.Black);
        Assert.Equal((160, 90), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(Colors.Black, At(document.BaseImage.Pixels, 5, 60));
        Assert.Equal(Blue, At(document.BaseImage.Pixels, 20 + 50, 10 + 50));
        Assert.Equal(new Point(30, 20), ((ArrowAnnotation)document.Annotations[0]).Start);

        // Each insertion is one undo step.
        document.Undo();
        Assert.Equal((140, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(new Point(10, 10), ((ArrowAnnotation)document.Annotations[0]).Start);
        document.Undo();
        document.Undo();
        Assert.Equal((100, 80), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(version, document.Version);
        document.Redo();
        Assert.Equal(Red, At(document.BaseImage.Pixels, 20, 20));
    }

    [AvaloniaFact]
    public void Inserted_bounds_follow_the_v1_1_1_rules()
    {
        Assert.Equal(new PixelRect(0, 0, 100, 80), EditorDocument.InsertedBounds(100, 80, new Rect(10, 10, 20, 20)));
        Assert.Equal(new PixelRect(0, 0, 141, 80), EditorDocument.InsertedBounds(100, 80, new Rect(90.5, 0, 50.2, 30)));
        Assert.Equal(new PixelRect(-21, -10, 121, 90), EditorDocument.InsertedBounds(100, 80, new Rect(-20.5, -10, 30, 30)));
    }

    [AvaloniaFact]
    public void Insert_refuses_a_result_too_large_and_leaves_the_document_unchanged()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(100, 80, Blue), path);
        var document = EditorDocument.Load(path);
        Assert.Throws<InvalidDataException>(() =>
            document.InsertImage(Solid(10, 10, Red), new Rect(EditorDocument.MaxDimension, 0, 10, 10), Colors.White));
        Assert.False(document.CanUndo);
        Assert.Equal((100, 80), (document.BaseImage.Width, document.BaseImage.Height));
    }

    [AvaloniaFact]
    public void Crop_and_resize_move_and_scale_the_annotations()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Noise(200, 100), path);
        var document = EditorDocument.Load(path);
        document.Annotations.Add(new RectangleAnnotation { Start = new(60, 30), End = new(90, 60) });
        document.Annotations.Add(new LineAnnotation { Start = new(150, 80), End = new(190, 90) });
        document.Crop(new PixelRect(50, 20, 80, 60));
        Assert.Equal((80, 60), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Single(document.Annotations);
        Assert.Equal(new Point(10, 10), ((RectangleAnnotation)document.Annotations[0]).Start);

        document.Resize(160, 120);
        Assert.Equal((160, 120), (document.BaseImage.Width, document.BaseImage.Height));
        var rectangle = (RectangleAnnotation)document.Annotations[0];
        Assert.Equal(new Point(20, 20), rectangle.Start);
        Assert.Equal(8, rectangle.Thickness);
        document.Undo();
        document.Undo();
        Assert.Equal((200, 100), (document.BaseImage.Width, document.BaseImage.Height));
        Assert.Equal(2, document.Annotations.Count);
    }

    [AvaloniaFact]
    public void Save_then_reload_keeps_annotations_editable_and_the_base_redacted()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        var secret = Noise(160, 100, seed: 3);
        ImageIO.Save(secret, path);
        var document = EditorDocument.Load(path);
        document.Annotations.Add(new ArrowAnnotation { Start = new(10, 10), End = new(100, 60), Color = Blue, Heads = ArrowHeads.Both });
        document.Annotations.Add(new PixelateAnnotation { Start = new(100, 0), End = new(160, 50) });
        document.Annotations.Add(new TextAnnotation { Position = new(10, 60), Text = "Note", HasBackground = true });
        document.PushUndo();
        Assert.True(document.IsDirty);
        document.Save();
        Assert.False(document.IsDirty);

        var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
        Assert.True(File.Exists(basePath) && File.Exists(jsonPath));
        // The base kept on disk has the pixelated area burnt in, the rest of the capture intact.
        var savedBase = ImageIO.Load(basePath);
        Assert.NotEqual(At(secret, 130, 20), At(savedBase, 130, 20));
        Assert.Equal(At(secret, 20, 80), At(savedBase, 20, 80));
        Assert.Equal(1, System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("Version").GetInt32());

        var reloaded = EditorDocument.Load(path);
        Assert.Equal(3, reloaded.Annotations.Count);
        var arrow = Assert.IsType<ArrowAnnotation>(reloaded.Annotations[0]);
        Assert.Equal(ArrowHeads.Both, arrow.Heads);
        Assert.Equal(Blue, arrow.Color);
        Assert.Equal("Note", Assert.IsType<TextAnnotation>(reloaded.Annotations[2]).Text);
        Assert.False(reloaded.IsDirty);

        // With effects, the file is version 2; without annotations nor effects, the sidecar goes away.
        reloaded.SetEffects(ImageEffects.None with { Border = true });
        reloaded.Save();
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(File.ReadAllText(jsonPath)).RootElement.GetProperty("Version").GetInt32());
        Assert.True(EditorDocument.Load(path).Effects.Border);
        reloaded.Annotations.Clear();
        reloaded.SetEffects(ImageEffects.None);
        reloaded.Save();
        Assert.False(File.Exists(jsonPath));
        Assert.False(File.Exists(basePath));
    }

    [AvaloniaFact]
    public void Image_changed_elsewhere_drops_its_annotations()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(60, 40, Blue), path);
        var document = EditorDocument.Load(path);
        document.Annotations.Add(new LineAnnotation { Start = new(0, 0), End = new(50, 30) });
        document.Save();
        Assert.Single(EditorDocument.Load(path).Annotations);
        ImageIO.Save(Solid(61, 40, Red), path);
        Assert.Empty(EditorDocument.Load(path).Annotations);
    }

    [AvaloniaFact]
    public void Undo_history_keeps_the_last_100_steps_and_amends_the_last_one()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(60, 40, Blue), path);
        var document = EditorDocument.Load(path);
        for (int i = 0; i < 120; i++)
        {
            document.PushUndo();
            document.Annotations.Add(new LineAnnotation { Start = new(i, 0), End = new(i, 10) });
        }
        int undone = 0;
        while (document.CanUndo)
        {
            document.Undo();
            undone++;
        }
        Assert.Equal(100, undone);
        Assert.Equal(20, document.Annotations.Count);

        // A change abandoned leaves no step; amended moves make one step.
        var before = document.BeginChange();
        document.Annotations.Clear();
        document.RevertChange(before);
        Assert.Equal(20, document.Annotations.Count);
        Assert.True(document.CanRedo);
        document.PushUndo();
        Assert.False(document.CanRedo);
        document.Annotations[0].Translate(new Vector(1, 0));
        document.AmendLastChange();
        document.Annotations[0].Translate(new Vector(1, 0));
        document.AmendLastChange();
        Assert.Equal(new Point(2, 0), ((LineAnnotation)document.Annotations[0]).Start);
        document.Undo();
        Assert.Equal(new Point(0, 0), ((LineAnnotation)document.Annotations[0]).Start);
    }

    [AvaloniaFact]
    public void Every_stamp_and_preset_preview_draws()
    {
        foreach (var stamp in StampLibrary.All)
        {
            var image = Render((int)Math.Ceiling(stamp.Width), (int)Math.Ceiling(stamp.Height), dc => stamp.Render(dc, new Rect(0, 0, stamp.Width, stamp.Height)));
            Assert.True(CountDifferent(image, new PixelRect(0, 0, image.Width, image.Height), Colors.Transparent, 0) > 50, stamp.Id);
        }
        foreach (var tool in Enum.GetValues<EditorTool>().Where(ToolPresets.Supports))
        {
            var presets = ToolPresets.For(tool);
            Assert.NotEmpty(presets);
            var image = Render(140, 56, presets[0].RenderPreview);
            Assert.True(CountDifferent(image, new PixelRect(0, 0, 140, 56), Colors.Transparent, 0) > 50, tool.ToString());
            Assert.False(string.IsNullOrEmpty(presets[0].Description));
        }
    }
}
