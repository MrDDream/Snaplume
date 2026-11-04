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
    /// <summary>
    /// Memory the base images of the undo history may take (the images of the redo steps included, each counted
    /// once): beyond it, the oldest steps are forgotten. Big images resized or cropped many times reach it first.
    /// </summary>
    internal const long DefaultUndoMemoryBudget = 1L << 30;
    /// <summary>Bigger sidecar files are ignored: they cannot come from the editor.</summary>
    private const long MaxSidecarLength = 64 * 1024 * 1024;
    /// <summary>Limits of a sidecar file (see <see cref="TooMuchToEdit"/>).</summary>
    internal const int MaxSidecarAnnotations = 10_000;
    internal const int MaxSidecarMagnifiers = 100;
    internal const long MaxSidecarTextCharacters = 1_000_000;

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

    // Oldest step first; for the redo steps, the next one last.
    private readonly List<Snapshot> _undo = new();
    private readonly List<Snapshot> _redo = new();

    /// <summary>See <see cref="DefaultUndoMemoryBudget"/>; smaller in the tests.</summary>
    internal long UndoMemoryBudget { get; set; } = DefaultUndoMemoryBudget;

    /// <summary>Number of undo steps kept (for the tests).</summary>
    internal int UndoCount => _undo.Count;

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
            // The date within 2 seconds: a capture moved to a FAT or exFAT drive (USB stick) keeps it to 2 s only.
            if (data?.Annotations == null || data.Version < 1 || data.Version > SidecarData.CurrentVersion
                || data.FileLength != file.Length || (data.FileWriteTimeUtc - file.LastWriteTimeUtc).Duration() > TimeSpan.FromSeconds(2)
                || (data.BaseFileLength != 0 && data.BaseFileLength != new FileInfo(basePath).Length))
                return null;

            if (TooMuchToEdit(data.Annotations) is { } excess)
            {
                ErrorLog.Write($"Annotations ignorées dans {jsonPath}", new InvalidDataException(excess));
                return null;
            }

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

    /// <summary>
    /// Why the annotations of a sidecar file are too many to be edited (each one is drawn on every frame, each
    /// magnifier copies part of the image), or null. No editing session makes that many: the file is not from the
    /// editor, and it is ignored as a whole.
    /// </summary>
    internal static string? TooMuchToEdit(IReadOnlyList<Annotation?> annotations)
    {
        if (annotations.Count > MaxSidecarAnnotations) return $"{annotations.Count} annotations (au plus {MaxSidecarAnnotations})";
        int magnifiers = annotations.Count(a => a is MagnifierAnnotation);
        if (magnifiers > MaxSidecarMagnifiers) return $"{magnifiers} loupes (au plus {MaxSidecarMagnifiers})";
        long characters = annotations.OfType<TextAnnotation>().Sum(t => (long)(t.Text?.Length ?? 0));
        if (characters > MaxSidecarTextCharacters) return $"{characters} caractères de texte (au plus {MaxSidecarTextCharacters})";
        return null;
    }

    /// <summary>State to give back to <see cref="CommitChange"/> or <see cref="RevertChange"/>.</summary>
    public Snapshot BeginChange() => new(BaseImage, Annotations.Select(a => a.Clone()).ToList(), Effects, Version);

    /// <summary>Records the modifications made since <paramref name="before"/> as one undo step.</summary>
    public void CommitChange(Snapshot before)
    {
        _redo.Clear();
        AddUndoStep(before);
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
        _redo.Add(BeginChange());
        Restore(previous);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var next = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        AddUndoStep(BeginChange());
        Restore(next);
    }

    /// <summary>
    /// The history keeps the last steps only: each one holds a copy of the annotations, and the base images of all
    /// the steps stay within <see cref="UndoMemoryBudget"/> (the oldest undo steps go first, then the furthest redo
    /// steps); the step just added is always kept.
    /// </summary>
    private void AddUndoStep(Snapshot snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > MaxUndoSteps) _undo.RemoveAt(0);
        while (HistoryImageBytes() > UndoMemoryBudget)
        {
            if (_undo.Count > 1) _undo.RemoveAt(0);
            else if (_redo.Count > 0) _redo.RemoveAt(0);
            else break;
        }
    }

    /// <summary>Pixels of the base images held by the history only (each image once; the current one is not counted).</summary>
    private long HistoryImageBytes()
    {
        var images = new HashSet<EditorImage>(ReferenceEqualityComparer.Instance) { BaseImage };
        long total = 0;
        foreach (var step in _undo.Concat(_redo))
        {
            if (images.Add(step.BaseImage)) total += step.BaseImage.Pixels.Pixels.LongLength;
        }
        return total;
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
        var before = BeginChange();
        SetBaseImage(cropped);
        var offset = new Vector(-x, -y);
        foreach (var annotation in Annotations) annotation.Translate(offset);
        KeepAnnotationsInside();
        CommitChange(before);
    }

    /// <summary>
    /// After the image got smaller: the annotations left entirely outside are dropped (they would be invisible but
    /// still saved), and so are the magnifiers whose area left the image; a lens left outside comes back in.
    /// </summary>
    private void KeepAnnotationsInside()
    {
        var bounds = BaseImage.Bounds;
        Annotations.RemoveAll(a => !Rects.Touch(a.GetBounds(), bounds));
        foreach (var magnifier in Annotations.OfType<MagnifierAnnotation>().ToList())
        {
            var source = Rects.Intersect(magnifier.GetRect(), bounds);
            // A lens whose area left the image would only show the background.
            if (source is not Rect s || s.Width <= 0 || s.Height <= 0) Annotations.Remove(magnifier);
            else if (!Rects.Touch(magnifier.GetLensRect(), bounds)) magnifier.PlaceLens(bounds);
        }
    }

    /// <summary>
    /// Changes the size of the canvas without stretching the image, like "Canvas size" in Paint: space is added on
    /// each side by its margin (cut off where it is negative), the new parts filled with <paramref name="background"/>
    /// (transparent stays see-through), and the image keeps its pixels, the annotations following it. One undo step;
    /// the effects apply to the new size. See <see cref="CanvasSize.Margins"/> for the margins of a size and an anchor.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The canvas would be empty, or would leave out the whole image.</exception>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public void ExtendCanvas(int left, int top, int right, int bottom, Color background)
    {
        int w = BaseImage.Width, h = BaseImage.Height;
        long width = (long)w + left + right, height = (long)h + top + bottom;
        // Some of the image must stay: each margin cuts less than the whole image.
        if (width < 1 || height < 1 || -(long)left >= w || -(long)top >= h || -(long)right >= w || -(long)bottom >= h)
            throw new ArgumentOutOfRangeException(nameof(left), $"Marges {left}, {top}, {right}, {bottom} pour une image de {w} × {h}");
        if (left == 0 && top == 0 && right == 0 && bottom == 0) return;
        CheckCanvasSize(width, height);
        // Computed first: running out of memory must leave the document and its history untouched.
        var pixels = CanvasPixels(new PixelRect(-left, -top, (int)width, (int)height), background);
        ReplaceCanvas(pixels, new Vector(left, top), cuts: left < 0 || top < 0 || right < 0 || bottom < 0);
    }

    /// <summary>Refuses a canvas too big to edit, margins of the effects included, before anything is allocated.</summary>
    /// <exception cref="InvalidDataException">Wider or higher than <see cref="MaxDimension"/>, or too many pixels.</exception>
    private void CheckCanvasSize(long width, long height)
    {
        if (width > MaxDimension || height > MaxDimension)
            throw new InvalidDataException(Loc.T("App.ImageTooLarge", width, height));
        var (outputWidth, outputHeight) = OutputSizeOf((int)width, (int)height, Effects);
        ImageIO.CheckSize(outputWidth, outputHeight);
    }

    /// <summary>
    /// The base image on a canvas of <paramref name="canvas"/>, given in the coordinates of the image (it may start left
    /// of or above it, and stop before its other edges): its pixels copied exactly, the rest filled with <paramref name="background"/>.
    /// </summary>
    private PixelBuffer CanvasPixels(PixelRect canvas, Color background)
    {
        var source = BaseImage.Pixels;
        var result = new PixelBuffer(canvas.Width, canvas.Height);
        int stride = result.Stride;
        if (background.A != 0)
        {
            // Straight alpha BGRA, like the pixels of the image; the first row is copied to the others.
            for (int i = 0; i < stride; i += 4)
            {
                result.Pixels[i] = background.B;
                result.Pixels[i + 1] = background.G;
                result.Pixels[i + 2] = background.R;
                result.Pixels[i + 3] = background.A;
            }
            for (int row = 1; row < canvas.Height; row++) Buffer.BlockCopy(result.Pixels, 0, result.Pixels, row * stride, stride);
        }
        int x0 = Math.Max(0, canvas.X), x1 = Math.Min(source.Width, canvas.Right);
        int y0 = Math.Max(0, canvas.Y), y1 = Math.Min(source.Height, canvas.Bottom);
        for (int y = y0; y < y1; y++)
            Buffer.BlockCopy(source.Pixels, y * source.Stride + x0 * 4, result.Pixels, (y - canvas.Y) * stride + (x0 - canvas.X) * 4, (x1 - x0) * 4);
        return result;
    }

    /// <summary>
    /// Replaces the base image by a canvas of another size as one undo step, the annotations moved by
    /// <paramref name="offset"/>; when the canvas <paramref name="cuts"/> into the image, those left outside are dropped.
    /// </summary>
    private void ReplaceCanvas(PixelBuffer pixels, Vector offset, bool cuts)
    {
        var before = BeginChange();
        SetBaseImage(new EditorImage(pixels));
        if (offset != default)
        {
            foreach (var annotation in Annotations) annotation.Translate(offset);
        }
        if (cuts) KeepAnnotationsInside();
        CommitChange(before);
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
        if (!CheckResize(ref width, ref height)) return;
        // Computed first: running out of memory must leave the document and its history untouched.
        ApplyResize(BaseImage, ImageIO.Resize(BaseImage.Pixels, width, height));
    }

    /// <summary>
    /// <see cref="Resize"/> with the resampling in the background (seconds for a big image); the document is changed
    /// on the calling thread once it is done. Abandoned when the base image changed meanwhile (undo, crop...).
    /// </summary>
    /// <returns>False when nothing was changed (same size, or abandoned).</returns>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public async Task<bool> ResizeAsync(int width, int height)
    {
        if (!CheckResize(ref width, ref height)) return false;
        var source = BaseImage;
        var pixels = await Task.Run(() => ImageIO.Resize(source.Pixels, width, height));
        return ApplyResize(source, pixels);
    }

    /// <summary>Clamps the size and checks the output, before anything is allocated; false for the same size.</summary>
    private bool CheckResize(ref int width, ref int height)
    {
        width = Math.Clamp(width, 1, MaxDimension);
        height = Math.Clamp(height, 1, MaxDimension);
        if (width == BaseImage.Width && height == BaseImage.Height) return false;
        // The saved image also has the margins of the shadow.
        var (outputWidth, outputHeight) = OutputSizeOf(width, height, ResizedEffects(width, height));
        ImageIO.CheckSize(outputWidth, outputHeight);
        return true;
    }

    /// <summary>Replaces <paramref name="source"/> by its resized pixels as one undo step; false if it is no longer the base image.</summary>
    private bool ApplyResize(EditorImage source, PixelBuffer pixels)
    {
        if (!ReferenceEquals(source, BaseImage)) return false;
        int width = pixels.Width, height = pixels.Height;
        var effects = ResizedEffects(width, height);
        double sx = (double)width / BaseImage.Width, sy = (double)height / BaseImage.Height;
        var before = BeginChange();
        SetBaseImage(new EditorImage(pixels));
        Effects = effects;
        foreach (var annotation in Annotations) annotation.Scale(sx, sy);
        CommitChange(before);
        return true;
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
        CheckCanvasSize(bounds.Width, bounds.Height);
        var offset = new Vector(-bounds.X, -bounds.Y);
        bool grows = bounds.Width != w || bounds.Height != h;

        // Computed first: running out of memory must leave the document and its history untouched.
        var grown = grows ? CanvasPixels(bounds, background) : BaseImage.Pixels;
        var merged = SkiaPixels.Render(bounds.Width, bounds.Height, canvas =>
        {
            using (var baseImage = SkiaPixels.ToImage(grown))
                canvas.DrawImage(baseImage, 0, 0);
            var target = Rects.Offset(area, offset);
            bool shrinks = target.Width < image.Width || target.Height < image.Height;
            var sampling = shrinks
                ? new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear)
                : new SKSamplingOptions(SKCubicResampler.Mitchell);
            using var inserted = SkiaPixels.ToImage(image);
            using var paint = new SKPaint { IsAntialias = true };
            canvas.DrawImage(inserted, new SKRect((float)target.Left, (float)target.Top, (float)target.Right, (float)target.Bottom), sampling, paint);
        });
        ReplaceCanvas(merged, offset, cuts: false);
    }

    /// <summary>Base image with all annotations burnt in, and the image effects around it (Avalonia rendering).</summary>
    public PixelBuffer Flatten() => RenderImage(Annotations, Effects);

    /// <summary>
    /// Base image with its annotations, without the effects, reduced to fit in <paramref name="maxWidth"/> ×
    /// <paramref name="maxHeight"/> (never enlarged): the sample of the border presets.
    /// </summary>
    public PixelBuffer Thumbnail(int maxWidth, int maxHeight)
    {
        int w = BaseImage.Width, h = BaseImage.Height;
        double scale = Math.Min(1, Math.Min((double)maxWidth / w, (double)maxHeight / h));
        int width = Math.Max(1, (int)Math.Round(w * scale)), height = Math.Max(1, (int)Math.Round(h * scale));
        // Reduced by Skia (sharper than a reduction while drawing), then the annotations drawn over it at the same scale.
        var reduced = scale < 1 ? new EditorImage(ImageIO.Resize(BaseImage.Pixels, width, height)) : null;
        if (Annotations.Count == 0) return reduced?.Pixels ?? new PixelBuffer(w, h, (byte[])BaseImage.Pixels.Pixels.Clone());
        try
        {
            using var target = new RenderTargetBitmap(new PixelSize(width, height), AvaloniaImages.Dpi);
            using (var dc = target.CreateDrawingContext())
            {
                dc.DrawImage((reduced ?? BaseImage).Bitmap, new Rect(0, 0, width, height));
                using (dc.PushTransform(Matrix.CreateScale((double)width / w, (double)height / h)))
                using (dc.PushClip(BaseImage.Bounds))
                    AnnotationRenderer.Render(dc, BaseImage, Annotations);
            }
            return AvaloniaImages.ToPixelBuffer(target);
        }
        finally
        {
            reduced?.ReleaseBitmaps();
        }
    }

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
    /// image rendered: too big, out of memory), then the base image is written to a temporary file, the flattened
    /// image saved, the base image renamed into place and last the annotation file, replaced atomically. A sidecar
    /// that does not match its images is ignored.
    /// </summary>
    /// <param name="webpQuality">For a WebP capture: <see cref="ImageIO.WebPLossless"/>, or the quality of a lossy WebP.</param>
    public void Save(int jpegQuality = 90, int webpQuality = ImageIO.WebPLossless)
    {
        var job = PrepareSave(jpegQuality, webpQuality);
        job.Write();
        CompleteSave(job);
    }

    /// <summary>
    /// First part of <see cref="Save"/>, on the thread of the document: everything read from it (the flattened image,
    /// the base image to keep, the annotations serialized). <see cref="SaveJob.Write"/> may then run in the background
    /// while the document goes on changing, and <see cref="CompleteSave"/> ends on the thread of the document.
    /// </summary>
    public SaveJob PrepareSave(int jpegQuality = 90, int webpQuality = ImageIO.WebPLossless)
    {
        var (basePath, jsonPath) = SidecarFiles.PathsOf(FilePath);
        var flattened = Flatten();
        if (Annotations.Count == 0 && Effects.IsEmpty)
            return new SaveJob(FilePath, Version, flattened, jpegQuality, webpQuality);

        var annotationsJson = JsonSerializer.SerializeToUtf8Bytes(Annotations, AnnotationJson.Options);
        byte[]? effectsJson = Effects.IsEmpty ? null : JsonSerializer.SerializeToUtf8Bytes(Effects, AnnotationJson.Options);
        var redaction = RedactionKey();
        bool writeBase = !ReferenceEquals(BaseImage, _writtenBase) || redaction != _writtenRedaction || !File.Exists(basePath);
        return new SaveJob(FilePath, Version, flattened, jpegQuality, webpQuality)
        {
            Sidecar = (basePath, jsonPath, annotationsJson, effectsJson),
            RedactedBase = writeBase ? RedactedBase() : null,
            BaseImage = BaseImage,
            Redaction = redaction,
        };
    }

    /// <summary>
    /// Last part of <see cref="Save"/>, once <see cref="SaveJob.Write"/> succeeded: the document is clean if it did not
    /// change since <see cref="PrepareSave"/>.
    /// </summary>
    public void CompleteSave(SaveJob job)
    {
        if (job.BaseWritten)
        {
            _writtenBase = job.BaseImage;
            _writtenRedaction = job.Redaction;
        }
        _savedVersion = job.Version;
        RaiseChanged();
    }

    /// <summary>What <see cref="Save"/> writes, read from the document by <see cref="PrepareSave"/>.</summary>
    public sealed class SaveJob
    {
        private readonly string _filePath;
        private readonly PixelBuffer _flattened;
        private readonly int _jpegQuality, _webpQuality;

        internal SaveJob(string filePath, long version, PixelBuffer flattened, int jpegQuality, int webpQuality)
        {
            _filePath = filePath;
            Version = version;
            _flattened = flattened;
            _jpegQuality = jpegQuality;
            _webpQuality = webpQuality;
        }

        internal long Version { get; }
        /// <summary>Null without annotations and effects: the capture alone, its sidecar files deleted.</summary>
        internal (string BasePath, string JsonPath, byte[] Annotations, byte[]? Effects)? Sidecar { get; init; }
        /// <summary>The base image to write, null when the one in the sidecar folder is still right.</summary>
        internal PixelBuffer? RedactedBase { get; init; }
        internal EditorImage? BaseImage { get; init; }
        internal string? Redaction { get; init; }
        internal bool BaseWritten { get; private set; }

        /// <summary>Encodes and writes the files; any thread, once.</summary>
        public void Write()
        {
            if (Sidecar is not { } sidecar)
            {
                ImageIO.Save(_flattened, _filePath, _jpegQuality, _webpQuality);
                SidecarFiles.Delete(_filePath);
            }
            else
            {
                SidecarFiles.EnsureFolder(Path.GetDirectoryName(sidecar.BasePath)!);
                // The new base waits beside the old one until the capture is written: a failure in between leaves the
                // capture, its base and its annotation file as they were, still matching.
                var baseTemp = RedactedBase != null ? sidecar.BasePath + SidecarFiles.TempSuffix : null;
                try
                {
                    if (RedactedBase != null) ImageIO.Save(RedactedBase, baseTemp!);
                    ImageIO.Save(_flattened, _filePath, _jpegQuality, _webpQuality);
                    if (baseTemp != null)
                    {
                        File.Move(baseTemp, sidecar.BasePath, overwrite: true);
                        BaseWritten = true;
                    }
                }
                catch
                {
                    if (baseTemp != null) SidecarFiles.DeleteQuietly(baseTemp);
                    throw;
                }
                WriteSidecarJson(sidecar.JsonPath, new FileInfo(_filePath), new FileInfo(sidecar.BasePath).Length, sidecar.Annotations, sidecar.Effects);
            }
            SidecarFiles.CleanOrphans(Path.GetDirectoryName(_filePath)!);
        }
    }

    /// <param name="effectsJson">Null without effects: the file then keeps the format of version 1, still read by older versions.</param>
    private static void WriteSidecarJson(string jsonPath, FileInfo file, long baseLength, byte[] annotationsJson, byte[]? effectsJson)
    {
        var temp = jsonPath + SidecarFiles.TempSuffix;
        try
        {
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
        catch
        {
            SidecarFiles.DeleteQuietly(temp);
            throw;
        }
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
