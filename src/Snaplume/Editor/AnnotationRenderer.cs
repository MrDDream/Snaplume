using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snaplume.Editor;

/// <summary>
/// Draws annotations in order, the same way on screen and in the saved image. All the spotlights
/// make a single shade (the image minus the union of their areas), drawn at the place of the first one.
/// Magnifiers show what is below them, and the pixelated areas of the whole document wherever they are in the order.
/// </summary>
public static class AnnotationRenderer
{
    public static void Render(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations) =>
        Render(dc, baseImage, annotations, 0, annotations.Count);

    /// <summary>Draws the annotations from index <paramref name="from"/> (included) to <paramref name="to"/> (excluded).</summary>
    /// <param name="skip">Annotation left out (text being edited).</param>
    /// <param name="withMagnifiers">False inside a lens: a magnifier does not show the other ones.</param>
    public static void Render(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations,
        int from, int to, Annotation? skip = null, bool withMagnifiers = true)
    {
        int shadeIndex = -1;
        for (int i = 0; i < annotations.Count; i++)
        {
            if (annotations[i] is SpotlightAnnotation && !ReferenceEquals(annotations[i], skip))
            {
                shadeIndex = i;
                break;
            }
        }

        for (int i = from; i < to; i++)
        {
            var annotation = annotations[i];
            if (ReferenceEquals(annotation, skip)) continue;
            if (annotation is SpotlightAnnotation)
            {
                if (i == shadeIndex)
                {
                    var spotlights = annotations.OfType<SpotlightAnnotation>().Where(s => !ReferenceEquals(s, skip));
                    SpotlightAnnotation.DrawShade(dc, new Rect(0, 0, baseImage.PixelWidth, baseImage.PixelHeight), spotlights);
                }
                continue;
            }
            if (annotation is MagnifierAnnotation magnifier)
            {
                if (withMagnifiers) magnifier.Render(dc, baseImage, annotations, i, skip);
                continue;
            }
            annotation.Render(dc, baseImage);
        }
    }

    /// <summary>Draws one annotation of the document (the one being drawn or dragged), as <see cref="Render(DrawingContext, BitmapSource, IReadOnlyList{Annotation})"/> would.</summary>
    public static void RenderOne(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations, Annotation annotation,
        Annotation? skip = null)
    {
        if (annotation is MagnifierAnnotation magnifier)
        {
            int index = IndexOf(annotations, annotation);
            magnifier.Render(dc, baseImage, annotations, index < 0 ? annotations.Count : index, skip);
        }
        else
        {
            annotation.Render(dc, baseImage);
        }
    }

    private static int IndexOf(IReadOnlyList<Annotation> annotations, Annotation annotation)
    {
        for (int i = 0; i < annotations.Count; i++)
        {
            if (ReferenceEquals(annotations[i], annotation)) return i;
        }
        return -1;
    }
}
