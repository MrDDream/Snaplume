using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>The presets of the "Borders" gallery: valid effects, and the preset the effects of an image look like.</summary>
public class BorderPresetsTests
{
    private static BorderPreset Preset(string id) => BorderPresets.All.Single(p => p.Id == id);

    [Fact]
    public void Presets_are_valid_effects_with_distinct_names_and_looks()
    {
        Assert.Equal(10, BorderPresets.All.Count);
        Assert.Equal(BorderPresets.All.Count, BorderPresets.All.Select(p => p.Id).Distinct().Count());
        Assert.Equal(BorderPresets.All.Count, BorderPresets.All.Select(p => BorderPresets.Visible(p.Effects)).Distinct().Count());
        foreach (var preset in BorderPresets.All)
        {
            Assert.True(preset.Effects.IsValid(), preset.Id);
            // Within the ranges of the sliders of the effects panel, so that it can be fine-tuned from there.
            Assert.InRange(preset.Effects.ShadowBlur, 0, 60);
            Assert.InRange(preset.Effects.ShadowOffsetX, 0, 40);
            Assert.Equal(preset.Effects.ShadowOffsetX, preset.Effects.ShadowOffsetY);
            Assert.InRange(preset.Effects.BorderWidth, 1, 40);
            Assert.InRange(preset.Effects.CornerRadius, 0, 80);
            Assert.InRange(preset.Effects.TornSize, 4, 40);
        }
        Assert.Equal("None", BorderPresets.All[0].Id);
        Assert.True(BorderPresets.All[0].Effects.IsEmpty);
        Assert.Equal(TornEdges.Top | TornEdges.Bottom, Preset("TornTopBottom").Effects.TornEdges);
        Assert.Equal(6, Preset("ThickBlack").Effects.BorderWidth);
        // The grey outline is the border the effects panel first offers.
        Assert.Equal(ImageEffects.None with { Border = true }, Preset("Gray").Effects);
    }

    [Fact]
    public void Each_preset_is_found_from_its_effects_and_after_being_applied()
    {
        var current = ImageEffects.None with { Background = Colors.Black, TornSeed = 7, Shadow = true, CornerRadius = 30 };
        foreach (var preset in BorderPresets.All)
        {
            Assert.Same(preset, BorderPresets.Find(preset.Effects));
            var applied = preset.ApplyTo(current);
            Assert.Same(preset, BorderPresets.Find(applied));
            Assert.True(preset.Matches(applied));
            // The background and the shape of the torn edges stay those of the image.
            Assert.Equal(Colors.Black, applied.Background);
            Assert.Equal(7, applied.TornSeed);
            Assert.Equal(preset.Effects, applied with { Background = Colors.White, TornSeed = 1 });
        }
    }

    [Fact]
    public void Hidden_settings_do_not_change_the_preset_found_but_visible_ones_do()
    {
        // "None" in the effects panel turns every effect off and keeps their settings.
        var allOff = Preset("WhiteFrame").Effects with { Shadow = false, Border = false, CornerRadius = 0, TornEdges = TornEdges.None, TornSize = 30 };
        Assert.Equal("None", BorderPresets.Find(allOff)?.Id);
        // Settings of an effect that is off do not show.
        var thin = Preset("ThinBlack").Effects with { ShadowBlur = 40, ShadowOpacity = 0.9, TornSize = 25, Background = Colors.Black };
        Assert.Equal("ThinBlack", BorderPresets.Find(thin)?.Id);
        var torn = Preset("TornBottom").Effects with { TornSeed = 12, BorderColor = Colors.Red };
        Assert.Equal("TornBottom", BorderPresets.Find(torn)?.Id);

        // Anything that shows differently is no preset.
        Assert.Null(BorderPresets.Find(Preset("ThinBlack").Effects with { BorderWidth = 3 }));
        Assert.Null(BorderPresets.Find(Preset("Gray").Effects with { BorderColor = Colors.Red }));
        Assert.Null(BorderPresets.Find(Preset("Shadow").Effects with { ShadowOpacity = 0.5 }));
        Assert.Null(BorderPresets.Find(Preset("RoundedShadow").Effects with { CornerRadius = 20 }));
        Assert.Null(BorderPresets.Find(Preset("TornBottom").Effects with { TornSize = 20 }));
        Assert.Null(BorderPresets.Find(Preset("TornBottom").Effects with { TornEdges = TornEdges.Top }));
        Assert.Null(BorderPresets.Find(Preset("Shadow").Effects with { Border = true }));
        // Resizing the image scales the effects: they are no longer the preset.
        Assert.Null(BorderPresets.Find(Preset("ThickBlack").Effects.Scaled(0.5)));
    }

    [AvaloniaFact]
    public void Thumbnail_is_the_capture_reduced_with_its_annotations()
    {
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(800, 400, Colors.White), path);
        var document = EditorDocument.Load(path);
        document.SetEffects(Preset("ThickBlack").Effects);
        document.Annotations.Add(new RectangleAnnotation { Start = new(0, 0), End = new(400, 400), Color = Colors.Red, Filled = true });

        var thumbnail = document.Thumbnail(216, 136);
        // Fitted in the box, proportions kept; the effects are not part of it.
        Assert.Equal((216, 108), (thumbnail.Width, thumbnail.Height));
        Assert.True(Near(At(thumbnail, 50, 54), Colors.Red, 8));
        Assert.True(Near(At(thumbnail, 160, 54), Colors.White, 8));
        Assert.True(Near(At(thumbnail, 215, 107), Colors.White, 8));

        // Never enlarged.
        var small = EditorDocument.Load(path);
        Assert.Equal((800, 400), (small.Thumbnail(1000, 1000).Width, small.Thumbnail(1000, 1000).Height));

        // A sample of effects is laid out like those of the image, without the caches.
        var sample = EffectsRenderer.Sample(Preset("RoundedShadow").Effects, thumbnail.Width, thumbnail.Height)!;
        var layout = EffectsRenderer.For(Preset("RoundedShadow").Effects, thumbnail.Width, thumbnail.Height)!;
        Assert.NotSame(layout, sample);
        Assert.Equal((layout.OutputWidth, layout.OutputHeight), (sample.OutputWidth, sample.OutputHeight));
        Assert.NotNull(sample.OutlineGeometry);
        Assert.Null(EffectsRenderer.Sample(ImageEffects.None, 100, 100));
    }
}
