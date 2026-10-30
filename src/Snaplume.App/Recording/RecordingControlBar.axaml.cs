using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snaplume.Capturing;
using Snaplume.Core;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>Floating bar shown during a recording; excluded from the video when the system allows it.</summary>
public partial class RecordingControlBar : Window
{
    private static readonly Geometry PauseGeometry = Geometry.Parse("M9,6 V18 M15,6 V18");
    private static readonly Geometry ResumeGeometry = Geometry.Parse("M8,5.5 L18.5,12 L8,18.5 Z");

    private bool _paused;
    private bool _canPause = true;
    /// <summary>The user dragged the bar elsewhere.</summary>
    private bool _moved;

    public event Action? PauseClicked;
    public event Action? StopClicked;
    public event Action? CancelClicked;

    /// <summary>For the XAML previewer only.</summary>
    public RecordingControlBar() : this(new PixelRect(0, 0, 800, 600))
    {
    }

    /// <param name="area">Recorded area, absolute physical coordinates: the bar is placed next to it.</param>
    public RecordingControlBar(PixelRect area)
    {
        InitializeComponent();
        PauseIcon.Data = PauseGeometry;
        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this);
            FloatingBar.PlaceNearArea(this, area);
        };
        // The size is final after the first layout; once the user dragged the bar, it stays where it was put.
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_moved) FloatingBar.PlaceNearArea(this, area);
        };
    }

    public void ShowCountdown(int seconds) => TimeText.Text = Loc.T("Recording.Countdown", seconds);

    /// <summary>Some recorders cannot pause (the button stays disabled).</summary>
    public void SetCanPause(bool canPause)
    {
        _canPause = canPause;
        PauseButton.IsEnabled = canPause && PauseButton.IsEnabled;
    }

    public void SetRecording(TimeSpan elapsed)
    {
        PauseButton.IsEnabled = _canPause;
        TimeText.Text = _paused ? Loc.T("Recording.PausedTime", Format(elapsed)) : Format(elapsed);
        // Blink while recording.
        RecordDot.Opacity = _paused || elapsed.Milliseconds < 500 ? 1 : 0.25;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        PauseIcon.Data = paused ? ResumeGeometry : PauseGeometry;
        ToolTip.SetTip(PauseButton, Loc.T(paused ? "Recording.Resume" : "Recording.Pause"));
        RecordDot.Opacity = 1;
    }

    public void SetFinishing()
    {
        TimeText.Text = Loc.T("Recording.Finishing");
        PauseButton.IsEnabled = StopButton.IsEnabled = CancelButton.IsEnabled = false;
    }

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1
            ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture)
            : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _moved = true;
        BeginMoveDrag(e);
    }

    private void OnPause(object? sender, RoutedEventArgs e) => PauseClicked?.Invoke();
    private void OnStop(object? sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnCancel(object? sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
