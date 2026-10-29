using System.Windows;
using System.Windows.Media;

namespace Snaplume.Editor;

/// <summary>
/// Style settings of the drawing tools. Each annotation kind only uses some of them:
/// <see cref="Fill"/> is the fill of rectangles and ellipses or the white background of texts,
/// <see cref="Heads"/> is for arrows, <see cref="Shape"/> and <see cref="Letters"/> for numbered bullets.
/// </summary>
public sealed record DrawingStyle(Color Color, double Thickness, bool Fill, double FontSize, ArrowHeads Heads, StepShape Shape, bool Letters)
{
    public static readonly DrawingStyle Default = new(Colors.Red, 4, false, 24, ArrowHeads.End, StepShape.Circle, false);

    /// <summary>Gives the annotation the settings of this style that apply to its kind.</summary>
    /// <param name="area">Image bounds: a bullet target created by a new shape stays inside.</param>
    public void ApplyTo(Annotation annotation, Rect? area = null)
    {
        annotation.Color = Color;
        annotation.Thickness = Thickness;
        switch (annotation)
        {
            case ArrowAnnotation arrow:
                arrow.Heads = Heads;
                break;
            case RectangleAnnotation rectangle:
                rectangle.Filled = Fill;
                break;
            case EllipseAnnotation ellipse:
                ellipse.Filled = Fill;
                break;
            case TextAnnotation text:
                text.HasBackground = Fill;
                text.FontSize = FontSize;
                break;
            case StepAnnotation step:
                step.Shape = Shape;
                step.Letters = Letters;
                step.EnsureTarget(area);
                break;
        }
    }

    /// <summary>
    /// Style of an annotation; the settings its kind does not use come from <paramref name="fallback"/>.
    /// Null for stamps, which have no style.
    /// </summary>
    public static DrawingStyle? From(Annotation annotation, DrawingStyle? fallback = null)
    {
        if (annotation is StampAnnotation) return null;
        var style = (fallback ?? Default) with { Color = annotation.Color, Thickness = annotation.Thickness };
        return annotation switch
        {
            ArrowAnnotation arrow => style with { Heads = arrow.Heads },
            RectangleAnnotation rectangle => style with { Fill = rectangle.Filled },
            EllipseAnnotation ellipse => style with { Fill = ellipse.Filled },
            TextAnnotation text => style with { Fill = text.HasBackground, FontSize = text.FontSize },
            StepAnnotation step => style with { Shape = step.Shape, Letters = step.Letters },
            _ => style,
        };
    }

    /// <summary>This style with the settings that matter to <paramref name="tool"/> taken from <paramref name="source"/>.</summary>
    public DrawingStyle Merge(EditorTool tool, DrawingStyle source)
    {
        var style = this with { Color = source.Color, Thickness = source.Thickness };
        return tool switch
        {
            EditorTool.Arrow => style with { Heads = source.Heads },
            EditorTool.Rectangle or EditorTool.Ellipse => style with { Fill = source.Fill },
            EditorTool.Text => style with { Fill = source.Fill, FontSize = source.FontSize },
            EditorTool.Step => style with { Shape = source.Shape, Letters = source.Letters },
            _ => style,
        };
    }
}
