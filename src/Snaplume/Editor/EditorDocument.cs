using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>
/// A capture being edited: base image + vector annotations + image effects + undo history.
/// The flattened result overwrites the capture file, while the base image and the
/// annotations are kept in a hidden ".snaplume" folder to stay editable.
/// The base image kept there already has the pixelated areas burnt in, so that a hidden
/// secret never stays readable on disk.
/// </summary>
public sealed class EditorDocument
{
    /// <summary>Largest width or height of an image (resize); the number of pixels is limited by <see cref="ImageIO.MaxPixels"/>.</summary>
    public const int MaxDimension = 30000;

    private const string SidecarFolder = ".snaplume";
    private const string LegacySidecarFolder = ".librecapture";
    private const string BaseSuffix = ".base.png";
    private const string JsonSuffix = ".json";
    private const int MaxUndoSteps = 100;
    /// <summary>Bigger sidecar files are ignored: they cannot come from the editor.</summary>
    private const long MaxSidecarLength = 64 * 1024 * 1024;

    /// <summary>Folders whose legacy sidecar folder was already looked for in this session.</summary>
    private static readonly HashSet<string> MigratedFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>State of the document, for undo and for modifications that may be abandoned (see <see cref="BeginChange"/>).</summary>
    public sealed class Snapshot
    {
        internal Snapshot(BitmapSource baseImage, List<Annotation> annotations, ImageEffects effects, long version)
        {
            BaseImage = baseImage;
            Annotations = annotations;
            Effects = effects;
            Version = version;
        }

        internal BitmapSource BaseImage { get; }
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
    private BitmapSource? _writtenBase;
    private string? _writtenRedaction;

    public string FilePath { get; }
    public BitmapSource BaseImage { get; private set; }
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

    private EditorDocument(string path, BitmapSource baseImage, List<Annotation> annotations)
    {
        FilePath = path;
        BaseImage = baseImage;
        Annotations = annotations;
    }

    public static EditorDocument Load(string path)
    {
        var dir = Path.GetDirectoryName(path)!;
        MigrateLegacySidecars(dir);
        CleanOrphanSidecars(dir);
        var document = TryLoadSidecar(path) ?? new EditorDocument(path, ImageIO.Load(path), new List<Annotation>());
        document.UpdatePreviewLayout();
        return document;
    }

    /// <summary>Rebuilds <see cref="PreviewLayout"/> when the effects or the image size changed; never throws.</summary>
    private void UpdatePreviewLayout()
    {
        var key = (Effects, BaseImage.PixelWidth, BaseImage.PixelHeight);
        if (_previewLayoutFor == key) return;
        _previewLayoutFor = key;
        PreviewShadowFailed = false;
        try
        {
            _previewLayout = EffectsRenderer.For(key.Effects, key.PixelWidth, key.PixelHeight);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Aperçu des effets", ex);
            PreviewShadowFailed = key.Effects.Shadow;
            try
            {
                _previewLayout = EffectsRenderer.For(key.Effects with { Shadow = false }, key.PixelWidth, key.PixelHeight);
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
        var (basePath, jsonPath) = SidecarPaths(path);
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
            var document = new EditorDocument(path, ImageIO.Load(basePath), annotations) { Effects = effects };
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
            var (width, height) = OutputSizeOf(BaseImage.PixelWidth, BaseImage.PixelHeight, Effects);
            return new Size(width, height);
        }
    }

    public void Crop(Int32Rect area)
    {
        int x = Math.Clamp(area.X, 0, BaseImage.PixelWidth - 1);
        int y = Math.Clamp(area.Y, 0, BaseImage.PixelHeight - 1);
        area = new Int32Rect(x, y,
            Math.Clamp(area.Width, 1, BaseImage.PixelWidth - x),
            Math.Clamp(area.Height, 1, BaseImage.PixelHeight - y));
        var cropped = ImageIO.Normalize(new CroppedBitmap(BaseImage, area));
        PushUndo();
        BaseImage = cropped;
        var offset = new Vector(-area.X, -area.Y);
        foreach (var annotation in Annotations) annotation.Translate(offset);
        // Annotations left entirely outside would be invisible but still saved.
        var bounds = new Rect(0, 0, area.Width, area.Height);
        Annotations.RemoveAll(a => !a.GetBounds().IntersectsWith(bounds));
        foreach (var magnifier in Annotations.OfType<MagnifierAnnotation>().ToList())
        {
            var source = Rect.Intersect(magnifier.GetRect(), bounds);
            // A lens whose area left the image would only show the background.
            if (source.IsEmpty || source.Width <= 0 || source.Height <= 0) Annotations.Remove(magnifier);
            else if (!magnifier.GetLensRect().IntersectsWith(bounds)) magnifier.PlaceLens(bounds);
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
        Effects.Scaled(((double)width / BaseImage.PixelWidth + (double)height / BaseImage.PixelHeight) / 2);

    /// <summary>Resizes the image (high quality resampling); annotations and effect sizes follow.</summary>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxDimension);
        height = Math.Clamp(height, 1, MaxDimension);
        if (width == BaseImage.PixelWidth && height == BaseImage.PixelHeight) return;
        // Checked before anything is allocated: the saved image also has the margins of the shadow.
        var effects = ResizedEffects(width, height);
        var (outputWidth, outputHeight) = OutputSizeOf(width, height, effects);
        ImageIO.CheckSize(outputWidth, outputHeight);
        double sx = (double)width / BaseImage.PixelWidth, sy = (double)height / BaseImage.PixelHeight;

        // Computed first: running out of memory must leave the document and its history untouched.
        var resized = Render(width, height, dc => dc.DrawImage(BaseImage, new Rect(0, 0, width, height)));

        PushUndo();
        BaseImage = resized;
        Effects = effects;
        foreach (var annotation in Annotations) annotation.Scale(sx, sy);
        RaiseChanged();
    }

    /// <summary>
    /// Burns an inserted image (pasted, opened or captured) into the base image at <paramref name="area"/>,
    /// as one undo step. The annotations stay above it; what falls outside the image is cut off.
    /// </summary>
    public void InsertImage(BitmapSource image, Rect area)
    {
        int w = BaseImage.PixelWidth, h = BaseImage.PixelHeight;
        // Computed first: running out of memory must leave the document and its history untouched.
        var merged = Render(w, h, dc =>
        {
            dc.DrawImage(BaseImage, new Rect(0, 0, w, h));
            dc.DrawImage(image, area);
        });
        PushUndo();
        BaseImage = merged;
        RaiseChanged();
    }

    /// <summary>
    /// Enlarges the image to <paramref name="width"/> x <paramref name="height"/> (never smaller), the new area
    /// filled with <paramref name="background"/>. The image and its annotations keep their place, at the top left.
    /// </summary>
    /// <exception cref="InvalidDataException">The result, margins of the effects included, would be too big.</exception>
    public void ExtendCanvas(int width, int height, Color background)
    {
        int w = BaseImage.PixelWidth, h = BaseImage.PixelHeight;
        width = Math.Clamp(width, w, Math.Max(w, MaxDimension));
        height = Math.Clamp(height, h, Math.Max(h, MaxDimension));
        if (width == w && height == h) return;
        var (outputWidth, outputHeight) = OutputSizeOf(width, height, Effects);
        ImageIO.CheckSize(outputWidth, outputHeight);
        var extended = Render(width, height, dc =>
        {
            dc.DrawRectangle(new SolidColorBrush(background), null, new Rect(0, 0, width, height));
            dc.DrawImage(BaseImage, new Rect(0, 0, w, h));
        });
        PushUndo();
        BaseImage = extended;
        RaiseChanged();
    }

    /// <summary>Draws a new base image (high quality resampling), as 96 DPI Bgra32.</summary>
    private static BitmapSource Render(int width, int height, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) draw(dc);
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        return ImageIO.Normalize(target);
    }

    /// <summary>Base image with all annotations burnt in, and the image effects around it.</summary>
    public BitmapSource Flatten() => RenderImage(Annotations, Effects);

    /// <summary>Base image with only the pixelated areas burnt in: what is kept in the sidecar folder.</summary>
    private BitmapSource RedactedBase()
    {
        var pixelated = Annotations.OfType<PixelateAnnotation>().Cast<Annotation>().ToList();
        return pixelated.Count == 0 ? BaseImage : RenderImage(pixelated, ImageEffects.None);
    }

    private BitmapSource RenderImage(IReadOnlyList<Annotation> annotations, ImageEffects effects)
    {
        int w = BaseImage.PixelWidth, h = BaseImage.PixelHeight;
        // Checks the size of the output, margins included, before building anything.
        var layout = EffectsRenderer.For(effects, w, h);
        void DrawImage(DrawingContext dc)
        {
            dc.DrawImage(BaseImage, new Rect(0, 0, w, h));
            AnnotationRenderer.Render(dc, BaseImage, annotations);
        }

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            if (layout != null)
            {
                layout.Render(dc, DrawImage);
            }
            else
            {
                dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
                DrawImage(dc);
                dc.Pop();
            }
        }
        if (layout != null)
        {
            w = layout.OutputWidth;
            h = layout.OutputHeight;
        }
        var target = new RenderTargetBitmap(w, h, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
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
        var (basePath, jsonPath) = SidecarPaths(FilePath);
        var flattened = Flatten();
        if (Annotations.Count == 0 && Effects.IsEmpty)
        {
            ImageIO.Save(flattened, FilePath, jpegQuality);
            DeleteSidecar(FilePath);
        }
        else
        {
            var annotationsJson = JsonSerializer.SerializeToUtf8Bytes(Annotations, AnnotationJson.Options);
            byte[]? effectsJson = Effects.IsEmpty ? null : JsonSerializer.SerializeToUtf8Bytes(Effects, AnnotationJson.Options);
            var redaction = RedactionKey();
            bool writeBase = !ReferenceEquals(BaseImage, _writtenBase) || redaction != _writtenRedaction || !File.Exists(basePath);
            var redactedBase = writeBase ? RedactedBase() : null;

            var info = Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            info.Attributes |= FileAttributes.Hidden;
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
        CleanOrphanSidecars(Path.GetDirectoryName(FilePath)!);
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

    public static (string BasePath, string JsonPath) SidecarPaths(string file)
    {
        var folder = Path.Combine(Path.GetDirectoryName(file)!, SidecarFolder);
        var name = Path.GetFileName(file);
        return (Path.Combine(folder, name + BaseSuffix), Path.Combine(folder, name + JsonSuffix));
    }

    /// <summary>Renames the sidecar folder of older versions (".librecapture"), once per folder and session.</summary>
    public static void MigrateLegacySidecars(string dir)
    {
        lock (MigratedFolders)
        {
            if (!MigratedFolders.Add(Path.GetFullPath(dir))) return;
        }
        var folder = Path.Combine(dir, SidecarFolder);
        var legacy = Path.Combine(dir, LegacySidecarFolder);
        if (Directory.Exists(folder) || !Directory.Exists(legacy)) return;
        try { Directory.Move(legacy, folder); }
        catch (Exception ex) { ErrorLog.Write("Migration des annotations", ex); }
    }

    /// <summary>Deletes the sidecar files whose capture no longer exists (deleted or renamed outside Snaplume).</summary>
    private static void CleanOrphanSidecars(string dir)
    {
        try
        {
            var folder = Path.Combine(dir, SidecarFolder);
            if (!Directory.Exists(folder)) return;
            foreach (var file in Directory.EnumerateFiles(folder))
            {
                var name = Path.GetFileName(file);
                string? capture = name.EndsWith(BaseSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^BaseSuffix.Length]
                    : name.EndsWith(JsonSuffix, StringComparison.OrdinalIgnoreCase) ? name[..^JsonSuffix.Length]
                    : null;
                if (string.IsNullOrEmpty(capture) || File.Exists(Path.Combine(dir, capture))) continue;
                File.Delete(file);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Nettoyage des annotations orphelines", ex);
        }
    }

    public static void DeleteSidecar(string file)
    {
        MigrateLegacySidecars(Path.GetDirectoryName(file)!);
        var (basePath, jsonPath) = SidecarPaths(file);
        if (File.Exists(basePath)) File.Delete(basePath);
        if (File.Exists(jsonPath)) File.Delete(jsonPath);
    }

    public static void RenameSidecar(string oldFile, string newFile)
    {
        MigrateLegacySidecars(Path.GetDirectoryName(oldFile)!);
        MigrateLegacySidecars(Path.GetDirectoryName(newFile)!);
        var (oldBase, oldJson) = SidecarPaths(oldFile);
        var (newBase, newJson) = SidecarPaths(newFile);
        if (File.Exists(oldBase)) File.Move(oldBase, newBase, overwrite: true);
        if (File.Exists(oldJson)) File.Move(oldJson, newJson, overwrite: true);
    }

    private void Restore(Snapshot snapshot)
    {
        BaseImage = snapshot.BaseImage;
        Annotations = snapshot.Annotations;
        Effects = snapshot.Effects;
        Version = snapshot.Version;
        RaiseChanged();
    }
}
