using System.Windows;
using System.Windows.Interop;
using Snaplume.Core;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Capturing;

/// <summary>Small floating bar shown during a scrolling capture; hidden from the screenshots it takes.</summary>
public partial class ScrollStopBar : Window
{
    private int _height;
    private bool _paused;

    public event Action? StopClicked;

    /// <param name="area">Captured area, absolute physical coordinates: the bar is placed next to it.</param>
    public ScrollStopBar(PixelRect area)
    {
        InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            // Never takes the focus from the scrolled window, and never appears in its frames.
            AddExtendedStyle(hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            ExcludeFromCapture(hwnd);
            IgnoreDpiChanges(this);
        };
        Loaded += (_, _) => ScreenBounds = FloatingBar.PlaceNearArea(this, area);
    }

    /// <summary>Where the bar is on the screen (physical pixels), once shown.</summary>
    internal PixelRect? ScreenBounds { get; private set; }

    /// <summary>Height of the image stitched so far.</summary>
    public void SetProgress(int height)
    {
        _height = height;
        UpdateText();
    }

    /// <summary>Scrolling waits while the mouse is away from its place (e.g. on its way to the Stop button).</summary>
    public void SetPaused(bool paused)
    {
        if (paused == _paused) return;
        _paused = paused;
        UpdateText();
    }

    private void UpdateText() =>
        DetailText.Text = _paused ? Loc.T("Scroll.Paused")
            : _height > 0 ? Loc.T("Scroll.ProgressZone", _height)
            : Loc.T("Scroll.StopHintZone");

    private void OnStop(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
}
