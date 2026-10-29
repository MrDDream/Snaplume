using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Media;

namespace Snaplume.Editor;

/// <summary>Content of the sidecar file keeping annotations editable.</summary>
public sealed class SidecarData
{
    /// <summary>
    /// Format written by this version; a newer file is ignored rather than misread.
    /// 2 adds <see cref="Effects"/>; a file without effects is still written as version 1.
    /// </summary>
    public const int CurrentVersion = 2;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Flattened image the annotations belong to; a mismatch means it was modified elsewhere.</summary>
    public long FileLength { get; set; }
    public DateTime FileWriteTimeUtc { get; set; }
    /// <summary>Length of the base image file (0 in older files): detects a base saved without its annotations.</summary>
    public long BaseFileLength { get; set; }
    /// <summary>Effects of the whole image (version 2); absent in older files. Checked with <see cref="ImageEffects.IsValid"/>.</summary>
    public ImageEffects? Effects { get; set; }
    /// <summary>Read as-is from the file: null items are possible, see <see cref="Annotation.IsValid"/>.</summary>
    public List<Annotation?>? Annotations { get; set; } = new();
}

public static class AnnotationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        // Enums as names; numbers written by older versions are still read.
        Converters = { new ColorJsonConverter(), new PointConverter(), new JsonStringEnumConverter() },
        TypeInfoResolver = new DefaultJsonTypeInfoResolver { Modifiers = { IgnoreAnnotationKind } },
    };

    /// <summary>
    /// <see cref="Annotation.Kind"/> follows from the "type" discriminator and is not stored
    /// ([JsonIgnore] on the abstract property would not apply to its overrides).
    /// </summary>
    private static void IgnoreAnnotationKind(JsonTypeInfo info)
    {
        if (info.Kind != JsonTypeInfoKind.Object || !typeof(Annotation).IsAssignableFrom(info.Type)) return;
        for (int i = info.Properties.Count - 1; i >= 0; i--)
        {
            if (info.Properties[i].Name == nameof(Annotation.Kind)) info.Properties.RemoveAt(i);
        }
    }

    /// <summary>
    /// Colors as "#AARRGGBB" (also reads "#RRGGBB"). Never through <see cref="ColorConverter"/>: it also accepts
    /// "ContextColor &lt;uri&gt; ...", which makes WPF open a color profile at that address (a network path would
    /// leak the user's credentials) when a crafted sidecar or presets file is read.
    /// </summary>
    private sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("Couleur attendue");
            return HexColor.TryParse(reader.GetString(), out var color) ? color : throw new JsonException("Couleur invalide");
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue(HexColor.Format(value));
    }

    /// <summary>Points as compact [x, y] arrays.</summary>
    private sealed class PointConverter : JsonConverter<Point>
    {
        public override Point Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Point attendu");
            reader.Read();
            double x = reader.GetDouble();
            reader.Read();
            double y = reader.GetDouble();
            reader.Read();
            if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("Point attendu");
            return new Point(x, y);
        }

        public override void Write(Utf8JsonWriter writer, Point value, JsonSerializerOptions options)
        {
            writer.WriteStartArray();
            writer.WriteNumberValue(Math.Round(value.X, 2));
            writer.WriteNumberValue(Math.Round(value.Y, 2));
            writer.WriteEndArray();
        }
    }
}

/// <summary>
/// Strict "#RRGGBB" / "#AARRGGBB" colors, for everything read from a file. Parsed by hand: nothing else
/// (named colors, "sc#", "ContextColor" profiles) is accepted, and nothing is ever opened.
/// </summary>
public static class HexColor
{
    public static bool TryParse(string? text, out Color color)
    {
        color = default;
        if (text == null || (text.Length != 7 && text.Length != 9) || text[0] != '#') return false;
        uint value = 0;
        for (int i = 1; i < text.Length; i++)
        {
            int digit = text[i] switch
            {
                >= '0' and <= '9' => text[i] - '0',
                >= 'a' and <= 'f' => text[i] - 'a' + 10,
                >= 'A' and <= 'F' => text[i] - 'A' + 10,
                _ => -1,
            };
            if (digit < 0) return false;
            value = (value << 4) | (uint)digit;
        }
        if (text.Length == 7) value |= 0xFF000000;
        color = Color.FromArgb((byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value);
        return true;
    }

    /// <summary>Colors written in the code (presets, stamps); a typo fails at once.</summary>
    public static Color Parse(string text) =>
        TryParse(text, out var color) ? color : throw new FormatException($"Couleur invalide : {text}");

    public static string Format(Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";
}
