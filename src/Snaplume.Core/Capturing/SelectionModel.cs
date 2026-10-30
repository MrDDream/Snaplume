using Snaplume.Platform;

namespace Snaplume.Capturing;

/// <summary>Keys understood by the area selection.</summary>
public enum SelectionKey
{
    Escape,
    Enter,
    /// <summary>The whole monitor under the crosshair.</summary>
    F,
    /// <summary>All monitors.</summary>
    A,
    Left,
    Right,
    Up,
    Down,
}

/// <summary>
/// What the area selection does with the mouse and the keyboard, in pixels of the frozen desktop image: drag a
/// rectangle, or click the window / control highlighted under the crosshair (the wheel goes to its parents).
/// Kept apart from the overlay windows (one per monitor) so that they share one state and so that it can be tested.
/// </summary>
public sealed class SelectionModel
{
    /// <summary>A press followed by a move of fewer pixels is a click (on the highlighted window), not a drag.</summary>
    private const int DragThreshold = 3;

    private readonly IWindowSnapshot _windows;
    private bool _down;
    private int _sx, _sy;
    private IReadOnlyList<PixelRect> _candidates = Array.Empty<PixelRect>();
    private int _level;
    private bool _finished;

    /// <param name="width">Size of the desktop image (physical pixels).</param>
    /// <param name="monitors">Monitor bounds, relative to the desktop image.</param>
    /// <param name="windows">Windows and controls under a point (relative coordinates); <see cref="EmptyWindowSnapshot"/> to drag only.</param>
    public SelectionModel(int width, int height, IReadOnlyList<PixelRect> monitors, IWindowSnapshot windows)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Monitors = monitors.Count > 0 ? monitors : new[] { new PixelRect(0, 0, width, height) };
        _windows = windows;
    }

    public int Width { get; }
    public int Height { get; }
    public IReadOnlyList<PixelRect> Monitors { get; }

    /// <summary>Crosshair, in pixels of the desktop image; -1 until the mouse position is known.</summary>
    public int X { get; private set; } = -1;
    public int Y { get; private set; } = -1;

    public bool HasPosition => X >= 0;
    public bool IsDragging { get; private set; }

    /// <summary>The window or control under the crosshair (wheel: its parents), when not dragging.</summary>
    public PixelRect? Hover => _candidates.Count == 0 ? null : _candidates[Math.Clamp(_level, 0, _candidates.Count - 1)];

    /// <summary>What a release would select now: the rectangle dragged, or the highlighted window.</summary>
    public PixelRect? Focus => IsDragging ? PixelRect.FromCorners(_sx, _sy, X, Y) : Hover;

    /// <summary>Area chosen (relative to the desktop image).</summary>
    public event Action<PixelRect>? Completed;
    public event Action? Cancelled;
    /// <summary>Something to draw again.</summary>
    public event Action? Changed;

    /// <summary>The mouse is at this point (clamped to the image).</summary>
    public void MoveTo(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        if (x == X && y == Y) return;
        X = x;
        Y = y;
        if (_down && !IsDragging && (Math.Abs(X - _sx) >= DragThreshold || Math.Abs(Y - _sy) >= DragThreshold)) IsDragging = true;
        if (!_down) _candidates = _windows.FindAt(X, Y);
        Changed?.Invoke();
    }

    public void Press()
    {
        if (!HasPosition || _finished) return;
        _down = true;
        _sx = X;
        _sy = Y;
    }

    public void Release()
    {
        if (!_down) return;
        _down = false;
        if (IsDragging)
        {
            IsDragging = false;
            var rect = PixelRect.FromCorners(_sx, _sy, X, Y);
            if (rect.Width >= 2 && rect.Height >= 2) Complete(rect);
            else
            {
                _candidates = _windows.FindAt(X, Y);
                Changed?.Invoke();
            }
        }
        else if (Hover is PixelRect hover)
        {
            Complete(hover);
        }
    }

    /// <summary>Right click: drops the rectangle being dragged, or cancels the selection.</summary>
    public void RightClick()
    {
        if (_down)
        {
            _down = IsDragging = false;
            _candidates = _windows.FindAt(X, Y);
            Changed?.Invoke();
        }
        else
        {
            Cancel();
        }
    }

    /// <summary>The wheel walks from a control to its parents (up) and back (down).</summary>
    /// <returns>True if handled.</returns>
    public bool Wheel(int direction)
    {
        if (_down || _candidates.Count == 0 || direction == 0) return false;
        _level = Math.Clamp(Math.Clamp(_level, 0, _candidates.Count - 1) + (direction > 0 ? 1 : -1), 0, _candidates.Count - 1);
        Changed?.Invoke();
        return true;
    }

    /// <param name="large">Shift: arrows move by 10 pixels.</param>
    /// <returns>True if handled.</returns>
    public bool Key(SelectionKey key, bool large = false)
    {
        int step = large ? 10 : 1;
        switch (key)
        {
            case SelectionKey.Escape: Cancel(); return true;
            case SelectionKey.Enter when Hover is PixelRect hover: Complete(hover); return true;
            case SelectionKey.F: Complete(MonitorAt(Math.Max(0, X), Math.Max(0, Y))); return true;
            case SelectionKey.A: Complete(new PixelRect(0, 0, Width, Height)); return true;
            case SelectionKey.Left: Nudge(-step, 0); return true;
            case SelectionKey.Right: Nudge(step, 0); return true;
            case SelectionKey.Up: Nudge(0, -step); return true;
            case SelectionKey.Down: Nudge(0, step); return true;
            default: return false;
        }
    }

    /// <summary>Moves the crosshair (keyboard); the overlay also moves the real pointer when the system allows it.</summary>
    public void Nudge(int dx, int dy) => MoveTo(Math.Max(0, X) + dx, Math.Max(0, Y) + dy);

    public void Cancel()
    {
        if (_finished) return;
        _finished = true;
        Cancelled?.Invoke();
    }

    private void Complete(PixelRect rect)
    {
        if (_finished) return;
        rect = rect.Intersect(new PixelRect(0, 0, Width, Height));
        if (rect.IsEmpty) return;
        _finished = true;
        Completed?.Invoke(rect);
    }

    /// <summary>
    /// Monitor containing the point (relative coordinates). In the dead zones between monitors
    /// of different sizes, the nearest one.
    /// </summary>
    public PixelRect MonitorAt(int x, int y) => CaptureGeometry.MonitorAt(Monitors, x, y);
}
