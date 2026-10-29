using System.ComponentModel;
using System.Windows;
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
/// </summary>
public partial class PlayerWindow : Window
{
    private const double FrameStep = 1.0 / 30; // MediaPlayer does not expose the frame rate
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };

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
        FitWindowToVideo(w, h);
        UpdateTime();
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

    private static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"mm\:ss");

    private void ShowOsd(string text)
    {
        OsdText.Text = text;
        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.2))));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(1.6))));
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
        if (Controller.IsMouseOver) return;
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

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e) => _player.Volume = e.NewValue;

    private void OnPlayClick(object sender, RoutedEventArgs e) => TogglePlay();
    private void OnMuteClick(object sender, RoutedEventArgs e) => ToggleMute();
    private void OnLoopClick(object sender, RoutedEventArgs e) => ToggleLoop();
    private void OnExportFrameClick(object sender, RoutedEventArgs e) => ExportFrame();
    private void OnFullscreenClick(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void OnSpeedClick(object sender, RoutedEventArgs e)
    {
        // Cycles through the usual speeds.
        int index = Array.FindIndex(Speeds, s => s > _player.SpeedRatio + 0.01);
        SetSpeed(index < 0 ? Speeds[0] : Speeds[index]);
    }

    /// <summary>mpv keyboard shortcuts.</summary>
    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        bool shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        switch (e.Key)
        {
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
            case Key.O: OpenWithDefaultPlayer(); break;
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
        _clock.Stop();
        _idle.Stop();
        _player.Stop();
        _player.Close();
        base.OnClosing(e);
    }
}
