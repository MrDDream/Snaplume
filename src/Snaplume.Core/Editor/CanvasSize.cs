using Avalonia.Media;

namespace Snaplume.Editor;

/// <summary>Where the image stays when the size of the canvas changes (see <see cref="CanvasSize.Margins"/>), in reading order.</summary>
public enum CanvasAnchor
{
    TopLeft, Top, TopRight, Left, Center, Right, BottomLeft, Bottom, BottomRight,
}

/// <summary>"Canvas size" of the editor: space added around the image without stretching it (see <see cref="EditorDocument.ExtendCanvas"/>).</summary>
public static class CanvasSize
{
    /// <summary>Background of the space added when nothing else was chosen: white, like a sheet of paper.</summary>
    public static readonly Color DefaultBackground = Colors.White;

    /// <summary>
    /// Margins to add on each side so that an image of <paramref name="width"/> × <paramref name="height"/> becomes
    /// <paramref name="newWidth"/> × <paramref name="newHeight"/> with the image at <paramref name="anchor"/>: the whole
    /// difference on the side opposite the anchor, or split between both sides of a centred image (the odd pixel goes
    /// to the right or to the bottom). Negative where the canvas gets smaller.
    /// </summary>
    public static (int Left, int Top, int Right, int Bottom) Margins(int width, int height, int newWidth, int newHeight, CanvasAnchor anchor)
    {
        int column = (int)anchor % 3, row = (int)anchor / 3;
        int dx = newWidth - width, dy = newHeight - height;
        int left = Before(dx, column), top = Before(dy, row);
        return (left, top, dx - left, dy - top);
    }

    /// <summary>Part of the difference that goes before the image (to the left or above it): none, half or all of it.</summary>
    private static int Before(int difference, int position) => position switch
    {
        0 => 0,
        1 => difference / 2,
        _ => difference,
    };

    /// <summary>The background kept in the settings ("#AARRGGBB"), white when missing or unreadable.</summary>
    public static Color BackgroundFrom(string? setting) => HexColor.TryParse(setting, out var color) ? color : DefaultBackground;
}
