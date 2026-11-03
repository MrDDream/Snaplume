using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Player;
using Snaplume.Preferences;

namespace Snaplume.Tests;

/// <summary>
/// Settings › Video without a screen: the "Video format" card at the top of the page and the ffmpeg module card in each
/// of its states, with the application's themes (light, dark, right to left). The pictures are saved next to the tests.
/// </summary>
[Collection(ApplicationState.Name)]
public class SettingsVideoTests
{
    /// <summary>The module as installed by Snaplume: AV1, VP9, Opus and WebP, no HEVC encoder that works here.</summary>
    private static readonly FfmpegInfo Module = new(FfmpegModule.ExecutablePath, FfmpegSource.Module, "n8.1.3-9-g29e619e767-20260930",
        new HashSet<string> { "libsvtav1", "libaom-av1", "libvpx-vp9", "libopus", "aac", "libwebp_anim", "libwebp", "libopenh264" });

    [AvaloniaFact]
    public async Task The_video_format_card_says_when_a_recording_is_converted()
    {
        using var scope = new Scope(new AppSettings());
        var window = await scope.OpenSettingsAsync(new AppSettings { VideoFormat = "av1", VideoCompression = "medium" });
        SetFfmpeg(window, Module, canRecordHevc: false);
        var format = window.FindControl<ComboBox>("VideoFormatBox")!;
        var compression = window.FindControl<ComboBox>("VideoCompressionBox")!;
        var route = window.FindControl<TextBlock>("VideoRouteText")!;

        Assert.Equal(new[] { WebFormat.Mp4H264, WebFormat.Mp4Hevc, WebFormat.Mp4Av1, WebFormat.WebmVp9 }, Values<WebFormat>(format));
        Assert.Equal(WebFormat.Mp4Av1, Value(format));
        Assert.Contains("ne peut pas être encodé en direct", route.Text);
        Assert.True(window.FindControl<DockPanel>("KeepOriginalRow")!.IsEnabled);
        Assert.False(window.FindControl<Button>("MoreFormatsButton")!.IsVisible);
        Save(window, "settings-video-format.png");

        Select(compression, SizeTargets.Custom);
        Assert.True(window.FindControl<StackPanel>("VideoMegabytesPanel")!.IsVisible);
        Assert.Contains("taille maximale", route.Text);
        window.FindControl<TextBox>("VideoMegabytesBox")!.Text = "0,2";
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("entre 1 et 4000", route.Text);
        window.FindControl<TextBox>("VideoMegabytesBox")!.Text = "18,5";
        Dispatcher.UIThread.RunJobs();
        Save(window, "settings-video-format-size.png");

        // H.264 without compression: as before, nothing to keep.
        Select(format, WebFormat.Mp4H264);
        Select(compression, RecordingCompression.None);
        Assert.Equal(Loc.T("Settings.VideoFormat.RouteDirect", Loc.T("Player.Web.Format.mp4")), route.Text);
        Assert.False(window.FindControl<DockPanel>("KeepOriginalRow")!.IsEnabled);

        // Saved into the copy of the settings.
        Select(format, WebFormat.WebmVp9);
        Select(compression, SizeTargets.Custom);
        window.FindControl<ToggleSwitch>("KeepOriginalCheck")!.IsChecked = true;
        var save = (Task<bool>)typeof(SettingsWindow).GetMethod("SaveVideoFormatAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
        Assert.True(await save);
        Assert.Equal(("webm", SizeTargets.Custom, 18.5, true),
            (window.Result.VideoFormat, window.Result.VideoCompression, window.Result.VideoCompressionMegabytes, window.Result.VideoKeepOriginal));
    }

    [AvaloniaFact]
    public async Task Hevc_is_recorded_directly_when_the_recorder_can()
    {
        using var scope = new Scope(new AppSettings());
        var window = await scope.OpenSettingsAsync(new AppSettings { VideoFormat = "hevc", VideoCompression = "high" });
        var hevcModule = Module with { Encoders = new HashSet<string>(Module.Encoders) { "hevc_nvenc" } };
        SetFfmpeg(window, hevcModule, canRecordHevc: true);
        var route = window.FindControl<TextBlock>("VideoRouteText")!;
        Assert.Equal(Loc.T("Settings.VideoFormat.RouteDirect", Loc.T("Player.Web.Format.hevc")), route.Text);
        SetFfmpeg(window, hevcModule, canRecordHevc: false);
        Assert.Contains("pas d'encodeur", route.Text);
    }

    [AvaloniaFact]
    public async Task Without_ffmpeg_only_h264_and_a_link_to_the_module()
    {
        using var scope = new Scope(new AppSettings());
        var window = await scope.OpenSettingsAsync(new AppSettings { VideoFormat = "webm" });
        SetFfmpeg(window, null, canRecordHevc: false);
        var format = window.FindControl<ComboBox>("VideoFormatBox")!;
        // The saved format stays listed, greyed, so that saving does not forget it.
        Assert.Equal(new[] { WebFormat.Mp4H264, WebFormat.WebmVp9 }, Values<WebFormat>(format));
        Assert.Equal(WebFormat.WebmVp9, Value(format));
        Assert.True(window.FindControl<Button>("MoreFormatsButton")!.IsVisible);
        Assert.Contains("indisponible", window.FindControl<TextBlock>("VideoRouteText")!.Text);
        ShowFfmpeg(window, FfmpegModule.IsSupported ? "NotInstalled" : "Missing", null);
        Save(window, "settings-video-no-ffmpeg.png");
    }

    [AvaloniaFact]
    public async Task The_ffmpeg_card_in_each_state()
    {
        using var scope = new Scope(new AppSettings());
        var window = await scope.OpenSettingsAsync(new AppSettings());
        SetFfmpeg(window, Module, canRecordHevc: false);
        var chip = window.FindControl<Border>("FfmpegChip")!;
        var status = window.FindControl<TextBlock>("FfmpegStatus")!;

        ShowFfmpeg(window, "Module", Module, 213_000_000);
        Assert.Equal(Loc.T("Settings.Ffmpeg.StateInstalled"), window.FindControl<TextBlock>("FfmpegChipText")!.Text);
        Assert.Contains("success", chip.Classes);
        Assert.Equal("ffmpeg 8.1.3 · 213 Mo sur le disque", status.Text);
        Assert.Contains("n8.1.3-9-g29e619e767-20260930", ToolTip.GetTip(status) as string);
        Assert.Equal(FfmpegModule.IsSupported, window.FindControl<Button>("FfmpegUninstallButton")!.IsVisible);
        Assert.True(window.FindControl<Border>("FfmpegFormatsRow")!.IsVisible);
        var chips = window.FindControl<ItemsControl>("FfmpegFormatChips")!.Items.OfType<Border>().ToList();
        Assert.Equal(WebFormats.All.Length, chips.Count);
        Assert.Equal(1, chips.Count(c => c.Classes.Contains("unavailable"))); // HEVC: no encoder in this module
        SaveCard(window, "settings-ffmpeg-installed.png");

        ShowFfmpeg(window, "NotInstalled", null);
        Assert.Equal(FfmpegModule.IsSupported, window.FindControl<Button>("FfmpegInstallButton")!.IsVisible);
        Assert.Contains("81", window.FindControl<Button>("FfmpegInstallButton")!.Content as string);
        Assert.DoesNotContain("success", chip.Classes);
        SaveCard(window, "settings-ffmpeg-not-installed.png");

        ShowFfmpeg(window, "Installing", null);
        var progress = window.FindControl<ProgressBar>("FfmpegProgress")!;
        progress.Value = 0.42;
        status.Text = FfmpegModule.ProgressText(34_000_000, FfmpegModule.Package.Size);
        Assert.True(progress.IsVisible);
        Assert.True(window.FindControl<Button>("FfmpegCancelButton")!.IsVisible);
        Assert.False(window.FindControl<Button>("FfmpegInstallButton")!.IsVisible);
        Assert.False(window.FindControl<Border>("FfmpegFormatsRow")!.IsVisible);
        Dispatcher.UIThread.RunJobs();
        SaveCard(window, "settings-ffmpeg-installing.png");

        ShowFfmpeg(window, "Broken", null);
        Assert.Contains("error", chip.Classes);
        Assert.Equal(Loc.T("Settings.Ffmpeg.Reinstall"), window.FindControl<Button>("FfmpegInstallButton")!.Content);
        SaveCard(window, "settings-ffmpeg-broken.png");

        var inPath = new FfmpegInfo(@"C:\Tools\ffmpeg\bin\ffmpeg.exe", FfmpegSource.Path, "7.1.1-essentials_build-www.gyan.dev", Module.Encoders);
        ShowFfmpeg(window, "Path", inPath);
        Assert.Contains(@"C:\Tools\ffmpeg\bin\ffmpeg.exe", window.FindControl<TextBlock>("FfmpegLocation")!.Text);
        Assert.Equal("ffmpeg 7.1.1", status.Text);
        SaveCard(window, "settings-ffmpeg-path.png");
    }

    [AvaloniaFact]
    public async Task Dark_and_right_to_left()
    {
        using var scope = new Scope(new AppSettings(), ThemeVariant.Dark);
        var window = await scope.OpenSettingsAsync(new AppSettings { VideoFormat = "hevc", VideoCompression = "discord" },
            FlowDirection.RightToLeft);
        SetFfmpeg(window, Module, canRecordHevc: false);
        ShowFfmpeg(window, "Module", Module, 213_000_000);
        Save(window, "settings-video-format-dark-rtl.png");
        SaveCard(window, "settings-ffmpeg-installed-dark-rtl.png");
        ShowFfmpeg(window, "Broken", null);
        SaveCard(window, "settings-ffmpeg-broken-dark-rtl.png");
    }

    [AvaloniaFact]
    public void The_conversion_bar_shows_the_progress()
    {
        foreach (var (theme, name) in new[] { (ThemeVariant.Light, "conversion-bar.png"), (ThemeVariant.Dark, "conversion-bar-dark.png") })
        {
            using var scope = new Scope(new AppSettings(), theme);
            var bar = new Snaplume.Recording.ConversionBar(new Snaplume.Capturing.PixelRect(100, 100, 1280, 720));
            bar.Show();
            bar.SetJob(Loc.T("Video.Converting", Loc.T("Player.Web.Format.av1")), "Video_2026-11-03_14-22-05", 1);
            bar.SetProgress(0.42);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Video_2026-11-03_14-22-05 · 1 en attente", bar.FindControl<TextBlock>("DetailText")!.Text);
            Assert.Equal("42 %", bar.FindControl<TextBlock>("PercentText")!.Text);
            Snaplume.Imaging.ImageIO.Save(Snaplume.Imaging.AvaloniaImages.ToPixelBuffer(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(bar)!),
                Path.Combine(AppContext.BaseDirectory, name));
            bar.Close();
        }
    }

    // ---- Helpers ----

    /// <summary>What the ffmpeg search would have found, and whether the recorder makes HEVC.</summary>
    private static void SetFfmpeg(SettingsWindow window, FfmpegInfo? ffmpeg, bool canRecordHevc)
    {
        Field(window, "_ffmpegKnown", true);
        Field(window, "_videoFfmpeg", ffmpeg);
        Field(window, "_canRecordHevc", canRecordHevc);
        Method(window, "ShowVideoFormats");
    }

    private static void ShowFfmpeg(SettingsWindow window, string state, FfmpegInfo? ffmpeg, long moduleBytes = 0)
    {
        var type = typeof(SettingsWindow).GetNestedType("FfmpegState", BindingFlags.NonPublic)!;
        Method(window, "ShowFfmpeg", Enum.Parse(type, state), ffmpeg, moduleBytes, null);
    }

    private static void Field(object target, string name, object? value) =>
        target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);

    private static void Method(object target, string name, params object?[] args)
    {
        target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(target, args);
        Dispatcher.UIThread.RunJobs();
    }

    private static object? Value(ComboBox box) => (box.SelectedItem as Snaplume.Ui.ChoiceItem)?.Value;

    private static T[] Values<T>(ComboBox box) => box.Items.OfType<Snaplume.Ui.ChoiceItem>().Select(i => (T)i.Value).ToArray();

    private static void Select(ComboBox box, object value)
    {
        box.SelectedItem = box.Items.OfType<Snaplume.Ui.ChoiceItem>().First(i => Equals(i.Value, value));
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>The top of the video page.</summary>
    private static void Save(Window window, string name)
    {
        window.FindControl<ScrollViewer>("ContentScroller")!.Offset = default;
        Dispatcher.UIThread.RunJobs();
        Snaplume.Imaging.ImageIO.Save(Snaplume.Imaging.AvaloniaImages.ToPixelBuffer(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!),
            Path.Combine(AppContext.BaseDirectory, name));
    }

    /// <summary>The bottom of the video page: the ffmpeg card.</summary>
    private static void SaveCard(Window window, string name)
    {
        window.FindControl<ScrollViewer>("ContentScroller")!.Offset = new Vector(0, 10_000);
        Dispatcher.UIThread.RunJobs();
        Snaplume.Imaging.ImageIO.Save(Snaplume.Imaging.AvaloniaImages.ToPixelBuffer(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!),
            Path.Combine(AppContext.BaseDirectory, name));
    }

    private sealed class Scope : IDisposable
    {
        private readonly Application _app = Application.Current!;
        private readonly ResourceInclude _colors = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        private readonly ResourceInclude _icons = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        private readonly StyleInclude _styles = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        private readonly AppSettings _settings = AppServices.Settings;
        private readonly ThemeVariant? _theme;
        private readonly List<Window> _windows = new();

        public Scope(AppSettings settings, ThemeVariant? theme = null)
        {
            _theme = _app.RequestedThemeVariant;
            _app.Resources.MergedDictionaries.Add(_colors);
            _app.Resources.MergedDictionaries.Add(_icons);
            _app.Styles.Add(_styles);
            if (theme != null) _app.RequestedThemeVariant = theme;
            AppServices.Settings = settings;
        }

        /// <summary>The settings on the video page, once the real ffmpeg search of the window is over (it would overwrite the states set).</summary>
        public async Task<SettingsWindow> OpenSettingsAsync(AppSettings settings, FlowDirection direction = FlowDirection.LeftToRight)
        {
            var window = new SettingsWindow(settings, "PageVideo") { Height = 900, FlowDirection = direction };
            _windows.Add(window);
            window.Show();
            Dispatcher.UIThread.RunJobs();
            await FfmpegTools.DetectAsync();
            var status = window.FindControl<TextBlock>("FfmpegStatus")!;
            for (int i = 0; i < 200 && (status.Text == Loc.T("Settings.Ffmpeg.Checking") || !(bool)Ready(window)); i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            return window;
        }

        private static object Ready(SettingsWindow window) =>
            typeof(SettingsWindow).GetField("_ffmpegKnown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;

        public void Dispose()
        {
            foreach (var window in _windows) window.Close();
            AppServices.Settings = _settings;
            _app.RequestedThemeVariant = _theme;
            _app.Styles.Remove(_styles);
            _app.Resources.MergedDictionaries.Remove(_icons);
            _app.Resources.MergedDictionaries.Remove(_colors);
        }
    }
}
