using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snaplume.Editor;

/// <summary>
/// Draws annotations in order, the same way on screen and in the saved image. All the spotlights
/// make a single shade (the image minus the union of their areas), drawn at the place of the first one.
/// </summary>
public static class AnnotationRenderer
{
    public static void Render(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations) =>
        Render(dc, baseImage, annotations, 0, annotations.Count);

    /// <summary>Draws the annotations from index <paramref name="from"/> (included) to <paramref name="to"/> (excluded).</summary>
    /// <param name="skip">Annotation left out (text being edited).</param>
    public static void Render(DrawingContext dc, BitmapSource baseImage, IReadOnlyList<Annotation> annotations,
        int from, int to, Annotation? skip = null)
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
            annotation.Render(dc, baseImage);
        }
    }
}
