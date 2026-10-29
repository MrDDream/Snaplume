using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>
/// Minimal video player in the spirit of mpv: black window, controls that fade away,
/// on-screen messages and mpv keyboard shortcuts. Uses the Windows media engine.
/// Also cuts an extract (in / out points, exported to MP4) and makes animated GIFs, offline.
/// </summary>
public partial class PlayerWindow : Window
{
    private const double FrameStep = 1.0 / 30; // MediaPlayer does not expose the frame rate
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };
    /// <summary>Shortest extract that can be exported.</summary>
    private static readonly TimeSpan MinRange = TimeSpan.FromMilliseconds(200);
    /// <summary>Width of the seek bar's thumb: its center runs from half of it to the width minus half.</summary>
    private const double SeekThumbWidth = 12;
    /// <summary>Below this window width, the extract's times are not written in the controller (no room).</summary>
    private const double RangeTextMinWidth = 780;

    private readonly MediaPlayer _player = new() { ScrubbingEnabled = true };
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private bool _playing;
    private bool _playingBeforeClick;
    private bool _loop;
    private bool _updatingSeek;
    private bool _overController;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;
    private Point _lastMouse;
    private bool _mediaReady;
    private bool _closing;
    /// <summary>Extract chosen with I and O (null: from the start / to the end).</summary>
    private TimeSpan? _inPoint;
    private TimeSpan? _outPoint;
    /// <summary>Export in progress (one at a time), to cancel it.</summary>
    private CancellationTokenSource? _export;

    public string FilePath { get; }

    public PlayerWindow(string path)
    {
        InitializeComponent();
        FilePath = path;
        Title = Loc.T("Player.TitleWithFile", Path.GetFileName(path));

        _player.MediaOpened += OnMediaOpened;
        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.Volume = VolumeBar.Value;
        _clock.Tick += (_, _) => UpdateTime();
        _idle.Tick += (_, _) => HideController();

        Loaded += (_, _) =>
        {
            _player.Open(new Uri(path));
            Play();
            _clock.Start();
            _idle.Start();
        };
    }

    // ---- Media ----

    private void OnMediaOpened(object? sender, EventArgs e)
    {
        int w = Math.Max(1, _player.NaturalVideoWidth), h = Math.Max(1, _player.NaturalVideoHeight);
        VideoImage.Source = new DrawingImage(new VideoDrawing { Player = _player, Rect = new Rect(0, 0, w, h) });
        if (_player.NaturalDuration.HasTimeSpan) SeekBar.Maximum = Math.Max(0.1, _player.NaturalDuration.TimeSpan.TotalSeconds);
        _mediaReady = _player.NaturalVideoWidth > 0 && _player.NaturalVideoHeight > 0;
        FitWindowToVideo(w, h);
        UpdateTime();
        PositionRange();
    }

    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_loop)
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }
        _player.Pause();
        _playing = false;
        UpdatePlayIcon();
        ShowController();
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        _mediaReady = false;
        // Nothing plays: stop the clock and keep the controller visible.
        _playing = false;
        UpdatePlayIcon();
        _clock.Stop();
        _idle.Stop();
        Controller.BeginAnimation(OpacityProperty, null);
        Controller.Opacity = 1;
        Root.Cursor = null;
        ShowError(e.ErrorException?.Message);
    }

    private void ShowError(string? message)
    {
        ErrorText.Text = Loc.T("Player.Error") + "\n" + message +
                         "\n\n" + Loc.T("Player.ErrorOpenDefault");
        ErrorText.Visibility = Visibility.Visible;
    }

    /// <summary>Opens at the video size (100 % on the screen), limited to 85 % of the work area.</summary>
    private void FitWindowToVideo(int width, int height)
    {
        if (_fullscreen || WindowState != WindowState.Normal) return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var area = SystemParameters.WorkArea;
        double scale = Math.Min(1, Math.Min(area.Width * 0.85 / (width / dpi.DpiScaleX), area.Height * 0.85 / (height / dpi.DpiScaleY)));
        double chromeW = ActualWidth - ((FrameworkElement)Content).ActualWidth;
        double chromeH = ActualHeight - ((FrameworkElement)Content).ActualHeight;
        Width = Math.Max(MinWidth, width / dpi.DpiScaleX * scale + chromeW);
        Height = Math.Max(MinHeight, height / dpi.DpiScaleY * scale + chromeH);
        Left = area.Left + (area.Width - Width) / 2;
        Top = area.Top + (area.Height - Height) / 2;
    }

    private void Play()
    {
        if (_player.NaturalDuration.HasTimeSpan && _player.Position >= _player.NaturalDuration.TimeSpan - TimeSpan.FromMilliseconds(50))
            _player.Position = TimeSpan.Zero;
        _player.Play();
        _playing = true;
        UpdatePlayIcon();
    }

    private void Pause()
    {
        _player.Pause();
        _playing = false;
        UpdatePlayIcon();
    }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
        ShowOsd(Loc.T(_playing ? "Player.Osd.Play" : "Player.Osd.Pause"));
    }

    private void SeekBy(double seconds)
    {
        var target = _player.Position + TimeSpan.FromSeconds(seconds);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_player.NaturalDuration.HasTimeSpan && target > _player.NaturalDuration.TimeSpan) target = _player.NaturalDuration.TimeSpan;
        _player.Position = target;
        UpdateTime();
        ShowOsd($"{Format(target)} / {Format(Duration)}");
    }

    private void StepFrame(int direction)
    {
        Pause();
        var target = _player.Position + TimeSpan.FromSeconds(FrameStep * direction);
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (_player.NaturalDuration.HasTimeSpan && target > _player.NaturalDuration.TimeSpan) target = _player.NaturalDuration.TimeSpan;
        _player.Position = target;
        UpdateTime();
        ShowOsd(Loc.T(direction > 0 ? "Player.Osd.NextFrame" : "Player.Osd.PreviousFrame"));
    }

    private void SetSpeed(double speed)
    {
        speed = Math.Clamp(Math.Round(speed, 2), 0.1, 8);
        _player.SpeedRatio = speed;
        SpeedText.Text = speed.ToString("0.##", Loc.Culture) + "×";
        ShowOsd(Loc.T("Player.Osd.Speed", SpeedText.Text));
    }

    private void ChangeVolume(double delta)
    {
        VolumeBar.Value = Math.Clamp(VolumeBar.Value + delta, 0, 1);
        ShowOsd(Loc.T("Player.Osd.Volume", VolumeBar.Value * 100));
    }

    private void ToggleMute()
    {
        _player.IsMuted = !_player.IsMuted;
        VolumeIcon.Data = (Geometry)FindResource(_player.IsMuted ? "MutedGeometry" : "VolumeGeometry");
        ShowOsd(Loc.T(_player.IsMuted ? "Player.Osd.Muted" : "Player.Osd.Unmuted"));
    }

    private void ToggleLoop()
    {
        _loop = !_loop;
        LoopIcon.Opacity = _loop ? 1 : 0.5;
        ShowOsd(Loc.T(_loop ? "Player.Osd.LoopOn" : "Player.Osd.LoopOff"));
    }

    private void ToggleFullscreen()
    {
        if (!_fullscreen)
        {
            _stateBeforeFullscreen = WindowState;
            WindowStyle = WindowStyle.None;
            if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; // re-maximize without borders
            WindowState = WindowState.Maximized;
            _fullscreen = true;
        }
        else
        {
            WindowStyle = WindowStyle.SingleBorderWindow;
            WindowState = _stateBeforeFullscreen;
            _fullscreen = false;
        }
    }

    /// <summary>Saves the displayed frame as a PNG in the workspace (like mpv's "s").</summary>
    private void ExportFrame()
    {
        int w = _player.NaturalVideoWidth, h = _player.NaturalVideoHeight;
        if (w <= 0 || h <= 0) return;
        try
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) dc.DrawVideo(_player, new Rect(0, 0, w, h));
            var bitmap = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(visual);
            bitmap.Freeze();

            var folder = Workspaces.ActiveFolder;
            Directory.CreateDirectory(folder);
            var name = $"{Path.GetFileNameWithoutExtension(FilePath)}_{(int)_player.Position.TotalMinutes:00}-{_player.Position.Seconds:00}";
            var path = FileOps.UniquePath(folder, name, ".png");
            ImageIO.Save(bitmap, path);
            App.Instance.Captures.NotifySaved(path);
            ShowOsd(Loc.T("Player.Osd.FrameSaved"));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Export d'une image de la vidéo", ex);
            ShowOsd(Loc.T("Player.Osd.FrameFailed"));
        }
    }

    // ---- Extract (in / out points) and exports ----

    private bool CanEdit => _mediaReady && Duration > TimeSpan.Zero;

    /// <summary>Start and end of the extract (the whole video when no point is set).</summary>
    private (TimeSpan Start, TimeSpan End) Range => (_inPoint ?? TimeSpan.Zero, _outPoint ?? Duration);

    private void SetIn()
    {
        if (!CanEdit) return;
        var position = _player.Position;
        // A start after the end: the end goes.
        if (_outPoint is { } end && position > end - MinRange) _outPoint = null;
        _inPoint = position;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.InPoint", FormatPrecise(position)));
    }

    private void SetOut()
    {
        if (!CanEdit) return;
        var position = _player.Position;
        if (_inPoint is { } start && position < start + MinRange) _inPoint = null;
        _outPoint = position;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.OutPoint", FormatPrecise(position)));
    }

    private void ClearIn()
    {
        if (_inPoint == null) return;
        _inPoint = null;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.InCleared"));
    }

    private void ClearOut()
    {
        if (_outPoint == null) return;
        _outPoint = null;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.OutCleared"));
    }

    private void ClearRange()
    {
        _inPoint = _outPoint = null;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.RangeCleared"));
    }

    private void UpdateRange()
    {
        bool any = _inPoint != null || _outPoint != null;
        var (start, end) = Range;
        RangeText.Text = $"{FormatPrecise(start)} → {FormatPrecise(end)}";
        RangeText.Visibility = any && ActualWidth >= RangeTextMinWidth ? Visibility.Visible : Visibility.Collapsed;
        ClearRangeButton.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        PositionRange();
    }

    /// <summary>Highlights the extract on the seek bar, with a mark at each point set.</summary>
    private void PositionRange()
    {
        double width = SeekBar.ActualWidth, max = SeekBar.Maximum;
        if ((_inPoint == null && _outPoint == null) || max <= 0 || width <= SeekThumbWidth)
        {
            RangeBand.Visibility = InMarker.Visibility = OutMarker.Visibility = Visibility.Collapsed;
            return;
        }
        double X(TimeSpan t) => SeekThumbWidth / 2 + Math.Clamp(t.TotalSeconds / max, 0, 1) * (width - SeekThumbWidth);
        var (start, end) = Range;
        double left = X(start), right = X(end);
        Canvas.SetLeft(RangeBand, left);
        RangeBand.Width = Math.Max(2, right - left);
        RangeBand.Visibility = Visibility.Visible;
        Canvas.SetLeft(InMarker, left - InMarker.Width / 2);
        InMarker.Visibility = _inPoint != null ? Visibility.Visible : Visibility.Collapsed;
        Canvas.SetLeft(OutMarker, right - OutMarker.Width / 2);
        OutMarker.Visibility = _outPoint != null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Exports the extract between the in and out points to a new MP4 next to the video.</summary>
    private async void ExportClip()
    {
        if (!CanEdit || IsExporting()) return;
        if (_inPoint == null && _outPoint == null)
        {
            ShowOsd(Loc.T("Player.Osd.NoRange"), 3);
            return;
        }
        var (start, end) = Range;
        if (end - start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        var source = FilePath;
        var name = FileOps.SanitizeFileName($"{Path.GetFileNameWithoutExtension(source)}_{Loc.T("Player.ClipFileSuffix")}");
        await RunExportAsync("Player.ExportingClip", Path.GetDirectoryName(source)!, name, ".mp4", gif: false,
            (path, progress, cancel) => VideoEditing.ExportClipAsync(source, path, start, end, progress, cancel));
    }

    /// <summary>Asks the GIF options, then makes the GIF next to the video.</summary>
    private async void ExportGif()
    {
        if (!CanEdit || IsExporting()) return;
        // Same rule as the MP4 extract: a selection (or a whole video) shorter than MinRange is refused.
        var (start, end) = Range;
        if (end - start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        var dialog = new GifExportDialog(this, Duration, _inPoint, _outPoint, _player.NaturalVideoWidth, _player.NaturalVideoHeight);
        if (dialog.ShowDialog() != true || dialog.Result is not { } settings || _closing) return;
        if (settings.End - settings.Start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        var source = FilePath;
        await RunExportAsync("Player.ExportingGif", Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source), ".gif", gif: true,
            (path, progress, cancel) => VideoEditing.ExportGifAsync(source, path, settings, progress, cancel));
    }

    private bool IsExporting()
    {
        if (_export == null) return false;
        ShowOsd(Loc.T("Player.Osd.ExportBusy"));
        return true;
    }

    /// <summary>
    /// Runs an export with the progress panel (cancel button, Esc): picks a free file name,
    /// announces the new file to the gallery, reports the result.
    /// </summary>
    /// <param name="labelKey">Text of the progress panel, {0} being the percentage.</param>
    private async Task RunExportAsync(string labelKey, string folder, string baseName, string extension, bool gif,
        Func<string, IProgress<double>, CancellationToken, Task> export)
    {
        using var cancel = new CancellationTokenSource();
        _export = cancel;
        ExportText.Text = Loc.T(labelKey, 0.0);
        ExportScale.ScaleX = 0;
        ExportPanel.Visibility = Visibility.Visible;
        var progress = new Progress<double>(value =>
        {
            if (_export != cancel) return;
            ExportScale.ScaleX = Math.Clamp(value, 0, 1);
            ExportText.Text = Loc.T(labelKey, Math.Clamp(value, 0, 1) * 100);
        });
        try
        {
            // Never overwrites anything: a new name, next to the video.
            var path = FileOps.UniquePath(folder, baseName, extension);
            await export(path, progress, cancel.Token);
            App.Instance.Captures.NotifySaved(path);
            var message = Loc.T(gif ? "Player.Osd.GifSaved" : "Player.Osd.ClipSaved", Path.GetFileName(path));
            if (IsActive && !_closing)
            {
                ShowOsd(message, 4);
            }
            else
            {
                Action open = gif ? () => ShowInExplorer(path) : () => App.Instance.OpenPlayer(path);
                App.Instance.Tray?.ShowInfo(Loc.T(gif ? "Player.GifSavedTitle" : "Player.ClipSavedTitle"), message, open);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing) ShowOsd(Loc.T("Player.Osd.ExportCancelled"));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(gif ? "Export GIF" : "Export d'un extrait vidéo", ex);
            if (!_closing) ShowOsd(Loc.T(gif ? "Player.Osd.GifFailed" : "Player.Osd.ClipFailed"), 4);
        }
        finally
        {
            if (_export == cancel) _export = null;
            ExportPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void CancelExport() => _export?.Cancel();

    private static void ShowInExplorer(string path)
    {
        try
        {
            FileOps.ShowInExplorer(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans l'explorateur", ex);
        }
    }

    // ---- Display ----

    private TimeSpan Duration => _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;

    private void UpdateTime()
    {
        var position = _player.Position;
        TimeText.Text = $"{Format(position)} / {Format(Duration)}";
        if (SeekBar.IsMouseCaptureWithin) return; // the user is dragging
        _updatingSeek = true;
        SeekBar.Value = Math.Min(SeekBar.Maximum, position.TotalSeconds);
        _updatingSeek = false;
    }

    private void UpdatePlayIcon() =>
        PlayIcon.Data = (Geometry)FindResource(_playing ? "PauseGeometry" : "PlayGeometry");

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    /// <summary>Time with tenths of a second (in / out points).</summary>
    private static string FormatPrecise(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f") : t.ToString(@"mm\:ss\.f");

    /// <param name="seconds">Time the message stays fully visible before fading.</param>
    private void ShowOsd(string text, double seconds = 1.2)
    {
        OsdText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(seconds + 0.4))));
        Osd.BeginAnimation(OpacityProperty, fade);
    }

    private void ShowController()
    {
        Controller.BeginAnimation(OpacityProperty, null);
        Controller.Opacity = 1;
        Root.Cursor = null;
        _idle.Stop();
        _idle.Start();
    }

    private void HideController()
    {
        _idle.Stop();
        if (_overController || !_playing) return;
        Controller.BeginAnimation(OpacityProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(300)));
        if (_fullscreen) Root.Cursor = Cursors.None;
    }

    // ---- Input ----

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p == _lastMouse) return; // ignore synthetic moves
        _lastMouse = p;
        ShowController();
    }

    private void OnControllerEnter(object sender, MouseEventArgs e) => _overController = true;

    private void OnControllerLeave(object sender, MouseEventArgs e) => _overController = false;

    private void OnVideoMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Controller.IsMouseOver || ExportPanel.IsMouseOver) return;
        if (e.ClickCount == 2)
        {
            // The first click of the double-click paused (or resumed) the video: undo it.
            if (_playing != _playingBeforeClick)
            {
                if (_playingBeforeClick) Play();
                else Pause();
                Osd.BeginAnimation(OpacityProperty, null);
                Osd.Opacity = 0;
            }
            ToggleFullscreen();
        }
        else if (e.ClickCount == 1)
        {
            _playingBeforeClick = _playing;
            TogglePlay();
        }
    }

    private void OpenWithDefaultPlayer()
    {
        // FileOps logs the reason (e.g. no application associated with MP4 files).
        if (!FileOps.OpenWithDefaultApp(FilePath))
            MessageBox.Show(this, Loc.T("Player.OpenFailed"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>Mouse wheel seeks, like mpv (Ctrl+wheel changes the volume).</summary>
    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers == ModifierKeys.Control) ChangeVolume(e.Delta > 0 ? 0.05 : -0.05);
        else SeekBy(e.Delta > 0 ? 10 : -10);
        e.Handled = true;
    }

    private void OnSeekChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingSeek) return;
        _player.Position = TimeSpan.FromSeconds(e.NewValue);
        TimeText.Text = $"{Format(_player.Position)} / {Format(Duration)}";
    }

    private void OnSeekBarSizeChanged(object sender, SizeChangedEventArgs e) => UpdateRange();

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _player.Volume = e.NewValue;

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();
    private void OnLoopClick(object sender, RoutedEventArgs e) => ToggleLoop();
    private void OnExportFrameClick(object sender, RoutedEventArgs e) => ExportFrame();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnSetInClick(object sender, RoutedEventArgs e) => SetIn();
    private void OnSetOutClick(object sender, RoutedEventArgs e) => SetOut();
    private void OnClearRangeClick(object sender, RoutedEventArgs e) => ClearRange();
    private void OnExportClipClick(object sender, RoutedEventArgs e) => ExportClip();
    private void OnExportGifClick(object sender, RoutedEventArgs e) => ExportGif();
    private void OnCancelExportClick(object sender, RoutedEventArgs e) => CancelExport();

    private void OnExportMenuClick(object sender, RoutedEventArgs e)
    {
        if (ExportButton.ContextMenu is not { } menu) return;
        ExportClipItem.IsEnabled = ExportGifItem.IsEnabled = CanEdit && _export == null;
        menu.PlacementTarget = ExportButton;
        menu.Placement = PlacementMode.Top;
        // The controller stays while the menu is open (the mouse has left it for the menu).
        menu.Closed -= OnExportMenuClosed;
        menu.Closed += OnExportMenuClosed;
        _idle.Stop();
        menu.IsOpen = true;
    }

    private void OnExportMenuClosed(object sender, RoutedEventArgs e) => ShowController();

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        // Cycles through the usual speeds.
        int index = Array.FindIndex(Speeds, s => s > _player.SpeedRatio + 0.01);
        SetSpeed(index < 0 ? Speeds[0] : Speeds[index]);
    }

    /// <summary>mpv keyboard shortcuts, plus I / O (extract), E (MP4 extract), G (GIF), F1 (help).</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        bool control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        switch (e.Key)
        {
            case Key.I when shift: ClearIn(); break;
            case Key.I: SetIn(); break;
            // O opens the video elsewhere when it cannot be played here (as the error message says).
            case Key.O when control || !_mediaReady: OpenWithDefaultPlayer(); break;
            case Key.O when shift: ClearOut(); break;
            case Key.O: SetOut(); break;
            case Key.E: ExportClip(); break;
            case Key.G: ExportGif(); break;
            case Key.F1: ShowOsd(Loc.T("Player.Help"), 8); break;
            case Key.Space or Key.P: TogglePlay(); break;
            case Key.Right: SeekBy(shift ? 1 : 5); break;
            case Key.Left: SeekBy(shift ? -1 : -5); break;
            case Key.Up: SeekBy(60); break;
            case Key.Down: SeekBy(-60); break;
            case Key.OemPeriod: StepFrame(1); break;
            case Key.OemComma: StepFrame(-1); break;
            case Key.Home: SeekBy(-_player.Position.TotalSeconds); break;
            case Key.Oem4 when shift: SetSpeed(_player.SpeedRatio / 2); break;   // {
            case Key.Oem6 when shift: SetSpeed(_player.SpeedRatio * 2); break;   // }
            case Key.Oem4: SetSpeed(_player.SpeedRatio / 1.1); break;            // [
            case Key.Oem6: SetSpeed(_player.SpeedRatio * 1.1); break;            // ]
            case Key.Back: SetSpeed(1); break;
            case Key.D9 or Key.NumPad9 or Key.Divide: ChangeVolume(-0.05); break;
            case Key.D0 or Key.NumPad0 or Key.Multiply: ChangeVolume(0.05); break;
            case Key.M: ToggleMute(); break;
            case Key.L: ToggleLoop(); break;
            case Key.S: ExportFrame(); break;
            case Key.F or Key.Enter: ToggleFullscreen(); break;
            case Key.Escape when _fullscreen: ToggleFullscreen(); break;
            case Key.Escape when _export != null: CancelExport(); break;
            case Key.Q or Key.Escape:
                // Closing: the controller must not come back (its timer would restart).
                e.Handled = true;
                Close();
                return;
            default: return;
        }
        ShowController();
        e.Handled = true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // An unfinished export is abandoned (its partial file deleted).
        _closing = true;
        _export?.Cancel();
        _clock.Stop();
        _idle.Stop();
        _player.Stop();
        _player.Close();
        base.OnClosing(e);
    }
}
