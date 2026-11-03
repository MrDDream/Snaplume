using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>
/// "Export / compress": format (MP4 H.264; with ffmpeg also HEVC, MP4 AV1, WebM, animated WebP), and a file size to
/// stay under (Discord, e-mail, typed) or a quality. Without ffmpeg, the module can be downloaded from the dialog, which
/// then lists every format. Shown with <c>ShowDialog&lt;WebExportSettings?&gt;</c>: the settings, or null if cancelled.
/// The rules are in <see cref="WebExportForm"/>; the last choices are kept in the settings.
/// </summary>
public partial class WebExportDialog : Window
{
    /// <summary>An item of a combo box: what it stands for, its text, and why it is greyed (tooltip).</summary>
    private sealed record Option(object Value, string Name, bool Enabled = true, string Tip = "")
    {
        public override string ToString() => Name;
    }

    private static readonly WebQuality[] Qualities = { WebQuality.High, WebQuality.Medium, WebQuality.Light };
    private static readonly string[] Targets = { SizeTargets.Off, SizeTargets.Discord, SizeTargets.Nitro, SizeTargets.Email, SizeTargets.Custom };

    private readonly WebExportForm _form;
    private readonly bool _ready;
    /// <summary>The size field is being filled from the list: it does not switch the list to "custom".</summary>
    private bool _fillingSize;
    /// <summary>The items of the format list, in its order.</summary>
    private List<Option> _formatOptions = new();
    /// <summary>Download of the ffmpeg module in progress, cancelled with its button or by closing the dialog.</summary>
    private CancellationTokenSource? _download;
    private bool _closed;

    /// <summary>For the XAML previewer only.</summary>
    public WebExportDialog() : this(TimeSpan.FromSeconds(30), null, null, new VideoInfo(1920, 1080, 30, true, TimeSpan.FromSeconds(30)),
        WebFormats.All.Select(f => new WebFormatAvailability(f, WebEncoderRoute.System, "")).ToList(), hasFfmpeg: true)
    {
    }

    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    /// <param name="formats">The formats to list and what this computer can make of them (<see cref="WebVideoExport.Offered"/>).</param>
    /// <param name="hasFfmpeg">
    /// An ffmpeg was found. Without one, only MP4 H.264 is listed, and the other formats are offered: the module,
    /// downloaded from the dialog (Windows), or the system's package (Linux).
    /// </param>
    public WebExportDialog(TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, VideoInfo source, IReadOnlyList<WebFormatAvailability> formats,
        bool hasFfmpeg)
    {
        InitializeComponent();
        _form = new WebExportForm(duration, inPoint, outPoint, source, formats);
        var settings = AppServices.Settings;

        var ranges = new List<Option> { new(false, Loc.T("Player.Gif.RangeAll", PlayerWindow.Format(duration))) };
        if (_form.HasSelection)
            ranges.Add(new Option(true, Loc.T("Player.Gif.RangeSelection", PlayerWindow.Format(_form.In), PlayerWindow.Format(_form.Out))));
        RangeBox.ItemsSource = ranges;
        RangeBox.SelectedItem = ranges[^1];

        // Greyed formats keep their place, with the reason as tooltip (shown on disabled items too).
        FormatBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem item || e.Index < 0 || e.Index >= _formatOptions.Count) return;
            var option = _formatOptions[e.Index];
            item.IsEnabled = option.Enabled;
            ToolTip.SetTip(item, option.Tip.Length > 0 ? option.Tip : null);
            ToolTip.SetShowOnDisabled(item, true);
        };
        ModuleSizeText.Text = Loc.T("Player.Web.DownloadModule", FfmpegModule.DownloadMegabytes);
        ModuleNote.Text = Loc.T("Player.Web.MoreFormatsPackage");
        ShowFormats(formats, hasFfmpeg, WebFormats.FromId(settings.WebExportFormat));

        var targets = Targets.Select(t => new Option(t, t == SizeTargets.Off || t == SizeTargets.Custom
            ? Loc.T("Player.Web.Target." + t)
            : Loc.T("Player.Web.Target." + t, SizeTargets.Megabytes(t)))).ToList();
        TargetBox.ItemsSource = targets;
        TargetBox.SelectedItem = targets.FirstOrDefault(t => (string)t.Value == settings.WebExportTarget) ?? targets[0];
        MegabytesBox.Text = WebExportForm.MegabytesFor(SizeTargets.Megabytes(settings.WebExportTarget) is > 0 and var preset
            ? preset
            : settings.WebExportMegabytes);

        var qualities = Qualities.Select(q => new Option(q, Loc.T("Player.Web.Quality." + WebFormats.QualityId(q)))).ToList();
        QualityBox.ItemsSource = qualities;
        QualityBox.SelectedItem = qualities.FirstOrDefault(q => (WebQuality)q.Value == WebFormats.QualityFromId(settings.WebExportQuality)) ?? qualities[1];

        Closed += (_, _) =>
        {
            _closed = true;
            _download?.Cancel();
        };
        RangeBox.SelectionChanged += (_, _) => Update();
        FormatBox.SelectionChanged += (_, _) => Update();
        QualityBox.SelectionChanged += (_, _) => Update();
        TargetBox.SelectionChanged += (_, _) => OnTargetChanged();
        MegabytesBox.TextChanged += (_, _) => OnSizeTyped();
        _ready = true;
        Update();
    }

    private string SelectedTarget => (TargetBox.SelectedItem as Option)?.Value as string ?? SizeTargets.Off;

    /// <summary>A preset puts its size in the field; "custom" keeps what is there.</summary>
    private void OnTargetChanged()
    {
        if (!_ready) return;
        if (SizeTargets.Megabytes(SelectedTarget) is > 0 and var megabytes)
        {
            _fillingSize = true;
            MegabytesBox.Text = WebExportForm.MegabytesFor(megabytes);
            _fillingSize = false;
        }
        Update();
    }

    /// <summary>Typing another size than the preset's makes it a custom size (e.g. 20 MB for a Discord server).</summary>
    private void OnSizeTyped()
    {
        if (!_ready || _fillingSize) return;
        var target = SelectedTarget;
        if (SizeTargets.Megabytes(target) is > 0 and var preset
            && !(WebExportForm.TryParseMegabytes(MegabytesBox.Text ?? "", out var typed) && Math.Abs(typed - preset) < 0.001))
        {
            TargetBox.SelectedItem = TargetBox.Items.OfType<Option>().First(o => (string)o.Value == SizeTargets.Custom);
            return; // Update() follows the selection change
        }
        Update();
    }

    /// <summary>Copies the fields into the form.</summary>
    private WebExportForm Read()
    {
        _form.UseSelection = (RangeBox.SelectedItem as Option)?.Value is true;
        _form.Format = (FormatBox.SelectedItem as Option)?.Value as WebFormat? ?? WebFormat.Mp4H264;
        _form.Quality = (QualityBox.SelectedItem as Option)?.Value as WebQuality? ?? WebQuality.Medium;
        _form.TargetId = SelectedTarget;
        _form.MegabytesText = MegabytesBox.Text ?? "";
        return _form;
    }

    /// <summary>Note on the format, what the export will make, or why it cannot.</summary>
    private void Update()
    {
        if (!_ready) return;
        var form = Read();
        bool target = form.TargetId != SizeTargets.Off;
        MegabytesPanel.IsVisible = target;
        QualityPanel.IsVisible = !target;
        FormatNote.Text = Loc.T("Player.Web.Note." + WebFormats.Id(form.Format));
        if (form.TryRead(out _, out var error))
        {
            SummaryText.Text = form.Summary();
            SummaryText.IsVisible = true;
            ErrorText.IsVisible = false;
        }
        else
        {
            SummaryText.IsVisible = false;
            ErrorText.Text = error;
            ErrorText.IsVisible = error.Length > 0;
        }
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (!Read().TryRead(out var result, out var error))
        {
            ErrorText.Text = error;
            ErrorText.IsVisible = true;
            return;
        }
        Remember(result);
        Close(result);
    }

    /// <summary>The choices come back the next time, also after a restart.</summary>
    private void Remember(WebExportSettings result)
    {
        var settings = AppServices.Settings;
        settings.WebExportFormat = WebFormats.Id(result.Format);
        settings.WebExportQuality = WebFormats.QualityId(result.Quality);
        settings.WebExportTarget = result.TargetId;
        if (result.TargetId == SizeTargets.Custom && result.TargetBytes is { } bytes) settings.WebExportMegabytes = WebVideoExport.Megabytes(bytes);
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // The choices still apply until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    /// <summary>
    /// Fills the format list (the remembered format if it is offered and available, else MP4 H.264) and shows, without
    /// ffmpeg, how to get the other formats.
    /// </summary>
    private void ShowFormats(IReadOnlyList<WebFormatAvailability> formats, bool hasFfmpeg, WebFormat preferred)
    {
        _form.Formats = formats;
        _formatOptions = formats.Select(a =>
        {
            var name = Loc.T("Player.Web.Format." + WebFormats.Id(a.Format));
            return new Option(a.Format, a.IsAvailable ? name : Loc.T("Player.Web.Unavailable", name), a.IsAvailable, a.Reason);
        }).ToList();
        FormatBox.ItemsSource = _formatOptions;
        var format = _form.Available(preferred);
        FormatBox.SelectedItem = _formatOptions.FirstOrDefault(o => (WebFormat)o.Value == format) ?? _formatOptions.FirstOrDefault();

        bool module = FfmpegModule.IsSupported;
        MoreFormatsPanel.IsVisible = !hasFfmpeg;
        ModuleButton.IsVisible = !hasFfmpeg && module && _download == null;
        ModuleNote.IsVisible = !hasFfmpeg && !module;
        ModuleError.IsVisible = false;
    }

    /// <summary>Downloads and installs the ffmpeg module (progress and Cancel in the dialog), then lists every format.</summary>
    private async void OnModuleClick(object? sender, RoutedEventArgs e)
    {
        if (_download != null) return;
        using var cancel = new CancellationTokenSource();
        _download = cancel;
        long size = FfmpegModule.Package.Size;
        ModuleButton.IsVisible = false;
        ModuleError.IsVisible = false;
        ModuleProgress.IsIndeterminate = false;
        ShowModuleProgress(0, size);
        ModuleProgressPanel.IsVisible = true;
        string? failure;
        try
        {
            var progress = new Progress<long>(received =>
            {
                if (_download != cancel) return;
                ShowModuleProgress(received, size);
                // The last bytes are in: unzipping follows.
                if (received >= size)
                {
                    ModuleProgress.IsIndeterminate = true;
                    ModuleStatus.Text = Loc.T("Settings.Ffmpeg.Unpacking");
                }
            });
            failure = await FfmpegModule.TryInstallAsync(progress, cancel.Token);
        }
        finally
        {
            if (_download == cancel) _download = null;
        }
        if (_closed) return;
        if (failure == null && !cancel.IsCancellationRequested && !await RefreshFormatsAsync())
            failure = Loc.T("Settings.Ffmpeg.Broken"); // installed, but it does not start
        if (_closed) return;
        ModuleProgressPanel.IsVisible = false;
        ModuleButton.IsVisible = MoreFormatsPanel.IsVisible;
        if (failure != null)
        {
            ModuleError.Text = failure;
            ModuleError.IsVisible = true;
        }
    }

    private void ShowModuleProgress(long received, long total)
    {
        ModuleProgress.Value = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
        ModuleStatus.Text = FfmpegModule.ProgressText(received, total);
    }

    private void OnCancelModule(object? sender, RoutedEventArgs e) => _download?.Cancel();

    /// <summary>Looks for ffmpeg again (after the installation) and lists what it can make; false if there is still none.</summary>
    private async Task<bool> RefreshFormatsAsync()
    {
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
        var video = AppServices.HasPlatform ? AppServices.Platform.Video : null;
        var formats = await Task.Run(() => WebVideoExport.Offered(c => video?.CanTranscode(c) == true, ffmpeg, OperatingSystem.IsWindows()));
        if (_closed) return ffmpeg != null;
        ShowFormats(formats, ffmpeg != null, (FormatBox.SelectedItem as Option)?.Value as WebFormat? ?? WebFormat.Mp4H264);
        Update();
        return ffmpeg != null;
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
