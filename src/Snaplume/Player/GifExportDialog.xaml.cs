using System.Windows;
using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>Options of a GIF made from the video: part, frame rate, size, speed, loop.</summary>
public partial class GifExportDialog : Window
{
    private sealed record Option(object Value, string Name);

    private static readonly double[] Speeds = { 0.5, 1, 1.5, 2 };
    private const int DefaultWidth = 800;
    private const int SmallestWidth = 16;

    // Choices kept for the next GIF of the session.
    private static int _lastFps = 12;
    private static double _lastSpeed = 1;
    private static bool _lastLoop = true;
    private static bool _lastDither;

    private readonly TimeSpan _duration;
    private readonly TimeSpan _in;
    private readonly TimeSpan _out;
    private readonly int _videoWidth;
    private readonly int _videoHeight;
    private bool _ready;

    public GifExportSettings? Result { get; private set; }

    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    public GifExportDialog(Window owner, TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, int videoWidth, int videoHeight)
    {
        InitializeComponent();
        Owner = owner;
        _duration = duration;
        _in = inPoint ?? TimeSpan.Zero;
        _out = outPoint ?? duration;
        _videoWidth = Math.Max(1, videoWidth);
        _videoHeight = Math.Max(1, videoHeight);

        var ranges = new List<Option> { new(false, Loc.T("Player.Gif.RangeAll", PlayerWindow.Format(duration))) };
        if (inPoint != null || outPoint != null)
            ranges.Add(new Option(true, Loc.T("Player.Gif.RangeSelection", PlayerWindow.Format(_in), PlayerWindow.Format(_out))));
        RangeBox.ItemsSource = ranges;
        RangeBox.SelectedItem = ranges[^1];

        var speeds = Speeds.Select(s => new Option(s, s.ToString("0.#", Loc.Culture) + "×")).ToList();
        SpeedBox.ItemsSource = speeds;
        SpeedBox.SelectedItem = speeds.FirstOrDefault(s => (double)s.Value == _lastSpeed) ?? speeds[1];

        FpsBox.Text = _lastFps.ToString(Loc.Culture);
        WidthBox.Text = Math.Min(DefaultWidth, _videoWidth).ToString(Loc.Culture);
        LoopCheck.IsChecked = _lastLoop;
        DitherCheck.IsChecked = _lastDither;
        _ready = true;
        Update();
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    private bool UseSelection => (RangeBox.SelectedItem as Option)?.Value is true;

    private void OnChanged(object sender, RoutedEventArgs e) => Update();

    /// <summary>Height for the typed width and a summary of the GIF to come.</summary>
    private void Update()
    {
        if (!_ready) return;
        ErrorText.Visibility = Visibility.Collapsed;
        if (TryRead(out var settings, out _))
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

    /// <summary>Settings typed, or the reason they are not acceptable.</summary>
    private bool TryRead(out GifExportSettings settings, out string error)
    {
        settings = null!;
        error = "";
        if (!int.TryParse(FpsBox.Text.Trim(), System.Globalization.NumberStyles.Integer, Loc.Culture, out var fps) ||
            fps < GifExportSettings.MinFramesPerSecond || fps > GifExportSettings.MaxFramesPerSecond)
        {
            error = Loc.T("Player.Gif.FpsError", GifExportSettings.MinFramesPerSecond, GifExportSettings.MaxFramesPerSecond);
            return false;
        }
        if (!int.TryParse(WidthBox.Text.Trim(), System.Globalization.NumberStyles.Integer, Loc.Culture, out var width) ||
            width < SmallestWidth || width > _videoWidth)
        {
            error = Loc.T("Player.Gif.WidthError", SmallestWidth, _videoWidth);
            return false;
        }
        int height = Math.Max(1, (int)Math.Round((double)width * _videoHeight / _videoWidth));
        double speed = (SpeedBox.SelectedItem as Option)?.Value as double? ?? 1;
        var start = UseSelection ? _in : TimeSpan.Zero;
        var end = UseSelection ? _out : _duration;
        settings = new GifExportSettings(start, end, fps, width, height, speed, LoopCheck.IsChecked == true, DitherCheck.IsChecked == true);
        if (settings.FrameCount > GifExportSettings.MaxFrames)
        {
            error = Loc.T("Player.Gif.TooLong", GifExportSettings.MaxFrames);
            return false;
        }
        return true;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!TryRead(out var settings, out var error))
        {
            ErrorText.Text = error;
            ErrorText.Visibility = Visibility.Visible;
            return;
        }
        _lastFps = settings.FramesPerSecond;
        _lastSpeed = settings.Speed;
        _lastLoop = settings.Loop;
        _lastDither = settings.Dither;
        Result = settings;
        DialogResult = true;
    }
}
