using System.Windows;

namespace Snaplume.Capturing;

/// <summary>Integer rectangle in physical screen pixels.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public bool IsEmpty => Width <= 0 || Height <= 0;
    public long Area => (long)Width * Height;

    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    /// <summary>Squared distance from a point to the nearest pixel of the rectangle (0 inside).</summary>
    public long DistanceSquared(int x, int y)
    {
        long dx = Math.Max(0, Math.Max(X - x, x - (Right - 1)));
        long dy = Math.Max(0, Math.Max(Y - y, y - (Bottom - 1)));
        return dx * dx + dy * dy;
    }

    public PixelRect Offset(int dx, int dy) => this with { X = X + dx, Y = Y + dy };

    public PixelRect Intersect(PixelRect other)
    {
        int left = Math.Max(X, other.X), top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right), bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromLTRB(left, top, right, bottom) : default;
    }

    public PixelRect Union(PixelRect other) =>
        FromLTRB(Math.Min(X, other.X), Math.Min(Y, other.Y), Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));

    public static PixelRect FromLTRB(int left, int top, int right, int bottom) =>
        new(left, top, right - left, bottom - top);

    /// <summary>Rectangle spanning two pixels, both included.</summary>
    public static PixelRect FromCorners(int x0, int y0, int x1, int y1) =>
        new(Math.Min(x0, x1), Math.Min(y0, y1), Math.Abs(x1 - x0) + 1, Math.Abs(y1 - y0) + 1);

    /// <summary>From a WinForms / GDI rectangle (e.g. Screen.Bounds, already in physical pixels).</summary>
    public static PixelRect From(System.Drawing.Rectangle r) => new(r.X, r.Y, r.Width, r.Height);

    public Rect ToRect() => new(X, Y, Width, Height);

    public Int32Rect ToInt32Rect() => new(X, Y, Width, Height);
}
