// Renders the Snaplume logo (docs/branding/snaplume.svg) to PNG with SkiaSharp.
//
// Only the SVG subset the logo uses is understood: <svg viewBox>, <g>, <rect> (x, y, width, height, rx),
// <path d>, the presentation attributes fill, stroke, stroke-width, stroke-linecap, stroke-linejoin (inherited
// from <g>), and transform="translate(...) scale(...)". Anything else stops the tool with an error, so a
// richer logo cannot be rendered wrongly without notice.

using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using SkiaSharp;

var root = FindRepositoryRoot();
var svgPath = Path.Combine(root, "docs", "branding", "snaplume.svg");
var svg = XDocument.Load(svgPath).Root ?? throw new InvalidDataException("Empty SVG");
var viewBox = (svg.Attribute("viewBox")?.Value ?? "0 0 120 120").Split(' ', StringSplitOptions.RemoveEmptyEntries)
    .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();

int[] sizes = { 16, 22, 24, 32, 48, 64, 128, 256, 512 };
foreach (var size in sizes)
{
    var target = Path.Combine(root, "packaging", "linux", "icons", "hicolor", $"{size}x{size}", "apps", "io.github.mrddream.Snaplume.png");
    Render(size, target);
}
Render(64, Path.Combine(root, "src", "Snaplume.Platform.Linux", "Assets", "tray.png"));
return 0;

void Render(int size, string target)
{
    var info = new SKImageInfo(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
    using var surface = SKSurface.Create(info);
    var canvas = surface.Canvas;
    canvas.Clear(SKColors.Transparent);
    canvas.Scale(size / viewBox[2], size / viewBox[3]);
    canvas.Translate(-viewBox[0], -viewBox[1]);
    foreach (var element in svg.Elements()) Draw(canvas, element, new Style());
    using var image = surface.Snapshot();
    using var data = image.Encode(SKEncodedImageFormat.Png, 100);
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    File.WriteAllBytes(target, data.ToArray());
    Console.WriteLine($"{size,4} px  {Path.GetRelativePath(root, target)}");
}

void Draw(SKCanvas canvas, XElement element, Style inherited)
{
    var style = inherited.With(element);
    canvas.Save();
    if (element.Attribute("transform")?.Value is { } transform) ApplyTransform(canvas, transform);
    switch (element.Name.LocalName)
    {
        case "title" or "desc":
            break;
        case "g":
            foreach (var child in element.Elements()) Draw(canvas, child, style);
            break;
        case "rect":
            float x = Number(element, "x"), y = Number(element, "y"), w = Number(element, "width"), h = Number(element, "height");
            float rx = Number(element, "rx");
            using (var path = new SKPath())
            {
                path.AddRoundRect(new SKRect(x, y, x + w, y + h), rx, element.Attribute("ry") != null ? Number(element, "ry") : rx);
                Paint(canvas, path, style);
            }
            break;
        case "path":
            using (var path = SKPath.ParseSvgPathData(element.Attribute("d")?.Value ?? throw new InvalidDataException("path without d")))
                Paint(canvas, path, style);
            break;
        default:
            throw new NotSupportedException($"SVG element <{element.Name.LocalName}> is not supported by this generator");
    }
    canvas.Restore();
}

void Paint(SKCanvas canvas, SKPath path, Style style)
{
    if (style.Fill is { } fill)
    {
        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = fill };
        canvas.DrawPath(path, paint);
    }
    if (style.Stroke is { } stroke)
    {
        using var paint = new SKPaint
        {
            IsAntialias = true,
            Style = SKPaintStyle.Stroke,
            Color = stroke,
            StrokeWidth = style.StrokeWidth,
            StrokeCap = style.LineCap,
            StrokeJoin = style.LineJoin,
        };
        canvas.DrawPath(path, paint);
    }
}

static void ApplyTransform(SKCanvas canvas, string transform)
{
    foreach (Match m in Regex.Matches(transform, @"(\w+)\s*\(([^)]*)\)"))
    {
        var args = m.Groups[2].Value.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
        switch (m.Groups[1].Value)
        {
            case "translate":
                canvas.Translate(args[0], args.Length > 1 ? args[1] : 0);
                break;
            case "scale":
                canvas.Scale(args[0], args.Length > 1 ? args[1] : args[0]);
                break;
            default:
                throw new NotSupportedException($"SVG transform {m.Groups[1].Value} is not supported by this generator");
        }
    }
}

static float Number(XElement element, string name) =>
    element.Attribute(name) is { } a ? float.Parse(a.Value, CultureInfo.InvariantCulture) : 0;

static string FindRepositoryRoot()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir != null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Snaplume.slnx"))) return dir.FullName;
    throw new DirectoryNotFoundException("Snaplume.slnx not found above " + AppContext.BaseDirectory);
}

/// <summary>Presentation attributes, inherited from the parent group as in SVG.</summary>
internal sealed record Style(SKColor? Fill = null, SKColor? Stroke = null, float StrokeWidth = 1,
    SKStrokeCap LineCap = SKStrokeCap.Butt, SKStrokeJoin LineJoin = SKStrokeJoin.Miter, bool FillSet = false)
{
    public Style With(XElement element)
    {
        var style = this;
        // SVG's initial fill is black; an unset fill only matters on shapes, where the logo always sets one.
        if (element.Attribute("fill")?.Value is { } fill) style = style with { Fill = Color(fill), FillSet = true };
        if (element.Attribute("stroke")?.Value is { } stroke) style = style with { Stroke = Color(stroke) };
        if (element.Attribute("stroke-width")?.Value is { } width) style = style with { StrokeWidth = float.Parse(width, CultureInfo.InvariantCulture) };
        if (element.Attribute("stroke-linecap")?.Value is { } cap)
            style = style with { LineCap = cap switch { "round" => SKStrokeCap.Round, "square" => SKStrokeCap.Square, _ => SKStrokeCap.Butt } };
        if (element.Attribute("stroke-linejoin")?.Value is { } join)
            style = style with { LineJoin = join switch { "round" => SKStrokeJoin.Round, "bevel" => SKStrokeJoin.Bevel, _ => SKStrokeJoin.Miter } };
        if (!style.FillSet && element.Name.LocalName is "rect" or "path") style = style with { Fill = SKColors.Black };
        return style;
    }

    private static SKColor? Color(string value) => value == "none" ? null : SKColor.Parse(value);
}
