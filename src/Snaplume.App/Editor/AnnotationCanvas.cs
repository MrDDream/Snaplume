using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Snaplume.Imaging;

namespace Snaplume.Editor;

/// <summary>
/// Displays the document and handles mouse interaction. Its coordinate space is the image pixel space;
/// zooming is done by a layout transform on a parent. The drawing is split in layers (image, annotations
/// below, the one being drawn or dragged, annotations above, then the frames and handles): while the mouse
/// changes one annotation, only its layer is drawn again.
/// </summary>
public sealed class AnnotationCanvas : Control
{
    private enum DragMode { None, Create, Move, Handle, Crop, FloatMove, FloatHandle }

    /// <summary>Smallest width or height of an inserted image being resized, in image pixels.</summary>
    private const double MinFloatingSize = 4;

    /// <summary>Smallest drag, in screen pixels, that draws a shape (a plain click does not).</summary>
    private const double MinDragOnScreen = 3;

    /// <summary>Distance, in screen pixels, under which a moved image sticks to an edge of the capture.</summary>
    private const double SnapOnScreen = 8;

    private static readonly IImmutableSolidColorBrush AccentBrush = new ImmutableSolidColorBrush(Color.FromRgb(0x25, 0x63, 0xEB));
    private static readonly IImmutableSolidColorBrush CropShade = new ImmutableSolidColorBrush(Color.FromArgb(0x88, 0, 0, 0));
    private static readonly ImmutableDashStyle Dash = new(new double[] { 4, 3 }, 0);
    private static readonly ImmutableDashStyle Dot = new(new double[] { 1, 2 }, 0);

    private static readonly Cursor ArrowCursor = new(StandardCursorType.Arrow);
    private static readonly Cursor CrossCursor = new(StandardCursorType.Cross);
    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);
    private static readonly Cursor HandCursor = new(StandardCursorType.Hand);
    private static readonly Cursor MoveCursor = new(StandardCursorType.SizeAll);

    private readonly Layer _imageLayer, _belowLayer, _activeLayer, _aboveLayer, _overlayLayer;

    private EditorDocument? _document;
    private EditorTool _tool;
    private Annotation? _selected;
    private TextAnnotation? _editingText;
    private double _viewScale = 1;
    private BitmapInterpolationMode _interpolation = BitmapInterpolationMode.HighQuality;

    private DragMode _mode;
    private Annotation? _creating;
    private int _handleIndex;
    private Point _dragStart, _lastPosition;
    /// <summary>State before the drag in progress: committed as one undo step, or restored by Escape.</summary>
    private EditorDocument.Snapshot? _checkpoint;
    private Rect? _cropRect;
    private IPointer? _capturedPointer;

    // Image being inserted (pasted, opened or captured), like a paste in Paint: it floats over the image, can be
    // moved and resized, and is only burnt into the document by CommitFloatingImage.
    private PixelBuffer? _floatingImage;
    private Bitmap? _floatingBitmap;
    private Rect _floatingRect;
    /// <summary>Place of the inserted image before the drag in progress.</summary>
    private Rect _floatingStart;

    // Pens one screen pixel wide, rebuilt when the zoom changes.
    private IPen? _selectionPen, _selectionDashPen, _cropPen, _floatingOutlinePen;

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
        // The image and its annotations are clipped while rendering; the selection handles may stick out
        // into the margins of the effects, so that the handles at the edges of the image stay usable.
        ClipToBounds = false;
        Cursor = ArrowCursor;
        _imageLayer = AddLayer(DrawImageLayer);
        _belowLayer = AddLayer(dc => DrawAnnotations(dc, Part.Below));
        _activeLayer = AddLayer(dc => DrawAnnotations(dc, Part.Active));
        _aboveLayer = AddLayer(dc => DrawAnnotations(dc, Part.Above));
        _overlayLayer = AddLayer(DrawOverlay);
    }

    private Layer AddLayer(Action<DrawingContext> draw)
    {
        var layer = new Layer(draw);
        VisualChildren.Add(layer);
        LogicalChildren.Add(layer);
        return layer;
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
                EditorTool.Select => ArrowCursor,
                EditorTool.Text => TextCursor,
                _ => CrossCursor,
            };
            if (value == EditorTool.Crop) SetSelected(null);
        }
    }

    /// <summary>Screen units per image pixel; keeps handles a constant size on screen.</summary>
    public double ViewScale
    {
        get => _viewScale;
        set
        {
            _viewScale = value;
            _selectionPen = _selectionDashPen = _cropPen = _floatingOutlinePen = null;
            InvalidateVisual();
            _overlayLayer.InvalidateVisual();
        }
    }

    /// <summary>Smooth when the image is shown smaller, sharp pixels when it is enlarged.</summary>
    public BitmapInterpolationMode Interpolation
    {
        get => _interpolation;
        set
        {
            if (_interpolation == value) return;
            _interpolation = value;
            InvalidateLayers();
        }
    }

    public Annotation? Selected => _selected;

    /// <summary>True while the mouse draws or drags something.</summary>
    public bool IsDragging => _mode != DragMode.None;

    /// <summary>Bounds of the image, in image pixels.</summary>
    public Rect ImageArea => _document == null ? default : _document.BaseImage.Bounds;

    /// <summary>Hides the text annotation while its text box is shown (null to show it again).</summary>
    public void SetEditingText(TextAnnotation? text)
    {
        _editingText = text;
        InvalidateLayers();
    }

    public void Select(Annotation? annotation) => SetSelected(annotation);

    /// <summary>True while an inserted image waits to be placed.</summary>
    public bool HasFloatingImage => _floatingImage != null;

    /// <summary>Where the inserted image is, in image pixels.</summary>
    public Rect FloatingRect => _floatingRect;

    /// <summary>Shows <paramref name="image"/> over the document at <paramref name="area"/>, to be moved and resized.</summary>
    public void FloatImage(PixelBuffer image, Rect area)
    {
        CancelOperation();
        SetSelected(null);
        _floatingImage = image;
        _floatingBitmap = AvaloniaImages.ToBitmap(image);
        _floatingRect = area;
        InvalidateFloating();
        FloatingImageChanged?.Invoke();
    }

    /// <summary>Burns the inserted image into the document, as one undo step. Returns false if there was none.</summary>
    public bool CommitFloatingImage()
    {
        if (_floatingImage is not { } image || _document == null) return false;
        CancelOperation();
        // Not drawn floating any more once the document shows it.
        var bitmap = _floatingBitmap;
        _floatingImage = null;
        _floatingBitmap = null;
        try
        {
            _document.InsertImage(image, _floatingRect, Colors.White);
        }
        catch (Exception ex)
        {
            // Mostly not enough memory for a huge image: it keeps floating, nothing is lost.
            _floatingImage = image;
            _floatingBitmap = bitmap;
            InvalidateFloating();
            FloatingImageFailed?.Invoke(ex);
            return false;
        }
        bitmap?.Dispose();
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
        _floatingBitmap?.Dispose();
        _floatingBitmap = null;
        InvalidateFloating();
        FloatingImageChanged?.Invoke();
    }

    public void NudgeFloatingImage(Vector offset)
    {
        if (_floatingImage == null || IsDragging) return;
        _floatingRect = Rects.Offset(_floatingRect, offset);
        InvalidateFloating();
    }

    private Point[] FloatingHandles() =>
        new[] { _floatingRect.TopLeft, _floatingRect.TopRight, _floatingRect.BottomRight, _floatingRect.BottomLeft };

    /// <summary>Applies a style change to the selection as one undoable step.</summary>
    public void ApplyToSelection(Action<Annotation> change)
    {
        if (_selected == null || _document == null) return;
        // Told once changed: the window shows the new style.
        var before = _document.BeginChange();
        change(_selected);
        _document.CommitChange(before);
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
        ReleaseCapture();
        InvalidateLayers();
        return true;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        foreach (var layer in VisualChildren.OfType<Layer>()) layer.Measure(availableSize);
        return _document == null ? default : new Size(_document.BaseImage.Width, _document.BaseImage.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var layer in VisualChildren.OfType<Layer>()) layer.Arrange(new Rect(finalSize));
        return finalSize;
    }

    // ---- Drawing ----

    /// <summary>
    /// Nothing visible: a transparent area where the canvas takes the mouse, the image with a margin for the
    /// handles at its edges, and the inserted image wherever it is (it may be placed beside the capture).
    /// </summary>
    public override void Render(DrawingContext context)
    {
        if (_document == null) return;
        var area = Rects.Grow(_document.BaseImage.Bounds, 8 / _viewScale);
        if (_floatingImage != null) area = Rects.Union(area, Rects.Grow(_floatingRect, 8 / _viewScale));
        context.FillRectangle(Brushes.Transparent, area);
    }

    private enum Part { Below, Active, Above }

    /// <summary>Clips to the image and to the outline of the effects (rounded corners, torn edges), as in the saved image.</summary>
    private IDisposable[] PushImageClip(DrawingContext dc)
    {
        var effects = _document!.PreviewLayout;
        return new[]
        {
            (IDisposable)dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = _interpolation }),
            dc.PushClip(_document.BaseImage.Bounds),
            effects?.PushOutline(dc) ?? NoState,
        };
    }

    private static readonly IDisposable NoState = new EmptyState();

    private sealed class EmptyState : IDisposable
    {
        public void Dispose()
        {
        }
    }

    private static void Pop(IDisposable[] states)
    {
        for (int i = states.Length - 1; i >= 0; i--) states[i].Dispose();
    }

    /// <summary>The base image, and the inserted image where it will be once burnt in: over the image, under the annotations.</summary>
    private void DrawImageLayer(DrawingContext dc)
    {
        if (_document == null) return;
        var states = PushImageClip(dc);
        dc.DrawImage(_document.BaseImage.Bitmap, _document.BaseImage.Bounds);
        if (_floatingBitmap != null) dc.DrawImage(_floatingBitmap, _floatingRect);
        Pop(states);
    }

    private void DrawAnnotations(DrawingContext dc, Part part)
    {
        if (_document == null) return;
        var annotations = _document.Annotations;
        var image = _document.BaseImage;
        var active = ActiveAnnotation();
        bool live = NeedsLiveRendering(active);
        int split = active == null ? -1 : annotations.IndexOf(active);
        if (split < 0)
        {
            active = null;
            split = annotations.Count;
        }
        var states = PushImageClip(dc);
        switch (part)
        {
            case Part.Below when live:
                AnnotationRenderer.Render(dc, image, annotations, 0, annotations.Count, _editingText);
                break;
            case Part.Below:
                AnnotationRenderer.Render(dc, image, annotations, 0, split, _editingText);
                break;
            case Part.Active when !live && active != null:
                AnnotationRenderer.RenderOne(dc, image, annotations, active, _editingText);
                break;
            case Part.Above when !live:
                AnnotationRenderer.Render(dc, image, annotations, Math.Min(split + 1, annotations.Count), annotations.Count, _editingText);
                break;
        }
        Pop(states);
    }

    /// <summary>The border of the effects, the crop shade, then the selection frame and the inserted image's frame.</summary>
    private void DrawOverlay(DrawingContext dc)
    {
        if (_document == null) return;
        var bounds = _document.BaseImage.Bounds;
        using (dc.PushClip(bounds))
        {
            _document.PreviewLayout?.DrawBorder(dc);
            if (_mode == DragMode.Crop && _cropRect is Rect crop)
            {
                var shade = new GeometryGroup { FillRule = FillRule.EvenOdd };
                shade.Children.Add(new RectangleGeometry(bounds));
                shade.Children.Add(new RectangleGeometry(crop));
                dc.DrawGeometry(CropShade, null, shade);
                _cropPen ??= new ImmutablePen(Brushes.White.ToImmutable(), 1 / _viewScale, Dash);
                dc.DrawRectangle(null, _cropPen, crop);
            }
        }

        if (_selected != null && !ReferenceEquals(_selected, _editingText) && _document.Annotations.Contains(_selected))
            DrawFrame(dc, _selected is LineAnnotation ? null : _selected.GetBounds(), _selected.GetHandles());
        if (_floatingBitmap != null) DrawFloatingOutside(dc, bounds);
    }

    /// <summary>
    /// The inserted image where it goes past the edges, on the white the image will grow by once it is placed
    /// (see <see cref="EditorDocument.InsertImage"/>), the new outline dotted; then its frame and handles.
    /// </summary>
    private void DrawFloatingOutside(DrawingContext dc, Rect bounds)
    {
        var grown = EditorDocument.InsertedBounds((int)bounds.Width, (int)bounds.Height, _floatingRect);
        var future = new Rect(grown.X, grown.Y, grown.Width, grown.Height);
        if (future != bounds)
        {
            var outside = new CombinedGeometry(GeometryCombineMode.Exclude, new RectangleGeometry(future), new RectangleGeometry(bounds));
            using (dc.PushGeometryClip(outside))
            using (dc.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = _interpolation }))
            {
                dc.DrawRectangle(Brushes.White, null, future);
                dc.DrawImage(_floatingBitmap!, _floatingRect);
            }
            _floatingOutlinePen ??= new ImmutablePen(Brushes.Gray.ToImmutable(), 1 / _viewScale, Dot);
            dc.DrawRectangle(null, _floatingOutlinePen, future);
        }
        DrawFrame(dc, _floatingRect, FloatingHandles());
    }

    /// <summary>Dashed frame around <paramref name="bounds"/> (none if null) and square handles, one screen pixel thin.</summary>
    private void DrawFrame(DrawingContext dc, Rect? bounds, IReadOnlyList<Point> handles)
    {
        double unit = 1 / _viewScale;
        _selectionPen ??= new ImmutablePen(AccentBrush, unit);
        _selectionDashPen ??= new ImmutablePen(AccentBrush, unit, Dash);
        if (bounds is Rect frame) dc.DrawRectangle(null, _selectionDashPen, Rects.Grow(frame, 3 * unit));
        double size = 9 * unit;
        foreach (var h in handles)
            dc.DrawRectangle(Brushes.White, _selectionPen, new Rect(h.X - size / 2, h.Y - size / 2, size, size));
    }

    /// <summary>
    /// The annotation being drawn or dragged changes others, so everything is drawn live in one layer: the shared
    /// shade follows a spotlight, and a lens shows the pixelated areas and the annotations below it.
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

    /// <summary>Annotation drawn on its own layer because the mouse changes it.</summary>
    private Annotation? ActiveAnnotation() => _mode switch
    {
        DragMode.Create => _creating,
        DragMode.Move or DragMode.Handle when _checkpoint != null => _selected,
        _ => null,
    };

    private void InvalidateLayers()
    {
        InvalidateVisual();
        _imageLayer.InvalidateVisual();
        _belowLayer.InvalidateVisual();
        _activeLayer.InvalidateVisual();
        _aboveLayer.InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    /// <summary>Only what the mouse is changing is drawn again.</summary>
    private void InvalidateActive()
    {
        if (NeedsLiveRendering(ActiveAnnotation())) _belowLayer.InvalidateVisual();
        else _activeLayer.InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    private void InvalidateFloating()
    {
        InvalidateVisual();
        _imageLayer.InvalidateVisual();
        _overlayLayer.InvalidateVisual();
    }

    // ---- Mouse ----

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (_document == null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
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
                BeginDrag(DragMode.FloatHandle, p, e);
            }
            else if (_floatingRect.Contains(p))
            {
                BeginDrag(DragMode.FloatMove, p, e);
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
                BeginDrag(DragMode.Handle, p, e);
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
                    BeginDrag(DragMode.Move, p, e);
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
                var beforeStep = _document.BeginChange();
                _document.Annotations.Add(step);
                SetSelected(step);
                _document.CommitChange(beforeStep);
                break;

            case EditorTool.Stamp:
                if (HitTestAll(p) is StampAnnotation existingStamp)
                {
                    // Clicking an existing stamp moves it instead of stacking a new one.
                    SetSelected(existingStamp);
                    existingStamp.BeginMove(p);
                    BeginDrag(DragMode.Move, p, e);
                    break;
                }
                if (StampLibrary.Find(CurrentStampId) is not { } definition) break;
                var beforeStamp = _document.BeginChange();
                var size = new Vector(definition.Width, definition.Height) * CurrentStampScale;
                var stamp = new StampAnnotation { StampId = definition.Id, Start = p - size / 2, End = p + size / 2 };
                _document.Annotations.Add(stamp);
                SetSelected(stamp);
                _document.CommitChange(beforeStamp);
                break;

            case EditorTool.Magnifier when HitTestAll(p) is MagnifierAnnotation existingMagnifier:
                // Clicking a magnifier moves its lens or its area instead of drawing a new one.
                SetSelected(existingMagnifier);
                existingMagnifier.BeginMove(p);
                BeginDrag(DragMode.Move, p, e);
                break;

            case EditorTool.Crop:
                SetSelected(null);
                BeginDrag(DragMode.Crop, p, e);
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
                BeginDrag(DragMode.Create, p, e);
                break;
        }
        InvalidateLayers();
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (_document == null) return;
        var p = e.GetPosition(this);
        if (_mode == DragMode.None)
        {
            UpdateHoverCursor(p);
            return;
        }

        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
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
                freehand.AddPoint(p);
                break;

            case DragMode.Create when _creating is TwoPointAnnotation shape:
                // A circle magnifier always takes a square area.
                bool square = shift || shape is MagnifierAnnotation { LensShape: MagnifierShape.Circle };
                shape.End = square ? Constrain(shape.Start, p, shape is LineAnnotation) : p;
                if (shape is MagnifierAnnotation magnifier) magnifier.PlaceLens(ImageArea);
                break;

            case DragMode.FloatMove:
                var moved = Rects.Offset(_floatingStart, p - _dragStart);
                // Alt places it freely, without sticking to the edges.
                _floatingRect = e.KeyModifiers.HasFlag(KeyModifiers.Alt) ? moved : SnapToEdges(moved);
                _lastPosition = p;
                InvalidateFloating();
                return;

            case DragMode.FloatHandle:
                _floatingRect = ResizeFloating(p, keepRatio: !shift);
                _lastPosition = p;
                InvalidateFloating();
                return;

            case DragMode.Crop:
                _cropRect = Rects.Intersect(Rects.Between(_dragStart, p), ImageArea);
                break;
        }
        _lastPosition = p;
        InvalidateActive();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.InitialPressMouseButton != MouseButton.Left || _mode == DragMode.None || _document == null) return;
        var mode = _mode;
        var checkpoint = _checkpoint;
        _mode = DragMode.None;
        _checkpoint = null;
        ReleaseCapture();

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
                    _document.Crop(new PixelRect((int)Math.Round(r.X), (int)Math.Round(r.Y),
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

    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        base.OnPointerCaptureLost(e);
        // Another window or a system dialog took the mouse in the middle of a drag.
        if (_mode != DragMode.None && ReferenceEquals(e.Pointer, _capturedPointer))
        {
            _capturedPointer = null;
            CancelOperation();
        }
    }

    /// <summary>A shape drawn by a click or a tiny drag is dropped; the limit is on screen, whatever the zoom.</summary>
    private bool IsTooSmall(Annotation created)
    {
        double min = MinDragOnScreen / _viewScale;
        double Length(TwoPointAnnotation t) => ((Vector)(t.End - t.Start)).Length;
        return created switch
        {
            FreehandAnnotation f => f.Points.Count < 2,
            // The lens shows whole pixels: at least 2 of them each way.
            MagnifierAnnotation m => Length(m) < min || m.GetRect().Width < 2 || m.GetRect().Height < 2,
            // Less than a pixel high or wide would be invisible.
            BoxAnnotation b => Length(b) < min || b.GetRect().Width < 1 || b.GetRect().Height < 1,
            TwoPointAnnotation t => Length(t) < min,
            _ => false,
        };
    }

    private void BeginDrag(DragMode mode, Point p, PointerEventArgs e)
    {
        _mode = mode;
        _dragStart = _lastPosition = p;
        _capturedPointer = e.Pointer;
        e.Pointer.Capture(this);
    }

    private void ReleaseCapture()
    {
        var pointer = _capturedPointer;
        _capturedPointer = null;
        if (pointer?.Captured == this) pointer.Capture(null);
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
        if (annotation is FreehandAnnotation freehand) freehand.AddPoint(p);
        return annotation;
    }

    /// <summary>Shift: 45° steps for lines, squares/circles for boxes.</summary>
    private static Point Constrain(Point origin, Point p, bool isLine)
    {
        Vector v = p - origin;
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
    /// Moves an inserted image near an edge of the capture onto it: aligned with the edge inside, or right next
    /// to it outside (two captures side by side, the capture then grows to hold both).
    /// </summary>
    internal Rect SnapToEdges(Rect r)
    {
        var image = ImageArea;
        double tolerance = SnapOnScreen / _viewScale;
        static double Nearest(double tolerance, params double[] moves)
        {
            double best = 0, bestDistance = double.MaxValue;
            foreach (var m in moves)
            {
                if (Math.Abs(m) <= tolerance && Math.Abs(m) < bestDistance)
                {
                    best = m;
                    bestDistance = Math.Abs(m);
                }
            }
            return best;
        }
        double dx = Nearest(tolerance, -r.Left, image.Right - r.Right, image.Right - r.Left, -r.Right);
        double dy = Nearest(tolerance, -r.Top, image.Bottom - r.Bottom, image.Bottom - r.Top, -r.Bottom);
        return Rects.Offset(r, new Vector(dx, dy));
    }

    /// <summary>
    /// Inserted image resized by its handle <see cref="_handleIndex"/> dragged to <paramref name="p"/>, the opposite
    /// corner staying in place; with <paramref name="keepRatio"/>, its proportions follow the dominant axis.
    /// </summary>
    private Rect ResizeFloating(Point p, bool keepRatio)
    {
        var start = _floatingStart;
        var anchor = new[] { start.BottomRight, start.BottomLeft, start.TopLeft, start.TopRight }[_handleIndex];
        Vector v = p - anchor;
        double width = Math.Max(Math.Abs(v.X), MinFloatingSize), height = Math.Max(Math.Abs(v.Y), MinFloatingSize);
        if (keepRatio && start.Width > 0 && start.Height > 0)
        {
            double ratio = start.Width / start.Height;
            width = Math.Max(width, height * ratio);
            height = width / ratio;
        }
        // The handle may be dragged past the anchor: the image then grows on the other side.
        return Rects.Between(anchor, anchor + new Vector(v.X < 0 ? -width : width, v.Y < 0 ? -height : height));
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
            cursor = HitHandle(FloatingHandles(), p) >= 0 ? HandCursor : _floatingRect.Contains(p) ? MoveCursor : ArrowCursor;
        else if (_selected != null && _tool != EditorTool.Crop && HitHandle(_selected.GetHandles(), p) >= 0) cursor = HandCursor;
        else if (_tool == EditorTool.Select && HitTestAll(p) != null) cursor = MoveCursor;
        else if (_tool == EditorTool.Magnifier && HitTestAll(p) is MagnifierAnnotation) cursor = MoveCursor;
        else cursor = _tool switch
        {
            EditorTool.Select => ArrowCursor,
            EditorTool.Text => TextCursor,
            _ => CrossCursor,
        };
        if (!ReferenceEquals(Cursor, cursor)) Cursor = cursor;
    }

    private void SetSelected(Annotation? annotation)
    {
        if (ReferenceEquals(_selected, annotation)) return;
        _selected = annotation;
        _overlayLayer.InvalidateVisual();
        SelectionChanged?.Invoke();
    }

    private void OnDocumentChanged()
    {
        if (_document is { IsEffectsChange: true })
        {
            // Only the outline and the border change (a slider being dragged): the annotations themselves do not.
            InvalidateLayers();
            return;
        }
        if (_selected != null && _document != null && !_document.Annotations.Contains(_selected)) SetSelected(null);
        InvalidateLayers();
        InvalidateMeasure();
    }

    /// <summary>One layer of the drawing: the size of the canvas, drawn by the canvas, transparent to the mouse.</summary>
    private sealed class Layer : Control
    {
        private readonly Action<DrawingContext> _draw;

        public Layer(Action<DrawingContext> draw)
        {
            _draw = draw;
            IsHitTestVisible = false;
            ClipToBounds = false;
        }

        public override void Render(DrawingContext context) => _draw(context);
    }
}
