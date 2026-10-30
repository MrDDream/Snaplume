using System.Globalization;
using Avalonia.Controls;
using Avalonia.Threading;
using Snaplume.Capturing;
using Snaplume.Imaging;
using Snaplume.Platform;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Core;

/// <summary>
/// Screenshots: area selection, monitor, all monitors, active window, same area again, scrolling capture; then
/// saved into the active workspace, copied, and shown as the settings ask. Also the text (OCR) and code (QR)
/// captures, and picking an image to insert in the editor. Every screen access goes through the platform services.
/// </summary>
public sealed class CaptureService
{
    /// <summary>
    /// Larger captures are saved but not copied: the clipboard copy (bitmap + PNG) is slow and holds a lot of
    /// memory, and few applications accept such images.
    /// </summary>
    private const long MaxClipboardPixels = 50_000_000;

    private readonly App _app;
    private readonly ScreenOperation _operation = new();
    private PixelRect? _lastRegion; // absolute screen coordinates
    /// <summary>Cancelled by <see cref="StopThen"/>: the capture in progress ends as soon as possible.</summary>
    private CancellationTokenSource _stop = new();
    private Action? _afterEnd;

    public event Action<string>? CaptureSaved;

    public CaptureService(App app) => _app = app;

    private AppSettings Settings => _app.Settings;
    private IPlatform Platform => _app.Platform;

    /// <summary>True while a capture (or a text or code capture) is in progress.</summary>
    public bool IsBusy => _operation.IsBusy;

    /// <summary>Announces a file added to the workspace by another feature (e.g. a video).</summary>
    public void NotifySaved(string path) => CaptureSaved?.Invoke(path);

    /// <summary>
    /// Ends the capture in progress as soon as possible (countdown and selection cancelled, scrolling
    /// stopped: what was captured is still saved, and the mouse put back), then runs the action once
    /// the service is idle (immediately, through the dispatcher, if nothing is being captured).
    /// </summary>
    public void StopThen(Action action)
    {
        if (!IsBusy)
        {
            Dispatcher.UIThread.Post(action);
            return;
        }
        _afterEnd += action;
        _stop.Cancel();
        RegionSelectorWindow.CancelCurrent();
    }

    /// <param name="delaySeconds">Timer, e.g. to capture an open menu.</param>
    /// <param name="hide">Window to hide during the capture (e.g. the workspace).</param>
    /// <param name="settleMs">Pause letting a menu or window disappear before the screenshot.</param>
    /// <param name="quiet">Only save (and copy): no editor, no workspace, no notification.</param>
    /// <returns>Path of the saved file, or null if cancelled.</returns>
    public async Task<string?> CaptureAsync(CaptureKind mode, int delaySeconds = 0, Window? hide = null, int settleMs = 0, bool quiet = false)
    {
        if (!Begin(hide, out var stop)) return null;
        try
        {
            await _operation.SettleAsync(settleMs);
            if (delaySeconds > 0) await CountdownAsync(delaySeconds, stop);
            if (stop.IsCancellationRequested) return null;

            var platform = Platform;
            string? title = null;
            // The active window is read before anything of Snaplume can take its place.
            PixelRect? activeWindow = null;
            if (mode == CaptureKind.ActiveWindow)
            {
                if (platform.Has(PlatformFeatures.ActiveWindow)) activeWindow = ReadForegroundWindow(platform);
                if (activeWindow == null)
                {
                    // Not possible here (Wayland) or unknown: the user clicks the window instead, and is told why.
                    mode = CaptureKind.Region;
                    title = Loc.T("Capture.ActiveWindowUnavailable");
                }
            }
            if (mode == CaptureKind.Scrolling && !platform.Has(PlatformFeatures.ScrollingCapture))
            {
                mode = CaptureKind.Region;
                title = Loc.T("Scroll.Unavailable");
            }
            bool scrolling = mode == CaptureKind.Scrolling;

            // A scrolling capture takes its own frames, without the cursor (it is moved into the area).
            var desktop = await platform.Screen.CaptureDesktopAsync(Settings.IncludeCursor && !scrolling, stop);
            if (stop.IsCancellationRequested) return null;
            var area = await SelectAreaAsync(mode, desktop, activeWindow, title);
            if (area is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;

            if (mode is CaptureKind.Region or CaptureKind.RepeatLast) _lastRegion = CaptureGeometry.ToScreen(rect, desktop.Bounds);

            PixelBuffer image;
            bool nothingScrolled = false;
            if (scrolling)
            {
                var result = await RunScrollingAsync(platform, desktop, CaptureGeometry.ToScreen(rect, desktop.Bounds), stop);
                if (result == null)
                {
                    _app.Tray?.ShowWarning(Loc.T("Scroll.PointerRefusedTitle"), Loc.T("Scroll.PointerRefused"));
                    return null;
                }
                image = result.Image;
                nothingScrolled = result.NothingScrolled;
            }
            else
            {
                image = desktop.Image.Crop(rect.X, rect.Y, rect.Width, rect.Height);
            }
            // Even when Snaplume is quitting: what was captured is kept.
            var path = await SaveToWorkspaceAsync(image);
            CaptureSaved?.Invoke(path);

            bool tooLargeToCopy = Settings.CopyToClipboardAfterCapture && (long)image.Width * image.Height > MaxClipboardPixels;
            bool copied = Settings.CopyToClipboardAfterCapture && !tooLargeToCopy && await TryCopyAsync(platform, image);
            if (tooLargeToCopy) ErrorLog.Write("Copie de la capture", $"{image.Width} x {image.Height} px : trop grande, non copiée");
            if (nothingScrolled) ErrorLog.Write("Capture défilante", "Rien n'a défilé");

            if (!quiet && !stop.IsCancellationRequested) ShowResult(path, copied, tooLargeToCopy, nothingScrolled);
            return path;
        }
        catch (OperationCanceledException)
        {
            // Stopped, or the system's permission to capture the screen refused (Wayland portal).
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture", ex);
            _app.Tray?.ShowError(Loc.T("Capture.Failed"), ex.Message);
            return null;
        }
        finally
        {
            End();
        }
    }

    private static PixelRect? ReadForegroundWindow(IPlatform platform)
    {
        try
        {
            return platform.Windows.ForegroundWindowBounds();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture de la fenêtre active", ex);
            return null;
        }
    }

    /// <summary>Scrolls the area and stitches it, with the Stop bar next to it.</summary>
    /// <returns>Null if the system refused the control of the mouse.</returns>
    private async Task<ScrollCaptureResult?> RunScrollingAsync(IPlatform platform, DesktopImage desktop, PixelRect area, CancellationToken stop)
    {
        var monitors = desktop.Monitors.Select(m => m.Bounds).ToList();
        if (monitors.Count == 0) monitors.Add(desktop.Bounds);
        var capture = new ScrollingCapture(platform.Pointer, platform.Screen.CreateGrabber, monitors)
        {
            CreateBar = (target, stopClicked) =>
            {
                var bar = new ScrollStopBar(target);
                bar.StopClicked += stopClicked;
                bar.Show();
                return bar;
            },
            // A selection opening (another feature) must not have the wheel turning under it.
            ExternalStop = () => _app.IsExiting || RegionSelectorWindow.IsOpen,
        };
        return await capture.RunAsync(area, stop);
    }

    /// <summary>Hidden window back, editor or quick panel, and a notification when nothing else shows the result.</summary>
    private void ShowResult(string path, bool copied, bool tooLargeToCopy, bool nothingScrolled)
    {
        var panelShown = true;
        if (_operation.HasHiddenWindow)
        {
            // Bring back the window hidden for the capture (quick panel or workspace).
            _operation.RestoreWindow(activate: true);
        }
        else if (Settings.ShowPanelAfterCapture)
        {
            _app.ShowQuickPanel();
        }
        else panelShown = false;

        if (Settings.OpenEditorAfterCapture) _app.OpenEditor(path);

        var name = Path.GetFileName(path);
        void Open() => _app.OpenEditor(path);
        // Problems are always told, whatever else shows the capture.
        if (nothingScrolled)
            _app.Tray?.ShowWarning(Loc.T("Scroll.NothingScrolledTitle"), Loc.T("Scroll.NothingScrolled", name), Open);
        else if (tooLargeToCopy)
            _app.Tray?.ShowInfo(Loc.T("Capture.Saved"), Loc.T("Capture.TooLargeToCopy", name), Open);
        else if (!panelShown && !Settings.OpenEditorAfterCapture)
            _app.Tray?.ShowInfo(Loc.T("Capture.Saved"), copied ? Loc.T("Capture.SavedAndCopied", name) : name, Open);
    }

    /// <summary>The capture is saved even if the clipboard is unavailable (held by another application).</summary>
    private static async Task<bool> TryCopyAsync(IPlatform platform, PixelBuffer image)
    {
        try
        {
            // The PNG flavour is encoded in the background; the clipboard is set from the UI thread.
            var png = await Task.Run(() => ImageIO.EncodePng(image));
            return await platform.Clipboard.SetImageAsync(image, png);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie de la capture", ex);
            return false;
        }
    }

    /// <summary>Select an area of the screen and read its text (OCR); nothing is saved in the workspace.</summary>
    /// <returns>The window showing the text, or null if cancelled or failed.</returns>
    public Task<Window?> CaptureTextAsync(Window? hide = null, int settleMs = 0) =>
        PickAndShowAsync(Loc.T("Ocr.SelectTitle"), image => _app.ShowOcr(image), "OCR", "Ocr.Failed", hide, settleMs);

    /// <summary>Select an area of the screen and read the QR codes and barcodes in it; nothing is saved in the workspace.</summary>
    /// <returns>The window showing the codes, or null if cancelled or failed.</returns>
    public Task<Window?> CaptureQrAsync(Window? hide = null, int settleMs = 0) =>
        PickAndShowAsync(Loc.T("Qr.SelectTitle"), image => _app.ShowQr(image), "QR code", "Qr.Failed", hide, settleMs);

    /// <summary>
    /// Select an area of the screen and return its image, e.g. to insert it into a capture being edited;
    /// nothing is saved or copied. <paramref name="hide"/> is hidden meanwhile, then brought back.
    /// </summary>
    /// <returns>The image, or null if cancelled, failed, or another capture is in progress.</returns>
    public async Task<PixelBuffer?> PickImageAsync(Window? hide)
    {
        if (!Begin(hide, out var stop)) return null;
        try
        {
            await _operation.SettleAsync(0);
            if (stop.IsCancellationRequested) return null;

            var desktop = await Platform.Screen.CaptureDesktopAsync(Settings.IncludeCursor, stop);
            if (await RegionSelectorWindow.PickAsync(desktop) is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;
            return desktop.Image.Crop(rect.X, rect.Y, rect.Width, rect.Height);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture à insérer", ex);
            _app.Tray?.ShowError(Loc.T("Capture.Failed"), ex.Message);
            return null;
        }
        finally
        {
            _operation.RestoreWindow(activate: true);
            End();
        }
    }

    /// <summary>Lets the user select an area, then shows a window working on that part of the screen.</summary>
    /// <param name="logContext">Label of the error log.</param>
    /// <param name="failedKey">Title of the notification shown on failure.</param>
    private async Task<Window?> PickAndShowAsync(string title, Func<PixelBuffer, Window?> show, string logContext, string failedKey,
        Window? hide, int settleMs)
    {
        if (!Begin(hide, out var stop)) return null;
        try
        {
            await _operation.SettleAsync(settleMs);
            if (stop.IsCancellationRequested) return null;

            var desktop = await Platform.Screen.CaptureDesktopAsync(includeCursor: false, stop);
            if (await RegionSelectorWindow.PickAsync(desktop, title) is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;

            return show(desktop.Image.Crop(rect.X, rect.Y, rect.Width, rect.Height));
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write(logContext, ex);
            _app.Tray?.ShowError(Loc.T(failedKey), ex.Message);
            return null;
        }
        finally
        {
            End();
        }
    }

    /// <summary>Starts an operation, unless one is already running (here, or a video choosing its area).</summary>
    private bool Begin(Window? hide, out CancellationToken stop)
    {
        stop = default;
        if (!_operation.TryBegin(hide)) return false;
        if (_stop.IsCancellationRequested)
        {
            _stop.Dispose();
            _stop = new CancellationTokenSource();
        }
        stop = _stop.Token;
        return true;
    }

    /// <summary>Ends the operation, then runs the <see cref="StopThen"/> actions.</summary>
    private void End()
    {
        _operation.End();
        var after = _afterEnd;
        _afterEnd = null;
        if (after != null) Dispatcher.UIThread.Post(after);
    }

    private async Task<PixelRect?> SelectAreaAsync(CaptureKind mode, DesktopImage desktop, PixelRect? activeWindow, string? title)
    {
        var full = new PixelRect(0, 0, desktop.Bounds.Width, desktop.Bounds.Height);
        switch (mode)
        {
            case CaptureKind.Screen:
                return MonitorUnderCursor(desktop);

            case CaptureKind.AllScreens:
                return full;

            case CaptureKind.ActiveWindow when activeWindow is PixelRect window:
                var visible = CaptureGeometry.ToDesktop(window, desktop.Bounds);
                return visible.IsEmpty ? null : visible;

            case CaptureKind.Scrolling:
                return await RegionSelectorWindow.PickAsync(desktop, Loc.T("Scroll.SelectTitle"));

            case CaptureKind.RepeatLast when _lastRegion is PixelRect last:
                var relative = CaptureGeometry.ToDesktop(last, desktop.Bounds);
                return relative.IsEmpty ? await RegionSelectorWindow.PickAsync(desktop, title) : relative;

            default:
                return await RegionSelectorWindow.PickAsync(desktop, title);
        }
    }

    /// <summary>Monitor under the mouse (relative to the desktop image); the primary one if the mouse is unknown.</summary>
    private PixelRect MonitorUnderCursor(DesktopImage desktop)
    {
        var monitors = desktop.Monitors.Select(m => m.Bounds.Offset(-desktop.Bounds.X, -desktop.Bounds.Y)).ToList();
        (int X, int Y)? cursor = desktop.Cursor;
        if (cursor == null && Platform.Pointer.TryGetPosition(out int x, out int y)) cursor = (x, y);
        if (cursor is (int cx, int cy)) return CaptureGeometry.MonitorAt(monitors, cx - desktop.Bounds.X, cy - desktop.Bounds.Y);
        var primary = desktop.Monitors.FirstOrDefault(m => m.IsPrimary) ?? desktop.Monitors.FirstOrDefault();
        return primary != null
            ? primary.Bounds.Offset(-desktop.Bounds.X, -desktop.Bounds.Y)
            : new PixelRect(0, 0, desktop.Bounds.Width, desktop.Bounds.Height);
    }

    /// <summary>The time left is shown in the tooltip of the tray icon (a window could close the menu to capture).</summary>
    private async Task CountdownAsync(int seconds, CancellationToken stop)
    {
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                _app.Tray?.SetTooltip(Loc.T("Capture.Countdown", remaining));
                await Task.Delay(1000, stop);
            }
        }
        finally
        {
            _app.Tray?.SetTooltip(TrayText());
        }
    }

    /// <summary>The usual tooltip of the tray icon: the application, and the active workspace if not the base one.</summary>
    private static string TrayText()
    {
        var workspace = Workspaces.ActiveName;
        return workspace.Length == 0 ? "Snaplume" : Loc.T("Tray.TooltipWorkspace", Workspaces.DisplayName(workspace));
    }

    /// <summary>
    /// Saves into the active workspace (created again if it was deleted meanwhile). The folder and the
    /// name are chosen now; encoding and writing run in the background (a scrolling capture can take
    /// seconds to encode).
    /// </summary>
    private Task<string> SaveToWorkspaceAsync(PixelBuffer image)
    {
        var folder = Workspaces.ActiveFolder;
        var extension = Settings.ImageFormat == "jpg" ? ".jpg" : ".png";
        var baseName = FileOps.SanitizeFileName(Settings.FileNamePrefix) + TimeStamp();
        int quality = Settings.JpegQuality;
        return Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var path = FileOps.UniquePath(folder, baseName, extension);
            ImageIO.Save(image, path, quality);
            return path;
        });
    }

    /// <summary>Date and time for file names, identical in every language.</summary>
    internal static string TimeStamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
}
