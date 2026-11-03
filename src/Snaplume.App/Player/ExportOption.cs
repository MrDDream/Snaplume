using Snaplume.Core;

namespace Snaplume.Player;

/// <summary>An item of a combo box of the export dialogs: what it stands for, its text, and why it is greyed (tooltip).</summary>
public sealed record ExportOption(object Value, string Name, bool Enabled = true, string Tip = "")
{
    public override string ToString() => Name;

    /// <summary>
    /// The parts of the video an export may take: the whole video, and the extract chosen in the player when there is
    /// one (the last item, selected by default). The value is true for the extract.
    /// </summary>
    /// <param name="selection">Start and end of the extract, null without one.</param>
    public static List<ExportOption> Ranges(TimeSpan duration, (TimeSpan In, TimeSpan Out)? selection)
    {
        var ranges = new List<ExportOption> { new(false, Loc.T("Player.Export.RangeAll", PlayerWindow.Format(duration))) };
        if (selection is var (start, end))
            ranges.Add(new ExportOption(true, Loc.T("Player.Export.RangeSelection", PlayerWindow.Format(start), PlayerWindow.Format(end))));
        return ranges;
    }
}
