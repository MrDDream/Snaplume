using System.Globalization;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;
using Snaplume.Ui;

namespace Snaplume.Player;

/// <summary>
/// Minimal video player in the spirit of mpv: black window, controls that fade away, on-screen messages and mpv
/// keyboard shortcuts. The platform decodes the video (<see cref="IVideoPlayer"/>); its pictures are copied into a
/// bitmap shown here. Also cuts an extract (in / out points, exported to MP4) and makes animated GIFs, offline.
/// </summary>
public partial class PlayerWindow : Window
{
    /// <summary>The players do not expose the frame rate: one step is a 30 fps frame.</summary>
    private const double FrameStep = 1.0 / 30;
    private static readonly double[] Speeds = { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2, 3, 4 };
    /// <summary>Shortest extract that can be exported.</summary>
    private static readonly TimeSpan MinRange = TimeSpan.FromMilliseconds(200);
    /// <summary>Width of the seek bar's thumb (Fluent slider): its center runs from half of it to the width minus half.</summary>
    private const double SeekThumbWidth = 20;
    /// <summary>Below this window width, the extract's times are not written in the controller (no room).</summary>
    private const double RangeTextMinWidth = 780;

    private static readonly Geometry PlayGeometry = Geometry.Parse("M8,5 L19,12 L8,19 Z");
    private static readonly Geometry PauseGeometry = Geometry.Parse("M8,5 H10.5 V19 H8 Z M13.5,5 H16 V19 H13.5 Z");
    private static readonly Geometry VolumeGeometry = Geometry.Parse("M4,9 H8 L13,5 V19 L8,15 H4 Z M16,9 A4,4 0 0 1 16,15 M18.5,6.5 A7.5,7.5 0 0 1 18.5,17.5");
    private static readonly Geometry MutedGeometry = Geometry.Parse("M4,9 H8 L13,5 V19 L8,15 H4 Z M16,9.5 L21,14.5 M21,9.5 L16,14.5");

    private readonly IVideoPlayer? _player;
    private readonly DispatcherTimer _clock = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly DispatcherTimer _idle = new() { Interval = TimeSpan.FromSeconds(2.5) };
    private readonly DispatcherTimer _osdTimer = new();
    private bool _playing;
    private bool _playingBeforeClick;
    private bool _loop;
    private bool _updatingSeek;
    private bool _seeking;
    private bool _overController;
    private bool _fullscreen;
    private WindowState _stateBeforeFullscreen;
    private Point _lastMouse;
    private bool _mediaReady;
    private bool _fitted;
    private bool _closing;
    private bool _muted;
    private double _speed = 1;
    /// <summary>Extract chosen with I and O (null: from the start / to the end).</summary>
    private TimeSpan? _inPoint;
    private TimeSpan? _outPoint;
    /// <summary>Export in progress (one at a time), to cancel it.</summary>
    private CancellationTokenSource? _export;

    // The picture: the latest frame of the player (copied on its thread), shown on the UI thread.
    private readonly object _frameLock = new();
    private PixelBuffer? _latest;
    private bool _framePosted;
    private WriteableBitmap? _bitmap;

    /// <summary>For the XAML previewer only.</summary>
    public PlayerWindow() : this("")
    {
    }

    public PlayerWindow(string path)
    {
        InitializeComponent();
        FilePath = path;
        Title = Loc.T("Player.TitleWithFile", Path.GetFileName(path));
        PlayIcon.Data = PauseGeometry;
        VolumeIcon.Data = VolumeGeometry;

        try
        {
            if (AppServices.HasPlatform) _player = AppServices.Platform.Video.CreatePlayer();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecteur vidéo", ex);
        }
        SpeedButton.IsVisible = _player is IVideoPlayerSpeed;

        _clock.Tick += (_, _) => UpdateTime();
        _idle.Tick += (_, _) => HideController();
        _osdTimer.Tick += (_, _) =>
        {
            _osdTimer.Stop();
            Fade(Osd, 0, 400);
        };

        SeekBar.ValueChanged += OnSeekChanged;
        SeekBar.SizeChanged += (_, _) => UpdateRange();
        // A drag of the seek bar is not fought by the clock.
        SeekBar.AddHandler(PointerPressedEvent, (_, _) => _seeking = true, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerReleasedEvent, (_, _) => _seeking = false, RoutingStrategies.Tunnel, handledEventsToo: true);
        SeekBar.AddHandler(PointerCaptureLostEvent, (_, _) => _seeking = false, RoutingStrategies.Bubble, handledEventsToo: true);
        VolumeBar.ValueChanged += (_, e) => OnVolumeChanged(e.NewValue);

        Root.PointerMoved += OnMouseMove;
        Root.PointerPressed += OnVideoPointerPressed;
        Root.PointerWheelChanged += OnMouseWheel;
        Controller.PointerEntered += (_, _) => _overController = true;
        Controller.PointerExited += (_, _) => _overController = false;
        var exportMenu = (MenuFlyout)ExportButton.Flyout!;
        exportMenu.Opening += (_, _) =>
        {
            ExportClipItem.IsEnabled = ExportGifItem.IsEnabled = ExportWebItem.IsEnabled = CanEdit && _export == null;
            // The controller stays while the menu is open (the mouse has left it for the menu).
            _idle.Stop();
        };
        exportMenu.Closed += (_, _) => ShowController();
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        if (_player != null)
        {
            _player.FrameReady += OnFrameReady;
            _player.StateChanged += () => Dispatcher.UIThread.Post(OnPlayerStateChanged);
            _player.Volume = VolumeBar.Value;
        }

        Opened += async (_, _) => await OpenAsync();
    }

    /// <summary>The video being played.</summary>
    public string FilePath { get; }

    // ---- Media ----

    private async Task OpenAsync()
    {
        if (_player == null)
        {
            ShowError(null);
            return;
        }
        try
        {
            await _player.OpenAsync(FilePath);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecteur vidéo", ex);
            OnMediaFailed(ex.Message);
            return;
        }
        if (_closing) return;
        if (_player.Error is { } error)
        {
            OnMediaFailed(error);
            return;
        }
        SeekBar.Maximum = Math.Max(0.1, Duration.TotalSeconds);
        UpdateTime();
        Play();
        _clock.Start();
        _idle.Start();
    }

    private void OnPlayerStateChanged()
    {
        if (_closing || _player == null) return;
        if (_player.Error is { } error)
        {
            OnMediaFailed(error);
            return;
        }
        if (Duration > TimeSpan.Zero) SeekBar.Maximum = Math.Max(0.1, Duration.TotalSeconds);
        if (_player.HasEnded && _playing)
        {
            if (_loop)
            {
                _player.Seek(TimeSpan.Zero);
                _player.Play();
                return;
            }
            _player.Pause();
            _playing = false;
            UpdatePlayIcon();
            ShowController();
        }
        UpdateTime();
        UpdateRange();
    }

    private void OnMediaFailed(string? message)
    {
        _mediaReady = false;
        // Nothing plays: stop the clock and keep the controller visible.
        _playing = false;
        UpdatePlayIcon();
        _clock.Stop();
        _idle.Stop();
        Controller.Transitions = null;
        Controller.Opacity = 1;
        Root.Cursor = null;
        ShowError(message);
    }

    private void ShowError(string? message)
    {
        // The Windows message names its media engine; elsewhere a neutral one.
        var title = OperatingSystem.IsWindows() ? Loc.T("Player.Error") : Loc.T("Player.ErrorGeneric");
        ErrorText.Text = title + "\n" + message + "\n\n" + Loc.T("Player.ErrorOpenDefault");
        ErrorText.IsVisible = true;
    }

    /// <summary>A picture from the player (any thread): kept, then shown once on the UI thread.</summary>
    private void OnFrameReady(PixelBuffer frame)
    {
        lock (_frameLock)
        {
            if (_closing) return;
            if (_latest == null || _latest.Width != frame.Width || _latest.Height != frame.Height)
                _latest = new PixelBuffer(frame.Width, frame.Height);
            Buffer.BlockCopy(frame.Pixels, 0, _latest.Pixels, 0, frame.Stride * frame.Height);
            if (_framePosted) return;
            _framePosted = true;
        }
        Dispatcher.UIThread.Post(ShowFrame, DispatcherPriority.Render);
    }

    private void ShowFrame()
    {
        int width, height;
        bool resized = false;
        lock (_frameLock)
        {
            _framePosted = false;
            if (_closing || _latest is not { } frame) return;
            width = frame.Width;
            height = frame.Height;
            if (_bitmap == null || _bitmap.PixelSize.Width != width || _bitmap.PixelSize.Height != height)
            {
                var old = _bitmap;
                _bitmap = new WriteableBitmap(new PixelSize(width, height), AvaloniaImages.Dpi, PixelFormat.Bgra8888, AlphaFormat.Opaque);
                VideoImage.Source = _bitmap;
                old?.Dispose();
                resized = true;
            }
            using var locked = _bitmap.Lock();
            for (int y = 0; y < height; y++)
                Marshal.Copy(frame.Pixels, y * frame.Stride, locked.Address + y * locked.RowBytes, frame.Stride);
        }
        VideoImage.InvalidateVisual();
        if (resized)
        {
            _mediaReady = true;
            if (!_fitted)
            {
                _fitted = true;
                FitWindowToVideo(width, height);
            }
        }
    }

    /// <summary>Size of the video, from its pictures (0 until the first one).</summary>
    private (int Width, int Height) VideoSize
    {
        get
        {
            lock (_frameLock) return _latest is { } f ? (f.Width, f.Height) : (0, 0);
        }
    }

    /// <summary>Opens at the video size (100 % on the screen), limited to 85 % of the work area.</summary>
    private void FitWindowToVideo(int width, int height)
    {
        if (_fullscreen || WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen == null) return;
        double scaling = screen.Scaling > 0 ? screen.Scaling : 1;
        var area = screen.WorkingArea;
        double scale = Math.Min(1, Math.Min(area.Width * 0.85 / width, area.Height * 0.85 / height));
        Width = Math.Max(MinWidth, width / scaling * scale);
        Height = Math.Max(MinHeight, height / scaling * scale);
        Position = new PixelPoint(area.X + (int)((area.Width - Width * scaling) / 2), area.Y + (int)((area.Height - Height * scaling) / 2));
    }

    private void Play()
    {
        if (_player == null) return;
        if (Duration > TimeSpan.Zero && (_player.HasEnded || _player.Position >= Duration - TimeSpan.FromMilliseconds(50)))
            _player.Seek(TimeSpan.Zero);
        _player.Play();
        _playing = true;
        UpdatePlayIcon();
    }

    private void Pause()
    {
        _player?.Pause();
        _playing = false;
        UpdatePlayIcon();
    }

    private void TogglePlay()
    {
        if (_playing) Pause();
        else Play();
        ShowOsd(Loc.T(_playing ? "Player.Osd.Play" : "Player.Osd.Pause"));
    }

    private TimeSpan PlaybackPosition => _player?.Position ?? TimeSpan.Zero;

    private void SeekTo(TimeSpan target)
    {
        if (_player == null) return;
        if (target < TimeSpan.Zero) target = TimeSpan.Zero;
        if (Duration > TimeSpan.Zero && target > Duration) target = Duration;
        _player.Seek(target);
        UpdateTime(target);
    }

    private void SeekBy(double seconds)
    {
        var target = PlaybackPosition + TimeSpan.FromSeconds(seconds);
        SeekTo(target);
        ShowOsd($"{Format(Clamp(target))} / {Format(Duration)}");
    }

    private TimeSpan Clamp(TimeSpan t) => t < TimeSpan.Zero ? TimeSpan.Zero : Duration > TimeSpan.Zero && t > Duration ? Duration : t;

    private void StepFrame(int direction)
    {
        Pause();
        SeekTo(PlaybackPosition + TimeSpan.FromSeconds(FrameStep * direction));
        ShowOsd(Loc.T(direction > 0 ? "Player.Osd.NextFrame" : "Player.Osd.PreviousFrame"));
    }

    private void SetSpeed(double speed)
    {
        if (_player is not IVideoPlayerSpeed adjustable) return;
        speed = Math.Clamp(Math.Round(speed, 2), 0.1, 8);
        try
        {
            adjustable.Speed = speed;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Vitesse de lecture", ex);
            return;
        }
        _speed = speed;
        SpeedText.Text = speed.ToString("0.##", Loc.Culture) + "×";
        ShowOsd(Loc.T("Player.Osd.Speed", SpeedText.Text));
    }

    private void ChangeVolume(double delta)
    {
        VolumeBar.Value = Math.Clamp(VolumeBar.Value + delta, 0, 1);
        ShowOsd(Loc.T("Player.Osd.Volume", VolumeBar.Value * 100));
    }

    private void OnVolumeChanged(double value)
    {
        if (_player == null) return;
        // Moving the volume unmutes, as in most players.
        if (_muted) SetMuted(false, announce: false);
        _player.Volume = value;
    }

    private void ToggleMute()
    {
        SetMuted(!_muted, announce: true);
    }

    /// <summary>The players only have a volume: muting sets it to 0 and keeps the bar's value to come back to.</summary>
    private void SetMuted(bool muted, bool announce)
    {
        _muted = muted;
        if (_player != null) _player.Volume = muted ? 0 : VolumeBar.Value;
        VolumeIcon.Data = muted ? MutedGeometry : VolumeGeometry;
        if (announce) ShowOsd(Loc.T(muted ? "Player.Osd.Muted" : "Player.Osd.Unmuted"));
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
            WindowState = WindowState.FullScreen;
            _fullscreen = true;
        }
        else
        {
            WindowState = _stateBeforeFullscreen;
            _fullscreen = false;
            Root.Cursor = null;
        }
    }

    /// <summary>Saves the displayed picture as a PNG in the workspace (like mpv's "s").</summary>
    private async void ExportFrame()
    {
        PixelBuffer? picture;
        lock (_frameLock)
        {
            picture = _latest is { } f ? new PixelBuffer(f.Width, f.Height, (byte[])f.Pixels.Clone()) : null;
        }
        if (picture == null) return;
        try
        {
            var folder = Workspaces.ActiveFolder;
            var position = PlaybackPosition;
            var name = string.Create(CultureInfo.InvariantCulture,
                $"{Path.GetFileNameWithoutExtension(FilePath)}_{(int)position.TotalMinutes:00}-{position.Seconds:00}");
            var path = await Task.Run(() =>
            {
                Directory.CreateDirectory(folder);
                var target = FileOps.UniquePath(folder, name, ".png");
                ImageIO.Save(picture.MakeOpaque(), target);
                return target;
            });
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
        var position = PlaybackPosition;
        // A start after the end: the end goes.
        if (_outPoint is { } end && position > end - MinRange) _outPoint = null;
        _inPoint = position;
        UpdateRange();
        ShowOsd(Loc.T("Player.Osd.InPoint", FormatPrecise(position)));
    }

    private void SetOut()
    {
        if (!CanEdit) return;
        var position = PlaybackPosition;
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
        RangeText.IsVisible = any && Bounds.Width >= RangeTextMinWidth;
        ClearRangeButton.IsVisible = any;
        PositionRange();
    }

    /// <summary>Highlights the extract on the seek bar, with a mark at each point set.</summary>
    private void PositionRange()
    {
        double width = SeekBar.Bounds.Width, max = SeekBar.Maximum;
        if ((_inPoint == null && _outPoint == null) || max <= 0 || width <= SeekThumbWidth)
        {
            RangeBand.IsVisible = InMarker.IsVisible = OutMarker.IsVisible = false;
            return;
        }
        double X(TimeSpan t) => SeekThumbWidth / 2 + Math.Clamp(t.TotalSeconds / max, 0, 1) * (width - SeekThumbWidth);
        var (start, end) = Range;
        double left = X(start), right = X(end);
        Canvas.SetLeft(RangeBand, left);
        RangeBand.Width = Math.Max(2, right - left);
        RangeBand.IsVisible = true;
        Canvas.SetLeft(InMarker, left - InMarker.Width / 2);
        InMarker.IsVisible = _inPoint != null;
        Canvas.SetLeft(OutMarker, right - OutMarker.Width / 2);
        OutMarker.IsVisible = _outPoint != null;
        // The band and the marks sit on the slider's track, whatever the slider's height.
        double middle = SeekBar.Bounds.Height / 2;
        Canvas.SetTop(RangeBand, middle - RangeBand.Height / 2);
        Canvas.SetTop(InMarker, middle - InMarker.Height / 2);
        Canvas.SetTop(OutMarker, middle - OutMarker.Height / 2);
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
        var video = AppServices.Platform.Video;
        await RunExportAsync("Player.ExportingClip", Path.GetDirectoryName(source)!, name, ".mp4", ExportKind.Clip,
            (path, progress, cancel) => video.ExportClipAsync(source, path, start, end, progress, cancel));
    }

    /// <summary>
    /// Asks the format and the size (or quality), then re-encodes the video or the extract next to it
    /// ("name.discord.mp4", "name.webm"...).
    /// </summary>
    private async void ExportWeb()
    {
        if (!CanEdit || IsExporting()) return;
        var (start, end) = Range;
        if (end - start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        var source = FilePath;
        var video = AppServices.Platform.Video;
        var duration = Duration;
        VideoInfo info;
        FfmpegInfo? ffmpeg;
        IReadOnlyList<WebFormatAvailability> formats;
        try
        {
            // The encoders are looked for once per session (a few short runs of ffmpeg the first time).
            var probe = video.ProbeAsync(source);
            ffmpeg = await FfmpegTools.DetectAsync();
            var (width, height) = VideoSize;
            info = await probe ?? new VideoInfo(width, height, 0, true, duration);
            if (info.Width <= 0 || info.Height <= 0) info = info with { Width = width, Height = height };
            formats = await Task.Run(() => WebFormats.All
                .Select(f => WebVideoExport.Availability(f, video.CanTranscode, ffmpeg, OperatingSystem.IsWindows())).ToList());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Export compressé", ex);
            ShowOsd(Loc.T("Player.Osd.WebFailed"), 4);
            return;
        }
        if (_closing || info.Width <= 0 || info.Height <= 0) return;
        var dialog = new WebExportDialog(duration, _inPoint, _outPoint, info, formats, ffmpeg == null && FfmpegModule.IsSupported);
        var settings = await dialog.ShowDialog<WebExportSettings?>(this);
        if (settings == null || _closing) return;
        if (settings.End - settings.Start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        await RunExportAsync("Player.Web.Exporting", Path.GetDirectoryName(source)!, WebVideoExport.BaseName(source, settings),
            WebFormats.Extension(settings.Format), ExportKind.Web,
            (path, progress, cancel) => WebVideoExport.ExportAsync(video, ffmpeg, source, path, settings, info, progress, cancel));
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
        var (width, height) = VideoSize;
        var dialog = new GifExportDialog(Duration, _inPoint, _outPoint, width, height);
        var settings = await dialog.ShowDialog<GifExportSettings?>(this);
        if (settings == null || _closing) return;
        if (settings.End - settings.Start < MinRange)
        {
            ShowOsd(Loc.T("Player.Osd.RangeTooShort"), 3);
            return;
        }
        var source = FilePath;
        var duration = Duration;
        var video = AppServices.Platform.Video;
        await RunExportAsync("Player.ExportingGif", Path.GetDirectoryName(source)!, Path.GetFileNameWithoutExtension(source), ".gif", ExportKind.Gif,
            (path, progress, cancel) => VideoGifExport.ExportAsync(video, source, path, settings, duration, progress, cancel));
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
    private async Task RunExportAsync(string labelKey, string folder, string baseName, string extension, ExportKind kind,
        Func<string, IProgress<double>, CancellationToken, Task> export)
    {
        // A GIF or a web export is shown in its folder from the notification; an extract opens in the player.
        bool reveal = kind != ExportKind.Clip;
        var (savedKey, savedTitleKey, failedKey, logTitle) = kind switch
        {
            ExportKind.Gif => ("Player.Osd.GifSaved", "Player.GifSavedTitle", "Player.Osd.GifFailed", "Export GIF"),
            ExportKind.Web => ("Player.Osd.WebSaved", "Player.Web.SavedTitle", "Player.Osd.WebFailed", "Export compressé"),
            _ => ("Player.Osd.ClipSaved", "Player.ClipSavedTitle", "Player.Osd.ClipFailed", "Export d'un extrait vidéo"),
        };
        using var cancel = new CancellationTokenSource();
        _export = cancel;
        ExportText.Text = Loc.T(labelKey, 0.0);
        ExportBar.Width = 0;
        ExportPanel.IsVisible = true;
        var progress = new Progress<double>(value =>
        {
            if (_export != cancel) return;
            value = Math.Clamp(value, 0, 1);
            ExportBar.Width = 240 * value;
            ExportText.Text = Loc.T(labelKey, value * 100);
        });
        try
        {
            // Never overwrites anything: a new name, next to the video.
            var path = FileOps.UniquePath(folder, baseName, extension);
            await export(path, progress, cancel.Token);
            App.Instance.Captures.NotifySaved(path);
            var message = kind == ExportKind.Web
                ? Loc.T(savedKey, Path.GetFileName(path), WebVideoExport.Megabytes(new FileInfo(path).Length))
                : Loc.T(savedKey, Path.GetFileName(path));
            if (IsActive && !_closing)
            {
                ShowOsd(message, 4);
            }
            else
            {
                Action open = reveal ? () => Reveal(path) : () => App.Instance.OpenPlayer(path);
                App.Instance.Tray?.ShowInfo(Loc.T(savedTitleKey), message, open);
            }
        }
        catch (OperationCanceledException)
        {
            if (!_closing) ShowOsd(Loc.T("Player.Osd.ExportCancelled"));
        }
        catch (WebExportException ex)
        {
            // Expected (size out of reach...): the message says what to do.
            ErrorLog.Write(logTitle, ex.Message);
            if (!_closing) ShowOsd(ex.Message, 6);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(logTitle, ex);
            if (!_closing) ShowOsd(Loc.T(failedKey), 4);
        }
        finally
        {
            if (_export == cancel) _export = null;
            ExportPanel.IsVisible = false;
        }
    }

    private enum ExportKind
    {
        Clip,
        Gif,
        Web,
    }

    private void CancelExport() => _export?.Cancel();

    private static void Reveal(string path)
    {
        try
        {
            FileOps.Reveal(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans le gestionnaire de fichiers", ex);
        }
    }

    // ---- Display ----

    private TimeSpan Duration => _player?.Duration ?? TimeSpan.Zero;

    private void UpdateTime() => UpdateTime(PlaybackPosition);

    private void UpdateTime(TimeSpan position)
    {
        TimeText.Text = $"{Format(position)} / {Format(Duration)}";
        if (_seeking) return; // the user is dragging
        _updatingSeek = true;
        SeekBar.Value = Math.Min(SeekBar.Maximum, position.TotalSeconds);
        _updatingSeek = false;
    }

    private void UpdatePlayIcon() => PlayIcon.Data = _playing ? PauseGeometry : PlayGeometry;

    internal static string Format(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>Time with tenths of a second (in / out points).</summary>
    private static string FormatPrecise(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss\.f", CultureInfo.InvariantCulture) : t.ToString(@"mm\:ss\.f", CultureInfo.InvariantCulture);

    /// <param name="seconds">Time the message stays fully visible before fading.</param>
    private void ShowOsd(string text, double seconds = 1.2)
    {
        OsdText.Text = text;
        Osd.Transitions = null;
        Osd.Opacity = 1;
        _osdTimer.Stop();
        _osdTimer.Interval = TimeSpan.FromSeconds(seconds);
        _osdTimer.Start();
    }

    /// <summary>Animates the opacity of a control to a value.</summary>
    private static void Fade(Control control, double to, int milliseconds)
    {
        control.Transitions = new Transitions { new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(milliseconds) } };
        control.Opacity = to;
    }

    private void ShowController()
    {
        Controller.Transitions = null;
        Controller.Opacity = 1;
        Root.Cursor = null;
        _idle.Stop();
        if (!_closing) _idle.Start();
    }

    private void HideController()
    {
        _idle.Stop();
        if (_overController || !_playing) return;
        Fade(Controller, 0, 300);
        if (_fullscreen) Root.Cursor = new Cursor(StandardCursorType.None);
    }

    // ---- Input ----

    private void OnMouseMove(object? sender, PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        if (p == _lastMouse) return; // ignore synthetic moves
        _lastMouse = p;
        ShowController();
    }

    private void OnVideoPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        if (Controller.IsPointerOver || ExportPanel.IsPointerOver) return;
        if (e.ClickCount == 2)
        {
            // The first click of the double-click paused (or resumed) the video: undo it.
            if (_playing != _playingBeforeClick)
            {
                if (_playingBeforeClick) Play();
                else Pause();
                _osdTimer.Stop();
                Osd.Transitions = null;
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

    private async void OpenWithDefaultPlayer()
    {
        // FileOps logs the reason (e.g. no application associated with MP4 files).
        if (!FileOps.OpenWithDefaultApp(FilePath))
            await Dialogs.ShowAsync(this, Loc.T("Player.OpenFailed"), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
    }

    /// <summary>Mouse wheel seeks, like mpv (Ctrl+wheel changes the volume).</summary>
    private void OnMouseWheel(object? sender, PointerWheelEventArgs e)
    {
        if (e.Delta.Y == 0) return;
        if (e.KeyModifiers == KeyModifiers.Control) ChangeVolume(e.Delta.Y > 0 ? 0.05 : -0.05);
        else SeekBy(e.Delta.Y > 0 ? 10 : -10);
        e.Handled = true;
    }

    private void OnSeekChanged(object? sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSeek || _player == null) return;
        var target = TimeSpan.FromSeconds(e.NewValue);
        _player.Seek(target);
        TimeText.Text = $"{Format(target)} / {Format(Duration)}";
    }

    private void OnPlayClick(object? sender, RoutedEventArgs e) => TogglePlay();
    private void OnMuteClick(object? sender, RoutedEventArgs e) => ToggleMute();
    private void OnLoopClick(object? sender, RoutedEventArgs e) => ToggleLoop();
    private void OnExportFrameClick(object? sender, RoutedEventArgs e) => ExportFrame();
    private void OnFullscreenClick(object? sender, RoutedEventArgs e) => ToggleFullscreen();
    private void OnSetInClick(object? sender, RoutedEventArgs e) => SetIn();
    private void OnSetOutClick(object? sender, RoutedEventArgs e) => SetOut();
    private void OnClearRangeClick(object? sender, RoutedEventArgs e) => ClearRange();
    private void OnExportClipClick(object? sender, RoutedEventArgs e) => ExportClip();
    private void OnExportGifClick(object? sender, RoutedEventArgs e) => ExportGif();
    private void OnExportWebClick(object? sender, RoutedEventArgs e) => ExportWeb();
    private void OnCancelExportClick(object? sender, RoutedEventArgs e) => CancelExport();

    private void OnSpeedClick(object? sender, RoutedEventArgs e)
    {
        // Cycles through the usual speeds.
        int index = Array.FindIndex(Speeds, s => s > _speed + 0.01);
        SetSpeed(index < 0 ? Speeds[0] : Speeds[index]);
    }

    /// <summary>
    /// mpv keyboard shortcuts, plus I / O (extract), E (MP4 extract), C (export / compress), G (GIF), F1 (help),
    /// Ctrl+O (default player).
    /// </summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Typing in the export menu or another popup is left alone.
        if (e.Source is TextBox) return;
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        bool control = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.I when shift: ClearIn(); break;
            case Key.I: SetIn(); break;
            // O opens the video elsewhere when it cannot be played here (as the error message says).
            case Key.O when control || !_mediaReady: OpenWithDefaultPlayer(); break;
            case Key.O when shift: ClearOut(); break;
            case Key.O: SetOut(); break;
            case Key.E: ExportClip(); break;
            case Key.C when !control: ExportWeb(); break;
            case Key.G: ExportGif(); break;
            case Key.F1: ShowOsd(Loc.T("Player.Web.Help"), 8); break;
            case Key.Space or Key.P: TogglePlay(); break;
            case Key.Right: SeekBy(shift ? 1 : 5); break;
            case Key.Left: SeekBy(shift ? -1 : -5); break;
            case Key.Up: SeekBy(60); break;
            case Key.Down: SeekBy(-60); break;
            case Key.OemPeriod: StepFrame(1); break;
            case Key.OemComma: StepFrame(-1); break;
            case Key.Home: SeekTo(TimeSpan.Zero); break;
            case Key.OemOpenBrackets when shift: SetSpeed(_speed / 2); break;   // {
            case Key.OemCloseBrackets when shift: SetSpeed(_speed * 2); break;  // }
            case Key.OemOpenBrackets: SetSpeed(_speed / 1.1); break;            // [
            case Key.OemCloseBrackets: SetSpeed(_speed * 1.1); break;           // ]
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

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (e.Cancel) return;
        // An unfinished export is abandoned (its partial file deleted).
        lock (_frameLock) _closing = true;
        _export?.Cancel();
        _clock.Stop();
        _idle.Stop();
        _osdTimer.Stop();
        if (_player != null)
        {
            _player.FrameReady -= OnFrameReady;
            try
            {
                _player.Dispose();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Lecteur vidéo", ex);
            }
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        VideoImage.Source = null;
        _bitmap?.Dispose();
        _bitmap = null;
    }
}
