using System.Globalization;
using System.Windows;
using System.Windows.Media.Imaging;
using Snaplume.Capturing;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Core;

public enum CaptureKind
{
    /// <summary>Interactive: drag an area or click a window / control.</summary>
    Region,
    /// <summary>Monitor under the mouse cursor.</summary>
    Screen,
    /// <summary>All monitors.</summary>
    AllScreens,
    ActiveWindow,
    /// <summary>Same area as the previous region capture.</summary>
    RepeatLast,
    /// <summary>Interactive selection, then the area is scrolled and stitched into one tall image.</summary>
    Scrolling,
}

public sealed class CaptureService
{
    /// <summary>
    /// Larger captures are saved but not copied: the clipboard copy (bitmap + PNG) runs on the UI thread
    /// and would freeze Snaplume for a long time, and few applications accept such images.
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
            _app.Dispatcher.BeginInvoke(action);
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

            var foreground = GetForegroundWindow();
            bool scrolling = mode == CaptureKind.Scrolling;
            // A scrolling capture takes its own frames, without the cursor (it is moved into the area).
            var snap = ScreenSnapshot.Take(Settings.IncludeCursor && !scrolling);
            var area = SelectArea(mode, snap, foreground);
            if (area is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;

            if (mode is CaptureKind.Region or CaptureKind.RepeatLast) _lastRegion = rect.Offset(snap.Bounds.X, snap.Bounds.Y);

            BitmapSource image;
            bool nothingScrolled = false;
            if (scrolling)
            {
                var result = await ScrollingCapture.RunAsync(rect.Offset(snap.Bounds.X, snap.Bounds.Y), stop);
                image = result.Image;
                nothingScrolled = result.NothingScrolled;
            }
            else
            {
                image = ImageIO.Normalize(new CroppedBitmap(snap.Image, rect.ToInt32Rect()));
            }
            // Even when Snaplume is quitting: what was captured is kept.
            var path = await SaveToWorkspaceAsync(image);
            CaptureSaved?.Invoke(path);

            bool tooLargeToCopy = Settings.CopyToClipboardAfterCapture && (long)image.PixelWidth * image.PixelHeight > MaxClipboardPixels;
            bool copied = Settings.CopyToClipboardAfterCapture && !tooLargeToCopy && await TryCopyAsync(image);
            if (tooLargeToCopy) ErrorLog.Write("Copie de la capture", $"{image.PixelWidth} x {image.PixelHeight} px : trop grande, non copiée");
            if (nothingScrolled) ErrorLog.Write("Capture défilante", "Rien n'a défilé");

            if (!quiet && !stop.IsCancellationRequested) ShowResult(path, copied, tooLargeToCopy, nothingScrolled);
            return path;
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
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
    private static async Task<bool> TryCopyAsync(BitmapSource image)
    {
        MemoryStream png;
        try
        {
            // The PNG flavour is encoded in the background (the image is frozen); the clipboard needs the UI thread.
            png = await Task.Run(() => ImageIO.ToPng(image));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie de la capture", ex);
            return false;
        }
        return ClipboardHelper.TrySetImage(image, png);
    }

    /// <summary>Select an area of the screen and read its text (OCR); nothing is saved in the workspace.</summary>
    /// <returns>The window showing the text, or null if cancelled or failed.</returns>
    public Task<Window?> CaptureTextAsync(Window? hide = null, int settleMs = 0) =>
        PickAndShowAsync(Loc.T("Ocr.SelectTitle"), image => _app.ShowOcr(image), "OCR", "Ocr.Failed", hide, settleMs);

    /// <summary>Select an area of the screen and read the QR codes and barcodes in it; nothing is saved in the workspace.</summary>
    /// <returns>The window showing the codes, or null if cancelled or failed.</returns>
    public Task<Window?> CaptureQrAsync(Window? hide = null, int settleMs = 0) =>
        PickAndShowAsync(Loc.T("Qr.SelectTitle"), Qr.QrService.Show, "QR code", "Qr.Failed", hide, settleMs);

    /// <summary>Lets the user select an area, then shows a window working on that part of the screen.</summary>
    /// <param name="logContext">Label of the error log.</param>
    /// <param name="failedKey">Title of the notification shown on failure.</param>
    private async Task<Window?> PickAndShowAsync(string title, Func<BitmapSource, Window> show, string logContext, string failedKey,
        Window? hide, int settleMs)
    {
        if (!Begin(hide, out var stop)) return null;
        try
        {
            await _operation.SettleAsync(settleMs);
            if (stop.IsCancellationRequested) return null;

            var snap = ScreenSnapshot.Take(includeCursor: false);
            if (RegionSelectorWindow.Pick(snap, title) is not PixelRect rect || rect.IsEmpty || stop.IsCancellationRequested) return null;

            return show(ImageIO.Normalize(new CroppedBitmap(snap.Image, rect.ToInt32Rect())));
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
        if (after != null) _app.Dispatcher.BeginInvoke(after);
    }

    private PixelRect? SelectArea(CaptureKind mode, ScreenSnapshot snap, IntPtr foreground)
    {
        var full = new PixelRect(0, 0, snap.Bounds.Width, snap.Bounds.Height);
        switch (mode)
        {
            case CaptureKind.Screen:
                return snap.MonitorUnderCursor();

            case CaptureKind.AllScreens:
                return full;

            case CaptureKind.ActiveWindow:
                return WindowFinder.GetWindowBounds(foreground) is PixelRect window
                    ? window.Offset(-snap.Bounds.X, -snap.Bounds.Y).Intersect(full)
                    : null;

            case CaptureKind.Scrolling:
                return RegionSelectorWindow.Pick(snap, Loc.T("Scroll.SelectTitle"));

            case CaptureKind.RepeatLast when _lastRegion is PixelRect last:
                var relative = last.Offset(-snap.Bounds.X, -snap.Bounds.Y).Intersect(full);
                return relative.IsEmpty ? RegionSelectorWindow.Pick(snap) : relative;

            default:
                return RegionSelectorWindow.Pick(snap);
        }
    }

    private async Task CountdownAsync(int seconds, CancellationToken stop)
    {
        try
        {
            for (int remaining = seconds; remaining > 0; remaining--)
            {
                _app.Tray?.SetStatus(Loc.T("Capture.Countdown", remaining));
                await Task.Delay(1000, stop);
            }
        }
        finally
        {
            _app.Tray?.SetStatus(null);
        }
    }

    /// <summary>
    /// Saves into the active workspace (created again if it was deleted meanwhile). The folder and the
    /// name are chosen now; encoding and writing run in the background (a scrolling capture can take
    /// seconds to encode), the image being frozen.
    /// </summary>
    private Task<string> SaveToWorkspaceAsync(BitmapSource image)
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

/// <summary>
/// A screen operation (capture, text or code capture, video) with an optional window (quick panel,
/// workspace) hidden while it runs so that it does not appear on the screenshot. Each service runs one
/// at a time; across services, only one may be in its interactive part (countdown, area selection,
/// scrolling) at a time: e.g. no video selection opens while a scrolling capture turns the wheel.
/// A video that is recording is past that part: screenshots may still be taken meanwhile.
/// </summary>
internal sealed class ScreenOperation
{
    /// <summary>The operation in its interactive part, whatever the service.</summary>
    private static ScreenOperation? _interactive;

    private Window? _hidden;

    public bool IsBusy { get; private set; }

    /// <summary>True until the hidden window is shown again.</summary>
    public bool HasHiddenWindow => _hidden != null;

    /// <summary>Marks the operation as running (and interactive) and hides the window.</summary>
    /// <returns>False if this operation, or another interactive one, is already running (nothing is done).</returns>
    public bool TryBegin(Window? hide)
    {
        if (IsBusy || _interactive != null || RegionSelectorWindow.IsOpen) return false;
        IsBusy = true;
        _interactive = this;
        if (hide is { IsVisible: true })
        {
            hide.Hide();
            _hidden = hide;
        }
        return true;
    }

    /// <summary>The interactive part is over (e.g. a video now records): other operations may start.</summary>
    public void EndInteractive()
    {
        if (_interactive == this) _interactive = null;
    }

    /// <summary>Lets a menu or the hidden window disappear from the screen before the screenshot.</summary>
    public Task SettleAsync(int settleMs)
    {
        if (_hidden != null) settleMs = Math.Max(settleMs, 250);
        return settleMs > 0 ? Task.Delay(settleMs) : Task.CompletedTask;
    }

    /// <summary>Shows the hidden window again, if any.</summary>
    public void RestoreWindow(bool activate)
    {
        var window = _hidden;
        _hidden = null;
        if (window == null) return;
        window.Show();
        if (activate) window.Activate();
    }

    /// <summary>Ends the operation; the hidden window comes back if it was not restored yet.</summary>
    public void End()
    {
        RestoreWindow(activate: false);
        EndInteractive();
        IsBusy = false;
    }
}
