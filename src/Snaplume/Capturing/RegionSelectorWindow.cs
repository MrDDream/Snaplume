using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Snaplume.Core;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Capturing;

/// <summary>
/// Borderless topmost window covering the whole virtual screen (all monitors)
/// and showing the frozen screenshot so the user can pick an area.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    /// <summary>The selector being shown: screenshots, text captures and videos share the screen.</summary>
    private static RegionSelectorWindow? _current;

    private readonly ScreenSnapshot _snap;
    private readonly SelectionOverlay _overlay;

    /// <summary>Selected area relative to the snapshot, or null if cancelled.</summary>
    public PixelRect? Result { get; private set; }

    /// <summary>True while a selection is on screen; another one must not be opened.</summary>
    public static bool IsOpen => _current != null;

    /// <summary>
    /// Lets the user pick an area of the snapshot (modal).
    /// </summary>
    /// <param name="title">Optional prefix of the help line, e.g. "Video recording".</param>
    /// <returns>The area relative to the snapshot, or null if cancelled or if a selection is already open.</returns>
    public static PixelRect? Pick(ScreenSnapshot snap, string? title = null)
    {
        if (_current != null) return null;
        var selector = new RegionSelectorWindow(snap, title);
        _current = selector;
        try
        {
            selector.ShowDialog();
        }
        finally
        {
            _current = null;
        }
        return selector.Result;
    }

    /// <summary>Closes the selection on screen, if any, as if the user had cancelled it.</summary>
    public static void CancelCurrent() => _current?.Close();

    private RegionSelectorWindow(ScreenSnapshot snap, string? title)
    {
        _snap = snap;
        // List the windows under the mouse now: once shown, this window would cover them all.
        _ = snap.Windows;

        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = 0;
        Top = 0;
        Width = 200;
        Height = 200;
        Title = Loc.T("Selection.WindowTitle");
        // The screenshot is drawn in pixel coordinates: never mirrored, even in Arabic.
        FlowDirection = FlowDirection.LeftToRight;

        var image = new Image { Source = snap.Image, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.NearestNeighbor);
        _overlay = new SelectionOverlay(snap, title);
        _overlay.Completed += rect =>
        {
            Result = rect;
            Close();
        };
        _overlay.Cancelled += Close;

        var root = new Grid();
        root.Children.Add(image);
        root.Children.Add(_overlay);
        Content = root;

        SourceInitialized += (_, _) =>
        {
            // The window spans monitors with possibly different DPI; the overlay maps pixels
            // from its actual size anyway.
            IgnoreDpiChanges(this);
            PlaceOverVirtualScreen();
        };
        Loaded += (_, _) =>
        {
            PlaceOverVirtualScreen();
            SetForegroundWindow(Handle);
            Activate();
            Keyboard.Focus(_overlay);
            _overlay.UpdateMouse();
        };
    }

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>Positions the window in physical pixels, independently of the WPF DPI scale.</summary>
    private void PlaceOverVirtualScreen()
    {
        var b = _snap.Bounds;
        SetWindowPos(Handle, HWND_TOPMOST, b.X, b.Y, b.Width, b.Height, SWP_SHOWWINDOW);
    }
}
