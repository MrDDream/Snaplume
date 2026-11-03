using Snaplume.Core;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Media;

/// <summary>
/// Microphones of the PulseAudio or PipeWire sound server, as "pactl list sources" lists them (the monitors of the
/// outputs left out: they are the system sound). The name is what pulsesrc takes as its device.
/// </summary>
internal static class AudioInputs
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);

    /// <summary>The microphones; empty when pactl is missing or the sound server does not answer.</summary>
    public static IReadOnlyList<AudioDeviceInfo> List()
    {
        var pactl = ExternalTools.Find("pactl");
        if (pactl == null) return Array.Empty<AudioDeviceInfo>();
        try
        {
            var result = Task.Run(() => ExternalTools.RunAsync(pactl, new[] { "list", "sources" }, Timeout, environment: ExternalTools.CLocale))
                .GetAwaiter().GetResult();
            if (!result.Succeeded)
            {
                ErrorLog.Write("Liste des microphones", result.Errors);
                return Array.Empty<AudioDeviceInfo>();
            }
            return Parse(result.Text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des microphones", ex);
            return Array.Empty<AudioDeviceInfo>();
        }
    }

    /// <summary>Reads the output of "LC_ALL=C pactl list sources": one block per source, "Source #N" then indented fields.</summary>
    internal static IReadOnlyList<AudioDeviceInfo> Parse(string output)
    {
        var inputs = new List<AudioDeviceInfo>();
        string? name = null, description = null;
        bool monitor = false;

        void Flush()
        {
            if (name != null && !monitor && !name.EndsWith(".monitor", StringComparison.Ordinal))
                inputs.Add(new AudioDeviceInfo(name, string.IsNullOrWhiteSpace(description) ? name : description));
            name = description = null;
            monitor = false;
        }

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("Source #", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }
            if (Field(line, "Name:") is { } value) name = value;
            else if (Field(line, "Description:") is { } text) description = text;
            else if (Field(line, "Monitor of Sink:") is { } sink) monitor = sink != "n/a";
        }
        Flush();
        return inputs;
    }

    private static string? Field(string line, string label) =>
        line.StartsWith(label, StringComparison.Ordinal) ? line[label.Length..].Trim() : null;
}
