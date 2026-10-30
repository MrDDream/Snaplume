using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using static Snaplume.Editor.Freezables;

namespace Snaplume.Editor;

public enum EditorTool
{
    // New tools go at the end: older presets.json files may store tools as numbers.
    Select, Arrow, Line, Rectangle, Ellipse, Text, Highlight, Pixelate, Spotlight, Step, Stamp, Pen, Crop, Magnifier,
}

/// <summary>
/// Displays the document and handles mouse interaction. Its coordinate space is
/// the image pixel space; zooming is done by a LayoutTransform on a parent.
/// </summary>
public sealed class AnnotationCanvas : FrameworkElement
{
    private enum DragMode { None, Create, Move, Handle, Crop, FloatMove, FloatHandle }

    /// <summary>Smallest width or height of an inserted image being resized, in image pixels.</summary>
    private const double MinFloatingSize = 4;

    /// <summary>Smallest drag, in screen pixels, that draws a shape (a plain click does not).</summary>
    private const double MinDragOnScreen = 3;

    private static readonly Brush AccentBrush = Frozen(new SolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB)));
    private static readonly Brush CropShade = Frozen(new SolidColorBrush(Color.FromArgb(0x88, 0, 0, 0)));

    private EditorDocument? _document;
    private EditorTool _tool;
    private Annotation? _selected;
    private TextAnnotation? _editingText;
    private double _viewScale = 1;

    private DragMode _mode;
    private Annotation? _creating;
    private int _handleIndex;
    private Point _dragStart, _lastPosition;
    /// <summary>State before the drag in progress: committed as one undo step, or restored by Escape.</summary>
    private EditorDocument.Snapshot? _checkpoint;
    private Rect? _cropRect;

    // Image being inserted (pasted, opened or captured), like a paste in Paint: it floats over the image, can be
    // moved and resized, and is only burnt into the document by CommitFloatingImage.
    private BitmapSource? _floatingImage;
    private Rect _floatingRect;
    /// <summary>Place of the inserted image before the drag in progress.</summary>
    private Rect _floatingStart;

    // Annotations below and above the one being drawn or dragged, recorded once instead of at each mouse move.
    private DrawingGroup? _layerBelow, _layerAbove;
    private Annotation? _layerActive;
    private bool _layersValid;

    // Pens one screen pixel wide, rebuilt when the zoom changes.
    private Pen? _selectionPen, _selectionDashPen, _cropPen;

    public event Action? SelectionChanged;
    /// <summary>An image started or stopped floating (see <see cref="FloatImage"/>).</summary>
    public event Action? FloatingImageChanged;
    /// <summary>The inserted image could not be burnt into the document (it keeps floating).</summary>
    public event Action<Exception>? FloatingImageFailed;
    /// <summary>The text annotation to edit, and whether it is a new one.</summary>
    public event Action<TextAnnotation, bool>? TextEditRequested;

    /// <summary>Style of the next annotations drawn.</summary>
    public DrawingStyle CurrentStyle { get; set; } = DrawingStyle.Default;
    public string CurrentStampId { get; set; } = "ok-circle";
    /// <summary>Stamp size factor (1 = 64 px for the square stamps).</summary>
    public double CurrentStampScale { get; set; } = 1;

    public AnnotationCanvas()
    {
        Focusable = true;
        FocusVisualStyle = null;
        // The image and its annotations are clipped while rendering; the selection handles may stick out
        // into the margins of the effects, so that the handles at the edges of the image stay usable.
        ClipToBounds = false;
        Cursor = Cursors.Arrow;
    }

    public EditorDocument? Document
    {
        get => _document;
        set
        {
            if (_document != null) _document.Changed -= OnDocumentChanged;
            _document = value;
            if (_document != null) _document.Changed += OnDocumentChanged;
            SetSelected(null);
            InvalidateLayers();
            InvalidateMeasure();
        }
    }

    public EditorTool Tool
    {
        get => _tool;
        set
        {
            CancelOperation();
            _tool = value;
            Cursor = value switch
            {
                EditorTool.Select => Cursors.Arrow,
                EditorTool.Text => Cursors.IBeam,
                _ => Cursors.Cross,
            };
            if (value == EditorTool.Crop) SetSelected(null);
        }
    }

    /// <summary>Screen DIPs per image pixel; keeps handles a constant size on screen.</summary>
    public double ViewScale
    {
        get => _viewScale;
        set
        {
            _viewScale = value;
            _selectionPen = _selectionDashPen = _cropPen = null;
            InvalidateVisual();
        }
    }

    public Annotation? Selected => _selected;

    /// <summary>True while the mouse draws or drags something.</summary>
    public bool IsDragging => _mode != DragMode.None;

    /// <summary>Bounds of the image, in image pixels.</summary>
    public Rect ImageArea => _document == null ? Rect.Empty : new Rect(0, 0, _document.BaseImage.PixelWidth, _document.BaseImage.PixelHeight);

    /// <summary>Hides the text annotation while its text box is shown (null to show it again).</summary>
    public void SetEditingText(TextAnnotation? text)
    {
        _editingText = text;
        InvalidateLayers();
    }

    public void Select(Annotation? annotation) => SetSelected(annotation);

    /// <summary>True while an inserted image waits to be placed.</summary>
    public bool HasFloatingImage => _floatingImage != null;

    /// <summary>Shows <paramref name="image"/> over the document at <paramref name="area"/>, to be moved and resized.</summary>
    public void FloatImage(BitmapSource image, Rect area)
    {
        CancelOperation();
        SetSelected(null);
        _floatingImage = image;
        _floatingRect = area;
        InvalidateVisual();
        FloatingImageChanged?.Invoke();
    }

    /// <summary>Burns the inserted image into the document, as one undo step. Returns false if there was none.</summary>
    public bool CommitFloatingImage()
    {
        if (_floatingImage is not { } image || _document == null) return false;
        CancelOperation();
        // Not drawn floating any more once the document shows it.
        _floatingImage = null;
        try
        {
            _document.InsertImage(image, _floatingRect);
        }
        catch (Exception ex)
        {
            // Mostly not enough memory for a huge image: it keeps floating, nothing is lost.
            _floatingImage = image;
            InvalidateVisual();
            FloatingImageFailed?.Invoke(ex);
            return false;
        }
        InvalidateLayers();
        FloatingImageChanged?.Invoke();
        return true;
    }

    /// <summary>Drops the inserted image without changing the document.</summary>
    public void CancelFloatingImage()
    {
        if (_floatingImage == null) return;
        CancelOperation();
        _floatingImage = null;
        InvalidateVisual();
        FloatingImageChanged?.Invoke();
    }

    public void NudgeFloatingImage(Vector offset)
    {
        if (_floatingImage == null || IsDragging) return;
        _floatingRect.Offset(offset);
        InvalidateVisual();
    }

    private Point[] FloatingHandles() =>
        new[] { _floatingRect.TopLeft, _floatingRect.TopRight, _floatingRect.BottomRight, _floatingRect.BottomLeft };

    /// <summary>Applies a style change to the selection as one undoable step.</summary>
    public void ApplyToSelection(Action<Annotation> change)
    {
        if (_selected == null || _document == null) return;
        _document.PushUndo();
        change(_selected);
        InvalidateLayers();
    }

    /// <summary>
    /// Aborts a drag in progress: a shape being drawn disappears, a moved or resized one goes back
    /// where it was, without undo step. Returns false if nothing was in progress.
    /// </summary>
    public bool CancelOperation()
    {
        if (_mode == DragMode.None) return false;
        var mode = _mode;
        _mode = DragMode.None;
        if (_document != null)
        {
            if (mode == DragMode.Create && _creating != null)
            {
                _document.Annotations.Remove(_creating);
            }
            else if (mode is DragMode.Move or DragMode.Handle && _checkpoint != null)
            {
                int index = _selected == null ? -1 : _document.Annotations.IndexOf(_selected);
                _document.RevertChange(_checkpoint);
                if (index >= 0 && index < _document.Annotations.Count) SetSelected(_document.Annotations[index]);
            }
        }
        if (mode is DragMode.FloatMove or DragMode.FloatHandle) _floatingRect = _floatingStart;
        _selected?.EndHandleDrag();
        _creating = null;
        _checkpoint = null;
        _cropRect = null;
        ReleaseMouseCapture();
        InvalidateLayers();
        return true;
    }

    protected override Size MeasureOverride(Size availableSize) =>
        _document == null ? new Size() : new Size(_document.BaseImage.PixelWidth, _document.BaseImage.PixelHeight);

    protected override void OnRender(DrawingContext dc)
    {
        if (_document == null) return;
        var image = _document.BaseImage;
        var bounds = new Rect(0, 0, image.PixelWidth, image.PixelHeight);
        // Rounded corners and torn edges cut the image and its annotations, as in the saved image;
        // the margins and the shadow are drawn around the canvas by EffectsBackdrop. The layout is
        // built by the document when it changes, not here.
        var effects = _document.PreviewLayout;
        dc.PushClip(new RectangleGeometry(bounds));
        effects?.PushOutline(dc);
        dc.DrawImage(image, bounds);
        // Where it will be once burnt in: over the image, under the annotations.
        if (_floatingImage != null) dc.DrawImage(_floatingImage, _floatingRect);

        var annotations = _document.Annotations;
        var active = ActiveAnnotation();
        if (NeedsLiveRendering(active))
        {
            AnnotationRenderer.Render(dc, image, annotations, 0, annotations.Count, _editingText);
        }
        else
        {
            var (below, above) = UpdateLayers(active);
            dc.DrawDrawing(below);
            if (active != null) AnnotationRenderer.RenderOne(dc, image, annotations, active, _editingText);
            dc.DrawDrawing(above);
        }
        effects?.PopOutline(dc);
        effects?.DrawBorder(dc);

        if (_mode == DragMode.Crop && _cropRect is Rect crop)
        {
            var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
            shade.Children.Add(new RectangleGeometry(bounds));
            shade.Children.Add(new RectangleGeometry(crop));
            dc.DrawGeometry(CropShade, null, shade);
            _cropPen ??= Frozen(new Pen(Brushes.White, 1 / _viewScale) { DashStyle = DashStyles.Dash });
            dc.DrawRectangle(null, _cropPen, crop);
        }
        dc.Pop();

        if (_selected != null && !ReferenceEquals(_selected, _editingText) && annotations.Contains(_selected))
            DrawSelection(dc, _selected);
        if (_floatingImage != null) DrawFrame(dc, _floatingRect, FloatingHandles());
    }

    /// <summary>
    /// The annotation being drawn or dragged changes others, so everything is drawn live instead of from the
    /// recorded layers: the shared shade follows a spotlight, and a lens shows the pixelated areas and the
    /// annotations below it.
    /// </summary>
    private bool NeedsLiveRendering(Annotation? active)
    {
        if (active == null) return false;
        if (active is SpotlightAnnotation) return true;
        var annotations = _document!.Annotations;
        int index = annotations.IndexOf(active);
        for (int i = 0; i < annotations.Count; i++)
        {
            if (annotations[i] is MagnifierAnnotation && (i > index || active is PixelateAnnotation) && !ReferenceEquals(annotations[i], active))
                return true;
        }
        return false;
    }

    /// <summary>Annotation drawn live because the mouse changes it.</summary>
    private Annotation? ActiveAnnotation() => _mode switch
    {
        DragMode.Create => _creating,
        DragMode.Move or DragMode.Handle when _checkpoint != null => _selected,
        _ => null,
    };

    private void InvalidateLayers()
    {
        _layersValid = false;
        InvalidateVisual();
    }

    /// <summary>Records the annotations around the active one, when the document or the active annotation changed.</summary>
    private (DrawingGroup Below, DrawingGroup Above) UpdateLayers(Annotation? active)
    {
        var annotations = _document!.Annotations;
        int split = active == null ? -1 : annotations.IndexOf(active);
        if (split < 0)
        {
            active = null;
            split = annotations.Count;
        }
        if (_layersValid && ReferenceEquals(active, _layerActive) && _layerBelow != null && _layerAbove != null)
            return (_layerBelow, _layerAbove);

        var image = _document.BaseImage;
        _layerBelow = Record(dc => AnnotationRenderer.Render(dc, image, annotations, 0, split, _editingText));
        _layerAbove = Record(dc => AnnotationRenderer.Render(dc, image, annotations, Math.Min(split + 1, annotations.Count), annotations.Count, _editingText));
        _layerActive = active;
        _layersValid = true;
        return (_layerBelow, _layerAbove);
    }

    private static DrawingGroup Record(Action<DrawingContext> draw)
    {
        var group = new DrawingGroup();
        using (var dc = group.Open()) draw(dc);
        if (group.CanFreeze) group.Freeze();
        return group;
    }

    private void DrawSelection(DrawingContext dc, Annotation annotation) =>
        DrawFrame(dc, annotation is LineAnnotation ? null : annotation.GetBounds(), annotation.GetHandles());

    /// <summary>Dashed frame around <paramref name="bounds"/> (none if null) and square handles, one screen pixel thin.</summary>
    private void DrawFrame(DrawingContext dc, Rect? bounds, IReadOnlyList<Point> handles)
    {
        double unit = 1 / _viewScale;
        _selectionPen ??= Frozen(new Pen(AccentBrush, unit));
        _selectionDashPen ??= Frozen(new Pen(AccentBrush, unit) { DashStyle = DashStyles.Dash });
        if (bounds is Rect frame)
        {
            frame.Inflate(3 * unit, 3 * unit);
            dc.DrawRectangle(null, _selectionDashPen, frame);
        }
        double size = 9 * unit;
        foreach (var h in handles)
            dc.DrawRectangle(Brushes.White, _selectionPen, new Rect(h.X - size / 2, h.Y - size / 2, size, size));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (_document == null) return;
        Focus();
        e.Handled = true;
        var p = e.GetPosition(this);

        // An inserted image is placed whatever the current tool; a click beside it burns it in.
        if (_floatingImage != null)
        {
            _floatingStart = _floatingRect;
            int floatingHandle = HitHandle(FloatingHandles(), p);
            if (floatingHandle >= 0)
            {
                _handleIndex = floatingHandle;
                BeginDrag(DragMode.FloatHandle, p);
            }
            else if (_floatingRect.Contains(p))
            {
                BeginDrag(DragMode.FloatMove, p);
            }
            else
            {
                CommitFloatingImage();
            }
            return;
        }

        // The handles of the selection can be dragged whatever the current tool.
        if (_selected != null && _tool != EditorTool.Crop)
        {
            int handle = HitHandle(_selected.GetHandles(), p);
            if (handle >= 0)
            {
                _handleIndex = handle;
                _selected.BeginHandleDrag(handle);
                BeginDrag(DragMode.Handle, p);
                return;
            }
        }

        switch (_tool)
        {
            case EditorTool.Select:
                var hit = HitTestAll(p);
                if (e.ClickCount == 2 && hit is TextAnnotation text)
                {
                    TextEditRequested?.Invoke(text, false);
                    return;
                }
                SetSelected(hit);
                if (hit != null)
                {
                    hit.BeginMove(p);
                    BeginDrag(DragMode.Move, p);
                }
                break;

            case EditorTool.Text:
                if (HitTestAll(p) is TextAnnotation existing)
                {
                    SetSelected(existing);
                    TextEditRequested?.Invoke(existing, false);
                }
                else
                {
                    SetSelected(null);
                    var created = new TextAnnotation { Position = p };
                    CurrentStyle.ApplyTo(created);
                    TextEditRequested?.Invoke(created, true);
                }
                break;

            case EditorTool.Step:
                var step = new StepAnnotation { Center = p, Number = _document.NextStepNumber };
                CurrentStyle.ApplyTo(step, ImageArea);
                _document.PushUndo();
                _document.Annotations.Add(step);
                SetSelected(step);
                break;

            case EditorTool.Stamp:
                if (HitTestAll(p) is StampAnnotation existingStamp)
                {
                    // Clicking an existing stamp moves it instead of stacking a new one.
                    SetSelected(existingStamp);
                    existingStamp.BeginMove(p);
                    BeginDrag(DragMode.Move, p);
                    break;
                }
                if (StampLibrary.Find(CurrentStampId) is not { } definition) break;
                _document.PushUndo();
                var size = new Vector(definition.Width, definition.Height) * CurrentStampScale;
                var stamp = new StampAnnotation { StampId = definition.Id, Start = p - size / 2, End = p + size / 2 };
                _document.Annotations.Add(stamp);
                SetSelected(stamp);
                break;

            case EditorTool.Magnifier when HitTestAll(p) is MagnifierAnnotation existingMagnifier:
                // Clicking a magnifier moves its lens or its area instead of drawing a new one.
                SetSelected(existingMagnifier);
                existingMagnifier.BeginMove(p);
                BeginDrag(DragMode.Move, p);
                break;

            case EditorTool.Crop:
                SetSelected(null);
                BeginDrag(DragMode.Crop, p);
                _cropRect = new Rect(p, p);
                break;

            default:
                var shape = CreateAnnotation(p);
                if (shape == null) return;
                // The undo step is recorded on release, only if the shape is kept.
                _checkpoint = _document.BeginChange();
                _document.Annotations.Add(shape);
                _creating = shape;
                SetSelected(null);
                BeginDrag(DragMode.Create, p);
                break;
        }
        InvalidateLayers();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_document == null) return;
        var p = e.GetPosition(this);
        if (_mode == DragMode.None)
        {
            UpdateHoverCursor(p);
            return;
        }

        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (_mode)
        {
            case DragMode.Move when _selected != null:
                if (p == _lastPosition) return;
                EnsureCheckpoint();
                _selected.Translate(p - _lastPosition);
                break;

            case DragMode.Handle when _selected != null:
                EnsureCheckpoint();
                if (shift && _selected is LineAnnotation line)
                    p = Constrain(_handleIndex == 0 ? line.End : line.Start, p, isLine: true);
                _selected.MoveHandle(_handleIndex, p);
                break;

            case DragMode.Create when _creating is FreehandAnnotation freehand:
                if (freehand.Points.Count < FreehandAnnotation.MaxPoints) freehand.Points.Add(p);
                break;

            case DragMode.Create when _creating is TwoPointAnnotation shape:
                // A circle magnifier always takes a square area.
                bool square = shift || shape is MagnifierAnnotation { LensShape: MagnifierShape.Circle };
                shape.End = square ? Constrain(shape.Start, p, shape is LineAnnotation) : p;
                if (shape is MagnifierAnnotation magnifier) magnifier.PlaceLens(ImageArea);
                break;

            case DragMode.FloatMove:
                _floatingRect.Offset(p - _lastPosition);
                break;

            case DragMode.FloatHandle:
                _floatingRect = ResizeFloating(p, keepRatio: !shift);
                break;

            case DragMode.Crop:
                var image = ImageArea;
                var crop = new Rect(_dragStart, p);
                crop.Intersect(image);
                _cropRect = crop.IsEmpty ? null : crop;
                break;
        }
        _lastPosition = p;
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_mode == DragMode.None || _document == null) return;
        var mode = _mode;
        var checkpoint = _checkpoint;
        _mode = DragMode.None;
        _checkpoint = null;
        ReleaseMouseCapture();

        switch (mode)
        {
            case DragMode.Create when _creating != null:
                var created = _creating;
                _creating = null;
                if (IsTooSmall(created))
                {
                    _document.Annotations.Remove(created);
                }
                else
                {
                    if (checkpoint != null) _document.CommitChange(checkpoint);
                    SetSelected(created);
                }
                break;

            case DragMode.Crop:
                if (_cropRect is Rect r && r.Width >= 2 && r.Height >= 2)
                {
                    _document.Crop(new Int32Rect((int)Math.Round(r.X), (int)Math.Round(r.Y),
                        (int)Math.Round(r.Width), (int)Math.Round(r.Height)));
                }
                _cropRect = null;
                break;

            case DragMode.FloatMove or DragMode.FloatHandle:
                // The inserted image is not part of the document yet: nothing to record.
                break;

            default:
                _selected?.EndHandleDrag();
                // A click that did not move anything leaves no undo step.
                if (checkpoint != null) _document.CommitChange(checkpoint);
                break;
        }
        InvalidateLayers();
    }

    /// <summary>A shape drawn by a click or a tiny drag is dropped; the limit is on screen, whatever the zoom.</summary>
    private bool IsTooSmall(Annotation created)
    {
        double min = MinDragOnScreen / _viewScale;
        return created switch
        {
            FreehandAnnotation f => f.Points.Count < 2,
            // The lens shows whole pixels: at least 2 of them each way.
            MagnifierAnnotation m => (m.End - m.Start).Length < min || m.GetRect().Width < 2 || m.GetRect().Height < 2,
            // Less than a pixel high or wide would be invisible.
            BoxAnnotation b => (b.End - b.Start).Length < min || b.GetRect().Width < 1 || b.GetRect().Height < 1,
            TwoPointAnnotation t => (t.End - t.Start).Length < min,
            _ => false,
        };
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        base.OnLostMouseCapture(e);
        if (_mode != DragMode.None && Mouse.LeftButton != MouseButtonState.Pressed) CancelOperation();
    }

    private void BeginDrag(DragMode mode, Point p)
    {
        _mode = mode;
        _dragStart = _lastPosition = p;
        CaptureMouse();
    }

    /// <summary>Saves the state before the first change of a move or resize.</summary>
    private void EnsureCheckpoint()
    {
        if (_checkpoint != null || _document == null) return;
        _checkpoint = _document.BeginChange();
        InvalidateLayers();
    }

    private Annotation? CreateAnnotation(Point p)
    {
        Annotation? annotation = _tool switch
        {
            EditorTool.Arrow => new ArrowAnnotation(),
            EditorTool.Line => new LineAnnotation(),
            EditorTool.Rectangle => new RectangleAnnotation(),
            EditorTool.Ellipse => new EllipseAnnotation(),
            EditorTool.Highlight => new HighlightAnnotation(),
            EditorTool.Pixelate => new PixelateAnnotation(),
            EditorTool.Spotlight => new SpotlightAnnotation(),
            EditorTool.Pen => new FreehandAnnotation(),
            EditorTool.Magnifier => new MagnifierAnnotation(),
            _ => null,
        };
        if (annotation == null) return null;
        CurrentStyle.ApplyTo(annotation);
        if (annotation is TwoPointAnnotation shape) shape.Start = shape.End = p;
        if (annotation is MagnifierAnnotation magnifier) magnifier.LensCenter = p;
        if (annotation is FreehandAnnotation freehand) freehand.Points.Add(p);
        return annotation;
    }

    /// <summary>Shift: 45° steps for lines, squares/circles for boxes.</summary>
    private static Point Constrain(Point origin, Point p, bool isLine)
    {
        var v = p - origin;
        if (isLine)
        {
            double step = Math.PI / 4;
            double angle = Math.Round(Math.Atan2(v.Y, v.X) / step) * step;
            return origin + new Vector(Math.Cos(angle), Math.Sin(angle)) * v.Length;
        }
        double d = Math.Max(Math.Abs(v.X), Math.Abs(v.Y));
        return origin + new Vector(Math.Sign(v.X) * d, Math.Sign(v.Y) * d);
    }

    private Annotation? HitTestAll(Point p)
    {
        if (_document == null) return null;
        double tolerance = 4 / _viewScale;
        for (int i = _document.Annotations.Count - 1; i >= 0; i--)
        {
            if (_document.Annotations[i].HitTest(p, tolerance)) return _document.Annotations[i];
        }
        return null;
    }

    /// <summary>
    /// Inserted image resized by its handle <see cref="_handleIndex"/> dragged to <paramref name="p"/>, the opposite
    /// corner staying in place; with <paramref name="keepRatio"/>, its proportions follow the dominant axis.
    /// </summary>
    private Rect ResizeFloating(Point p, bool keepRatio)
    {
        var start = _floatingStart;
        var anchor = new[] { start.BottomRight, start.BottomLeft, start.TopLeft, start.TopRight }[_handleIndex];
        var v = p - anchor;
        double width = Math.Max(Math.Abs(v.X), MinFloatingSize), height = Math.Max(Math.Abs(v.Y), MinFloatingSize);
        if (keepRatio && start.Width > 0 && start.Height > 0)
        {
            double ratio = start.Width / start.Height;
            width = Math.Max(width, height * ratio);
            height = width / ratio;
        }
        // The handle may be dragged past the anchor: the image then grows on the other side.
        return new Rect(anchor, anchor + new Vector(v.X < 0 ? -width : width, v.Y < 0 ? -height : height));
    }

    private int HitHandle(IReadOnlyList<Point> handles, Point p)
    {
        double tolerance = 7 / _viewScale;
        for (int i = 0; i < handles.Count; i++)
        {
            if (Math.Abs(handles[i].X - p.X) <= tolerance && Math.Abs(handles[i].Y - p.Y) <= tolerance) return i;
        }
        return -1;
    }

    private void UpdateHoverCursor(Point p)
    {
        Cursor cursor;
        if (_floatingImage != null)
            cursor = HitHandle(FloatingHandles(), p) >= 0 ? Cursors.Hand : _floatingRect.Contains(p) ? Cursors.SizeAll : Cursors.Arrow;
        else if (_selected != null && _tool != EditorTool.Crop && HitHandle(_selected.GetHandles(), p) >= 0) cursor = Cursors.Hand;
        else if (_tool == EditorTool.Select && HitTestAll(p) != null) cursor = Cursors.SizeAll;
        else if (_tool == EditorTool.Magnifier && HitTestAll(p) is MagnifierAnnotation) cursor = Cursors.SizeAll;
        else cursor = _tool switch
        {
            EditorTool.Select => Cursors.Arrow,
            EditorTool.Text => Cursors.IBeam,
            _ => Cursors.Cross,
        };
        if (Cursor != cursor) Cursor = cursor;
    }

    private void SetSelected(Annotation? annotation)
    {
        if (ReferenceEquals(_selected, annotation)) return;
        _selected = annotation;
        InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private void OnDocumentChanged()
    {
        if (_document is { IsEffectsChange: true })
        {
            // Only the outline and the border change (a slider being dragged): the recorded layers stay valid.
            InvalidateVisual();
            return;
        }
        if (_selected != null && _document != null && !_document.Annotations.Contains(_selected)) SetSelected(null);
        InvalidateLayers();
        InvalidateMeasure();
    }

}
