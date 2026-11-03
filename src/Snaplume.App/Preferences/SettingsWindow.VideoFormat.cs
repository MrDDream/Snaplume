using Avalonia.Controls;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;
using Snaplume.Ui;

namespace Snaplume.Preferences;

/// <summary>
/// Settings › Video › "Video format": the format and compression of the recordings, with a line saying whether the
/// recorder makes them directly or the recording is converted once it stops (<see cref="RecordingPlanner"/>). The
/// formats follow the rules of the export dialog: MP4 H.264 alone until there is an ffmpeg (the card below installs
/// the module).
/// </summary>
public partial class SettingsWindow
{
    /// <summary>The format saved, kept in the list even when it is not available here.</summary>
    private WebFormat _savedVideoFormat;
    /// <summary>The ffmpeg search is over (<see cref="RefreshFfmpegAsync"/>): until then, the list is not final.</summary>
    private bool _ffmpegKnown;
    /// <summary>The ffmpeg found, or null.</summary>
    private FfmpegInfo? _videoFfmpeg;
    /// <summary>The recorder encodes HEVC itself on this computer.</summary>
    private bool _canRecordHevc;
    /// <summary>Availability of each format of the list, in its order.</summary>
    private List<WebFormatAvailability> _videoFormats = new();
    private bool _loadingVideoFormat;

    private void LoadVideoFormat(AppSettings settings)
    {
        _loadingVideoFormat = true;
        _savedVideoFormat = RecordingFormats.FromId(settings.VideoFormat);
        var compressions = RecordingCompression.All.Select(id => new ChoiceItem(id, CompressionName(id))).ToList();
        VideoCompressionBox.ItemsSource = compressions;
        VideoCompressionBox.SelectedItem = compressions.FirstOrDefault(c => Equals(c.Value, RecordingCompression.Normalize(settings.VideoCompression)))
                                           ?? compressions[0];
        VideoMegabytesBox.Text = WebExportForm.MegabytesFor(settings.VideoCompressionMegabytes);
        KeepOriginalCheck.IsChecked = settings.VideoKeepOriginal;

        // Greyed formats keep their place, with the reason as tooltip (shown on disabled items too).
        VideoFormatBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem item || e.Index < 0 || e.Index >= _videoFormats.Count) return;
            var format = _videoFormats[e.Index];
            item.IsEnabled = format.IsAvailable;
            ToolTip.SetTip(item, format.Reason.Length > 0 ? format.Reason : null);
            ToolTip.SetShowOnDisabled(item, true);
        };
        VideoFormatBox.SelectionChanged += (_, _) => UpdateVideoRoute();
        VideoCompressionBox.SelectionChanged += (_, _) => UpdateVideoRoute();
        VideoMegabytesBox.TextChanged += (_, _) => UpdateVideoRoute();
        _loadingVideoFormat = false;
        ShowVideoFormats();
    }

    private static string CompressionName(string id) => id switch
    {
        RecordingCompression.None => Loc.T("Settings.VideoFormat.CompressionNone"),
        RecordingCompression.High or RecordingCompression.Medium or RecordingCompression.Light =>
            Loc.T("Settings.VideoFormat.QualityPreset", Loc.T("Player.Web.Quality." + id)),
        SizeTargets.Custom => Loc.T("Settings.VideoFormat.CustomSize"),
        _ => Loc.T("Player.Web.Target." + id, SizeTargets.Megabytes(id)),
    };

    private WebFormat SelectedVideoFormat => (VideoFormatBox.SelectedItem as ChoiceItem)?.Value as WebFormat? ?? _savedVideoFormat;

    private string SelectedCompression => (VideoCompressionBox.SelectedItem as ChoiceItem)?.Value as string ?? RecordingCompression.None;

    /// <summary>
    /// Fills the format list: before the ffmpeg search, MP4 H.264 and the format saved; then what this computer offers,
    /// the format saved staying listed (greyed) when it is no longer available, so that saving does not forget it.
    /// </summary>
    private void ShowVideoFormats()
    {
        var current = VideoFormatBox.SelectedItem is ChoiceItem { Value: WebFormat selected } ? selected : _savedVideoFormat;
        bool windows = OperatingSystem.IsWindows();
        List<WebFormatAvailability> formats;
        if (_ffmpegKnown)
        {
            var video = AppServices.HasPlatform ? AppServices.Platform.Video : null;
            formats = RecordingFormats.Offered(c => video?.CanTranscode(c) == true, _videoFfmpeg, windows).ToList();
        }
        else
        {
            formats = new List<WebFormatAvailability> { new(WebFormat.Mp4H264, WebEncoderRoute.System, "") };
        }
        foreach (var format in new[] { current, _savedVideoFormat }.Distinct())
        {
            if (formats.Any(f => f.Format == format)) continue;
            formats.Add(_ffmpegKnown
                ? new WebFormatAvailability(format, WebEncoderRoute.Unavailable, Loc.T(windows ? "Player.Web.NeedsModule" : "Player.Web.NeedsFfmpeg"))
                : new WebFormatAvailability(format, WebEncoderRoute.Ffmpeg, ""));
        }
        formats = formats.OrderBy(f => Array.IndexOf(RecordingFormats.All, f.Format)).ToList();
        _videoFormats = formats;
        var items = formats.Select(f =>
        {
            var name = Loc.T("Player.Web.Format." + WebFormats.Id(f.Format));
            return new ChoiceItem(f.Format, f.IsAvailable ? name : Loc.T("Player.Web.Unavailable", name));
        }).ToList();
        _loadingVideoFormat = true;
        VideoFormatBox.ItemsSource = items;
        VideoFormatBox.SelectedItem = items.FirstOrDefault(i => Equals(i.Value, current)) ?? items[0];
        _loadingVideoFormat = false;

        bool noFfmpeg = _ffmpegKnown && _videoFfmpeg == null;
        MoreFormatsButton.IsVisible = noFfmpeg;
        MoreFormatsText.Text = Loc.T(FfmpegModule.IsSupported ? "Settings.VideoFormat.MoreFormats" : "Settings.VideoFormat.MoreFormatsPackage");
        UpdateVideoRoute();
    }

    /// <summary>The size typed for "custom", in MB, if valid.</summary>
    private bool TryReadVideoMegabytes(out double megabytes) =>
        WebExportForm.TryParseMegabytes(VideoMegabytesBox.Text ?? "", out megabytes)
        && megabytes >= SizeTargets.MinMegabytes && megabytes <= SizeTargets.MaxMegabytes;

    /// <summary>What the fields say; false (with the message) when the custom size is not valid.</summary>
    private bool TryReadVideoChoice(out RecordingFormatChoice choice, out string error)
    {
        error = "";
        var compression = SelectedCompression;
        double megabytes = Result.VideoCompressionMegabytes;
        if (compression == SizeTargets.Custom && !TryReadVideoMegabytes(out megabytes))
            error = Loc.T("Player.Web.SizeError", SizeTargets.MinMegabytes, SizeTargets.MaxMegabytes);
        choice = new RecordingFormatChoice(SelectedVideoFormat, compression, megabytes, KeepOriginalCheck.IsChecked == true);
        return error.Length == 0;
    }

    /// <summary>The line under the choices: recorded directly, converted afterwards and why, or why it cannot be.</summary>
    private void UpdateVideoRoute()
    {
        if (_loadingVideoFormat) return;
        bool valid = TryReadVideoChoice(out var choice, out var error);
        VideoMegabytesPanel.IsVisible = choice.Compression == SizeTargets.Custom;
        var plan = RecordingPlanner.Plan(choice, Result.VideoFramerate, codec => codec == VideoCodec.H264 || _canRecordHevc);
        KeepOriginalRow.IsEnabled = plan.Conversion != null;
        var availability = _videoFormats.FirstOrDefault(f => f.Format == choice.Format);
        bool warning = !valid || availability is { IsAvailable: false };
        VideoRouteText.Text = !valid ? error
            : availability is { IsAvailable: false } ? Loc.T("Settings.VideoFormat.RouteUnavailable", availability.Reason)
            // Whether the recorder has an HEVC encoder is known with the ffmpeg search.
            : !_ffmpegKnown && choice.Format == WebFormat.Mp4Hevc && choice.TargetBytes == null ? Loc.T("Settings.Ffmpeg.Checking")
            : RecordingPlanner.Describe(plan, choice.Format);
        VideoRouteText.Classes.Set("warning", warning);
        VideoRouteIcon.Classes.Set("warning", warning);
        VideoRouteIcon.Data = Resource<Avalonia.Media.Geometry>(plan.Conversion == null && !warning ? "IconCheck" : "IconInfo");
    }

    /// <summary>Copies the choices into <see cref="Result"/>; false (page shown, field focused) when the custom size is wrong.</summary>
    private async Task<bool> SaveVideoFormatAsync()
    {
        if (!TryReadVideoChoice(out var choice, out var error))
        {
            ShowPage(nameof(PageVideo));
            VideoMegabytesBox.Focus();
            await Dialogs.ShowAsync(this, error, Loc.T("Settings.Title"), DialogButtons.Ok, DialogIcon.Warning);
            return false;
        }
        Result.VideoFormat = WebFormats.Id(choice.Format);
        Result.VideoCompression = choice.Compression;
        if (choice.Compression == SizeTargets.Custom) Result.VideoCompressionMegabytes = choice.CustomMegabytes;
        Result.VideoKeepOriginal = choice.KeepOriginal;
        return true;
    }
}
