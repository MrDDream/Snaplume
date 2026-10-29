using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;

namespace Snaplume.Core;

public sealed class AppSettings
{
    /// <summary>Interface language code ("fr", "en"...), empty for the Windows language.</summary>
    public string Language { get; set; } = "";
    /// <summary>"auto" (follows Windows), "light" or "dark".</summary>
    public string Theme { get; set; } = "auto";
    /// <summary>Root capture folder (the "Base" workspace).</summary>
    public string WorkspaceFolder { get; set; } = DefaultWorkspaceFolder;
    /// <summary>Active workspace: a direct sub-folder of <see cref="WorkspaceFolder"/>, empty for the root.</summary>
    public string ActiveWorkspace { get; set; } = "";
    public string FileNamePrefix { get; set; } = "Capture_";
    /// <summary>"png" or "jpg".</summary>
    public string ImageFormat { get; set; } = "png";
    public int JpegQuality { get; set; } = 90;

    public bool OpenEditorAfterCapture { get; set; }
    public bool CopyToClipboardAfterCapture { get; set; } = true;
    /// <summary>Shows the quick panel after a capture (stored under its former name).</summary>
    [JsonPropertyName("ShowWorkspaceAfterCapture")]
    public bool ShowPanelAfterCapture { get; set; } = true;
    public bool IncludeCursor { get; set; }
    public int DelaySeconds { get; set; } = 3;
    public bool StartWithWindows { get; set; }

    public string HotkeyRegion { get; set; } = "PrintScreen";
    public string HotkeyScreen { get; set; } = "Shift+PrintScreen";
    public string HotkeyWindow { get; set; } = "Alt+PrintScreen";
    public string HotkeyRepeat { get; set; } = "Ctrl+PrintScreen";
    public string HotkeyDelayed { get; set; } = "Ctrl+Shift+PrintScreen";
    public string HotkeyVideo { get; set; } = "Ctrl+Alt+PrintScreen";
    /// <summary>Text capture (OCR); none by default, PrintScreen combinations are all taken.</summary>
    public string HotkeyText { get; set; } = "";
    /// <summary>OCR language tag ("fr-FR"...), empty for the Windows language.</summary>
    public string OcrLanguage { get; set; } = "";

    public bool VideoSystemAudio { get; set; } = true;
    public bool VideoMicrophone { get; set; }
    public bool VideoShowClicks { get; set; }
    public int VideoFramerate { get; set; } = 30;
    /// <summary>Webcam inlaid in a corner of the recording (picture-in-picture).</summary>
    public bool VideoWebcam { get; set; }
    /// <summary>Camera device name (ScreenRecorderLib), empty for the first camera found.</summary>
    public string VideoWebcamDevice { get; set; } = "";
    /// <summary>"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</summary>
    public string VideoWebcamCorner { get; set; } = "BottomRight";
    /// <summary>Width of the webcam picture, in % of the width of the recorded area.</summary>
    public int VideoWebcamSize { get; set; } = 20;

    /// <summary>Corners the webcam picture can be placed in (values of <see cref="VideoWebcamCorner"/>).</summary>
    public static readonly string[] WebcamCorners = { "TopLeft", "TopRight", "BottomLeft", "BottomRight" };

    public static string DefaultWorkspaceFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Snaplume");

    /// <summary>
    /// Settings live next to the executable when a "portable" marker file is present
    /// (run without installation), otherwise in %APPDATA%\Snaplume.
    /// </summary>
    public static string SettingsFolder { get; } = IsPortable
        ? AppContext.BaseDirectory
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Snaplume");

    private static bool IsPortable => File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"));

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
        // The installer may have registered the start with Windows: show it checked, so that saving keeps it.
        if (!settings.StartWithWindows && RunEntryPointsToThisExe()) settings.StartWithWindows = true;
        if (migrated && settings.StartWithWindows)
        {
            try
            {
                settings.ApplyStartWithWindows();
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Démarrage avec Windows", ex);
            }
        }
        return settings;
    }

    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>
    /// True when the "Snaplume" value of the Run key starts this executable (e.g. created by the
    /// installer). A value starting another copy of Snaplume does not count.
    /// </summary>
    private static bool RunEntryPointsToThisExe()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue("Snaplume") is not string command || Environment.ProcessPath is not { } exe) return false;
            return CommandExecutable(command) is { } target && IsSameFile(target, exe);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Démarrage avec Windows", ex);
            return false;
        }
    }

    /// <summary>Executable of a command line: the quoted path, or the text up to the first space.</summary>
    private static string? CommandExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static bool IsSameFile(string a, string b)
    {
        try
        {
            a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(a));
            b = Path.GetFullPath(b);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
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
        ImageFormat = ImageFormat is "png" or "jpg" ? ImageFormat : defaults.ImageFormat;
        HotkeyRegion ??= defaults.HotkeyRegion;
        HotkeyScreen ??= defaults.HotkeyScreen;
        HotkeyWindow ??= defaults.HotkeyWindow;
        HotkeyRepeat ??= defaults.HotkeyRepeat;
        HotkeyDelayed ??= defaults.HotkeyDelayed;
        HotkeyVideo ??= defaults.HotkeyVideo;
        HotkeyText ??= defaults.HotkeyText;
        OcrLanguage ??= defaults.OcrLanguage;

        WorkspaceFolder = NormalizeFolder(WorkspaceFolder) ?? defaults.WorkspaceFolder;
        ActiveWorkspace = ActiveWorkspace?.Trim() is { } active && Workspaces.IsPlainName(active) ? active : "";

        JpegQuality = Math.Clamp(JpegQuality, 10, 100);
        DelaySeconds = Math.Clamp(DelaySeconds, 0, 60);
        VideoFramerate = Math.Clamp(VideoFramerate, 5, 60);
        VideoWebcamDevice = VideoWebcamDevice?.Trim() ?? defaults.VideoWebcamDevice;
        VideoWebcamCorner = WebcamCorners.FirstOrDefault(c => string.Equals(c, VideoWebcamCorner, StringComparison.OrdinalIgnoreCase))
                            ?? defaults.VideoWebcamCorner;
        VideoWebcamSize = Math.Clamp(VideoWebcamSize, 10, 40);
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

    public void ApplyStartWithWindows()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        key.DeleteValue(LegacyName, throwOnMissingValue: false);
        if (StartWithWindows) key.SetValue("Snaplume", $"\"{Environment.ProcessPath}\" --tray");
        else key.DeleteValue("Snaplume", throwOnMissingValue: false);
    }

    /// <summary>
    /// True when Windows 11 routes the Print Screen key to the Snipping Tool,
    /// which prevents other applications from using it.
    /// </summary>
    public static bool SnippingToolOwnsPrintScreen()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
        return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int value && value == 1;
    }
}
