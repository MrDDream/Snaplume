using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Core;

namespace Snaplume.Capturing;

/// <summary>Small floating bar shown during a scrolling capture; kept out of the frames it takes when the system allows it.</summary>
public partial class ScrollStopBar : Window, IScrollCaptureBar
{
    private int _height;
    private bool _paused;

    public event Action? StopClicked;

    /// <summary>For the XAML previewer only.</summary>
    public ScrollStopBar() : this(new PixelRect(0, 0, 800, 600))
    {
    }

    /// <param name="area">Captured area, absolute physical coordinates: the bar is placed next to it.</param>
    public ScrollStopBar(PixelRect area)
    {
        InitializeComponent();
        // Never takes the focus from the scrolled window, and never appears in its frames.
        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this);
            ScreenBounds = FloatingBar.PlaceNearArea(this, area);
        };
    }

    /// <summary>Where the bar is on the screen (physical pixels), once shown.</summary>
    public PixelRect? ScreenBounds { get; private set; }

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

    private void OnStop(object? sender, RoutedEventArgs e) => StopClicked?.Invoke();
}
