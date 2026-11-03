using System.Globalization;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Player;

/// <summary>
/// The fields of the "Export / compress" dialog, checked and turned into <see cref="WebExportSettings"/>: kept out of
/// the window so that the rules (formats available, sizes typed, what the plan will be) can be tested.
/// </summary>
public sealed class WebExportForm
{
    /// <param name="inPoint">Start of the extract chosen in the player, if any.</param>
    /// <param name="outPoint">End of the extract chosen in the player, if any.</param>
    /// <param name="formats">Availability of each format on this computer (<see cref="WebVideoExport.Availability"/>).</param>
    public WebExportForm(TimeSpan duration, TimeSpan? inPoint, TimeSpan? outPoint, VideoInfo source, IReadOnlyList<WebFormatAvailability> formats)
    {
        Duration = duration;
        HasSelection = inPoint != null || outPoint != null;
        In = inPoint ?? TimeSpan.Zero;
        Out = outPoint ?? duration;
        Source = source;
        Formats = formats;
        UseSelection = HasSelection;
    }

    public TimeSpan Duration { get; }
    public bool HasSelection { get; }
    public TimeSpan In { get; }
    public TimeSpan Out { get; }
    public VideoInfo Source { get; }
    /// <summary>The formats listed (<see cref="WebVideoExport.Offered"/>); replaced once the ffmpeg module is installed.</summary>
    public IReadOnlyList<WebFormatAvailability> Formats { get; set; }

    public bool UseSelection { get; set; }
    public WebFormat Format { get; set; } = WebFormat.Mp4H264;
    public WebQuality Quality { get; set; } = WebQuality.Medium;
    /// <summary>One of <see cref="SizeTargets"/>.</summary>
    public string TargetId { get; set; } = SizeTargets.Off;
    /// <summary>The size typed, in MB (the field shows the preset's own size).</summary>
    public string MegabytesText { get; set; } = "10";

    public bool IsAvailable(WebFormat format) => Formats.FirstOrDefault(f => f.Format == format)?.IsAvailable == true;

    /// <summary>The format remembered if it is available here, else MP4 H.264.</summary>
    public WebFormat Available(WebFormat format) => IsAvailable(format) ? format : WebFormat.Mp4H264;

    /// <summary>Text of the size field for a preset ("10"), in the interface's culture.</summary>
    public static string MegabytesFor(double megabytes) => megabytes.ToString("0.##", Loc.Culture);

    /// <summary>A size typed with a comma or a point, in MB.</summary>
    public static bool TryParseMegabytes(string text, out double megabytes)
    {
        text = text.Trim();
        return (double.TryParse(text, NumberStyles.Float, Loc.Culture, out megabytes)
                || double.TryParse(text.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out megabytes))
               && double.IsFinite(megabytes);
    }

    /// <summary>Settings typed, or the reason (ready to show) why they are not acceptable.</summary>
    public bool TryRead(out WebExportSettings settings, out string error)
    {
        settings = null!;
        error = "";
        if (!IsAvailable(Format))
        {
            error = Formats.FirstOrDefault(f => f.Format == Format)?.Reason ?? "";
            return false;
        }
        long? target = null;
        if (TargetId != SizeTargets.Off)
        {
            if (!TryParseMegabytes(MegabytesText, out var megabytes) || megabytes < SizeTargets.MinMegabytes || megabytes > SizeTargets.MaxMegabytes)
            {
                error = Loc.T("Player.Web.SizeError", SizeTargets.MinMegabytes, SizeTargets.MaxMegabytes);
                return false;
            }
            target = (long)(megabytes * SizeTargets.BytesPerMegabyte);
        }
        bool selection = UseSelection && HasSelection;
        var start = selection ? In : TimeSpan.Zero;
        var end = selection ? Out : Duration;
        settings = new WebExportSettings(start, end, Format, Quality, target, TargetId);
        if (WebVideoExport.Plan(settings, Source, out var refusal) == null)
        {
            error = refusal;
            return false;
        }
        return true;
    }

    /// <summary>What the export will make ("1280 × 720 · 30 fps · 1 450 kbit/s"), or "" when the fields are not valid.</summary>
    public string Summary()
    {
        if (!TryRead(out var settings, out _)) return "";
        var plan = WebVideoExport.Plan(settings, Source, out _)!;
        var length = WebVideoExport.FormatDuration(settings.End - settings.Start);
        if (settings.TargetBytes is { } target)
        {
            if (settings.Format == WebFormat.WebP)
                return Loc.T("Player.Web.SummaryWebPTarget", plan.Width, plan.Height, plan.FramesPerSecond, length, WebVideoExport.Megabytes(target));
            return Loc.T("Player.Web.SummaryTarget", plan.Width, plan.Height, plan.FramesPerSecond, length,
                (plan.VideoBitrate + plan.AudioBitrate) / 1000.0, WebVideoExport.Megabytes(target));
        }
        return Loc.T("Player.Web.Summary", plan.Width, plan.Height, plan.FramesPerSecond, length);
    }
}
