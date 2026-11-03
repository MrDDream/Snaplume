using Avalonia.Controls;
using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>
/// "Export / compress": format (MP4 H.264 or HEVC, MP4 AV1, WebM, animated WebP), and a file size to stay under
/// (Discord, e-mail, typed) or a quality. Shown with <c>ShowDialog&lt;WebExportSettings?&gt;</c>: the settings, or
/// null if cancelled. The rules are in <see cref="WebExportForm"/>; the last choices are kept in the settings.
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

    /// <summary>For the XAML previewer only.</summary>
    public WebExportDialog() : this(TimeSpan.FromSeconds(30), null, null, new VideoInfo(1920, 1080, 30, true, TimeSpan.FromSeconds(30)),
        WebFormats.All.Select(f => new WebFormatAvailability(f, WebEncoderRoute.System, "")).ToList(), showModuleHint: false)
    {
    }

    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    /// <param name="formats">What this computer can make (<see cref="WebVideoExport.Availability"/>).</param>
    /// <param name="showModuleHint">Windows without ffmpeg: offers the module of the settings.</param>
    public WebExportDialog(TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, VideoInfo source, IReadOnlyList<WebFormatAvailability> formats,
        bool showModuleHint)
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
        var formatOptions = WebFormats.All.Select(f =>
        {
            var availability = formats.FirstOrDefault(a => a.Format == f);
            bool enabled = availability?.IsAvailable == true;
            var name = Loc.T("Player.Web.Format." + WebFormats.Id(f));
            return new Option(f, enabled ? name : Loc.T("Player.Web.Unavailable", name), enabled, availability?.Reason ?? "");
        }).ToList();
        FormatBox.ContainerPrepared += (_, e) =>
        {
            if (e.Container is not ComboBoxItem item || e.Index < 0 || e.Index >= formatOptions.Count) return;
            var option = formatOptions[e.Index];
            item.IsEnabled = option.Enabled;
            ToolTip.SetTip(item, option.Tip.Length > 0 ? option.Tip : null);
            ToolTip.SetShowOnDisabled(item, true);
        };
        FormatBox.ItemsSource = formatOptions;
        var format = _form.Available(WebFormats.FromId(settings.WebExportFormat));
        FormatBox.SelectedItem = formatOptions.First(o => (WebFormat)o.Value == format);

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
        QualityBox.SelectedItem = qualities.First(q => (WebQuality)q.Value == WebFormats.QualityFromId(settings.WebExportQuality));

        ModuleHint.IsVisible = showModuleHint;

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

    /// <summary>Opens Settings › Video, where the module is installed; the dialog closes (its formats would be out of date).</summary>
    private void OnModuleClick(object? sender, RoutedEventArgs e)
    {
        Close(null);
        App.Instance.ShowSettings("PageVideo");
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
