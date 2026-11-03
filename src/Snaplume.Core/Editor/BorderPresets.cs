using Avalonia.Media;

namespace Snaplume.Editor;

/// <summary>
/// A ready-made set of image effects offered in the "Borders" gallery of the editor: one click replaces the
/// effects of the image. Sizes are in pixels of the image, like those set in the effects panel.
/// </summary>
/// <param name="Id">Stable name of the preset (its text in the editor is looked up from it).</param>
public sealed record BorderPreset(string Id, ImageEffects Effects)
{
    /// <summary>
    /// The effects after a click on this preset. The background color and the shape of the torn edges are not
    /// part of a preset: they are kept, so that the background chosen in the effects panel stays.
    /// </summary>
    public ImageEffects ApplyTo(ImageEffects current) =>
        Effects with { Background = current.Background, TornSeed = current.TornSeed };

    /// <summary>
    /// True when <paramref name="current"/> looks like this preset: the settings of an effect that is off
    /// (kept by the panel for the session), the background and the shape of the torn edges are not compared.
    /// </summary>
    public bool Matches(ImageEffects current) => BorderPresets.Visible(current) == BorderPresets.Visible(Effects);
}

/// <summary>The presets of the "Borders" gallery, in the order shown.</summary>
public static class BorderPresets
{
    /// <summary>The black of the editor's palette, also offered for the border in the effects panel.</summary>
    private static readonly Color Black = Color.FromRgb(0x21, 0x21, 0x21);

    public static IReadOnlyList<BorderPreset> All { get; } = new BorderPreset[]
    {
        new("None", ImageEffects.None),
        new("ThinBlack", ImageEffects.None with { Border = true, BorderColor = Black, BorderWidth = 2 }),
        new("ThickBlack", ImageEffects.None with { Border = true, BorderColor = Black, BorderWidth = 6 }),
        // The border of the effects panel, as it is first offered.
        new("Gray", ImageEffects.None with { Border = true }),
        // A photo print: wide white margin and a soft shadow.
        new("WhiteFrame", ImageEffects.None with
        {
            Border = true, BorderColor = Colors.White, BorderWidth = 12,
            Shadow = true, ShadowBlur = 20, ShadowOpacity = 0.3, ShadowOffsetX = 3, ShadowOffsetY = 3,
        }),
        new("Shadow", ImageEffects.None with { Shadow = true }),
        new("RoundedShadow", ImageEffects.None with { Shadow = true, CornerRadius = 12 }),
        new("RoundedBorder", ImageEffects.None with { Border = true, CornerRadius = 12 }),
        new("TornBottom", ImageEffects.None with { TornEdges = TornEdges.Bottom }),
        new("TornTopBottom", ImageEffects.None with { TornEdges = TornEdges.Top | TornEdges.Bottom }),
    };

    /// <summary>The preset that <paramref name="effects"/> look like (see <see cref="BorderPreset.Matches"/>), or null.</summary>
    public static BorderPreset? Find(ImageEffects effects) => All.FirstOrDefault(p => p.Matches(effects));

    /// <summary>The effects with what does not show (or is not part of a preset) reset to the defaults.</summary>
    internal static ImageEffects Visible(ImageEffects effects)
    {
        var d = ImageEffects.None;
        return effects with
        {
            ShadowBlur = effects.Shadow ? effects.ShadowBlur : d.ShadowBlur,
            ShadowOpacity = effects.Shadow ? effects.ShadowOpacity : d.ShadowOpacity,
            ShadowOffsetX = effects.Shadow ? effects.ShadowOffsetX : d.ShadowOffsetX,
            ShadowOffsetY = effects.Shadow ? effects.ShadowOffsetY : d.ShadowOffsetY,
            BorderColor = effects.Border ? effects.BorderColor : d.BorderColor,
            BorderWidth = effects.Border ? effects.BorderWidth : d.BorderWidth,
            TornSize = effects.TornEdges != TornEdges.None ? effects.TornSize : d.TornSize,
            TornSeed = d.TornSeed,
            Background = d.Background,
        };
    }
}
