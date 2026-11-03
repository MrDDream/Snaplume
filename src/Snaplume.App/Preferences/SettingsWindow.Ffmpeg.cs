using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Player;
using Snaplume.Ui;

namespace Snaplume.Preferences;

/// <summary>
/// Settings › Video › "Web formats module (ffmpeg)": which ffmpeg the exports and conversions use (version, where it
/// comes from, size of the module) with a status chip, and a chip per format it can make; on Windows, the download
/// (with progress and Cancel) and the removal of the module (<see cref="FfmpegModule"/>). Applied at once, like the
/// update check: Save and Cancel of the window do not concern it.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>What the ffmpeg card shows.</summary>
    internal enum FfmpegState
    {
        Checking,
        /// <summary>The module of Snaplume (Windows).</summary>
        Module,
        /// <summary>An ffmpeg in PATH (Windows, installed by the user).</summary>
        Path,
        /// <summary>The system's ffmpeg (Linux).</summary>
        System,
        /// <summary>No ffmpeg; the module can be installed (Windows).</summary>
        NotInstalled,
        /// <summary>No ffmpeg; the system's package is the way (Linux).</summary>
        Missing,
        /// <summary>The module is there but does not start.</summary>
        Broken,
        Installing,
    }

    /// <summary>Download in progress, cancelled with the button or by closing the window.</summary>
    private CancellationTokenSource? _ffmpegDownload;

    /// <summary>Looks for ffmpeg (again after an install or a removal) and shows it, with the formats of the recordings.</summary>
    private async Task RefreshFfmpegAsync()
    {
        ShowFfmpeg(FfmpegState.Checking, null);
        FfmpegInfo? ffmpeg;
        try
        {
            ffmpeg = await FfmpegTools.DetectAsync();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Recherche de ffmpeg", ex);
            ffmpeg = null;
        }
        bool installed = FfmpegModule.IsSupported && FfmpegModule.IsInstalled;
        bool hevcLive = false;
        long moduleBytes = 0;
        try
        {
            // Off the UI thread: the encoders of the recorder are looked for, the module's files counted.
            (hevcLive, moduleBytes) = await Task.Run(() => (AppServices.HasPlatform && AppServices.Platform.Recorder.CanRecord(VideoCodec.Hevc),
                installed ? FfmpegModule.InstalledBytes() : 0));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Encodeurs de l'enregistrement", ex);
        }
        _canRecordHevc = hevcLive;
        _videoFfmpeg = ffmpeg;
        _ffmpegKnown = true;
        ShowVideoFormats();
        if (_ffmpegDownload != null) return;

        var state = ffmpeg?.Source switch
        {
            FfmpegSource.Module => FfmpegState.Module,
            FfmpegSource.Path => FfmpegState.Path,
            FfmpegSource.System => FfmpegState.System,
            // The module exists but does not start (damaged): it can be installed again.
            _ when installed => FfmpegState.Broken,
            _ when FfmpegModule.IsSupported => FfmpegState.NotInstalled,
            _ => FfmpegState.Missing,
        };
        ShowFfmpeg(state, ffmpeg, moduleBytes);
    }

    /// <summary>Fills the card for a state (also called by the tests, with a made-up ffmpeg).</summary>
    /// <param name="moduleBytes">Size of the installed module on the disk, 0 when unknown.</param>
    /// <param name="error">Why the last installation or removal failed, shown under the status.</param>
    private void ShowFfmpeg(FfmpegState state, FfmpegInfo? ffmpeg, long moduleBytes = 0, string? error = null)
    {
        bool module = FfmpegModule.IsSupported;
        var version = ffmpeg is { Version.Length: > 0 } ? FfmpegTools.ShortVersion(ffmpeg.Version) : "?";

        var (chipKey, chipKind) = state switch
        {
            FfmpegState.Module => ("Settings.Ffmpeg.StateInstalled", "success"),
            FfmpegState.Path => ("Settings.Ffmpeg.StatePath", "success"),
            FfmpegState.System => ("Settings.Ffmpeg.StateSystem", "success"),
            FfmpegState.NotInstalled or FfmpegState.Missing => ("Settings.Ffmpeg.StateNotInstalled", ""),
            FfmpegState.Broken => ("Settings.Ffmpeg.StateBroken", "error"),
            FfmpegState.Installing => ("Settings.Ffmpeg.StateInstalling", "warning"),
            _ => (null, ""),
        };
        FfmpegChip.IsVisible = chipKey != null;
        if (chipKey != null) FfmpegChipText.Text = Loc.T(chipKey);
        foreach (var kind in new[] { "success", "warning", "error" }) FfmpegChip.Classes.Set(kind, kind == chipKind);

        FfmpegStatus.Text = state switch
        {
            FfmpegState.Module when moduleBytes > 0 => Loc.T("Settings.Ffmpeg.VersionSize", version, moduleBytes / 1_000_000.0),
            FfmpegState.Module or FfmpegState.Path or FfmpegState.System => Loc.T("Settings.Ffmpeg.Version", version),
            FfmpegState.NotInstalled => Loc.T("Settings.Ffmpeg.NotInstalledShort"),
            FfmpegState.Missing => Loc.T("Settings.Ffmpeg.MissingShort"),
            FfmpegState.Broken => Loc.T("Settings.Ffmpeg.Broken"),
            FfmpegState.Installing => FfmpegModule.ProgressText(0, FfmpegModule.Package.Size),
            _ => Loc.T("Settings.Ffmpeg.Checking"),
        };
        // The whole build name ("n8.1.3-9-g29e619e767-20260930") for those who need it.
        ToolTip.SetTip(FfmpegStatus, ffmpeg is { Version.Length: > 0 } ? Loc.T("Settings.Ffmpeg.FullVersion", ffmpeg.Version) : null);

        string? location = state switch
        {
            FfmpegState.Module or FfmpegState.Broken => Loc.T("Settings.Ffmpeg.ModuleFolder", FfmpegModule.Folder),
            FfmpegState.Path or FfmpegState.System when ffmpeg != null => Loc.T("Settings.Ffmpeg.Location", ffmpeg.Path),
            _ => null,
        };
        FfmpegLocation.IsVisible = location != null;
        FfmpegLocation.Text = location;
        ToolTip.SetTip(FfmpegLocation, location);

        FfmpegError.IsVisible = error != null;
        FfmpegError.Text = error;
        FfmpegProgress.IsVisible = state == FfmpegState.Installing;

        // An older module than the one this version downloads can be replaced.
        bool outdated = state == FfmpegState.Module && FfmpegModule.InstalledVersion() != FfmpegModule.Package.Version;
        bool install = module && (state is FfmpegState.NotInstalled or FfmpegState.Broken or FfmpegState.Path || outdated);
        FfmpegInstallButton.Content = state is FfmpegState.Broken || outdated
            ? Loc.T("Settings.Ffmpeg.Reinstall")
            : Loc.T("Settings.Ffmpeg.InstallSize", FfmpegModule.DownloadMegabytes);
        FfmpegInstallButton.IsVisible = install;
        FfmpegUninstallButton.IsVisible = module && state is FfmpegState.Module or FfmpegState.Broken;
        FfmpegCancelButton.IsVisible = state == FfmpegState.Installing;

        bool formats = state is not (FfmpegState.Checking or FfmpegState.Installing);
        FfmpegFormatsRow.IsVisible = formats;
        FfmpegRow.Classes.Set("last", !formats);
        if (formats) ShowFfmpegFormats(ffmpeg);
    }

    /// <summary>
    /// A chip per export format: ticked when this computer makes it (the encoder as tooltip), greyed with the reason
    /// otherwise (no ffmpeg, an encoder missing in Fedora's ffmpeg-free...).
    /// </summary>
    private void ShowFfmpegFormats(FfmpegInfo? ffmpeg)
    {
        var video = AppServices.HasPlatform ? AppServices.Platform.Video : null;
        bool windows = OperatingSystem.IsWindows();
        var offered = WebVideoExport.Offered(c => video?.CanTranscode(c) == true, ffmpeg, windows);
        var chips = WebFormats.All.Select(format =>
            offered.FirstOrDefault(a => a.Format == format)
            ?? new WebFormatAvailability(format, WebEncoderRoute.Unavailable, Loc.T(windows ? "Player.Web.NeedsModule" : "Player.Web.NeedsFfmpeg")));
        FfmpegFormatChips.ItemsSource = chips.Select(FormatChip).ToList();
    }

    private Border FormatChip(WebFormatAvailability format)
    {
        var name = Loc.T("Player.Web.Format." + WebFormats.Id(format.Format));
        var icon = new Avalonia.Controls.Shapes.Path { Data = Resource<Geometry>(format.IsAvailable ? "IconCheck" : "IconClose") };
        icon.Classes.Add("chip-icon");
        var chip = new Border
        {
            Margin = new Thickness(0, 0, 6, 6),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, new TextBlock { Text = name } } },
        };
        chip.Classes.Add("chip");
        if (!format.IsAvailable) chip.Classes.Add("unavailable");
        ToolTip.SetTip(chip, format.IsAvailable
            ? format.Encoder != null ? Loc.T("Settings.Ffmpeg.EncoderTip", format.Encoder) : Loc.T("Settings.Ffmpeg.SystemEncoderTip")
            : format.Reason);
        return chip;
    }

    private T? Resource<T>(string key) where T : class => this.TryFindResource(key, ActualThemeVariant, out var value) ? value as T : null;

    private async void OnInstallFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (_ffmpegDownload != null) return;
        using var cancel = new CancellationTokenSource();
        _ffmpegDownload = cancel;
        var package = FfmpegModule.Package;
        ShowFfmpeg(FfmpegState.Installing, null);
        FfmpegProgress.IsIndeterminate = false;
        ShowFfmpegProgress(0, package.Size);
        string? failure;
        try
        {
            var progress = new Progress<long>(received =>
            {
                if (_ffmpegDownload != cancel) return;
                ShowFfmpegProgress(received, package.Size);
                // The last bytes are in: unzipping follows.
                if (received >= package.Size)
                {
                    FfmpegProgress.IsIndeterminate = true;
                    FfmpegStatus.Text = Loc.T("Settings.Ffmpeg.Unpacking");
                }
            });
            // Cancelled: back to the state before.
            failure = await FfmpegModule.TryInstallAsync(progress, cancel.Token);
        }
        finally
        {
            if (_ffmpegDownload == cancel) _ffmpegDownload = null;
            FfmpegProgress.IsIndeterminate = false;
        }
        if (!IsVisible) return;
        await RefreshFfmpegAsync();
        if (failure != null) ShowFfmpegError(failure);
    }

    private void ShowFfmpegProgress(long received, long total)
    {
        FfmpegProgress.Value = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
        FfmpegStatus.Text = FfmpegModule.ProgressText(received, total);
    }

    private void ShowFfmpegError(string message)
    {
        FfmpegError.Text = message;
        FfmpegError.IsVisible = true;
    }

    private void OnCancelFfmpeg(object? sender, RoutedEventArgs e) => _ffmpegDownload?.Cancel();

    private async void OnUninstallFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (await Dialogs.ShowAsync(this, Loc.T("Settings.Ffmpeg.UninstallConfirm"), Loc.T("Settings.Ffmpeg.Title"), DialogButtons.OkCancel,
                DialogIcon.Question, DialogResult.Cancel) != DialogResult.Ok)
            return;
        string? failure = null;
        try
        {
            await Task.Run(() => FfmpegModule.Uninstall());
        }
        catch (Exception ex)
        {
            // An export or a conversion may still be running with it (its files are in use).
            ErrorLog.Write("Désinstallation du module ffmpeg", ex);
            failure = Loc.T("Settings.Ffmpeg.UninstallFailed");
        }
        await RefreshFfmpegAsync();
        if (failure != null) ShowFfmpegError(failure);
    }

    /// <summary>"More formats" of the format row: the module card, in view.</summary>
    private void OnShowFfmpegCard(object? sender, RoutedEventArgs e) => FfmpegCard.BringIntoView();
}
