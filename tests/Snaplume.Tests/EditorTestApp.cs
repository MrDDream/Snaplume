using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Headless;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Snaplume.Core;
using Snaplume.Imaging;

// Rendering tests ([AvaloniaFact]) run on a headless Avalonia with the real Skia renderer, one application for
// the whole assembly (the editor keeps caches of bitmaps and layouts between documents, as in the application).
[assembly: AvaloniaTestApplication(typeof(Snaplume.Tests.EditorTestApp))]
[assembly: AvaloniaTestIsolation(AvaloniaTestIsolationLevel.PerAssembly)]

namespace Snaplume.Tests;

/// <summary>Avalonia application of the tests: headless, drawn by Skia (not the stub renderer, which draws nothing).</summary>
public sealed class EditorTestApp : Application
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<EditorTestApp>()
        .UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false })
        .AfterSetup(_ => Loc.Initialize("fr"));

    public override void Initialize() => Styles.Add(new Avalonia.Themes.Fluent.FluentTheme());

    /// <summary>
    /// Settings, presets and the error log of the tests go to the test folder, never to the user's settings:
    /// a "portable" marker next to the test assembly, created before anything reads the settings folder.
    /// </summary>
    [ModuleInitializer]
    internal static void KeepSettingsInTestFolder()
    {
        try
        {
            var marker = Path.Combine(AppContext.BaseDirectory, "portable");
            if (!File.Exists(marker)) File.WriteAllText(marker, "");
        }
        catch (IOException)
        {
            // Read-only test folder: the settings folder of the user is used, as before.
        }
    }
}

/// <summary>Helpers of the editor tests: images, pixels, temporary folders.</summary>
internal static class TestImages
{
    public static PixelBuffer Solid(int width, int height, Color color)
    {
        var image = new PixelBuffer(width, height);
        for (int i = 0; i < image.Pixels.Length; i += 4)
        {
            image.Pixels[i] = color.B;
            image.Pixels[i + 1] = color.G;
            image.Pixels[i + 2] = color.R;
            image.Pixels[i + 3] = color.A;
        }
        return image;
    }

    /// <summary>Pixels all different from their neighbours: a "secret" that pixelating must hide.</summary>
    public static PixelBuffer Noise(int width, int height, int seed = 1)
    {
        var random = new Random(seed);
        var image = new PixelBuffer(width, height);
        random.NextBytes(image.Pixels);
        return image.MakeOpaque();
    }

    public static Color At(PixelBuffer image, int x, int y)
    {
        int i = (y * image.Width + x) * 4;
        return Color.FromArgb(image.Pixels[i + 3], image.Pixels[i + 2], image.Pixels[i + 1], image.Pixels[i]);
    }

    public static bool Near(Color a, Color b, int tolerance = 8) =>
        Math.Abs(a.R - b.R) <= tolerance && Math.Abs(a.G - b.G) <= tolerance
        && Math.Abs(a.B - b.B) <= tolerance && Math.Abs(a.A - b.A) <= tolerance;

    /// <summary>Number of pixels of the area different from <paramref name="color"/>.</summary>
    public static int CountDifferent(PixelBuffer image, PixelRect area, Color color, int tolerance = 8)
    {
        int count = 0;
        for (int y = Math.Max(0, area.Y); y < Math.Min(image.Height, area.Bottom); y++)
        for (int x = Math.Max(0, area.X); x < Math.Min(image.Width, area.Right); x++)
            if (!Near(At(image, x, y), color, tolerance)) count++;
        return count;
    }

    /// <summary>Draws with Avalonia into a new image of this size (transparent at first).</summary>
    public static PixelBuffer Render(int width, int height, Action<DrawingContext> draw)
    {
        using var target = new RenderTargetBitmap(new PixelSize(width, height), AvaloniaImages.Dpi);
        using (var dc = target.CreateDrawingContext()) draw(dc);
        return AvaloniaImages.ToPixelBuffer(target);
    }
}

/// <summary>A temporary folder deleted with its content.</summary>
internal sealed class TempFolder : IDisposable
{
    public TempFolder() => Directory.CreateDirectory(Path);

    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "snaplume-editor-" + Guid.NewGuid().ToString("N"));

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try { Directory.Delete(Path, true); }
        catch (IOException) { /* left for the system to clean */ }
    }
}
