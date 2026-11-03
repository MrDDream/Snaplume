using System.Globalization;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>
/// Floating bar of a video, next to the recorded area; excluded from the video when the system allows it.
/// </summary>
/// <remarks>
/// With the options (the usual way), it first shows the Record button and what the video takes in: microphone,
/// system sound, webcam, zoom on clicks, size of the area. Each change is saved in the settings at once. Once Record is
/// pressed, it counts down and becomes the recording bar: time, pause, stop. Escape or the cross cancel.
/// </remarks>
public partial class RecordingControlBar : Window
{
    private static readonly Geometry PauseGeometry = Geometry.Parse("M9,6 V18 M15,6 V18");
    private static readonly Geometry ResumeGeometry = Geometry.Parse("M8,5.5 L18.5,12 L8,18.5 Z");
    /// <summary>Webcam sizes offered, in % of the width of the recorded area (as in the settings).</summary>
    private static readonly (int Percent, string Key)[] WebcamSizes =
    {
        (15, "Settings.Video.WebcamSizeSmall"),
        (20, "Settings.Video.WebcamSizeMedium"),
        (30, "Settings.Video.WebcamSizeLarge"),
    };

    private readonly IReadOnlyList<PixelRect>? _monitors;
    /// <summary>Size of the area the user selected (the first choice of the size list).</summary>
    private readonly (int Width, int Height) _selectedSize;
    private PixelRect _area;
    private bool _paused;
    private bool _canPause = true;
    /// <summary>The bar shows the options (until Record is pressed).</summary>
    private bool _choosing;
    /// <summary>The toggles are being set from the settings: no save.</summary>
    private bool _loading;
    /// <summary>The user dragged the bar elsewhere.</summary>
    private bool _moved;

    public event Action? RecordClicked;
    public event Action? PauseClicked;
    public event Action? StopClicked;
    public event Action? CancelClicked;
    /// <summary>The settings button: the video page of the settings.</summary>
    public event Action? SettingsClicked;
    /// <summary>Another size was chosen for the recorded area (physical pixels).</summary>
    public event Action<int, int>? AreaSizeChosen;

    /// <summary>For the XAML previewer only.</summary>
    public RecordingControlBar() : this(new PixelRect(0, 0, 800, 600), showOptions: true)
    {
    }

    /// <param name="area">Recorded area, absolute physical coordinates: the bar is placed next to it.</param>
    /// <param name="showOptions">Starts with the options and the Record button; otherwise straight with the countdown.</param>
    /// <param name="monitors">Monitor bounds, to offer the sizes that fit; null hides the size (whole screen recorded).</param>
    /// <param name="clickZoom">The system can zoom on clicks; null asks the platform.</param>
    public RecordingControlBar(PixelRect area, bool showOptions = false, IReadOnlyList<PixelRect>? monitors = null, bool? clickZoom = null)
    {
        InitializeComponent();
        _area = area;
        _selectedSize = (area.Width, area.Height);
        _monitors = monitors;
        _choosing = showOptions;
        PauseIcon.Data = PauseGeometry;
        TimeText.Text = Format(TimeSpan.Zero);

        bool zoomAvailable = clickZoom ?? (AppServices.HasPlatform && AppServices.Platform.Has(PlatformFeatures.ClickZoom));
        ZoomToggle.IsEnabled = zoomAvailable;
        if (!zoomAvailable) ToolTip.SetTip(ZoomToggle, Loc.T("Recording.ZoomUnavailable"));
        SizeButton.IsVisible = SizeSeparator.IsVisible = monitors != null;
        UpdateSizeText();
        RefreshOptions();
        if (showOptions)
        {
            // The options take the keyboard (Escape, the lists): the window is activated.
            ShowActivated = true;
        }
        else
        {
            ShowRecordingButtons();
        }

        Opened += (_, _) =>
        {
            FloatingBar.ApplyNativeStyles(this, activatable: showOptions);
            FloatingBar.PlaceNearArea(this, _area);
            if (showOptions) Activate();
        };
        // The size is final after the first layout; once the user dragged the bar, it stays where it was put.
        SizeChanged += (_, _) =>
        {
            if (IsVisible && !_moved) FloatingBar.PlaceNearArea(this, _area);
        };
    }

    /// <summary>The options are shown (before Record).</summary>
    public bool IsChoosing => _choosing;

    /// <summary>The area moved or got another size: the bar follows it (unless the user moved the bar).</summary>
    public void SetArea(PixelRect area)
    {
        _area = area;
        UpdateSizeText();
        if (IsVisible && !_moved) FloatingBar.PlaceNearArea(this, area);
    }

    /// <summary>Shows the options as the settings are now (changed in the settings window meanwhile, for instance).</summary>
    public void RefreshOptions()
    {
        var settings = AppServices.Settings;
        _loading = true;
        try
        {
            MicrophoneToggle.IsChecked = settings.VideoMicrophone;
            SystemAudioToggle.IsChecked = settings.VideoSystemAudio;
            WebcamToggle.IsChecked = settings.VideoWebcam;
            ZoomToggle.IsChecked = settings.VideoClickZoom && ZoomToggle.IsEnabled;
        }
        finally
        {
            _loading = false;
        }
    }

    public void ShowCountdown(int seconds)
    {
        ShowRecordingButtons();
        TitleText.Text = Loc.T("Recording.Countdown", seconds);
    }

    /// <summary>Some recorders cannot pause (the button stays disabled).</summary>
    public void SetCanPause(bool canPause)
    {
        _canPause = canPause;
        PauseButton.IsEnabled = canPause && PauseButton.IsEnabled;
    }

    public void SetRecording(TimeSpan elapsed)
    {
        ShowRecordingButtons();
        PauseButton.IsEnabled = _canPause;
        TitleText.Text = Loc.T(_paused ? "Recording.PausedTitle" : "Recording.BarTitle");
        TimeText.Text = Format(elapsed);
        RecordDot.IsVisible = true;
        // Blink while recording.
        RecordDot.Opacity = _paused || elapsed.Milliseconds < 500 ? 1 : 0.25;
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        PauseIcon.Data = paused ? ResumeGeometry : PauseGeometry;
        var text = Loc.T(paused ? "Recording.Resume" : "Recording.Pause");
        PauseLabel.Text = text;
        ToolTip.SetTip(PauseButton, text);
        RecordDot.Opacity = 1;
    }

    public void SetFinishing()
    {
        ShowRecordingButtons();
        TitleText.Text = Loc.T("Recording.Finishing");
        PauseButton.IsEnabled = StopButton.IsEnabled = CloseButton.IsEnabled = false;
    }

    /// <summary>Hours always shown, as in a video player: 0:00:00.</summary>
    internal static string Format(TimeSpan t) => t.ToString(@"h\:mm\:ss", CultureInfo.InvariantCulture);

    /// <summary>The sizes of the list: the selected area, then the standard sizes that fit on the monitor of the area.</summary>
    public IReadOnlyList<(int Width, int Height)> SizeChoices()
    {
        var choices = new List<(int Width, int Height)> { _selectedSize };
        if (_monitors is { Count: > 0 })
            choices.AddRange(RecordingArea.PresetsFor(RecordingArea.MonitorOf(_area, _monitors)).Where(p => p != _selectedSize));
        return choices;
    }

    private void ShowRecordingButtons()
    {
        if (!RecordingButtons.IsVisible)
        {
            _choosing = false;
            RecordButton.IsVisible = OptionsPanel.IsVisible = false;
            RecordingButtons.IsVisible = true;
            ToolTip.SetTip(CloseButton, Loc.T("Recording.CancelTip"));
        }
    }

    private void UpdateSizeText() => SizeText.Text = SizeLabel(_area.Width, _area.Height);

    private static string SizeLabel(int width, int height) => Loc.T("Recording.SizeValue", width, height);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // Before the recording only: during it, Escape belongs to the recorded application.
        if (e.Key == Key.Escape && !e.Handled && (_choosing || !RecordDot.IsVisible))
        {
            e.Handled = true;
            CancelClicked?.Invoke();
        }
    }

    private void OnDrag(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _moved = true;
        BeginMoveDrag(e);
    }

    /// <summary>A toggle changed: saved at once, the next recordings start with it too.</summary>
    private void OnOptionChanged(object? sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var settings = AppServices.Settings;
        settings.VideoMicrophone = MicrophoneToggle.IsChecked == true;
        settings.VideoSystemAudio = SystemAudioToggle.IsChecked == true;
        settings.VideoWebcam = WebcamToggle.IsChecked == true;
        if (ZoomToggle.IsEnabled) settings.VideoClickZoom = ZoomToggle.IsChecked == true;
        Save(settings);
    }

    private static void Save(AppSettings settings)
    {
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // The choice still applies to this recording.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    private async void OnMicrophoneMenu(object? sender, RoutedEventArgs e)
    {
        var microphones = await ListAsync(r => r.Microphones(), "Liste des microphones");
        var settings = AppServices.Settings;
        var menu = new MenuFlyout();
        menu.Items.Add(Choice(Loc.T("Recording.MicrophoneDefault"), settings.VideoMicrophoneDevice.Length == 0, () => ChooseMicrophone("")));
        foreach (var microphone in microphones)
        {
            var id = microphone.Id;
            menu.Items.Add(Choice(microphone.Name, string.Equals(id, settings.VideoMicrophoneDevice, StringComparison.OrdinalIgnoreCase),
                () => ChooseMicrophone(id)));
        }
        menu.ShowAt(MicrophoneMenuButton);
    }

    private void ChooseMicrophone(string id)
    {
        var settings = AppServices.Settings;
        settings.VideoMicrophoneDevice = id;
        // Choosing a microphone means recording it.
        if (MicrophoneToggle.IsChecked != true) MicrophoneToggle.IsChecked = true; // saves
        else Save(settings);
    }

    private async void OnWebcamMenu(object? sender, RoutedEventArgs e)
    {
        var cameras = await ListAsync(r => r.Webcams(), "Liste des webcams");
        var settings = AppServices.Settings;
        var menu = new MenuFlyout();
        menu.Items.Add(Choice(Loc.T("Settings.Video.WebcamDefault"), settings.VideoWebcamDevice.Length == 0, () => ChooseWebcam("")));
        foreach (var camera in cameras.Where(c => !string.IsNullOrEmpty(c.Id)))
        {
            var id = camera.Id;
            menu.Items.Add(Choice(string.IsNullOrWhiteSpace(camera.Name) ? id : camera.Name,
                string.Equals(id, settings.VideoWebcamDevice, StringComparison.OrdinalIgnoreCase), () => ChooseWebcam(id)));
        }
        menu.Items.Add(new Separator());

        var corners = new MenuItem { Header = Loc.T("Settings.Video.WebcamCorner") };
        foreach (var corner in AppSettings.WebcamCorners)
        {
            var value = corner;
            corners.Items.Add(Choice(Loc.T("Settings.Video.Corner" + corner), settings.VideoWebcamCorner == corner, () =>
            {
                AppServices.Settings.VideoWebcamCorner = value;
                Save(AppServices.Settings);
            }));
        }
        menu.Items.Add(corners);

        var sizes = new MenuItem { Header = Loc.T("Settings.Video.WebcamSize") };
        foreach (var (percent, key) in WebcamSizes)
        {
            var value = percent;
            sizes.Items.Add(Choice(Loc.T(key, percent), settings.VideoWebcamSize == percent, () =>
            {
                AppServices.Settings.VideoWebcamSize = value;
                Save(AppServices.Settings);
            }));
        }
        menu.Items.Add(sizes);
        menu.ShowAt(WebcamMenuButton);
    }

    private void ChooseWebcam(string id)
    {
        var settings = AppServices.Settings;
        settings.VideoWebcamDevice = id;
        if (WebcamToggle.IsChecked != true) WebcamToggle.IsChecked = true; // saves
        else Save(settings);
    }

    private void OnSizeMenu(object? sender, RoutedEventArgs e)
    {
        var menu = new MenuFlyout();
        var choices = SizeChoices();
        for (int i = 0; i < choices.Count; i++)
        {
            var (width, height) = choices[i];
            var label = i == 0 ? Loc.T("Recording.SizeSelected", SizeLabel(width, height)) : SizeLabel(width, height);
            menu.Items.Add(Choice(label, width == _area.Width && height == _area.Height, () => AreaSizeChosen?.Invoke(width, height)));
        }
        menu.ShowAt(SizeButton);
    }

    /// <summary>A menu entry checked when it is the current choice. A TextBlock: an underscore in a device name is no access key.</summary>
    private static MenuItem Choice(string text, bool isChecked, Action click)
    {
        var item = new MenuItem
        {
            Header = new TextBlock { Text = text },
            ToggleType = MenuItemToggleType.Radio,
            IsChecked = isChecked,
        };
        item.Click += (_, _) => click();
        return item;
    }

    /// <summary>Devices of the recorder, listed off the UI thread (drivers can be slow to answer); empty on failure.</summary>
    private static async Task<IReadOnlyList<T>> ListAsync<T>(Func<IScreenRecorderFactory, IReadOnlyList<T>> list, string logTitle)
    {
        if (!AppServices.HasPlatform) return Array.Empty<T>();
        try
        {
            var recorder = AppServices.Platform.Recorder;
            return await Task.Run(() => list(recorder));
        }
        catch (Exception ex)
        {
            ErrorLog.Write(logTitle, ex);
            return Array.Empty<T>();
        }
    }

    private void OnRecord(object? sender, RoutedEventArgs e)
    {
        // The options are set: the bar becomes the recording bar (countdown, or the system's permission dialog).
        ShowRecordingButtons();
        RecordClicked?.Invoke();
    }
    private void OnSettings(object? sender, RoutedEventArgs e) => SettingsClicked?.Invoke();
    private void OnPause(object? sender, RoutedEventArgs e) => PauseClicked?.Invoke();
    private void OnStop(object? sender, RoutedEventArgs e) => StopClicked?.Invoke();
    private void OnCancel(object? sender, RoutedEventArgs e) => CancelClicked?.Invoke();
}
