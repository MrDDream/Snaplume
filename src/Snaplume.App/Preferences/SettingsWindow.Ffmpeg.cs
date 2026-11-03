using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Player;
using Snaplume.Ui;

namespace Snaplume.Preferences;

/// <summary>
/// Settings › Video › "Web formats module (ffmpeg)": which ffmpeg the exports use and what it can make; on Windows,
/// the download (with progress and Cancel) and the removal of the module (<see cref="FfmpegModule"/>). Applied at
/// once, like the update check: Save and Cancel of the window do not concern it.
/// </summary>
public partial class SettingsWindow
{
    /// <summary>Download in progress, cancelled with the button or by closing the window.</summary>
    private CancellationTokenSource? _ffmpegDownload;

    /// <summary>Shows the ffmpeg found (looked for again after an install or a removal).</summary>
    private async Task RefreshFfmpegAsync()
    {
        FfmpegStatus.Text = Loc.T("Settings.Ffmpeg.Checking");
        FfmpegFormats.IsVisible = false;
        SetFfmpegButtons(install: false, uninstall: false, cancel: false);
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
        if (_ffmpegDownload != null) return;

        bool module = FfmpegModule.IsSupported;
        bool installed = module && FfmpegModule.IsInstalled;
        FfmpegStatus.Text = ffmpeg?.Source switch
        {
            FfmpegSource.Module => Loc.T("Settings.Ffmpeg.Installed", Version(ffmpeg)),
            FfmpegSource.Path => Loc.T("Settings.Ffmpeg.InPath", Version(ffmpeg), ffmpeg.Path),
            FfmpegSource.System => Loc.T("Settings.Ffmpeg.System", Version(ffmpeg)),
            // The module exists but does not start (damaged): it can be installed again.
            _ when installed => Loc.T("Settings.Ffmpeg.Broken"),
            _ when module => Loc.T("Settings.Ffmpeg.NotInstalledAll", FfmpegModule.DownloadMegabytes),
            _ => Loc.T("Settings.Ffmpeg.MissingAll"),
        };
        // The formats are listed once there is an ffmpeg; before, the status says that only MP4 H.264 is offered.
        if (ffmpeg != null) ShowFfmpegFormats(ffmpeg);
        // An older module than the one this version downloads can be replaced.
        bool outdated = installed && FfmpegModule.InstalledVersion() != FfmpegModule.Package.Version;
        FfmpegInstallButton.Content = Loc.T(installed ? "Settings.Ffmpeg.Reinstall" : "Settings.Ffmpeg.Install");
        SetFfmpegButtons(install: module && (!installed || outdated || ffmpeg?.Source != FfmpegSource.Module), uninstall: installed, cancel: false);
    }

    private static string Version(FfmpegInfo ffmpeg) => ffmpeg.Version.Length > 0 ? ffmpeg.Version : "?";

    /// <summary>The export formats this ffmpeg makes, and those it cannot (Fedora's ffmpeg-free lacks some encoders).</summary>
    private void ShowFfmpegFormats(FfmpegInfo ffmpeg)
    {
        var video = AppServices.HasPlatform ? AppServices.Platform.Video : null;
        var formats = WebVideoExport.Offered(c => video?.CanTranscode(c) == true, ffmpeg, OperatingSystem.IsWindows());
        string Names(IEnumerable<WebFormatAvailability> list) =>
            string.Join(", ", list.Select(a => Loc.T("Player.Web.Format." + WebFormats.Id(a.Format))));
        var available = formats.Where(a => a.IsAvailable).ToList();
        var missing = formats.Where(a => !a.IsAvailable).ToList();
        var text = Loc.T("Settings.Ffmpeg.Formats", Names(available));
        if (missing.Count > 0) text += "\n" + Loc.T("Settings.Ffmpeg.FormatsMissing", Names(missing));
        FfmpegFormats.Text = text;
        FfmpegFormats.IsVisible = true;
    }

    private void SetFfmpegButtons(bool install, bool uninstall, bool cancel)
    {
        FfmpegInstallButton.IsVisible = install;
        FfmpegUninstallButton.IsVisible = uninstall;
        FfmpegCancelButton.IsVisible = cancel;
    }

    private async void OnInstallFfmpeg(object? sender, RoutedEventArgs e)
    {
        if (_ffmpegDownload != null) return;
        using var cancel = new CancellationTokenSource();
        _ffmpegDownload = cancel;
        var package = FfmpegModule.Package;
        SetFfmpegButtons(install: false, uninstall: false, cancel: true);
        FfmpegFormats.IsVisible = false;
        FfmpegProgress.IsIndeterminate = false;
        FfmpegProgress.Value = 0;
        FfmpegProgress.IsVisible = true;
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
            FfmpegProgress.IsVisible = false;
        }
        if (!IsVisible) return;
        await RefreshFfmpegAsync();
        if (failure != null) FfmpegStatus.Text = failure;
    }

    private void ShowFfmpegProgress(long received, long total)
    {
        FfmpegProgress.Value = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
        FfmpegStatus.Text = FfmpegModule.ProgressText(received, total);
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
            // An export may still be running with it (its files are in use).
            ErrorLog.Write("Désinstallation du module ffmpeg", ex);
            failure = Loc.T("Settings.Ffmpeg.UninstallFailed");
        }
        await RefreshFfmpegAsync();
        if (failure != null) FfmpegStatus.Text = failure;
    }
}
