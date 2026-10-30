using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Snaplume.Editor;

/// <summary>
/// Preview of the image effects under the <see cref="AnnotationCanvas"/>: the background of the margins
/// and the shadow, at the size of the flattened image. The canvas is placed over it with the margins of
/// the effects, so that the annotation coordinates stay those of the image.
/// </summary>
public sealed class EffectsBackdrop : Control
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
    public Thickness ImageMargin => _document?.PreviewLayout is { } layout ? layout.Padding.ToThickness() : default;

    private void OnDocumentChanged()
    {
        if (_document == null || (_drawn && ReferenceEquals(_drawnLayout, _document.PreviewLayout))) return;
        InvalidateMeasure();
        InvalidateVisual();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_document == null) return default;
        if (_document.PreviewLayout is { } layout) return new Size(layout.OutputWidth, layout.OutputHeight);
        return new Size(_document.BaseImage.Width, _document.BaseImage.Height);
    }

    public override void Render(DrawingContext context)
    {
        if (_document == null) return;
        var layout = _document.PreviewLayout;
        _drawnLayout = layout;
        _drawn = true;
        if (layout == null) return;
        using (context.PushTransform(Matrix.CreateTranslation(layout.Padding.Left, layout.Padding.Top)))
            layout.DrawBackdrop(context);
    }
}

/// <summary>
/// A small drawing made by code (a stamp, the sample of a preset), fitted into the control, always left to right.
/// Drawn rather than turned into a picture: it stays sharp at any scale of the screen.
/// </summary>
public sealed class DrawingPreview : Control
{
    private Action<DrawingContext>? _draw;
    private Size _drawingSize;

    public DrawingPreview()
    {
        FlowDirection = FlowDirection.LeftToRight;
        IsHitTestVisible = false;
    }

    /// <summary>What to draw, in a box of <paramref name="size"/> scaled uniformly to the control.</summary>
    public void SetDrawing(Action<DrawingContext>? draw, Size size)
    {
        _draw = draw;
        _drawingSize = size;
        InvalidateMeasure();
        InvalidateVisual();
    }

    public static DrawingPreview ForStamp(StampDefinition stamp, double height)
    {
        var preview = new DrawingPreview { Height = height };
        preview.SetStamp(stamp);
        return preview;
    }

    public void SetStamp(StampDefinition? stamp)
    {
        if (stamp == null) SetDrawing(null, default);
        else SetDrawing(dc => stamp.Render(dc, new Rect(0, 0, stamp.Width, stamp.Height)), new Size(stamp.Width, stamp.Height));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        if (_drawingSize.Width <= 0 || _drawingSize.Height <= 0) return default;
        double width = double.IsNaN(Width) ? double.PositiveInfinity : Width;
        double height = double.IsNaN(Height) ? double.PositiveInfinity : Height;
        width = Math.Min(Math.Min(width, availableSize.Width), double.IsNaN(MaxWidth) ? double.PositiveInfinity : MaxWidth);
        height = Math.Min(height, availableSize.Height);
        double scale = Math.Min(width / _drawingSize.Width, height / _drawingSize.Height);
        if (double.IsInfinity(scale)) scale = 1;
        return new Size(_drawingSize.Width * scale, _drawingSize.Height * scale);
    }

    public override void Render(DrawingContext context)
    {
        if (_draw == null || _drawingSize.Width <= 0 || _drawingSize.Height <= 0) return;
        double scale = Math.Min(Bounds.Width / _drawingSize.Width, Bounds.Height / _drawingSize.Height);
        if (scale <= 0 || double.IsNaN(scale)) return;
        double x = (Bounds.Width - _drawingSize.Width * scale) / 2, y = (Bounds.Height - _drawingSize.Height * scale) / 2;
        using (context.PushTransform(new Matrix(scale, 0, 0, scale, x, y))) _draw(context);
    }
}
