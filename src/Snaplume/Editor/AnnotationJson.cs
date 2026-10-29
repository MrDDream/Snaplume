using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Windows;
using System.Windows.Media;

namespace Snaplume.Editor;

/// <summary>Content of the sidecar file keeping annotations editable.</summary>
public sealed class SidecarData
{
    /// <summary>Format written by this version; a newer file is ignored rather than misread.</summary>
    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;
    /// <summary>Flattened image the annotations belong to; a mismatch means it was modified elsewhere.</summary>
    public long FileLength { get; set; }
    public DateTime FileWriteTimeUtc { get; set; }
    /// <summary>Length of the base image file (0 in older files): detects a base saved without its annotations.</summary>
    public long BaseFileLength { get; set; }
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

    private sealed class ColorJsonConverter : JsonConverter<Color>
    {
        public override Color Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            try
            {
                return (Color)ColorConverter.ConvertFromString(reader.GetString() ?? "#FFFF0000");
            }
            catch (FormatException ex)
            {
                throw new JsonException("Couleur invalide", ex);
            }
        }

        public override void Write(Utf8JsonWriter writer, Color value, JsonSerializerOptions options) =>
            writer.WriteStringValue($"#{value.A:X2}{value.R:X2}{value.G:X2}{value.B:X2}");
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
