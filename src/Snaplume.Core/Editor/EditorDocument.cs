using System.Text.Json;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using SkiaSharp;
using Snaplume.Core;
using Snaplume.Imaging;

namespace Snaplume.Editor;

/// <summary>
/// A capture being edited: base image + vector annotations + image effects + undo history.
/// The flattened result overwrites the capture file, while the base image and the
/// annotations are kept in a hidden ".snaplume" folder to stay editable (see <see cref="SidecarFiles"/>).
/// The base image kept there already has the pixelated areas burnt in, so that a hidden
/// secret never stays readable on disk.
/// </summary>
public sealed class EditorDocument
{
    /// <summary>Largest width or height of an image (resize); the number of pixels is limited by <see cref="ImageIO.MaxPixels"/>.</summary>
    public const int MaxDimension = 30000;

    private const int MaxUndoSteps = 100;
    /// <summary>Bigger sidecar files are ignored: they cannot come from the editor.</summary>
    private const long MaxSidecarLength = 64 * 1024 * 1024;

    /// <summary>State of the document, for undo and for modifications that may be abandoned (see <see cref="BeginChange"/>).</summary>
    public sealed class Snapshot
    {
        internal Snapshot(EditorImage baseImage, List<Annotation> annotations, ImageEffects effects, long version)
        {
            BaseImage = baseImage;
            Annotations = annotations;
            Effects = effects;
            Version = version;
        }

        internal EditorImage BaseImage { get; }
        internal List<Annotation> Annotations { get; }
        internal ImageEffects Effects { get; }
        internal long Version { get; }
    }

    // Oldest step first.
    private readonly List<Snapshot> _undo = new();
    private readonly Stack<Snapshot> _redo = new();

    // Each state of the document gets a new number; it is clean when back to the number saved.
    private long _lastVersion;
    private long _savedVersion;

    // Base image written in the sidecar folder, and the pixelated areas burnt into it.
    private EditorImage? _writtenBase;
    private string? _writtenRedaction;

    public string FilePath { get; }
    public EditorImage BaseImage { get; private set; }
    public List<Annotation> Annotations { get; private set; }
    /// <summary>Shadow, border, corners and edges of the whole image (see <see cref="SetEffects"/>).</summary>
    public ImageEffects Effects { get; private set; } = ImageEffects.None;
    /// <summary>Number of the current state (see <see cref="AmendLastChange"/>).</summary>
    public long Version { get; private set; }
    public bool IsDirty => Version != _savedVersion;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int NextStepNumber =>
        Math.Min(Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max(), StepAnnotation.MaxNumber - 1) + 1;

    public event Action? Changed;

    /// <summary>
    /// True while <see cref="Changed"/> is raised for a change of the effects alone (<see cref="SetEffects"/>):
    /// the image and the annotations are untouched, so what was drawn from them is still valid.
    /// </summary>
    public bool IsEffectsChange { get; private set; }

    // Effects as drawn on screen, built when the document changes rather than while rendering.
    private EffectsLayout? _previewLayout;
    private (ImageEffects Effects, int Width, int Height)? _previewLayoutFor;

    /// <summary>
    /// Layout of the effects shown on screen, null without effects. When it cannot be built (output too big,
    /// not enough memory), the effects are shown without their shadow and <see cref="PreviewShadowFailed"/> is set.
    /// </summary>
    public EffectsLayout? PreviewLayout
    {
        get
        {
            UpdatePreviewLayout();
            return _previewLayout;
        }
    }

    /// <summary>The shadow of the effects could not be shown (see <see cref="PreviewLayout"/>).</summary>
    public bool PreviewShadowFailed { get; private set; }

    private EditorDocument(string path, EditorImage baseImage, List<Annotation> annotations)
    {
        FilePath = path;
        BaseImage = baseImage;
        Annotations = annotations;
    }

    /// <summary>Opens a capture, with its annotations when the sidecar files still match it.</summary>
    /// <exception cref="InvalidDataException">Not a readable image, or too large.</exception>
    public static EditorDocument Load(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        SidecarFiles.MigrateLegacy(dir);
        SidecarFiles.CleanOrphans(dir);
        var document = TryLoadSidecar(path) ?? new EditorDocument(path, new EditorImage(ImageIO.Load(path)), new List<Annotation>());
        return document;
    }

    /// <summary>Rebuilds <see cref="PreviewLayout"/> when the effects or the image size changed; never throws.</summary>
    private void UpdatePreviewLayout()
    {
        var key = (Effects, BaseImage.Width, BaseImage.Height);
        if (_previewLayoutFor == key) return;
        _previewLayoutFor = key;
        PreviewShadowFailed = false;
        try
        {
            _previewLayout = EffectsRenderer.For(key.Effects, key.Width, key.Height);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Aperçu des effets", ex);
            PreviewShadowFailed = key.Effects.Shadow;
            try
            {
                _previewLayout = EffectsRenderer.For(key.Effects with { Shadow = false }, key.Width, key.Height);
            }
            catch (Exception fallbackEx)
            {
                ErrorLog.Write("Aperçu des effets sans ombre", fallbackEx);
                _previewLayout = null;
            }
        }
    }

    /// <summary>Raises <see cref="Changed"/>, the preview of the effects brought up to date first.</summary>
    private void RaiseChanged()
    {
        UpdatePreviewLayout();
        Changed?.Invoke();
    }

    /// <summary>Base image and annotations saved next to the capture, if they still match it.</summary>
    private static EditorDocument? TryLoadSidecar(string path)
    {
        var (basePath, jsonPath) = SidecarFiles.PathsOf(path);
        if (!File.Exists(jsonPath) || !File.Exists(basePath)) return null;
        try
        {
            var json = new FileInfo(jsonPath);
            if (json.Length > MaxSidecarLength) return null;
            var data = JsonSerializer.Deserialize<SidecarData>(File.ReadAllText(jsonPath), AnnotationJson.Options);
            var file = new FileInfo(path);
            if (data?.Annotations == null || data.Version < 1 || data.Version > SidecarData.CurrentVersion
                || data.FileLength != file.Length || data.FileWriteTimeUtc != file.LastWriteTimeUtc
                || (data.BaseFileLength != 0 && data.BaseFileLength != new FileInfo(basePath).Length))
                return null;

            var annotations = data.Annotations.Where(a => a != null && a.IsValid()).Select(a => a!).ToList();
            if (annotations.Count != data.Annotations.Count)
                ErrorLog.Write($"Annotations invalides ignorées dans {jsonPath}", new InvalidDataException($"{data.Annotations.Count - annotations.Count} annotation(s)"));
            // Effects that cannot be used are dropped, the annotations kept.
            var effects = data.Effects ?? ImageEffects.None;
            if (!effects.IsValid())
            {
                ErrorLog.Write($"Effets invalides ignorés dans {jsonPath}", new InvalidDataException(effects.ToString()));
                effects = ImageEffects.None;
            }
            var document = new EditorDocument(path, new EditorImage(ImageIO.Load(basePath)), annotations) { Effects = effects };
            document._writtenBase = document.BaseImage;
            document._writtenRedaction = document.RedactionKey();
            return document;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Lecture des annotations de {path}", ex);
            return null;
        }
    }

    /// <summary>State to give back to <see cref="CommitChange"/> or <see cref="RevertChange"/>.</summary>
    public Snapshot BeginChange() => new(BaseImage, Annotations.Select(a => a.Clone()).ToList(), Effects, Version);

    /// <summary>Records the modifications made since <paramref name="before"/> as one undo step.</summary>
    public void CommitChange(Snapshot before)
    {
        AddUndoStep(before);
        _redo.Clear();
        Version = ++_lastVersion;
        RaiseChanged();
    }

    /// <summary>Puts the document back as it was at <paramref name="before"/>, without undo step.</summary>
    public void RevertChange(Snapshot before) => Restore(before);

    /// <summary>Call before any modification so it can be undone.</summary>
    public void PushUndo() => CommitChange(BeginChange());

    /// <summary>Records a modification as part of the last undo step (repeated arrow-key moves).</summary>
    public void AmendLastChange()
    {
        _redo.Clear();
        Version = ++_lastVersion;
        RaiseChanged();
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var previous = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Push(BeginChange());
        Restore(previous);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        AddUndoStep(BeginChange());
        Restore(_redo.Pop());
    }

    /// <summary>The history keeps the last steps only: each one holds a copy of the annotations.</summary>
    private void AddUndoStep(Snapshot snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
    }

    public void NotifyChanged() => RaiseChanged();

    /// <summary>
    /// Changes the image effects as one undo step, or as part of the last one when
    /// <paramref name="amendLastChange"/> (a slider being dragged).
    /// </summary>
    public void SetEffects(ImageEffects effects, bool amendLastChange = false)
    {
        if (effects == Effects) return;
        IsEffectsChange = true;
        try
        {
            if (amendLastChange && CanUndo)
            {
                Effects = effects;
                AmendLastChange();
                return;
            }
            var before = BeginChange();
            Effects = effects;
            CommitChange(before);
        }
        finally
        {
            IsEffectsChange = false;
        }
    }

    /// <summary>Size of the flattened image: the image plus the margins of the effects.</summary>
    public Size OutputSize
    {
        get
        {
            var (width, height) = OutputSizeOf(BaseImage.Width, BaseImage.Height, Effects);
            return new Size(width, height);
        }
    }

    /// <summary>Keeps only <paramref name="area"/> of the image (clamped to it); annotations follow, those left outside are dropped.</summary>
    public void Crop(PixelRect area)
    {
        int x = Math.Clamp(area.X, 0, BaseImage.Width - 1);
        int y = Math.Clamp(area.Y, 0, BaseImage.Height - 1);
        int width = Math.Clamp(area.Width, 1, BaseImage.Width - x);
        int height = Math.Clamp(area.Height, 1, BaseImage.Height - y);
        var cropped = new EditorImage(BaseImage.Pixels.Crop(x, y, width, height));
        PushUndo();
        SetBaseImage(cropped);
        var offset = new Vector(-x, -y);
        foreach (var annotation in Annotations) annotation.Translate(offset);
        // Annotations left entirely outside would be invisible but still saved.
        var bounds = new Rect(0, 0, width, height);
        Annotations.RemoveAll(a => !Rects.Touch(a.GetBounds(), bounds));
        foreach (var magnifier in Annotations.OfType<MagnifierAnnotation>().ToList())
        {
            var source = Rects.Intersect(magnifier.GetRect(), bounds);
            // A lens whose area left the image would only show the background.
            if (source is not Rect s || s.Width <= 0 || s.Height <= 0) Annotations.Remove(magnifier);
            else if (!Rects.Touch(magnifier.GetLensRect(), bounds)) magnifier.PlaceLens(bounds);
        }
        RaiseChanged();
    }

    /// <summary>Size of the output for an image of this size with these effects (margins of the shadow included).</summary>
    public static (int Width, int Height) OutputSizeOf(int width, int height, ImageEffects effects)
    {
        var padding = effects.GetPadding();
        return (width + padding.Left + padding.Right, height + padding.Top + padding.Bottom);
    }

    /// <summary>Effects after resizing the image to this size: their sizes follow the mean factor, like the annotations.</summary>
    public ImageEffects ResizedEffects(int width, int height) =>
        Effects.Scaled(((double)width / BaseImage.Width + (double)height / BaseImage.Height) / 2);

    /// <summary>Resizes the image (high quality resampling); annotations and effect sizes follow.</summary>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxDimension);
        height = Math.Clamp(height, 1, MaxDimension);
        if (width == BaseImage.Width && height == BaseImage.Height) return;
        // Checked before anything is allocated: the saved image also has the margins of the shadow.
        var effects = ResizedEffects(width, height);
        var (outputWidth, outputHeight) = OutputSizeOf(width, height, effects);
        ImageIO.CheckSize(outputWidth, outputHeight);
        double sx = (double)width / BaseImage.Width, sy = (double)height / BaseImage.Height;

        // Computed first: running out of memory must leave the document and its history untouched.
        var resized = new EditorImage(ImageIO.Resize(BaseImage.Pixels, width, height));

        PushUndo();
        SetBaseImage(resized);
        Effects = effects;
        foreach (var annotation in Annotations) annotation.Scale(sx, sy);
        RaiseChanged();
    }

    /// <summary>
    /// Image after inserting an image at <paramref name="area"/>: the image itself, or bigger when the inserted
    /// image goes past its edges (see <see cref="InsertImage"/>).
    /// </summary>
    public static PixelRect InsertedBounds(int width, int height, Rect area)
    {
        var union = Rects.Union(new Rect(0, 0, width, height), area);
        int left = (int)Math.Floor(union.Left), top = (int)Math.Floor(union.Top);
        return new PixelRect(left, top, (int)Math.Ceiling(union.Right) - left, (int)Math.Ceiling(union.Bottom) - top);
    }

    /// <summary>
    /// Burns an inserted image (pasted, opened or captured) into the base image at <paramref name="area"/>, as one
    /// undo step; the annotations stay above it. When the inserted image goes past the edges, the image grows to
    /// hold it (e.g. two captures side by side), the new parts filled with <paramref name="background"/>; the
    /// annotations follow the image when it grows to the left or to the top.
    /// </summary>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public void InsertImage(PixelBuffer image, Rect area, Color background)
    {
        int w = BaseImage.Width, h = BaseImage.Height;
        var bounds = InsertedBounds(w, h, area);
        if (bounds.Width > MaxDimension || bounds.Height > MaxDimension)
            throw new InvalidDataException(Loc.T("App.ImageTooLarge", bounds.Width, bounds.Height));
        var (outputWidth, outputHeight) = OutputSizeOf(bounds.Width, bounds.Height, Effects);
        ImageIO.CheckSize(outputWidth, outputHeight);
        var offset = new Vector(-bounds.X, -bounds.Y);
        bool grows = bounds.Width != w || bounds.Height != h;

        // Computed first: running out of memory must leave the document and its history untouched.
        var merged = SkiaPixels.Render(bounds.Width, bounds.Height, canvas =>
        {
            canvas.Clear(grows ? new SKColor(background.R, background.G, background.B, background.A) : SKColors.Transparent);
            using (var baseImage = SkiaPixels.ToImage(BaseImage.Pixels))
                canvas.DrawImage(baseImage, (float)offset.X, (float)offset.Y);
            var target = Rects.Offset(area, offset);
            bool shrinks = target.Width < image.Width || target.Height < image.Height;
            var sampling = shrinks
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var inserted = SkiaPixels.ToImage(image);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(inserted, new SKRect((float)target.Left, (float)target.Top, (float)target.Right, (float)target.Bottom), sampling, paint);
        });
        PushUndo();
        SetBaseImage(new EditorImage(merged));
        if (offset != default)
        {
            foreach (var annotation in Annotations) annotation.Translate(offset);
        }
        RaiseChanged();
    }

    /// <summary>Base image with all annotations burnt in, and the image effects around it (Avalonia rendering).</summary>
    public PixelBuffer Flatten() => RenderImage(Annotations, Effects);

    /// <summary>Base image with only the pixelated areas burnt in: what is kept in the sidecar folder.</summary>
    internal PixelBuffer RedactedBase()
    {
        var pixelated = Annotations.OfType<PixelateAnnotation>().ToList();
        if (pixelated.Count == 0) return BaseImage.Pixels;
        var source = BaseImage.Pixels;
        var result = new PixelBuffer(source.Width, source.Height, (byte[])source.Pixels.Clone());
        foreach (var pixelate in pixelated)
        {
            var area = pixelate.GetPixelArea(BaseImage);
            if (area.Width < 1 || area.Height < 1) continue;
            var blocks = BaseImage.PixelatedPixels(pixelate.BlockSize);
            for (int y = area.Y; y < area.Y + area.Height; y++)
                Buffer.BlockCopy(blocks.Pixels, y * source.Stride + area.X * 4, result.Pixels, y * source.Stride + area.X * 4, area.Width * 4);
        }
        return result;
    }

    private PixelBuffer RenderImage(IReadOnlyList<Annotation> annotations, ImageEffects effects)
    {
        int w = BaseImage.Width, h = BaseImage.Height;
        // Checks the size of the output, margins included, before building anything.
        var layout = EffectsRenderer.For(effects, w, h);
        // Nothing to draw: the pixels as they are, exactly.
        if (layout == null && annotations.Count == 0)
            return new PixelBuffer(w, h, (byte[])BaseImage.Pixels.Pixels.Clone());

        void DrawImage(DrawingContext dc)
        {
            dc.DrawImage(BaseImage.Bitmap, BaseImage.Bounds);
            AnnotationRenderer.Render(dc, BaseImage, annotations);
        }

        int outputWidth = layout?.OutputWidth ?? w, outputHeight = layout?.OutputHeight ?? h;
        using var target = new RenderTargetBitmap(new PixelSize(outputWidth, outputHeight), AvaloniaImages.Dpi);
        using (var dc = target.CreateDrawingContext())
        {
            if (layout != null)
            {
                layout.Render(dc, DrawImage);
            }
            else
            {
                using (dc.PushClip(BaseImage.Bounds)) DrawImage(dc);
            }
        }
        return AvaloniaImages.ToPixelBuffer(target);
    }

    /// <summary>Identifies the pixelated areas burnt into the saved base image.</summary>
    private string RedactionKey() =>
        string.Join(";", Annotations.OfType<PixelateAnnotation>().Select(p => $"{p.GetPixelArea(BaseImage)}/{p.BlockSize}"));

    /// <summary>
    /// Writes in an order that keeps the annotations editable if something fails: everything that may fail
    /// without touching the disk comes first (the annotations serialized, the flattened image and the base
    /// image rendered: too big, out of memory), then the base image is written, the flattened image and last
    /// the annotation file, replaced atomically. A sidecar that does not match its images is ignored.
    /// </summary>
    public void Save(int jpegQuality = 90)
    {
        var (basePath, jsonPath) = SidecarFiles.PathsOf(FilePath);
        var flattened = Flatten();
        if (Annotations.Count == 0 && Effects.IsEmpty)
        {
            ImageIO.Save(flattened, FilePath, jpegQuality);
            SidecarFiles.Delete(FilePath);
        }
        else
        {
            var annotationsJson = JsonSerializer.SerializeToUtf8Bytes(Annotations, AnnotationJson.Options);
            byte[]? effectsJson = Effects.IsEmpty ? null : JsonSerializer.SerializeToUtf8Bytes(Effects, AnnotationJson.Options);
            var redaction = RedactionKey();
            bool writeBase = !ReferenceEquals(BaseImage, _writtenBase) || redaction != _writtenRedaction || !File.Exists(basePath);
            var redactedBase = writeBase ? RedactedBase() : null;

            SidecarFiles.EnsureFolder(Path.GetDirectoryName(basePath)!);
            if (redactedBase != null)
            {
                ImageIO.Save(redactedBase, basePath);
                _writtenBase = BaseImage;
                _writtenRedaction = redaction;
            }

            ImageIO.Save(flattened, FilePath, jpegQuality);
            WriteSidecarJson(jsonPath, new FileInfo(FilePath), new FileInfo(basePath).Length, annotationsJson, effectsJson);
        }

        _savedVersion = Version;
        SidecarFiles.CleanOrphans(Path.GetDirectoryName(FilePath)!);
        RaiseChanged();
    }

    /// <param name="effectsJson">Null without effects: the file then keeps the format of version 1, still read by older versions.</param>
    private static void WriteSidecarJson(string jsonPath, FileInfo file, long baseLength, byte[] annotationsJson, byte[]? effectsJson)
    {
        var temp = jsonPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(SidecarData.Version), effectsJson == null ? 1 : SidecarData.CurrentVersion);
            writer.WriteNumber(nameof(SidecarData.FileLength), file.Length);
            writer.WriteString(nameof(SidecarData.FileWriteTimeUtc), file.LastWriteTimeUtc);
            writer.WriteNumber(nameof(SidecarData.BaseFileLength), baseLength);
            if (effectsJson != null)
            {
                writer.WritePropertyName(nameof(SidecarData.Effects));
                writer.WriteRawValue(effectsJson, skipInputValidation: true);
            }
            writer.WritePropertyName(nameof(SidecarData.Annotations));
            writer.WriteRawValue(annotationsJson, skipInputValidation: true);
            writer.WriteEndObject();
        }
        File.Move(temp, jsonPath, overwrite: true);
    }

    private void Restore(Snapshot snapshot)
    {
        SetBaseImage(snapshot.BaseImage);
        Annotations = snapshot.Annotations;
        Effects = snapshot.Effects;
        Version = snapshot.Version;
        RaiseChanged();
    }

    /// <summary>The previous image stays in the undo history with its pixels only (see <see cref="EditorImage.ReleaseBitmaps"/>).</summary>
    private void SetBaseImage(EditorImage image)
    {
        var previous = BaseImage;
        BaseImage = image;
        if (!ReferenceEquals(previous, image)) previous.ReleaseBitmaps();
    }
}

/// <summary>Pixel work with SkiaSharp on <see cref="PixelBuffer"/> (straight alpha in and out).</summary>
internal static class SkiaPixels
{
    /// <summary>A Skia copy of the pixels, for drawing them.</summary>
    public static SKImage ToImage(PixelBuffer image)
    {
        var info = new SKImageInfo(image.Width, image.Height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        unsafe
        {
            fixed (byte* p = image.Pixels)
            {
                using var pixmap = new SKPixmap(info, (IntPtr)p, image.Stride);
                return SKImage.FromPixelCopy(pixmap) ?? throw new InvalidOperationException("Image Skia impossible");
            }
        }
    }

    /// <summary>Draws a new image of this size (transparent at first).</summary>
    public static PixelBuffer Render(int width, int height, Action<SKCanvas> draw)
    {
        var result = new PixelBuffer(width, height);
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                            ?? throw new InsufficientMemoryException($"Surface {width} × {height}");
        draw(surface.Canvas);
        surface.Canvas.Flush();
        unsafe
        {
            fixed (byte* p = result.Pixels)
            {
                var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Unpremul);
                if (!surface.ReadPixels(info, (IntPtr)p, result.Stride, 0, 0))
                    throw new InvalidOperationException("Lecture des pixels impossible");
            }
        }
        return result;
    }
}
