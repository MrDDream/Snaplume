using System.Runtime.InteropServices;
using System.Text;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Capturing;

/// <summary>
/// Snapshot of the visible windows (in Z-order) used to highlight capturable areas
/// under the mouse: controls, windows and monitors.
/// </summary>
public sealed class WindowFinder
{
    private sealed class Node
    {
        public required IntPtr Handle { get; init; }
        public required PixelRect Rect { get; init; }
        public List<PixelRect>? Children { get; set; }
    }

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.Ordinal)
    {
        "Progman", "WorkerW", // desktop: the monitor rectangle is used instead
    };

    private const int MinSize = 8;
    private const int MaxChildren = 4000;

    private readonly List<Node> _windows = new();
    private readonly PixelRect _virtual;
    private readonly IReadOnlyList<PixelRect> _monitors;

    private WindowFinder(PixelRect virtualBounds, IReadOnlyList<PixelRect> monitors)
    {
        _virtual = virtualBounds;
        _monitors = monitors;
    }

    /// <param name="virtualBounds">Virtual screen, absolute coordinates.</param>
    /// <param name="monitors">Monitors, relative to the virtual screen origin.</param>
    public static WindowFinder Create(PixelRect virtualBounds, IReadOnlyList<PixelRect> monitors)
    {
        var finder = new WindowFinder(virtualBounds, monitors);
        EnumWindows((hWnd, _) =>
        {
            finder.TryAdd(hWnd);
            return true;
        }, IntPtr.Zero);
        return finder;
    }

    /// <summary>
    /// Candidate areas containing the point, from the smallest (deepest control)
    /// to the largest (monitor). Coordinates are relative to the virtual screen.
    /// </summary>
    public IReadOnlyList<PixelRect> FindAt(int x, int y)
    {
        var result = new List<PixelRect>();
        var window = _windows.FirstOrDefault(n => n.Rect.Contains(x, y));
        if (window != null)
        {
            window.Children ??= LoadChildren(window);
            result.AddRange(window.Children.Where(c => c.Contains(x, y)));
            result.Add(window.Rect);
        }
        result.AddRange(_monitors.Where(m => m.Contains(x, y)));
        return result.Distinct().OrderBy(r => r.Area).ToList();
    }

    /// <summary>Visible bounds of a top-level window (without the invisible resize borders).</summary>
    public static PixelRect? GetWindowBounds(IntPtr hWnd)
    {
        if (DwmGetWindowAttribute(hWnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0
            && !GetWindowRect(hWnd, out r))
        {
            return null;
        }
        var rect = PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
        return rect.IsEmpty ? null : rect;
    }

    private void TryAdd(IntPtr hWnd)
    {
        if (!IsWindowVisible(hWnd) || IsIconic(hWnd)) return;
        if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0) return;
        if ((GetWindowLong(hWnd, GWL_EXSTYLE) & WS_EX_TRANSPARENT) != 0) return;
        if (IgnoredClasses.Contains(GetClassName(hWnd))) return;
        if (GetWindowBounds(hWnd) is not PixelRect bounds) return;

        var rel = ToRelative(bounds);
        if (rel.Width < MinSize || rel.Height < MinSize) return;
        _windows.Add(new Node { Handle = hWnd, Rect = rel });
    }

    private List<PixelRect> LoadChildren(Node node)
    {
        var list = new List<PixelRect>();
        EnumChildWindows(node.Handle, (child, _) =>
        {
            if (list.Count >= MaxChildren) return false;
            if (!IsWindowVisible(child) || !GetWindowRect(child, out var r)) return true;
            var rel = ToRelative(PixelRect.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)).Intersect(node.Rect);
            if (rel.Width >= MinSize && rel.Height >= MinSize && rel != node.Rect) list.Add(rel);
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private PixelRect ToRelative(PixelRect absolute) =>
        absolute.Offset(-_virtual.X, -_virtual.Y).Intersect(new PixelRect(0, 0, _virtual.Width, _virtual.Height));

    private static string GetClassName(IntPtr hWnd)
    {
        var sb = new StringBuilder(256);
        return Native.NativeMethods.GetClassName(hWnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }
}
