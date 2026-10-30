using System.Diagnostics;
using Snaplume.Capturing;
using Snaplume.Platform;

namespace Snaplume.Tests.Fakes;

/// <summary>
/// A browser window simulated for the scrolling capture: a tall page seen through a viewport, scrolled by the
/// fake wheel. The scrolling can be animated (smooth scrolling: frames taken too early show only part of the move),
/// accelerated once (a step that jumps past the viewport, to test the recovery), or ignored (a window that does not
/// react to the wheel). Plays both the pointer and the screen grabber; nothing touches the real mouse or screen.
/// </summary>
public sealed class FakeScrollingPage : IPointerControl
{
    private readonly object _lock = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _from, _to;
    private long _animationStart;
    private int _x, _y;

    /// <param name="area">Where the viewport is on the (fake) screen.</param>
    public FakeScrollingPage(PixelRect area, int pageHeight, int pixelsPerNotch = 40, int seed = 7)
    {
        Area = area;
        PageHeight = pageHeight;
        PixelsPerNotch = pixelsPerNotch;
        Page = BuildPage(area.Width, pageHeight, seed);
        _x = area.X - 500;
        _y = area.Y - 500;
    }

    public PixelRect Area { get; }
    public int PageHeight { get; }
    public int PixelsPerNotch { get; }
    /// <summary>The whole page, BGRA, opaque: what a perfect capture returns.</summary>
    public byte[] Page { get; }

    /// <summary>Length of the smooth-scrolling animation of a wheel step (0: jumps at once).</summary>
    public int AnimationMs { get; init; }
    /// <summary>The window does not react to the wheel.</summary>
    public bool IgnoresWheel { get; init; }
    /// <summary>Steps of at least this many notches scroll <see cref="BurstFactor"/> times further, <see cref="BurstCount"/> times.</summary>
    public int BurstNotches { get; init; } = int.MaxValue;
    public int BurstFactor { get; init; } = 3;
    public int BurstCount { get; set; }
    /// <summary>Esc is pressed after this many wheel steps.</summary>
    public int EscapeAfterScrolls { get; init; } = int.MaxValue;
    /// <summary>The user takes the mouse out of the area after this many wheel steps.</summary>
    public int LeaveAfterScrolls { get; init; } = int.MaxValue;
    /// <summary>Permission of the pointer (Wayland portal) granted.</summary>
    public bool Grant { get; init; } = true;

    public int Scrolls { get; private set; }
    public List<int> ScrollLog { get; } = new();
    public int Grabs { get; private set; }
    public bool Prepared { get; private set; }
    public bool Released { get; private set; }
    public int EscapeChecks { get; private set; }
    public (int X, int Y) StartPosition { get; private set; }

    /// <summary>Scroll position now (pixels from the top of the page), animation included.</summary>
    public int Offset
    {
        get
        {
            lock (_lock) return (int)Math.Round(CurrentOffset());
        }
    }

    private double CurrentOffset()
    {
        if (AnimationMs <= 0) return _to;
        double t = Math.Clamp((_clock.ElapsedMilliseconds - _animationStart) / (double)AnimationMs, 0, 1);
        return _from + (_to - _from) * t;
    }

    public IScreenGrabber CreateGrabber(PixelRect area) => new Grabber(this, area);

    public Task<bool> PrepareAsync(CancellationToken cancel = default)
    {
        Prepared = true;
        StartPosition = (_x, _y);
        return Task.FromResult(Grant);
    }

    public bool TryGetPosition(out int x, out int y)
    {
        lock (_lock)
        {
            if (Scrolls >= LeaveAfterScrolls)
            {
                x = Area.Right + 400;
                y = Area.Bottom + 400;
                return true;
            }
            x = _x;
            y = _y;
            return true;
        }
    }

    public bool SetPosition(int x, int y)
    {
        lock (_lock)
        {
            _x = x;
            _y = y;
        }
        return true;
    }

    public bool Scroll(int notches)
    {
        lock (_lock)
        {
            Scrolls++;
            ScrollLog.Add(notches);
            if (IgnoresWheel) return true;
            double distance = -notches * (double)PixelsPerNotch;
            if (Math.Abs(notches) >= BurstNotches && BurstCount > 0)
            {
                BurstCount--;
                distance *= BurstFactor;
            }
            _from = CurrentOffset();
            _to = Math.Clamp(_to + distance, 0, PageHeight - Area.Height);
            _animationStart = _clock.ElapsedMilliseconds;
        }
        return true;
    }

    public bool EscapePressed()
    {
        lock (_lock)
        {
            EscapeChecks++;
            return Scrolls >= EscapeAfterScrolls;
        }
    }

    public void ActivateWindowAt(int x, int y)
    {
    }

    public void Release() => Released = true;

    /// <summary>Same generator as the stitcher tests: lines of "text" separated by blank rows.</summary>
    private static byte[] BuildPage(int width, int height, int seed)
    {
        var rnd = new Random(seed);
        var p = new byte[width * 4 * height];
        for (int y = 0; y < height; y++)
        {
            bool textLine = (y / 6) % 3 != 0;
            for (int x = 0; x < width; x++)
            {
                int i = (y * width + x) * 4;
                byte v = textLine && x > 20 && x < width - 40 && rnd.Next(4) == 0 ? (byte)rnd.Next(0, 120) : (byte)250;
                p[i] = v;
                p[i + 1] = v;
                p[i + 2] = (byte)(v ^ (y & 7));
                p[i + 3] = 255;
            }
        }
        return p;
    }

    private sealed class Grabber : IScreenGrabber
    {
        private readonly FakeScrollingPage _page;

        public Grabber(FakeScrollingPage page, PixelRect area)
        {
            _page = page;
            Area = area;
        }

        public PixelRect Area { get; }
        public bool Disposed { get; private set; }

        public Task GrabAsync(byte[] target, CancellationToken cancel = default)
        {
            if (Disposed) throw new ObjectDisposedException(nameof(Grabber));
            int offset;
            lock (_page._lock)
            {
                _page.Grabs++;
                offset = (int)Math.Round(_page.CurrentOffset());
            }
            int stride = Area.Width * 4;
            Buffer.BlockCopy(_page.Page, offset * stride, target, 0, stride * Area.Height);
            return Task.CompletedTask;
        }

        public void Dispose() => Disposed = true;
    }
}

/// <summary>Records what the scrolling capture shows on its Stop bar.</summary>
public sealed class FakeScrollBar : IScrollCaptureBar
{
    public FakeScrollBar(Action stop) => Stop = stop;

    /// <summary>Clicks "Stop".</summary>
    public Action Stop { get; }
    public PixelRect? ScreenBounds { get; set; }
    public List<int> Progress { get; } = new();
    public bool Closed { get; private set; }
    public bool WasPaused { get; private set; }

    public void SetProgress(int height) => Progress.Add(height);
    public void SetPaused(bool paused) => WasPaused |= paused;
    public void Close() => Closed = true;
}
