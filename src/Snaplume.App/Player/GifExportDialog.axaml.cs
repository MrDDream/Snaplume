using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>
/// Options of a GIF made from the video: part, frame rate, size, speed, loop. Shown with
/// <c>ShowDialog&lt;GifExportSettings?&gt;</c>: the settings, or null if cancelled. The rules are in <see cref="GifExportForm"/>.
/// </summary>
public partial class GifExportDialog : Window
{
    private static readonly double[] Speeds = { 0.5, 1, 1.5, 2 };

    // Choices kept for the next GIF of the session.
    private static int _lastFps = 12;
    private static double _lastSpeed = 1;
    private static bool _lastLoop = true;
    private static bool _lastDither;

    private readonly GifExportForm _form;
    private readonly bool _ready;

    /// <summary>For the XAML previewer only.</summary>
    public GifExportDialog() : this(TimeSpan.FromSeconds(10), null, null, 1280, 720)
    {
    }

    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    public GifExportDialog(TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, int videoWidth, int videoHeight)
    {
        InitializeComponent();
        _form = new GifExportForm(duration, inPoint, outPoint, videoWidth, videoHeight);

        var ranges = ExportOption.Ranges(duration, _form.HasSelection ? (_form.In, _form.Out) : null);
        RangeBox.ItemsSource = ranges;
        RangeBox.SelectedItem = ranges[^1];

        var speeds = Speeds.Select(s => new ExportOption(s, s.ToString("0.#", Loc.Culture) + "×")).ToList();
        SpeedBox.ItemsSource = speeds;
        SpeedBox.SelectedItem = speeds.FirstOrDefault(s => (double)s.Value == _lastSpeed) ?? speeds[1];

        FpsBox.Text = _lastFps.ToString(Loc.Culture);
        WidthBox.Text = _form.WidthText;
        LoopCheck.IsChecked = _lastLoop;
        DitherCheck.IsChecked = _lastDither;

        RangeBox.SelectionChanged += (_, _) => Update();
        SpeedBox.SelectionChanged += (_, _) => Update();
        FpsBox.TextChanged += (_, _) => Update();
        WidthBox.TextChanged += (_, _) => Update();
        _ready = true;
        Update();
        Opened += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    /// <summary>Copies the fields into the form.</summary>
    private GifExportForm Read()
    {
        _form.UseSelection = (RangeBox.SelectedItem as ExportOption)?.Value is true;
        _form.FramesPerSecondText = FpsBox.Text ?? "";
        _form.WidthText = WidthBox.Text ?? "";
        _form.Speed = (SpeedBox.SelectedItem as ExportOption)?.Value as double? ?? 1;
        _form.Loop = LoopCheck.IsChecked == true;
        _form.Dither = DitherCheck.IsChecked == true;
        return _form;
    }

    /// <summary>Height for the typed width and a summary of the GIF to come.</summary>
    private void Update()
    {
        if (!_ready) return;
        ErrorText.IsVisible = false;
        if (Read().TryRead(out var settings, out _))
        {
            HeightText.Text = Loc.T("Player.Gif.HeightValue", settings.Height);
            var length = TimeSpan.FromSeconds((settings.End - settings.Start).TotalSeconds / settings.Speed);
            SummaryText.Text = Loc.T("Player.Gif.Summary", settings.FrameCount, length.TotalSeconds, settings.Width, settings.Height);
        }
        else
        {
            HeightText.Text = "";
            SummaryText.Text = "";
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!Read().TryRead(out var settings, out var error))
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }
        _lastFps = settings.FramesPerSecond;
        _lastSpeed = settings.Speed;
        _lastLoop = settings.Loop;
        _lastDither = settings.Dither;
        Close(settings);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
