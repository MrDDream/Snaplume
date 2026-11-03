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
            if (await CaptureAndSaveAsync(mode, delaySeconds, settleMs, stop) is not { } saved) return null;
            var (path, image, nothingScrolled) = saved;

            // The file is saved: what follows may fail without losing it, and its path is returned.
            try
            {
                await AfterSaveAsync(path, image, nothingScrolled, quiet, stop);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Capture enregistrée, suite", ex);
                _app.Tray?.ShowWarning(Loc.T("Capture.Saved"), Loc.T("Capture.AfterSaveFailed", Path.GetFileName(path), ex.Message));
            }
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

    /// <summary>Countdown, area, screenshot (or scrolling capture), then the file written into the workspace.</summary>
    /// <returns>The saved file and its image; null if cancelled.</returns>
    private async Task<(string Path, PixelBuffer Image, bool NothingScrolled)?> CaptureAndSaveAsync(CaptureKind mode, int delaySeconds, int settleMs,
        CancellationToken stop)
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

        // The desktop image (every monitor) is held only while the area is chosen.
        if (await SelectAreaAsync(mode, activeWindow, title, stop) is not { } selected) return null;
        if (mode is CaptureKind.Region or CaptureKind.RepeatLast) _lastRegion = selected.Area;

        PixelBuffer image;
        bool nothingScrolled = false;
        if (selected.Image is { } cropped)
        {
            image = cropped;
        }
        else
        {
            var result = await RunScrollingAsync(platform, selected.Monitors, selected.Area, stop);
            if (result == null)
            {
                _app.Tray?.ShowWarning(Loc.T("Scroll.PointerRefusedTitle"), Loc.T("Scroll.PointerRefused"));
                return null;
            }
            image = result.Image;
            nothingScrolled = result.NothingScrolled;
        }
        // Even when Snaplume is quitting: what was captured is kept.
        return (await SaveToWorkspaceAsync(image), image, nothingScrolled);
    }

    /// <summary>Announces, copies and shows a capture once saved.</summary>
    private async Task AfterSaveAsync(string path, PixelBuffer image, bool nothingScrolled, bool quiet, CancellationToken stop)
    {
        try
        {
            CaptureSaved?.Invoke(path);
        }
        catch (Exception ex)
        {
            // The gallery could not show it yet: the file is in the workspace all the same.
            ErrorLog.Write("Annonce de la capture", ex);
        }

        bool tooLargeToCopy = Settings.CopyToClipboardAfterCapture && ClipboardCopy.IsTooLarge(image);
        bool copied = Settings.CopyToClipboardAfterCapture && !tooLargeToCopy && await TryCopyAsync(Platform, image);
        if (tooLargeToCopy) ErrorLog.Write("Copie de la capture", $"{image.Width} x {image.Height} px : trop grande, non copiée");
        if (nothingScrolled) ErrorLog.Write("Capture défilante", "Rien n'a défilé");

        if (!quiet && !stop.IsCancellationRequested) ShowResult(path, copied, tooLargeToCopy, nothingScrolled);
        WarnIfWorkspaceUnavailable();
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
    /// <param name="monitors">Bounds of the monitors, absolute physical coordinates.</param>
    /// <returns>Null if the system refused the control of the mouse.</returns>
    private async Task<ScrollCaptureResult?> RunScrollingAsync(IPlatform platform, IReadOnlyList<PixelRect> monitors, PixelRect area,
        CancellationToken stop)
    {
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
            return await ClipboardCopy.CopyAsync(platform.Clipboard, image);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie de la capture", ex);
            return false;
        }
    }

    /// <summary>Select an area of the screen and read its text (OCR); nothing is saved in the workspace.</summary>
    /// <returns>The window showing the text, or null if cancelled or failed.</returns>
    public async Task<Window?> CaptureTextAsync(Window? hide = null, int settleMs = 0) =>
        (await PickAreaAsync(Loc.T("Ocr.SelectTitle"), includeCursor: false, hide, settleMs, activateHidden: false, "OCR", "Ocr.Failed",
            image => _app.ShowOcr(image))).Result;

    /// <summary>Select an area of the screen and read the QR codes and barcodes in it; nothing is saved in the workspace.</summary>
    /// <returns>The window showing the codes, or null if cancelled or failed.</returns>
    public async Task<Window?> CaptureQrAsync(Window? hide = null, int settleMs = 0) =>
        (await PickAreaAsync(Loc.T("Qr.SelectTitle"), includeCursor: false, hide, settleMs, activateHidden: false, "QR code", "Qr.Failed",
            image => _app.ShowQr(image))).Result;

    /// <summary>
    /// Select an area of the screen and return its image, e.g. to insert it into a capture being edited;
    /// nothing is saved or copied. <paramref name="hide"/> is hidden meanwhile, then brought back.
    /// </summary>
    /// <returns>The image (null if cancelled or failed), or <see cref="ImagePick.Busy"/> when another capture is in progress.</returns>
    public async Task<ImagePick> PickImageAsync(Window? hide)
    {
        var (image, busy) = await PickAreaAsync(null, Settings.IncludeCursor, hide, 0, activateHidden: true, "Capture à insérer", "Capture.Failed",
            image => image);
        return new ImagePick(image, busy);
    }

    /// <summary>
    /// Lets the user select an area, then hands its image to <paramref name="use"/> (e.g. a window working on that part
    /// of the screen). Errors are logged and notified.
    /// </summary>
    /// <param name="activateHidden">The hidden window comes back activated (else it comes back without the focus).</param>
    /// <param name="logContext">Label of the error log.</param>
    /// <param name="failedKey">Title of the notification shown on failure.</param>
    /// <returns>
    /// What <paramref name="use"/> returned (null if cancelled or failed); Busy when another operation was running
    /// (here, or a video choosing its area), nothing being done.
    /// </returns>
    private async Task<(T? Result, bool Busy)> PickAreaAsync<T>(string? title, bool includeCursor, Window? hide, int settleMs,
        bool activateHidden, string logContext, string failedKey, Func<PixelBuffer, T?> use) where T : class
    {
        if (!Begin(hide, out var stop)) return (null, true);
        try
        {
            await _operation.SettleAsync(settleMs);
            if (stop.IsCancellationRequested) return (null, false);
            var image = await PickImageOfAreaAsync(title, includeCursor, stop);
            return (image != null ? use(image) : null, false);
        }
        catch (OperationCanceledException)
        {
            return (null, false);
        }
        catch (Exception ex)
        {
            ErrorLog.Write(logContext, ex);
            _app.Tray?.ShowError(Loc.T(failedKey), ex.Message);
            return (null, false);
        }
        finally
        {
            if (activateHidden) _operation.RestoreWindow(activate: true);
            End();
        }
    }

    /// <summary>The image of the area the user selects; the desktop image is released once the area is cropped.</summary>
    private async Task<PixelBuffer?> PickImageOfAreaAsync(string? title, bool includeCursor, CancellationToken stop)
    {
        var desktop = await Platform.Screen.CaptureDesktopAsync(includeCursor, stop);
        if (await RegionSelectorWindow.PickAsync(desktop, title) is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;
        return desktop.Image.Crop(rect.X, rect.Y, rect.Width, rect.Height);
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

    /// <summary>The area chosen, absolute physical coordinates, with its image (null for a scrolling capture: it takes its own).</summary>
    /// <param name="Monitors">Bounds of the monitors, absolute physical coordinates.</param>
    private sealed record SelectedArea(PixelRect Area, PixelBuffer? Image, IReadOnlyList<PixelRect> Monitors);

    /// <summary>
    /// Takes the desktop image and the area of the capture (chosen by the user when <paramref name="mode"/> needs it), then
    /// crops the area: the desktop image, often much larger, is released as soon as this returns.
    /// </summary>
    /// <returns>Null if cancelled or empty.</returns>
    private async Task<SelectedArea?> SelectAreaAsync(CaptureKind mode, PixelRect? activeWindow, string? title, CancellationToken stop)
    {
        // A scrolling capture takes its own frames, without the cursor (it is moved into the area).
        bool scrolling = mode == CaptureKind.Scrolling;
        var desktop = await Platform.Screen.CaptureDesktopAsync(Settings.IncludeCursor && !scrolling, stop);
        if (stop.IsCancellationRequested) return null;
        var area = await SelectAreaAsync(mode, desktop, activeWindow, title);
        if (area is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;

        var monitors = desktop.Monitors.Select(m => m.Bounds).ToList();
        if (monitors.Count == 0) monitors.Add(desktop.Bounds);
        var image = scrolling ? null : desktop.Image.Crop(rect.X, rect.Y, rect.Width, rect.Height);
        return new SelectedArea(CaptureGeometry.ToScreen(rect, desktop.Bounds), image, monitors);
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
        (int X, int Y)? cursor = desktop.Cursor;
        if (cursor == null && Platform.Pointer.TryGetPosition(out int x, out int y)) cursor = (x, y);
        return CaptureGeometry.MonitorUnderCursor(desktop.Monitors, cursor) is { } monitor
            ? monitor.Bounds.Offset(-desktop.Bounds.X, -desktop.Bounds.Y)
            : new PixelRect(0, 0, desktop.Bounds.Width, desktop.Bounds.Height);
    }

    /// <summary>The time left is shown in the tooltip of the tray icon (a window could close the menu to capture).</summary>
    private async Task CountdownAsync(int seconds, CancellationToken stop)
    {
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                _app.SetTrayStatus(Loc.T("Capture.Countdown", remaining));
                await Task.Delay(1000, stop);
            }
        }
        finally
        {
            _app.SetTrayStatus(null);
        }
    }

    /// <summary>
    /// Captures and videos went to Base because the folder chosen for the active workspace cannot be reached (unplugged
    /// drive, offline share): said after each of them, with the folder, until it is back or another workspace is chosen.
    /// </summary>
    internal void WarnIfWorkspaceUnavailable()
    {
        if (Workspaces.UnavailableActive is not { } name) return;
        _app.Tray?.ShowWarning(Loc.T("Workspaces.Folder.FallbackTitle"),
            Loc.T("Workspaces.Folder.Fallback", Workspaces.DisplayName(name), Workspaces.FolderOf(name)));
    }

    /// <summary>
    /// Saves into the active workspace (created again if it was deleted meanwhile). The folder and the
    /// name are chosen now; encoding and writing run in the background (a scrolling capture can take
    /// seconds to encode).
    /// </summary>
    private Task<string> SaveToWorkspaceAsync(PixelBuffer image)
    {
        var folder = Workspaces.ActiveFolder;
        var extension = Settings.ImageExtension;
        var baseName = FileOps.SanitizeFileName(Settings.FileNamePrefix) + TimeStamp();
        int quality = Settings.JpegQuality, webpQuality = Settings.WebpQuality;
        return Task.Run(() =>
        {
            Directory.CreateDirectory(folder);
            var path = FileOps.UniquePath(folder, baseName, extension);
            ImageIO.Save(image, path, quality, webpQuality);
            return path;
        });
    }

    /// <summary>Date and time for file names, identical in every language.</summary>
    internal static string TimeStamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
}

/// <summary>An image picked on the screen (<see cref="CaptureService.PickImageAsync"/>).</summary>
/// <param name="Image">Null when cancelled or failed, or when <paramref name="Busy"/>.</param>
/// <param name="Busy">Another capture, or a video choosing its area, was in progress: nothing was done.</param>
public sealed record ImagePick(PixelBuffer? Image, bool Busy);
