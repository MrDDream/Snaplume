using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>
/// A capture being edited: base image + vector annotations + undo history.
/// The flattened result overwrites the capture file, while the base image and the
/// annotations are kept in a hidden ".snaplume" folder to stay editable.
/// The base image kept there already has the pixelated areas burnt in, so that a hidden
/// secret never stays readable on disk.
/// </summary>
public sealed class EditorDocument
{
    /// <summary>Largest width or height of an image (resize).</summary>
    public const int MaxDimension = 30000;
    /// <summary>Largest width × height of an image (resize): about 1 GB in memory.</summary>
    public const long MaxPixels = 250_000_000;

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
        internal Snapshot(BitmapSource baseImage, List<Annotation> annotations, long version)
        {
            BaseImage = baseImage;
            Annotations = annotations;
            Version = version;
        }

        internal BitmapSource BaseImage { get; }
        internal List<Annotation> Annotations { get; }
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
    /// <summary>Number of the current state (see <see cref="AmendLastChange"/>).</summary>
    public long Version { get; private set; }
    public bool IsDirty => Version != _savedVersion;
    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public int NextStepNumber =>
        Math.Min(Annotations.OfType<StepAnnotation>().Select(s => s.Number).DefaultIfEmpty(0).Max(), StepAnnotation.MaxNumber - 1) + 1;

    public event Action? Changed;

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
        return TryLoadSidecar(path) ?? new EditorDocument(path, ImageIO.Load(path), new List<Annotation>());
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
            var document = new EditorDocument(path, ImageIO.Load(basePath), annotations);
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
    public Snapshot BeginChange() => new(BaseImage, Annotations.Select(a => a.Clone()).ToList(), Version);

    /// <summary>Records the modifications made since <paramref name="before"/> as one undo step.</summary>
    public void CommitChange(Snapshot before)
    {
        AddUndoStep(before);
        _redo.Clear();
        Version = ++_lastVersion;
        Changed?.Invoke();
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
        Changed?.Invoke();
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

    public void NotifyChanged() => Changed?.Invoke();

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
        Changed?.Invoke();
    }

    /// <summary>Resizes the image (high quality resampling); annotations follow.</summary>
    public void Resize(int width, int height)
    {
        width = Math.Clamp(width, 1, MaxDimension);
        height = Math.Clamp(height, 1, MaxDimension);
        if ((long)width * height > MaxPixels) throw new ArgumentOutOfRangeException(nameof(width), "Image trop grande");
        if (width == BaseImage.PixelWidth && height == BaseImage.PixelHeight) return;
        double sx = (double)width / BaseImage.PixelWidth, sy = (double)height / BaseImage.PixelHeight;

        // Computed first: running out of memory must leave the document and its history untouched.
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var dc = visual.RenderOpen()) dc.DrawImage(BaseImage, new Rect(0, 0, width, height));
        var target = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        var resized = ImageIO.Normalize(target);

        PushUndo();
        BaseImage = resized;
        foreach (var annotation in Annotations) annotation.Scale(sx, sy);
        Changed?.Invoke();
    }

    /// <summary>Base image with all annotations burnt in.</summary>
    public BitmapSource Flatten() => RenderImage(Annotations);

    /// <summary>Base image with only the pixelated areas burnt in: what is kept in the sidecar folder.</summary>
    private BitmapSource RedactedBase()
    {
        var pixelated = Annotations.OfType<PixelateAnnotation>().Cast<Annotation>().ToList();
        return pixelated.Count == 0 ? BaseImage : RenderImage(pixelated);
    }

    private BitmapSource RenderImage(IReadOnlyList<Annotation> annotations)
    {
        int w = BaseImage.PixelWidth, h = BaseImage.PixelHeight;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushClip(new RectangleGeometry(new Rect(0, 0, w, h)));
            dc.DrawImage(BaseImage, new Rect(0, 0, w, h));
            AnnotationRenderer.Render(dc, BaseImage, annotations);
            dc.Pop();
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
    /// Writes in an order that keeps the annotations editable if something fails: the annotations are
    /// serialized first (nothing is written if that fails), then the base image, the flattened image and
    /// last the annotation file, replaced atomically. A sidecar that does not match its images is ignored.
    /// </summary>
    public void Save(int jpegQuality = 90)
    {
        var (basePath, jsonPath) = SidecarPaths(FilePath);
        if (Annotations.Count == 0)
        {
            ImageIO.Save(Flatten(), FilePath, jpegQuality);
            DeleteSidecar(FilePath);
        }
        else
        {
            var annotationsJson = JsonSerializer.SerializeToUtf8Bytes(Annotations, AnnotationJson.Options);

            var info = Directory.CreateDirectory(Path.GetDirectoryName(basePath)!);
            info.Attributes |= FileAttributes.Hidden;
            var redaction = RedactionKey();
            if (!ReferenceEquals(BaseImage, _writtenBase) || redaction != _writtenRedaction || !File.Exists(basePath))
            {
                ImageIO.Save(RedactedBase(), basePath);
                _writtenBase = BaseImage;
                _writtenRedaction = redaction;
            }

            ImageIO.Save(Flatten(), FilePath, jpegQuality);
            WriteSidecarJson(jsonPath, new FileInfo(FilePath), new FileInfo(basePath).Length, annotationsJson);
        }

        _savedVersion = Version;
        CleanOrphanSidecars(Path.GetDirectoryName(FilePath)!);
        Changed?.Invoke();
    }

    private static void WriteSidecarJson(string jsonPath, FileInfo file, long baseLength, byte[] annotationsJson)
    {
        var temp = jsonPath + ".tmp";
        using (var stream = File.Create(temp))
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteNumber(nameof(SidecarData.Version), SidecarData.CurrentVersion);
            writer.WriteNumber(nameof(SidecarData.FileLength), file.Length);
            writer.WriteString(nameof(SidecarData.FileWriteTimeUtc), file.LastWriteTimeUtc);
            writer.WriteNumber(nameof(SidecarData.BaseFileLength), baseLength);
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
        Version = snapshot.Version;
        Changed?.Invoke();
    }
}
