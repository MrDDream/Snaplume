using System.Globalization;
using Snaplume.Imaging;

namespace Snaplume.Core;

public enum CaptureKind
{
    /// <summary>Interactive: drag an area or click a window / control.</summary>
    Region,
    /// <summary>Monitor under the mouse cursor.</summary>
    Screen,
    /// <summary>All monitors.</summary>
    AllScreens,
    ActiveWindow,
    /// <summary>Same area as the previous region capture.</summary>
    RepeatLast,
    /// <summary>Interactive selection, then the area is scrolled and stitched into one tall image.</summary>
    Scrolling,
}

/// <summary>
/// Command line: [--tray] [--capture region|screen|all|window|repeat|scrolling|text|qr] [--delay N]
/// [--record region|screen] [--duration N] [--workspace name] [--exit] [--settings] [--window] [image to edit or video to play]
/// </summary>
/// <remarks>When Snaplume is already running, the command line is run by that instance ("--exit" is then ignored).</remarks>
public sealed record CommandLine(bool Tray, CaptureKind? Capture, int DelaySeconds, bool Exit, string? EditPath,
    string? Record, int DurationSeconds, bool Settings, bool Text, string? Workspace, bool Window = false, bool Qr = false)
{
    /// <summary>
    /// The option at <paramref name="i"/> takes the next argument as its value: always for --capture, --record and
    /// --workspace, only a number for --delay and --duration (else the next argument is read on its own). Shared by
    /// <see cref="Parse"/> and <see cref="WithFullPaths"/>, so that both see the same file paths.
    /// </summary>
    private static bool TakesValue(string[] args, int i) =>
        i + 1 < args.Length && args[i].ToLowerInvariant() switch
        {
            "--capture" or "--record" or "--workspace" => true,
            "--delay" or "--duration" => TryParseInt(args[i + 1], out _),
            _ => false,
        };

    public static CommandLine Parse(string[] args)
    {
        bool tray = false, exit = false, settings = false, text = false, window = false, qr = false;
        CaptureKind? capture = null;
        int delay = 0, duration = 0;
        string? editPath = null, record = null, workspace = null;
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--tray": tray = true; break;
                case "--exit": exit = true; break;
                case "--delay" when TakesValue(args, i):
                    TryParseInt(args[++i], out var d);
                    delay = Math.Clamp(d, 0, 60);
                    break;
                case "--capture" when TakesValue(args, i):
                    var kind = args[++i].ToLowerInvariant();
                    switch (kind)
                    {
                        case "text": text = true; break;
                        case "qr": qr = true; break;
                        case "scrolling" or "scroll": capture = CaptureKind.Scrolling; break;
                        case "region": capture = CaptureKind.Region; break;
                        case "screen": capture = CaptureKind.Screen; break;
                        case "all": capture = CaptureKind.AllScreens; break;
                        case "window": capture = CaptureKind.ActiveWindow; break;
                        case "repeat": capture = CaptureKind.RepeatLast; break;
                        default: ErrorLog.Write("Ligne de commande", $"--capture {kind} : mode inconnu, ignoré"); break;
                    }
                    break;
                case "--record" when TakesValue(args, i):
                    var area = args[++i].ToLowerInvariant();
                    if (area is "screen" or "region") record = area;
                    else ErrorLog.Write("Ligne de commande", $"--record {area} : mode inconnu, ignoré");
                    break;
                case "--duration" when TakesValue(args, i):
                    TryParseInt(args[++i], out var s);
                    duration = Math.Clamp(s, 1, 24 * 3600);
                    break;
                case "--workspace" when TakesValue(args, i):
                    workspace = args[++i].Trim();
                    break;
                case "--settings": settings = true; break;
                case "--window": window = true; break;
                case "--restarted": break;
                default:
                    if (File.Exists(args[i]) && ImageIO.IsCapture(args[i])) editPath = Path.GetFullPath(args[i]);
                    else ErrorLog.Write("Ligne de commande", $"{args[i]} : argument ignoré");
                    break;
            }
        }
        return new CommandLine(tray, capture, delay, exit, editPath, record, duration, settings, text, workspace, window, qr);
    }

    /// <summary>The arguments with file paths made absolute, for the instance already running (other current folder).</summary>
    public static string[] WithFullPaths(string[] args)
    {
        var result = (string[])args.Clone();
        for (int i = 0; i < result.Length; i++)
        {
            if (TakesValue(result, i))
            {
                i++;
                continue;
            }
            if (result[i].StartsWith("--", StringComparison.Ordinal)) continue;
            try
            {
                result[i] = Path.GetFullPath(result[i]);
            }
            catch (Exception)
            {
                // Not a path: sent as is, the running instance ignores it.
            }
        }
        return result;
    }

    private static bool TryParseInt(string text, out int value) =>
        int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
