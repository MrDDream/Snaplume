using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Imaging;

namespace Snaplume.Platform;

/// <summary>What the current system can do; the interface hides or explains what it cannot.</summary>
[Flags]
public enum PlatformFeatures
{
    None = 0,
    /// <summary>Windows and controls under the mouse are detected when selecting an area (not under Wayland).</summary>
    WindowDetection = 1 << 0,
    /// <summary>The wheel can be turned and the screen grabbed repeatedly (scrolling capture).</summary>
    ScrollingCapture = 1 << 1,
    /// <summary>Global keyboard shortcuts work while Snaplume is in the background.</summary>
    GlobalHotkeys = 1 << 2,
    /// <summary>Mouse clicks can be highlighted in videos.</summary>
    ClickHighlight = 1 << 3,
    /// <summary>Snaplume's own windows can stay visible without appearing in captures.</summary>
    ExcludeFromCapture = 1 << 4,
    /// <summary>Any area can be recorded; without it, the system chooses the area (Wayland portal).</summary>
    RecordAnyArea = 1 << 5,
    /// <summary>Starting with the user session can be turned on and off.</summary>
    StartWithSession = 1 << 6,
    /// <summary>The active window can be captured without user interaction.</summary>
    ActiveWindow = 1 << 7,
}

/// <summary>
/// Services that depend on the operating system, provided by Snaplume.Platform.Windows or Snaplume.Platform.Linux
/// (X11 or Wayland) and set once at startup in <see cref="AppServices.Platform"/>.
/// </summary>
public interface IPlatform
{
    /// <summary>"windows", "x11" or "wayland".</summary>
    string Id { get; }
    PlatformFeatures Features { get; }

    IScreenCapture Screen { get; }
    IWindowLocator Windows { get; }
    IPointerControl Pointer { get; }
    IGlobalHotkeys Hotkeys { get; }
    IScreenRecorderFactory Recorder { get; }
    IVideoTools Video { get; }
    IOcrEngine Ocr { get; }
    IDesktopShell Shell { get; }
    IImageClipboard Clipboard { get; }
    INativeWindows NativeWindows { get; }
    ISingleInstance SingleInstance { get; }

    /// <summary>Notification-area icon with its menu and notifications.</summary>
    ITray CreateTray(string tooltip);

    bool Has(PlatformFeatures feature) => (Features & feature) == feature;
}

// ---- Screen ----

/// <param name="Bounds">Physical pixels, in virtual desktop coordinates.</param>
/// <param name="Scale">Scale factor of the monitor (1 = 96 DPI).</param>
public sealed record MonitorInfo(PixelRect Bounds, double Scale, bool IsPrimary, string Name);

/// <summary>The whole desktop at one instant, as the selection overlay shows it.</summary>
/// <param name="Image">Every monitor, in physical pixels; opaque.</param>
/// <param name="Bounds">Virtual desktop area covered by <paramref name="Image"/>.</param>
/// <param name="Cursor">Mouse position when the image was taken, if known.</param>
public sealed record DesktopImage(PixelBuffer Image, PixelRect Bounds, IReadOnlyList<MonitorInfo> Monitors, (int X, int Y)? Cursor);

public interface IScreenCapture
{
    IReadOnlyList<MonitorInfo> Monitors { get; }

    /// <summary>Captures every monitor. May ask the user for permission the first time (Wayland portal).</summary>
    /// <exception cref="OperationCanceledException">The user refused or cancelled the permission.</exception>
    Task<DesktopImage> CaptureDesktopAsync(bool includeCursor, CancellationToken cancel = default);

    /// <summary>Grabs the same area again and again (scrolling capture); may be slow under Wayland.</summary>
    IScreenGrabber CreateGrabber(PixelRect area);
}

public interface IScreenGrabber : IDisposable
{
    PixelRect Area { get; }

    /// <summary>Copies the area into <paramref name="target"/> as BGRA (stride = width * 4).</summary>
    Task GrabAsync(byte[] target, CancellationToken cancel = default);
}

/// <summary>Windows and controls of the desktop, for the smart selection (empty when not available).</summary>
public interface IWindowLocator
{
    /// <summary>The windows on screen now; <see cref="IWindowSnapshot.FindAt"/> then answers without asking the system again.</summary>
    /// <param name="virtualBounds">The desktop area of the overlay image (<see cref="DesktopImage.Bounds"/>), absolute.</param>
    /// <param name="monitors">
    /// Monitor bounds relative to the top-left corner of <paramref name="virtualBounds"/>; <see cref="IWindowSnapshot.FindAt"/>
    /// takes and returns coordinates relative to it too (pixels of the overlay image), as the 1.1 WindowFinder did.
    /// </param>
    IWindowSnapshot Snapshot(PixelRect virtualBounds, IReadOnlyList<PixelRect> monitors);

    /// <summary>Bounds of the active window, visible part only, absolute physical pixels; null if unknown.</summary>
    PixelRect? ForegroundWindowBounds();
}

public interface IWindowSnapshot
{
    /// <summary>Areas under a point, smallest (a control) to largest (its window), physical pixels.</summary>
    IReadOnlyList<PixelRect> FindAt(int x, int y);
}

public sealed class EmptyWindowSnapshot : IWindowSnapshot
{
    public static readonly EmptyWindowSnapshot Instance = new();
    public IReadOnlyList<PixelRect> FindAt(int x, int y) => Array.Empty<PixelRect>();
}

// ---- Mouse and keyboard (scrolling capture) ----

public interface IPointerControl
{
    /// <summary>Asks for the permission to control the pointer, if the system needs one (Wayland portal).</summary>
    /// <returns>False if refused or not possible.</returns>
    Task<bool> PrepareAsync(CancellationToken cancel = default);

    bool TryGetPosition(out int x, out int y);
    bool SetPosition(int x, int y);

    /// <summary>Turns the wheel under the pointer; negative scrolls down.</summary>
    bool Scroll(int notches);

    /// <summary>True if Esc was pressed since the previous call (or is pressed now).</summary>
    bool EscapePressed();

    /// <summary>Brings the window under the point to the front, if the system allows it.</summary>
    void ActivateWindowAt(int x, int y);

    /// <summary>Ends what <see cref="PrepareAsync"/> started.</summary>
    void Release();
}

// ---- Global shortcuts ----

public interface IGlobalHotkeys : IDisposable
{
    bool IsSupported { get; }

    /// <param name="id">Stable identifier ("region", "screen"...), shown by the system under Wayland.</param>
    /// <param name="description">Text shown by the system (Wayland portal).</param>
    /// <returns>False if the shortcut is taken by another application.</returns>
    bool Register(string id, string description, Hotkey hotkey, Action action);

    void UnregisterAll();
}

// ---- Video ----

/// <param name="Area">Physical pixels; ignored when the system chooses the area (no <see cref="PlatformFeatures.RecordAnyArea"/>).</param>
/// <param name="Webcam">Camera inlaid in a corner, or null.</param>
public sealed record RecordingOptions(PixelRect Area, bool SystemAudio, bool Microphone, bool ShowClicks, int Framerate, WebcamOptions? Webcam);

/// <param name="Device">Camera id from <see cref="IScreenRecorderFactory.Webcams"/>, empty for the first one.</param>
/// <param name="Corner">"TopLeft", "TopRight", "BottomLeft" or "BottomRight".</param>
/// <param name="SizePercent">Width of the picture, in % of the width of the recording.</param>
public sealed record WebcamOptions(string Device, string Corner, int SizePercent);

public sealed record WebcamInfo(string Id, string Name);

public interface IScreenRecorderFactory
{
    IReadOnlyList<WebcamInfo> Webcams();

    /// <summary>Starts recording into an MP4 file.</summary>
    /// <exception cref="OperationCanceledException">The user refused the permission (Wayland portal).</exception>
    Task<IScreenRecording> StartAsync(RecordingOptions options, string outputPath, CancellationToken cancel = default);
}

public interface IScreenRecording : IDisposable
{
    /// <summary>The file is complete (after <see cref="Stop"/>). Raised on any thread.</summary>
    event Action? Completed;
    /// <summary>The recording failed; the text is for the error log. Raised on any thread.</summary>
    event Action<string>? Failed;

    /// <summary>The webcam was dropped because it could not be opened.</summary>
    bool WebcamDropped { get; }
    bool CanPause { get; }
    void Pause();
    void Resume();
    void Stop();
}

/// <summary>Video files: cutting, frames for GIF export, thumbnails, playback.</summary>
public interface IVideoTools
{
    /// <summary>Copies the part between <paramref name="start"/> and <paramref name="end"/> to a new MP4 file.</summary>
    Task ExportClipAsync(string source, string destination, TimeSpan start, TimeSpan end, IProgress<double>? progress, CancellationToken cancel);

    /// <summary>Frames at the given times, scaled to the size, in order.</summary>
    IAsyncEnumerable<PixelBuffer> ReadFramesAsync(string source, IReadOnlyList<TimeSpan> times, int width, int height, CancellationToken cancel);

    /// <summary>A frame near the start of the video, at most <paramref name="size"/> pixels wide or high; null if unreadable.</summary>
    Task<PixelBuffer?> ThumbnailAsync(string path, int size, CancellationToken cancel = default);

    IVideoPlayer CreatePlayer();
}

public interface IVideoPlayer : IDisposable
{
    /// <summary>A new picture to show (on any thread); the buffer may be reused for the next one.</summary>
    event Action<PixelBuffer>? FrameReady;
    /// <summary>Opened, playing, paused, ended or failed; on any thread.</summary>
    event Action? StateChanged;

    Task OpenAsync(string path);
    TimeSpan Duration { get; }
    TimeSpan Position { get; }
    bool IsPlaying { get; }
    bool HasEnded { get; }
    string? Error { get; }
    double Volume { get; set; }
    void Play();
    void Pause();
    void Seek(TimeSpan position);
}

// ---- Text recognition ----

public sealed record OcrLanguage(string Tag, string DisplayName);

public interface IOcrEngine
{
    /// <summary>Installed recognition languages; empty when OCR is not available.</summary>
    IReadOnlyList<OcrLanguage> Languages { get; }

    /// <summary>Language used when none is chosen (the user's language when installed).</summary>
    OcrLanguage? DefaultLanguage { get; }

    /// <summary>The text of the image, lines separated by new lines.</summary>
    /// <exception cref="InvalidOperationException">No OCR available (message ready to show).</exception>
    Task<string> RecognizeAsync(PixelBuffer image, string? languageTag, CancellationToken cancel = default);
}

// ---- Desktop integration ----

public interface IDesktopShell
{
    /// <summary>Opens a file, a folder or an allowed URI (already checked by <see cref="FileOps.OpenWithDefaultApp"/>).</summary>
    void Open(string target);

    /// <summary>Shows the file selected in the file manager.</summary>
    void Reveal(string path);

    /// <summary>Moves files or folders to the trash (recoverable).</summary>
    /// <returns>False if the operation failed or was cancelled.</returns>
    bool MoveToTrash(IReadOnlyList<string> paths);

    bool CanEmail { get; }

    /// <summary>Opens a new message in the e-mail application with the files attached.</summary>
    /// <exception cref="InvalidOperationException">No e-mail application (message ready to show).</exception>
    void Email(IReadOnlyList<string> paths, string subject);

    /// <summary>Prints images, one per page (the system or Snaplume shows the print dialog).</summary>
    /// <param name="owner">Native handle of the window asking, for the dialog.</param>
    /// <returns>False if the user cancelled.</returns>
    Task<bool> PrintImagesAsync(IReadOnlyList<string> paths, string title, IntPtr owner);

    string DownloadsFolder { get; }

    /// <summary>Snaplume starts with the user session (this executable, in the tray).</summary>
    bool StartsWithSession { get; }
    void SetStartWithSession(bool enabled);

    /// <summary>True when another tool owns the Print Screen key (Windows 11 Snipping Tool).</summary>
    bool PrintScreenTakenBySystem { get; }

    /// <summary>Path that cannot hold the workspace (drive root, personal or system folder), with the reason, or null.</summary>
    string? CheckWorkspaceFolder(string folder);

    /// <summary>
    /// The folder is on a network share: deleted captures do not go to the trash there, so the user is asked to
    /// confirm (Settings.NetworkFolderWarning). Not a refusal, unlike <see cref="CheckWorkspaceFolder"/>.
    /// </summary>
    bool IsNetworkFolder(string folder) => false;
}

public interface IImageClipboard
{
    /// <summary>Copies an image (PNG flavour first: it keeps transparency).</summary>
    /// <returns>False if the clipboard stayed locked by another application.</returns>
    Task<bool> SetImageAsync(PixelBuffer image, byte[] png);

    /// <summary>The image of the clipboard, or the first image file copied in the file manager; null if none.</summary>
    Task<PixelBuffer?> GetImageAsync();

    Task<bool> SetTextAsync(string text);
    Task<bool> SetFilesAsync(IReadOnlyList<string> paths);
}

/// <summary>What Avalonia does not offer on native windows.</summary>
public interface INativeWindows
{
    /// <summary>Keeps the window out of screenshots and recordings, when possible.</summary>
    void ExcludeFromCapture(IntPtr handle);

    /// <summary>Mouse clicks go through the window (recording frame).</summary>
    void MakeClickThrough(IntPtr handle);

    /// <summary>Hidden from the task bar and Alt+Tab, never activated by clicks.</summary>
    void MakeToolWindow(IntPtr handle);
}

/// <summary>One running Snaplume per user: a second launch forwards its command line and quits.</summary>
public interface ISingleInstance : IDisposable
{
    /// <returns>True if this process is the first one.</returns>
    bool TryAcquire();

    /// <summary>Sends the arguments to the first instance.</summary>
    /// <returns>False if it could not be reached.</returns>
    Task<bool> ForwardAsync(string[] args, CancellationToken cancel = default);

    /// <summary>Receives the command lines of later launches (on any thread).</summary>
    void Listen(Action<string[]> received);
}

// ---- Notification area ----

/// <summary>Item of the tray menu; a separator when <see cref="Text"/> is null.</summary>
public sealed record TrayMenuItem(string? Text, Action? Click = null, IReadOnlyList<TrayMenuItem>? Children = null,
    bool Enabled = true, bool Checked = false, string? Shortcut = null, bool Bold = false)
{
    public static readonly TrayMenuItem Separator = new((string?)null);
}

public interface ITray : IDisposable
{
    /// <summary>Left click (or double click) on the icon.</summary>
    event Action? Activated;

    /// <summary>Called each time before the menu opens, to rebuild it.</summary>
    Func<IReadOnlyList<TrayMenuItem>>? MenuBuilder { get; set; }

    void SetTooltip(string text);
    void ShowInfo(string title, string text, Action? onClick = null);
    void ShowWarning(string title, string text, Action? onClick = null);
    void ShowError(string title, string text);
}
