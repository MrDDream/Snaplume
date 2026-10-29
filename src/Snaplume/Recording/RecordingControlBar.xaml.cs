using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Snaplume.Capturing;
using Snaplume.Core;
using ScreenRecorderLib;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Recording;

/// <summary>Floating bar shown during a recording; excluded from the video.</summary>
public partial class RecordingControlBar : Window
{
    private const int Gap = 12;
    private readonly PixelRect _area;
    private bool _paused;

    public event Action? PauseClicked;
    public event Action? StopClicked;
    public event Action? CancelClicked;

    public RecordingControlBar(PixelRect area)
    {
        InitializeComponent();
        _area = area;
        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            AddExtendedStyle(hwnd, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);
            Recorder.SetExcludeFromCapture(hwnd, true);
        };
        Loaded += (_, _) => PlaceNearArea();
    }

    public void ShowCountdown(int seconds) => TimeText.Text = Loc.T("Recording.Countdown", seconds);

    public void SetRecording(TimeSpan elapsed)
    {
        PauseButton.IsEnabled = true;
        TimeText.Text = _paused ? Loc.T("Recording.PausedTime", Format(elapsed)) : Format(elapsed);
        // Blink while recording.
        RecordDot.Opacity = _paused || elapsed.Milliseconds < 500 ? 1 : 0.25;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        PauseIcon.Data = (System.Windows.Media.Geometry)FindResource(paused ? "ResumeGeometry" : "PauseGeometry");
        PauseButton.ToolTip = Loc.T(paused ? "Recording.Resume" : "Recording.Pause");
        RecordDot.Opacity = 1;
    }

    public void SetFinishing()
    {
        TimeText.Text = Loc.T("Recording.Finishing");
        PauseButton.IsEnabled = StopButton.IsEnabled = CancelButton.IsEnabled = false;
    }

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Centered under the area, or above it, or inside its bottom edge when there is no room.</summary>
    private void PlaceNearArea()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!GetWindowRect(hwnd, out var r)) return;
        int width = r.Right - r.Left, height = r.Bottom - r.Top;

        var screens = System.Windows.Forms.Screen.AllScreens;
        var screen = screens.FirstOrDefault(s => PixelRect.From(s.Bounds).Contains(_area.X + _area.Width / 2, _area.Bottom - 1)) ?? screens[0];
        var bounds = PixelRect.From(screen.WorkingArea);

        int x = Math.Clamp(_area.X + (_area.Width - width) / 2, bounds.X, Math.Max(bounds.X, bounds.Right - width));
        int y = _area.Bottom + Gap;
        if (y + height > bounds.Bottom) y = _area.Y - Gap - height;
        if (y < bounds.Y) y = _area.Bottom - Gap - height;
        SetWindowPos(hwnd, HWND_TOPMOST, x, y, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnPause(object sender, RoutedEventArgs e) => PauseClicked?.Invoke();
    private void OnStop(object sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnCancel(object sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
