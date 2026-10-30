using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Snaplume.Imaging;

public enum TextAlign { Start, Center, End }

/// <summary>
/// Text turned into vector outlines (PDF documents): shaped by HarfBuzz (Arabic joining, ligatures, right-to-left
/// scripts), wrapped on spaces in a box of a given width, the last line ended with "…" when the text is longer.
/// The font is the interface font of the system, or one that has the characters (font fallback).
/// </summary>
public static class TextOutline
{
    /// <summary>Interface fonts tried in order: Windows, then the usual Linux ones.</summary>
    public static readonly string[] FontFamilies = { "Segoe UI", "Noto Sans", "Cantarell", "DejaVu Sans", "Liberation Sans", "Arial" };

    private static readonly Dictionary<(bool Bold, int Character), SKTypeface> Cache = new();

    /// <summary>The outline of the text laid out in a box at (<paramref name="x"/>, <paramref name="y"/>), y downwards.</summary>
    /// <param name="rightToLeft">Lines start at the right edge (Start = right).</param>
    /// <returns>The outline (nonzero winding) and the height taken.</returns>
    public static (SKPath Path, double Height) Layout(string text, double size, bool bold, double x, double y, double width,
        TextAlign align, int maxLines, bool rightToLeft)
    {
        var path = new SKPath { FillType = SKPathFillType.Winding };
        text = text.Replace("\r", "").Trim();
        if (text.Length == 0) return (path, 0);

        var typeface = TypefaceFor(text, bold);
        using var font = new SKFont(typeface, (float)size) { Subpixel = true, Edging = SKFontEdging.Antialias };
        using var shaper = new SKShaper(typeface);
        var metrics = font.Metrics;
        double lineHeight = (-metrics.Ascent + metrics.Descent + metrics.Leading) * 1.15;

        var lines = Wrap(text, shaper, font, width, Math.Max(1, maxLines));
        double top = y;
        foreach (var line in lines)
        {
            var shaped = shaper.Shape(line, font);
            double lineWidth = shaped.Width;
            var effective = rightToLeft
                ? align switch { TextAlign.Start => TextAlign.End, TextAlign.End => TextAlign.Start, _ => align }
                : align;
            double left = effective switch
            {
                TextAlign.Center => x + (width - lineWidth) / 2,
                TextAlign.End => x + width - lineWidth,
                _ => x,
            };
            double baseline = top - metrics.Ascent * 1.075;
            for (int i = 0; i < shaped.Codepoints.Length; i++)
            {
                using var glyph = font.GetGlyphPath((ushort)shaped.Codepoints[i]);
                if (glyph == null || glyph.IsEmpty) continue;
                glyph.Transform(SKMatrix.CreateTranslation((float)(left + shaped.Points[i].X), (float)(baseline + shaped.Points[i].Y)));
                path.AddPath(glyph);
            }
            top += lineHeight;
        }
        return (path, top - y);
    }

    /// <summary>Lines of the text, wrapped on spaces (a word longer than the line is cut); "…" ends the last one when needed.</summary>
    private static List<string> Wrap(string text, SKShaper shaper, SKFont font, double width, int maxLines)
    {
        double Width(string s) => s.Length == 0 ? 0 : shaper.Shape(s, font).Width;
        var lines = new List<string>();
        foreach (var paragraph in text.Split('\n'))
        {
            var words = paragraph.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var current = "";
            foreach (var word in words)
            {
                var candidate = current.Length == 0 ? word : current + " " + word;
                if (Width(candidate) <= width)
                {
                    current = candidate;
                    continue;
                }
                if (current.Length > 0) lines.Add(current);
                current = word;
                // A single word wider than the line: cut it.
                while (Width(current) > width && current.Length > 1)
                {
                    int cut = current.Length - 1;
                    while (cut > 1 && Width(current[..cut]) > width) cut--;
                    lines.Add(current[..cut]);
                    current = current[cut..];
                }
            }
            lines.Add(current);
        }
        if (lines.Count <= maxLines) return lines;

        var kept = lines.Take(maxLines).ToList();
        var last = kept[^1];
        while (last.Length > 0 && Width(last.TrimEnd() + "…") > width) last = last[..^1];
        kept[^1] = last.TrimEnd() + "…";
        return kept;
    }

    /// <summary>The interface font if it has every character of the text, else a font of the system that has the first missing one.</summary>
    public static SKTypeface TypefaceFor(string text, bool bold)
    {
        var style = bold ? new SKFontStyle(SKFontStyleWeight.SemiBold, SKFontStyleWidth.Normal, SKFontStyleSlant.Upright) : SKFontStyle.Normal;
        int missing = 0;
        var primary = Interface(bold, style);
        using (var probe = new SKFont(primary))
        {
            for (int i = 0; i < text.Length; i += char.IsSurrogatePair(text, i) ? 2 : 1)
            {
                int c = char.ConvertToUtf32(text, i);
                if (char.IsWhiteSpace(text[i]) || probe.GetGlyph(c) != 0) continue;
                missing = c;
                break;
            }
        }
        if (missing == 0) return primary;
        lock (Cache)
        {
            if (Cache.TryGetValue((bold, missing), out var cached)) return cached;
            var fallback = SKFontManager.Default.MatchCharacter(null, style, null, missing) ?? primary;
            Cache[(bold, missing)] = fallback;
            return fallback;
        }
    }

    private static SKTypeface Interface(bool bold, SKFontStyle style)
    {
        lock (Cache)
        {
            if (Cache.TryGetValue((bold, 0), out var cached)) return cached;
            SKTypeface? found = null;
            foreach (var family in FontFamilies)
            {
                var candidate = SKFontManager.Default.MatchFamily(family, style);
                if (candidate != null && string.Equals(candidate.FamilyName, family, StringComparison.OrdinalIgnoreCase))
                {
                    found = candidate;
                    break;
                }
                candidate?.Dispose();
            }
            found ??= SKTypeface.FromFamilyName(null, style) ?? SKTypeface.Default;
            Cache[(bold, 0)] = found;
            return found;
        }
    }
}
