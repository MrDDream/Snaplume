using Snaplume.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Wayland;

/// <summary>A PipeWire stream started by the ScreenCast portal: its node and, when given, where it lies on the desktop (logical pixels).</summary>
internal readonly record struct PortalStream(uint Node, int X, int Y, int Width, int Height, uint SourceType);

internal static class PortalStreams
{
    /// <summary>Reads the "streams" result of ScreenCast.Start / RemoteDesktop.Start: a(ua{sv}).</summary>
    public static IReadOnlyList<PortalStream> Parse(Dictionary<string, VariantValue> results)
    {
        var list = new List<PortalStream>();
        if (!results.TryGetValue("streams", out var raw)) return list;
        var streams = DBusCalls.Unwrap(raw);
        if (streams.Type != VariantValueType.Array) return list;
        for (int i = 0; i < streams.Count; i++)
        {
            var item = DBusCalls.Unwrap(streams.GetItem(i));
            if (item.Type != VariantValueType.Struct || item.Count < 2) continue;
            uint node = DBusCalls.Unwrap(item.GetItem(0)).GetUInt32();
            var properties = DBusCalls.Unwrap(item.GetItem(1));
            int x = 0, y = 0, width = 0, height = 0;
            uint sourceType = 0;
            if (properties.Type == VariantValueType.Dictionary)
            {
                for (int p = 0; p < properties.Count; p++)
                {
                    var entry = properties.GetDictionaryEntry(p);
                    var value = DBusCalls.Unwrap(entry.Value);
                    switch (entry.Key.GetString())
                    {
                        case "position" when value.Type == VariantValueType.Struct && value.Count >= 2:
                            x = value.GetItem(0).GetInt32();
                            y = value.GetItem(1).GetInt32();
                            break;
                        case "size" when value.Type == VariantValueType.Struct && value.Count >= 2:
                            width = value.GetItem(0).GetInt32();
                            height = value.GetItem(1).GetInt32();
                            break;
                        case "source_type" when value.Type == VariantValueType.UInt32:
                            sourceType = value.GetUInt32();
                            break;
                    }
                }
            }
            list.Add(new PortalStream(node, x, y, width, height, sourceType));
        }
        return list;
    }

    /// <summary>
    /// A point of the screenshot (physical pixels) in the coordinates of the stream, which are logical pixels
    /// relative to the stream's corner. <paramref name="scale"/> is screenshot pixels per logical pixel.
    /// </summary>
    public static (double X, double Y) ToStream(PortalStream stream, int x, int y, double scale)
    {
        if (scale <= 0) scale = 1;
        double sx = x / scale - stream.X, sy = y / scale - stream.Y;
        if (stream.Width > 0) sx = Math.Clamp(sx, 0, stream.Width - 1);
        if (stream.Height > 0) sy = Math.Clamp(sy, 0, stream.Height - 1);
        return (sx, sy);
    }
}
