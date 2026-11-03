using System.Text.Json;
using System.Text.Json.Serialization;

namespace Snaplume.Core;

public sealed class AppSettings
{
    /// <summary>Interface language code ("fr", "en"...), empty for the system language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"auto" (follows the system), "light" or "dark".</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>Root capture folder (the "Base" workspace).</summary>
    public string WorkspaceFolder { get; set; } = DefaultWorkspaceFolder;
    /// <summary>Active workspace: a direct sub-folder of <see cref="WorkspaceFolder"/> or a custom one, empty for the root.</summary>
    public string ActiveWorkspace { get; set; } = "";
    /// <summary>
    /// Workspaces whose captures are in a folder of the user's choice (name → absolute path), anywhere; the others are
    /// the sub-folders of <see cref="WorkspaceFolder"/>. Absent from older settings files: no custom folder.
    /// </summary>
    public Dictionary<string, string> WorkspaceFolders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string FileNamePrefix { get; set; } = "Capture_";
    /// <summary>"png", "jpg" or "webp".</summary>
    public string ImageFormat { get; set; } = "png";
    public int JpegQuality { get; set; } = 90;
    /// <summary>100: lossless WebP (the default); 10 to 99: lossy WebP of that quality (Imaging.ImageIO.WebPLossless).</summary>
    public int WebpQuality { get; set; } = 100;

    /// <summary>Extension of the captures saved in <see cref="ImageFormat"/> (".png", ".jpg" or ".webp").</summary>
    [JsonIgnore]
    public string ImageExtension => ImageFormat switch
    {
        "jpg" => ".jpg",
        "webp" => ".webp",
        _ => ".png",
    };

    /// <summary>"Show the workspaces" of the quick panel: the side panel is open.</summary>
    public bool PanelWorkspacesOpen { get; set; }

    public bool OpenEditorAfterCapture { get; set; }
    public bool CopyToClipboardAfterCapture { get; set; } = true;
    /// <summary>Shows the quick panel after a capture (stored under its former name).</summary>
    [JsonPropertyName("ShowWorkspaceAfterCapture")]
    public bool ShowPanelAfterCapture { get; set; } = true;
    public bool IncludeCursor { get; set; }
    public int DelaySeconds { get; set; } = 3;
    /// <summary>Starts with the user session, in the tray (stored under its former name).</summary>
    [JsonPropertyName("StartWithWindows")]
    public bool StartWithSession { get; set; }

    public string HotkeyRegion { get; set; } = "PrintScreen";
    public string HotkeyScreen { get; set; } = "Shift+PrintScreen";
    public string HotkeyWindow { get; set; } = "Alt+PrintScreen";
    public string HotkeyRepeat { get; set; } = "Ctrl+PrintScreen";
    public string HotkeyDelayed { get; set; } = "Ctrl+Shift+PrintScreen";
    public string HotkeyVideo { get; set; } = "Ctrl+Alt+PrintScreen";
    /// <summary>Text capture (OCR); none by default, PrintScreen combinations are all taken.</summary>
    public string HotkeyText { get; set; } = "";
    /// <summary>Win+Shift+S captures an area with Snaplume instead of opening the Snipping Tool (Windows).</summary>
    public bool ReplaceSnippingShortcut { get; set; }
    /// <summary>OCR language tag ("fr-FR"...), empty for the system language.</summary>
    public string OcrLanguage { get; set; } = "";

    public bool VideoSystemAudio { get; set; } = true;
    public bool VideoMicrophone { get; set; }
    /// <summary>Microphone id (see IScreenRecorderFactory.Microphones), empty for the system's default one.</summary>
    public string VideoMicrophoneDevice { get; set; } = "";
    public bool VideoShowClicks { get; set; }
    /// <summary>The video zooms in briefly around each mouse click.</summary>
    public bool VideoClickZoom { get; set; }
    public int VideoFramerate { get; set; } = 30;
    /// <summary>Webcam inlaid in a corner of the recording (picture-in-picture).</summary>
    public bool VideoWebcam { get; set; }
    /// <summary>Camera device name (ScreenRecorderLib), empty for the first camera found.</summary>
    public string VideoWebcamDevice { get; set; } = "";
    /// <summary>"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</summary>
    public string VideoWebcamCorner { get; set; } = "BottomRight";
    /// <summary>Width of the webcam picture, in % of the width of the recorded area.</summary>
    public int VideoWebcamSize { get; set; } = 20;

    // Format of the recordings (Settings › Video › Video format, see Player.RecordingFormats). The defaults record
    // MP4 H.264 as always, with no conversion afterwards.
    /// <summary>"mp4", "hevc", "av1" or "webm" (Player.WebFormats.Id; animated WebP is not a recording format).</summary>
    public string VideoFormat { get; set; } = "mp4";
    /// <summary>"none", "high", "medium", "light", "discord", "nitro", "email" or "custom" (Player.RecordingCompression).</summary>
    public string VideoCompression { get; set; } = "none";
    /// <summary>Largest file for "custom", in MB.</summary>
    public double VideoCompressionMegabytes { get; set; } = 25;
    /// <summary>A recording converted afterwards keeps its MP4 H.264 original next to the converted file.</summary>
    public bool VideoKeepOriginal { get; set; }

    // Last choices of the player's "Export / compress" dialog (see Player.WebExportForm).
    /// <summary>"mp4", "hevc", "av1", "webm" or "webp" (Player.WebFormats.Id).</summary>
    public string WebExportFormat { get; set; } = "mp4";
    /// <summary>"high", "medium" or "light", used without a size to reach.</summary>
    public string WebExportQuality { get; set; } = "medium";
    /// <summary>"off", "discord", "nitro", "email" or "custom" (Player.SizeTargets).</summary>
    public string WebExportTarget { get; set; } = "off";
    /// <summary>Size typed for "custom", in MB.</summary>
    public double WebExportMegabytes { get; set; } = 10;

    /// <summary>
    /// Asks GitHub once a day whether a newer version was published (see <see cref="UpdateChecker"/>). Off by
    /// default: Snaplume contacts nothing unless the user asks for it.
    /// </summary>
    public bool CheckForUpdates { get; set; }
    public DateTime LastUpdateCheckUtc { get; set; }
    /// <summary>Version already announced by a notification: each new version is announced once.</summary>
    public string UpdateNotifiedVersion { get; set; } = "";

    /// <summary>Corners the webcam picture can be placed in (values of <see cref="VideoWebcamCorner"/>).</summary>
    public static readonly string[] WebcamCorners = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

    public static string DefaultWorkspaceFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snaplume");

    /// <summary>
    /// Settings live next to the executable when a "portable" marker file is present
    /// (run without installation), otherwise in %APPDATA%\Snaplume (Windows) or ~/.config/Snaplume (Linux).
    /// </summary>
    public static string SettingsFolder { get; } = IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snaplume");

    internal static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, PortableUpdate.Marker));

    private static string SettingsFile => Path.Combine(SettingsFolder, "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Settings folder used before the project was renamed Snaplume.</summary>
    private const string LegacyName = "LibreCapture";

    public static AppSettings Load()
    {
        var migrated = MigrateLegacyFolder();
        AppSettings? settings = null;
        if (File.Exists(SettingsFile))
        {
            try
            {
                settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsFile), JsonOptions);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Lecture des paramètres", ex);
                // The defaults will overwrite the file on the next save: keep a copy for the user.
                BackUpUnreadableFile();
            }
        }
        settings ??= new AppSettings();
        settings.Sanitize();
        // The installer may have registered the start with the session: show it checked, so that saving keeps it.
        if (!settings.StartWithSession && AppServices.HasPlatform && AppServices.Platform.Shell.StartsWithSession) settings.StartWithSession = true;
        if (migrated && settings.StartWithSession)
        {
            try
            {
                settings.ApplyStartWithSession();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Démarrage avec la session", ex);
            }
        }
        return settings;
    }

    private static void BackUpUnreadableFile()
    {
        try
        {
            File.Copy(SettingsFile, SettingsFile + ".bak", overwrite: true);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie des paramètres illisibles", ex);
        }
    }

    /// <summary>
    /// Repairs values a hand-edited or older settings.json may contain: null texts,
    /// relative folders, out-of-range numbers.
    /// </summary>
    public void Sanitize()
    {
        var defaults = new AppSettings();
        Language ??= defaults.Language;
        Theme ??= defaults.Theme;
        FileNamePrefix ??= defaults.FileNamePrefix;
        ImageFormat = ImageFormat is "png" or "jpg" or "webp" ? ImageFormat : defaults.ImageFormat;
        HotkeyRegion ??= defaults.HotkeyRegion;
        HotkeyScreen ??= defaults.HotkeyScreen;
        HotkeyWindow ??= defaults.HotkeyWindow;
        HotkeyRepeat ??= defaults.HotkeyRepeat;
        HotkeyDelayed ??= defaults.HotkeyDelayed;
        HotkeyVideo ??= defaults.HotkeyVideo;
        HotkeyText ??= defaults.HotkeyText;
        OcrLanguage ??= defaults.OcrLanguage;
        UpdateNotifiedVersion ??= defaults.UpdateNotifiedVersion;
        if (LastUpdateCheckUtc > DateTime.UtcNow) LastUpdateCheckUtc = default;

        WorkspaceFolder = NormalizeFolder(WorkspaceFolder) ?? defaults.WorkspaceFolder;
        ActiveWorkspace = ActiveWorkspace?.Trim() is { } active && Workspaces.IsPlainName(active) ? active : "";
        // Valid names and absolute folders only, names compared without case like the sub-folders.
        var customFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, folder) in WorkspaceFolders ?? new())
        {
            if (name is { } n && Workspaces.IsPlainName(n) && !n.StartsWith('.') && NormalizeFolder(folder) is { } full) customFolders.TryAdd(n, full);
        }
        WorkspaceFolders = customFolders;

        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        WebpQuality = Math.Clamp(WebpQuality, 10, 100);
        DelaySeconds = Math.Clamp(DelaySeconds, 0, 60);
        VideoFramerate = Math.Clamp(VideoFramerate, 5, 60);
        VideoMicrophoneDevice = VideoMicrophoneDevice?.Trim() ?? defaults.VideoMicrophoneDevice;
        VideoWebcamDevice = VideoWebcamDevice?.Trim() ?? defaults.VideoWebcamDevice;
        VideoWebcamCorner = WebcamCorners.FirstOrDefault(c => string.Equals(c, VideoWebcamCorner, StringComparison.OrdinalIgnoreCase))
                            ?? defaults.VideoWebcamCorner;
        VideoWebcamSize = Math.Clamp(VideoWebcamSize, 10, 40);
        WebExportFormat = Player.WebFormats.Id(Player.WebFormats.FromId(WebExportFormat));
        // "original" is the conversion of recordings, not a choice of the export dialog.
        WebExportQuality = Player.WebFormats.QualityFromId(WebExportQuality) is var quality && quality != Player.WebQuality.Original
            ? Player.WebFormats.QualityId(quality)
            : defaults.WebExportQuality;
        VideoFormat = Player.WebFormats.Id(Player.RecordingFormats.FromId(VideoFormat));
        VideoCompression = Player.RecordingCompression.Normalize(VideoCompression);
        VideoCompressionMegabytes = double.IsFinite(VideoCompressionMegabytes)
            ? Math.Clamp(VideoCompressionMegabytes, Player.SizeTargets.MinMegabytes, Player.SizeTargets.MaxMegabytes)
            : defaults.VideoCompressionMegabytes;
        WebExportTarget = WebExportTarget is Player.SizeTargets.Discord or Player.SizeTargets.Nitro or Player.SizeTargets.Email or Player.SizeTargets.Custom
            ? WebExportTarget
            : Player.SizeTargets.Off;
        WebExportMegabytes = double.IsFinite(WebExportMegabytes)
            ? Math.Clamp(WebExportMegabytes, Player.SizeTargets.MinMegabytes, Player.SizeTargets.MaxMegabytes)
            : defaults.WebExportMegabytes;
    }

    /// <summary>Full path of a folder, or null if it is relative or invalid.</summary>
    private static string? NormalizeFolder(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder)) return null;
        try
        {
            return Path.GetFullPath(folder);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Moves %APPDATA%\LibreCapture to %APPDATA%\Snaplume on first launch after the rename.</summary>
    private static bool MigrateLegacyFolder()
    {
        if (IsPortable) return false;
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), LegacyName);
        if (Directory.Exists(SettingsFolder) || !Directory.Exists(legacy)) return false;
        try
        {
            Directory.Move(legacy, SettingsFolder);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Migration des paramètres", ex);
            return false;
        }
    }

    /// <summary>Configured folder replaced for this session by <see cref="UseFallbackFolder"/>.</summary>
    private string? _unavailableFolder;
    private string? _fallbackFolder;

    /// <summary>
    /// Uses another capture folder for this session only, when the configured one cannot be
    /// created (USB drive or network share not connected): settings.json keeps the configured one.
    /// </summary>
    public void UseFallbackFolder(string folder)
    {
        _unavailableFolder ??= WorkspaceFolder;
        _fallbackFolder = folder;
        WorkspaceFolder = folder;
    }

    /// <summary>Writes the settings atomically (a crash never leaves a truncated file). Throws on failure.</summary>
    public void Save()
    {
        var data = this;
        if (_unavailableFolder != null && string.Equals(WorkspaceFolder, _fallbackFolder, StringComparison.OrdinalIgnoreCase))
        {
            data = Clone();
            data.WorkspaceFolder = _unavailableFolder;
        }
        Directory.CreateDirectory(SettingsFolder);
        var temp = SettingsFile + ".tmp";
        try
        {
            // Flushed to the disk before the rename: a power cut then leaves the old file or the new one, never an empty one.
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, data, JsonOptions);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temp, SettingsFile, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // The original error matters more.
            }
            throw;
        }
    }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Turns the start with the user session on or off, as <see cref="StartWithSession"/> says.</summary>
    public void ApplyStartWithSession()
    {
        if (AppServices.Platform.Has(Platform.PlatformFeatures.StartWithSession))
            AppServices.Platform.Shell.SetStartWithSession(StartWithSession);
    }
}
