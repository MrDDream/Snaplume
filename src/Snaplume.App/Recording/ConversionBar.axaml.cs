using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Capturing;
using Snaplume.Core;

namespace Snaplume.Recording;

/// <summary>
/// Progress of the conversion of a recording (<see cref="RecordingConverter"/>), next to where it was recorded: never
/// activated, kept out of screenshots and recordings when the system allows it.
/// </summary>
public partial class ConversionBar : Window
{
    private string _name = "";
    private int _waiting;
    private double _progress;

    public event Action? CancelClicked;

    /// <summary>For the XAML previewer only.</summary>
    public ConversionBar() : this(new PixelRect(0, 0, 800, 600))
    {
    }

    /// <param name="area">Recorded area, absolute physical coordinates: the bar is placed next to it.</param>
    public ConversionBar(PixelRect area)
    {
        InitializeComponent();
        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this);
            FloatingBar.PlaceNearArea(this, area);
        };
        SizeChanged += (_, _) =>
        {
            if (IsVisible) FloatingBar.PlaceNearArea(this, area);
        };
    }

    /// <summary>A new recording is converted.</summary>
    /// <param name="title">"Conversion en WebM (VP9)…".</param>
    /// <param name="name">Name of the recording.</param>
    /// <param name="waiting">Recordings converted after it.</param>
    public void SetJob(string title, string name, int waiting)
    {
        TitleText.Text = title;
        _name = name;
        _waiting = waiting;
        SetProgress(0);
    }

    public void SetWaiting(int waiting)
    {
        _waiting = waiting;
        UpdateDetail();
    }

    /// <param name="value">0 to 1 (again from 0 when a too large file is encoded again).</param>
    public void SetProgress(double value)
    {
        _progress = Math.Clamp(value, 0, 1);
        Progress.Value = _progress;
        UpdateDetail();
    }

    private void UpdateDetail()
    {
        PercentText.Text = Loc.T("Video.ConvertProgress", _progress * 100);
        DetailText.Text = _waiting > 0 ? _name + " · " + Loc.T("Video.ConvertWaiting", _waiting) : _name;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
