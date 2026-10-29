using System.Windows;
using System.Windows.Media;

namespace Snaplume.Editor;

/// <summary>
/// Preview of the image effects under the <see cref="AnnotationCanvas"/>: the background of the margins
/// and the shadow, at the size of the flattened image. The canvas is placed over it with the margins of
/// the effects, so that the annotation coordinates stay those of the image.
/// </summary>
public sealed class EffectsBackdrop : FrameworkElement
{
    private EditorDocument? _document;
    // What was drawn last: redrawn only when the layout of the effects changes. The layout is built by the
    // document when it changes (never here while rendering: a failure falls back to effects without shadow).
    private EffectsLayout? _drawnLayout;
    private bool _drawn;

    public EffectsBackdrop()
    {
        IsHitTestVisible = false;
        ClipToBounds = true;
    }

    public EditorDocument? Document
    {
        get => _document;
        set
        {
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            _drawn = false;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Margins between the edges of the backdrop and the image.</summary>
    public Thickness ImageMargin => _document?.PreviewLayout is { } layout ? layout.Padding.ToThickness() : new Thickness();

    private void OnDocumentChanged()
    {
        if (_document == null || (_drawn && ReferenceEquals(_drawnLayout, _document.PreviewLayout))) return;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_document == null) return new Size();
        if (_document.PreviewLayout is { } layout) return new Size(layout.OutputWidth, layout.OutputHeight);
        return new Size(_document.BaseImage.PixelWidth, _document.BaseImage.PixelHeight);
    }

    protected override void OnRender(DrawingContext dc)
    {
        if (_document == null) return;
        var layout = _document.PreviewLayout;
        _drawnLayout = layout;
        _drawn = true;
        if (layout == null) return;
        dc.PushTransform(new TranslateTransform(layout.Padding.Left, layout.Padding.Top));
        layout.DrawBackdrop(dc);
        dc.Pop();
    }
}
