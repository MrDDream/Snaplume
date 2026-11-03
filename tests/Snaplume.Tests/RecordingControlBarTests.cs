using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Recording;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Tests;

/// <summary>The video options bar opened without a screen, with the application's themes; the mouse is the headless one.</summary>
[Collection(ApplicationState.Name)]
public class RecordingControlBarTests
{
    private static readonly PixelRect Area = new(100, 100, 800, 600);
    private static readonly IReadOnlyList<PixelRect> Monitors = new[] { new PixelRect(0, 0, 1600, 900) };

    [AvaloniaFact]
    public void Options_show_the_settings_and_save_each_change()
    {
        using var scope = new BarScope(new AppSettings { VideoMicrophone = false, VideoSystemAudio = true, VideoWebcam = false, VideoClickZoom = true });
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: true));

        Assert.True(bar.IsChoosing);
        Assert.False(Toggle(bar, "MicrophoneToggle").IsChecked);
        Assert.True(Toggle(bar, "SystemAudioToggle").IsChecked);
        Assert.False(Toggle(bar, "WebcamToggle").IsChecked);
        Assert.True(Toggle(bar, "ZoomToggle").IsChecked);

        Click(bar, Toggle(bar, "MicrophoneToggle"));
        Click(bar, Toggle(bar, "SystemAudioToggle"));
        Click(bar, Toggle(bar, "ZoomToggle"));
        var settings = AppServices.Settings;
        Assert.True(settings.VideoMicrophone);
        Assert.False(settings.VideoSystemAudio);
        Assert.False(settings.VideoClickZoom);
        Assert.False(settings.VideoWebcam);

        // Changed elsewhere (settings window): shown again.
        settings.VideoWebcam = true;
        bar.RefreshOptions();
        Assert.True(Toggle(bar, "WebcamToggle").IsChecked);
    }

    [AvaloniaFact]
    public void Zoom_is_greyed_with_an_explanation_where_the_system_cannot_do_it()
    {
        using var scope = new BarScope(new AppSettings { VideoClickZoom = true });
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: false));
        var zoom = Toggle(bar, "ZoomToggle");
        Assert.False(zoom.IsEnabled);
        Assert.False(zoom.IsChecked);
        Assert.Equal(Loc.T("Recording.ZoomUnavailable"), ToolTip.GetTip(zoom));
        Assert.True(ToolTip.GetShowOnDisabled(zoom));
        // The saved choice is kept for a system that can.
        Click(bar, Toggle(bar, "MicrophoneToggle"));
        Assert.True(AppServices.Settings.VideoClickZoom);
    }

    [AvaloniaFact]
    public void Sizes_offered_are_the_selection_and_the_presets_that_fit()
    {
        using var scope = new BarScope(new AppSettings());
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: true));
        Assert.Equal(new[] { (800, 600), (640, 480), (854, 480), (1280, 720) }, bar.SizeChoices());
        Assert.Equal(Loc.T("Recording.SizeValue", 800, 600), bar.FindControl<TextBlock>("SizeText")!.Text);
        bar.SetArea(new PixelRect(0, 0, 1280, 720));
        Assert.Equal(Loc.T("Recording.SizeValue", 1280, 720), bar.FindControl<TextBlock>("SizeText")!.Text);

        // Whole screen: no size to choose.
        var screen = scope.Open(new RecordingControlBar(new PixelRect(0, 0, 1600, 900), showOptions: true, monitors: null, clickZoom: true));
        Assert.False(screen.FindControl<Button>("SizeButton")!.IsVisible);
    }

    [AvaloniaFact]
    public void Record_turns_the_options_into_the_recording_bar()
    {
        using var scope = new BarScope(new AppSettings());
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: true));
        int record = 0;
        bar.RecordClicked += () => record++;
        Assert.Equal("0:00:00", bar.FindControl<TextBlock>("TimeText")!.Text);

        Click(bar, bar.FindControl<Button>("RecordButton")!);
        Assert.Equal(1, record);
        Assert.False(bar.IsChoosing);
        Assert.False(bar.FindControl<StackPanel>("OptionsPanel")!.IsVisible);
        Assert.True(bar.FindControl<StackPanel>("RecordingButtons")!.IsVisible);

        bar.ShowCountdown(3);
        Assert.Equal(Loc.T("Recording.Countdown", 3), bar.FindControl<TextBlock>("TitleText")!.Text);
        bar.SetRecording(TimeSpan.FromSeconds(83));
        Assert.Equal("0:01:23", bar.FindControl<TextBlock>("TimeText")!.Text);
        Assert.True(bar.FindControl<Button>("PauseButton")!.IsEnabled);
    }

    [AvaloniaFact]
    public void Escape_cancels_before_the_recording_only()
    {
        using var scope = new BarScope(new AppSettings());
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: true));
        int cancel = 0;
        bar.CancelClicked += () => cancel++;
        bar.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, cancel);

        bar.SetRecording(TimeSpan.FromSeconds(1));
        bar.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(1, cancel);
    }

    [AvaloniaFact]
    public void Hovered_buttons_have_no_fluent_background_behind_their_content()
    {
        using var scope = new BarScope(new AppSettings());
        var bar = scope.Open(new RecordingControlBar(Area, showOptions: true, Monitors, clickZoom: true));
        foreach (var button in bar.GetVisualDescendants().OfType<Button>().Where(b => b.IsEffectivelyVisible))
        {
            var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), bar)!.Value;
            bar.MouseMove(center);
            Dispatcher.UIThread.RunJobs();
            foreach (var presenter in button.GetVisualDescendants().OfType<ContentPresenter>())
                Assert.NotEqual("PART_ContentPresenter", presenter.Name);
        }
    }

    [AvaloniaFact]
    public void Frame_is_movable_until_the_recording_starts()
    {
        using var scope = new BarScope(new AppSettings());
        var frame = scope.Open(new RecordingFrame(Area, Monitors));
        Assert.True(frame.IsMovable);
        Assert.Equal(new PixelPoint(96, 96), frame.Position);
        Assert.Equal(808, frame.Width);

        frame.SetArea(new PixelRect(200, 100, 640, 480));
        Assert.Equal(new PixelRect(200, 100, 640, 480), frame.Area);
        Assert.Equal(new PixelPoint(196, 96), frame.Position);
        Assert.Equal(648, frame.Width);
        Assert.Equal(488, frame.Height);

        frame.EndMoving();
        Assert.False(frame.IsMovable);

        // Without monitors (whole screen, or no options): a fixed frame.
        var fixedFrame = scope.Open(new RecordingFrame(Area));
        Assert.False(fixedFrame.IsMovable);
    }

    private static ToggleButton Toggle(Window bar, string name) => bar.FindControl<ToggleButton>(name)!;

    private static void Click(Window bar, Control control)
    {
        var center = control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), bar)!.Value;
        bar.MouseDown(center, MouseButton.Left);
        bar.MouseUp(center, MouseButton.Left);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The application's themes and test settings while the bars are open; everything put back afterwards.</summary>
    private sealed class BarScope : IDisposable
    {
        private readonly Application _app = Application.Current!;
        private readonly ResourceInclude _colors = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        private readonly ResourceInclude _icons = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        private readonly StyleInclude _styles = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        private readonly AppSettings _settings = AppServices.Settings;
        private readonly List<Window> _windows = new();

        public BarScope(AppSettings settings)
        {
            _app.Resources.MergedDictionaries.Add(_colors);
            _app.Resources.MergedDictionaries.Add(_icons);
            _app.Styles.Add(_styles);
            AppServices.Settings = settings;
        }

        public T Open<T>(T window) where T : Window
        {
            _windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            return window;
        }

        public void Dispose()
        {
            foreach (var window in _windows) window.Close();
            AppServices.Settings = _settings;
            _app.Styles.Remove(_styles);
            _app.Resources.MergedDictionaries.Remove(_icons);
            _app.Resources.MergedDictionaries.Remove(_colors);
        }
    }
}
