using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;

namespace Snaplume.Tests;

/// <summary>The "Export / compress" dialog of the player opened without a screen, with the application's themes.</summary>
[Collection(ApplicationState.Name)]
public class WebExportDialogTests
{
    private static readonly VideoInfo Source = new(1920, 1080, 30, true, TimeSpan.FromSeconds(60));

    private static List<WebFormatAvailability> Formats() => WebFormats.All.Select(f => f switch
    {
        WebFormat.Mp4Hevc => new WebFormatAvailability(f, WebEncoderRoute.Unavailable, Loc.T("Player.Web.NoHevcWindows")),
        WebFormat.Mp4H264 => new WebFormatAvailability(f, WebEncoderRoute.System, ""),
        _ => new WebFormatAvailability(f, WebEncoderRoute.Ffmpeg, "", "x"),
    }).ToList();

    [AvaloniaFact]
    public void Greyed_formats_explain_why_and_a_typed_size_becomes_custom()
    {
        using var scope = new Scope(new AppSettings { WebExportFormat = "hevc", WebExportTarget = "discord" });
        var dialog = scope.Open(new WebExportDialog(TimeSpan.FromSeconds(60), null, null, Source, Formats(), showModuleHint: false));
        var format = dialog.FindControl<ComboBox>("FormatBox")!;
        var target = dialog.FindControl<ComboBox>("TargetBox")!;
        var size = dialog.FindControl<TextBox>("MegabytesBox")!;

        // HEVC remembered but not available here: MP4 H.264 instead.
        Assert.Equal(WebFormat.Mp4H264, Value(format));
        Assert.Equal("10", size.Text);
        Assert.True(dialog.FindControl<TextBlock>("SummaryText")!.IsVisible);
        Assert.False(dialog.FindControl<StackPanel>("QualityPanel")!.IsVisible);

        format.IsDropDownOpen = true;
        Dispatcher.UIThread.RunJobs();
        var hevc = (ComboBoxItem)format.ContainerFromIndex(1)!;
        Assert.False(hevc.IsEnabled);
        Assert.Equal(Loc.T("Player.Web.NoHevcWindows"), ToolTip.GetTip(hevc));
        Assert.True(ToolTip.GetShowOnDisabled(hevc));
        format.IsDropDownOpen = false;

        size.Text = "20";
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(SizeTargets.Custom, Value(target));
        Assert.Equal("20", size.Text);
        Save(dialog, "web-export-dialog.png");
    }

    [AvaloniaFact]
    public void A_video_too_long_for_the_size_says_how_long_it_may_be()
    {
        using var scope = new Scope(new AppSettings { WebExportTarget = "discord" });
        var dialog = scope.Open(new WebExportDialog(TimeSpan.FromMinutes(30), null, null, Source with { Duration = TimeSpan.FromMinutes(30) },
            Formats(), showModuleHint: true));
        var error = dialog.FindControl<TextBlock>("ErrorText")!;
        Assert.True(error.IsVisible);
        Assert.Contains("5:08", error.Text);
        Assert.True(dialog.FindControl<Border>("ModuleHint")!.IsVisible);
        Save(dialog, "web-export-dialog-error.png");
    }

    [AvaloniaFact]
    public void Dark_theme_and_right_to_left()
    {
        using var scope = new Scope(new AppSettings { WebExportFormat = "webm", WebExportTarget = "off" }, ThemeVariant.Dark);
        var dialog = new WebExportDialog(TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25), Source, Formats(),
            showModuleHint: false) { FlowDirection = FlowDirection.RightToLeft };
        scope.Open(dialog);
        Assert.True(dialog.FindControl<StackPanel>("QualityPanel")!.IsVisible);
        Assert.False(dialog.FindControl<StackPanel>("MegabytesPanel")!.IsVisible);
        Assert.Equal(WebFormat.WebmVp9, Value(dialog.FindControl<ComboBox>("FormatBox")!));
        Save(dialog, "web-export-dialog-dark-rtl.png");
    }

    [AvaloniaFact]
    public async Task Settings_show_the_ffmpeg_module_row()
    {
        using var scope = new Scope(new AppSettings());
        var window = scope.Open(new Snaplume.Preferences.SettingsWindow(new AppSettings(), "PageVideo") { Height = 1400 });
        // Whatever this computer has (module, PATH, nothing): the row says it once the search is over.
        await FfmpegTools.DetectAsync();
        for (int i = 0; i < 50 && window.FindControl<TextBlock>("FfmpegStatus")!.Text == Loc.T("Settings.Ffmpeg.Checking"); i++)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
        var status = window.FindControl<TextBlock>("FfmpegStatus")!;
        Assert.NotEqual(Loc.T("Settings.Ffmpeg.Checking"), status.Text);
        Assert.False(window.FindControl<Button>("FfmpegCancelButton")!.IsVisible);
        if (FfmpegModule.IsSupported && !FfmpegModule.IsInstalled) Assert.True(window.FindControl<Button>("FfmpegInstallButton")!.IsVisible);
        window.FindControl<ScrollViewer>("ContentScroller")!.Offset = new Vector(0, 10_000);
        Dispatcher.UIThread.RunJobs();
        Save(window, "settings-ffmpeg-row.png");
    }

    private static object? Value(ComboBox box) => box.SelectedItem?.GetType().GetProperty("Value")!.GetValue(box.SelectedItem);

    /// <summary>Kept with the test results to look at the dialog; never committed.</summary>
    private static void Save(Window window, string name) =>
        Snaplume.Imaging.ImageIO.Save(Snaplume.Imaging.AvaloniaImages.ToPixelBuffer(Avalonia.Headless.HeadlessWindowExtensions.CaptureRenderedFrame(window)!),
            Path.Combine(AppContext.BaseDirectory, name));

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
            _app.RequestedThemeVariant = _theme;
            _app.Styles.Remove(_styles);
            _app.Resources.MergedDictionaries.Remove(_icons);
            _app.Resources.MergedDictionaries.Remove(_colors);
        }
    }
}
