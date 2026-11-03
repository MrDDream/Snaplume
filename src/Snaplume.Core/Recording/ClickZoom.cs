using Snaplume.Capturing;
using Snaplume.Core;

namespace Snaplume.Recording;

/// <summary>Part of the recorded area that fills the video, relative to the area; fractional while it moves.</summary>
public readonly record struct ZoomView(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>
    /// Whole pixels for the recorder: even sizes of at least 2 (H.264), kept inside an area of
    /// <paramref name="width"/> × <paramref name="height"/>.
    /// </summary>
    public PixelRect ToPixels(int width, int height)
    {
        int w = Math.Clamp((int)Math.Round(Width / 2) * 2, 2, Math.Max(2, width & ~1));
        int h = Math.Clamp((int)Math.Round(Height / 2) * 2, 2, Math.Max(2, height & ~1));
        int x = Math.Clamp((int)Math.Round(X), 0, Math.Max(0, width - w));
        int y = Math.Clamp((int)Math.Round(Y), 0, Math.Max(0, height - h));
        return new PixelRect(x, y, w, h);
    }

    /// <summary>The view part of the way from <paramref name="from"/> to <paramref name="to"/> (0 to 1).</summary>
    public static ZoomView Lerp(ZoomView from, ZoomView to, double amount) => new(
        from.X + (to.X - from.X) * amount,
        from.Y + (to.Y - from.Y) * amount,
        from.Width + (to.Width - from.Width) * amount,
        from.Height + (to.Height - from.Height) * amount);
}

/// <summary>
/// Zoom on clicks: each click makes the video zoom in around it (×2 by default), stay there for a moment, then zoom
/// back out to the whole area; every move is eased. A click while zoomed moves the view to the new point and keeps
/// it zoomed longer. Pure timing and geometry: the time is given by the caller (see <see cref="ClickZoomDriver"/>).
/// </summary>
/// <remarks>Not thread-safe: <see cref="ClickZoomDriver"/> serializes the calls.</remarks>
public sealed class ClickZoom
{
    public const double DefaultFactor = 2;
    public static readonly TimeSpan DefaultTransition = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan DefaultHold = TimeSpan.FromSeconds(1.5);

    private readonly double _factor;
    private readonly TimeSpan _transition;
    private readonly TimeSpan _hold;
    private ZoomView _from;
    private ZoomView _to;
    private TimeSpan _moveStart;
    /// <summary>When the zoom out starts; null while the whole area is shown.</summary>
    private TimeSpan? _releaseAt;

    /// <param name="width">Width of the recorded area, in pixels.</param>
    /// <param name="height">Height of the recorded area.</param>
    /// <param name="factor">Magnification when zoomed in (at least 1).</param>
    /// <param name="transition">Duration of each zoom in, move and zoom out.</param>
    /// <param name="hold">Time the view stays zoomed after the end of the zoom in.</param>
    public ClickZoom(int width, int height, double factor = DefaultFactor, TimeSpan? transition = null, TimeSpan? hold = null)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        _factor = Math.Max(1, factor);
        _transition = transition ?? DefaultTransition;
        _hold = hold ?? DefaultHold;
        _from = _to = Full;
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>The whole area.</summary>
    public ZoomView Full => new(0, 0, Width, Height);

    /// <summary>
    /// A click at (<paramref name="x"/>, <paramref name="y"/>), relative to the area, at the time <paramref name="now"/>
    /// (time only goes forward). Clicks outside the area are ignored.
    /// </summary>
    /// <returns>False when the click was ignored.</returns>
    public bool Click(double x, double y, TimeSpan now)
    {
        if (x < 0 || y < 0 || x >= Width || y >= Height) return false;
        // From wherever the view is now: zooming in, zoomed, or on its way out.
        _from = ViewAt(now);
        _to = Target(x, y);
        _moveStart = now;
        _releaseAt = now + _transition + _hold;
        return true;
    }

    /// <summary>The view at the time <paramref name="now"/>.</summary>
    public ZoomView ViewAt(TimeSpan now)
    {
        if (_releaseAt is not TimeSpan release) return Full;
        if (now < release) return ZoomView.Lerp(_from, _to, Ease(Progress(now - _moveStart)));
        // The move ended before the release (it lasts one transition, the release comes a hold later).
        double outward = Progress(now - release);
        return outward >= 1 ? Full : ZoomView.Lerp(_to, Full, Ease(outward));
    }

    /// <summary>The whole area is shown and stays so until the next click.</summary>
    public bool IsIdleAt(TimeSpan now) => _releaseAt is not TimeSpan release || now >= release + _transition;

    /// <summary>The zoomed view centered on the point, moved inside the area where the point is near an edge.</summary>
    public ZoomView Target(double x, double y)
    {
        double w = Width / _factor, h = Height / _factor;
        return new ZoomView(Math.Clamp(x - w / 2, 0, Width - w), Math.Clamp(y - h / 2, 0, Height - h), w, h);
    }

    private double Progress(TimeSpan elapsed) =>
        _transition <= TimeSpan.Zero ? 1 : Math.Clamp(elapsed.TotalMilliseconds / _transition.TotalMilliseconds, 0, 1);

    /// <summary>Ease in and out (cubic): starts and ends slowly.</summary>
    internal static double Ease(double t)
    {
        t = Math.Clamp(t, 0, 1);
        return t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    }
}

/// <summary>
/// Runs a <see cref="ClickZoom"/> during a recording: takes the clicks of the platform's mouse watcher (any thread),
/// and about 30 times a second while the view moves, gives the recorder the part of the area to record. The timer
/// stops while the whole area is shown.
/// </summary>
public sealed class ClickZoomDriver : IDisposable
{
    public const int FramesPerSecond = 30;

    private readonly object _gate = new();
    /// <summary>Held while the recorder applies a view: a slow recorder makes ticks skip, never pile up.</summary>
    private readonly object _applying = new();
    private readonly PixelRect _area;
    private readonly ClickZoom _zoom;
    private readonly Func<PixelRect, bool> _apply;
    private readonly TimeProvider _time;
    private readonly long _start;
    private readonly ITimer _timer;
    private PixelRect _applied;
    private bool _running;
    private bool _failed;
    private bool _disposed;

    /// <param name="area">Recorded area, absolute physical pixels (the coordinates of the clicks).</param>
    /// <param name="apply">
    /// Gives the recorder the view, in whole pixels relative to the area (the whole area when zoomed out). Called on
    /// a timer thread, one call at a time. Returns false when the recorder cannot take it now (paused, restarting):
    /// it is given again on the next tick. An exception turns the zoom off for the rest of the recording.
    /// </param>
    /// <param name="zoom">Timing and geometry; the default one for the area when null.</param>
    /// <param name="time">Clock and timer (tests); the system's when null.</param>
    public ClickZoomDriver(PixelRect area, Func<PixelRect, bool> apply, ClickZoom? zoom = null, TimeProvider? time = null)
    {
        _area = area;
        _apply = apply;
        _zoom = zoom ?? new ClickZoom(area.Width, area.Height);
        _time = time ?? TimeProvider.System;
        _start = _time.GetTimestamp();
        _applied = new PixelRect(0, 0, area.Width, area.Height);
        _timer = _time.CreateTimer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan Now => _time.GetElapsedTime(_start);

    /// <summary>Clicks are ignored while the recording is paused.</summary>
    public bool Paused { get; set; }

    /// <summary>A mouse click, absolute physical coordinates; clicks outside the area are ignored. Any thread.</summary>
    public void OnClick(int screenX, int screenY)
    {
        lock (_gate)
        {
            if (_disposed || _failed || Paused) return;
            // The center of the clicked pixel.
            if (!_zoom.Click(screenX - _area.X + 0.5, screenY - _area.Y + 0.5, Now)) return;
            if (_running) return;
            _running = true;
            _timer.Change(TimeSpan.Zero, TimeSpan.FromSeconds(1.0 / FramesPerSecond));
        }
    }

    /// <summary>Gives the recorder the view of now if it changed; stops the timer once the whole area is back.</summary>
    internal void Tick()
    {
        PixelRect view;
        bool idle;
        lock (_gate)
        {
            if (_disposed || _failed) return;
            var now = Now;
            view = _zoom.ViewAt(now).ToPixels(_area.Width, _area.Height);
            idle = _zoom.IsIdleAt(now);
        }
        if (!Monitor.TryEnter(_applying)) return;
        bool applied = true;
        try
        {
            if (view != _applied)
            {
                applied = _apply(view);
                if (applied) _applied = view;
            }
        }
        catch (Exception ex)
        {
            // The recorder refused: the video goes on without zoom, logged once.
            ErrorLog.Write("Zoom sur les clics", ex);
            lock (_gate)
            {
                _failed = true;
                _running = false;
                if (!_disposed) _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            }
            return;
        }
        finally
        {
            Monitor.Exit(_applying);
        }
        // Ticks go on until the recorder took the whole area back.
        if (!idle || !applied) return;
        lock (_gate)
        {
            // A click may have come meanwhile: it restarted the zoom, the timer goes on.
            if (_disposed || !_zoom.IsIdleAt(Now)) return;
            _running = false;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Last view given to the recorder (tests).</summary>
    internal PixelRect Applied => _applied;

    /// <summary>The timer is running (tests).</summary>
    internal bool IsRunning
    {
        get
        {
            lock (_gate) return _running;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _running = false;
        }
        _timer.Dispose();
        // A tick in progress ends before the recorder is released.
        lock (_applying)
        {
        }
    }
}
