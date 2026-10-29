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
}

public sealed class CaptureService
{
    private readonly App _app;
    private readonly ScreenOperation _operation = new();
    private PixelRect? _lastRegion; // absolute screen coordinates

    public event Action<string>? CaptureSaved;

    public CaptureService(App app) => _app = app;

    private AppSettings Settings => _app.Settings;

    /// <summary>True while a capture (or a text capture) is in progress.</summary>
    public bool IsBusy => _operation.IsBusy;

    /// <summary>Announces a file added to the workspace by another feature (e.g. a video).</summary>
    public void NotifySaved(string path) => CaptureSaved?.Invoke(path);

    /// <param name="delaySeconds">Timer, e.g. to capture an open menu.</param>
    /// <param name="hide">Window to hide during the capture (e.g. the workspace).</param>
    /// <param name="settleMs">Pause letting a menu or window disappear before the screenshot.</param>
    /// <param name="quiet">Only save (and copy): no editor, no workspace, no notification.</param>
    /// <returns>Path of the saved file, or null if cancelled.</returns>
    public async Task<string?> CaptureAsync(CaptureKind mode, int delaySeconds = 0, Window? hide = null, int settleMs = 0, bool quiet = false)
    {
        // One selection on screen at a time (a video may be choosing its area).
        if (RegionSelectorWindow.IsOpen || !_operation.TryBegin(hide)) return null;
        try
        {
            await _operation.SettleAsync(settleMs);
            if (delaySeconds > 0) await CountdownAsync(delaySeconds);

            var foreground = GetForegroundWindow();
            var snap = ScreenSnapshot.Take(Settings.IncludeCursor);
            var area = SelectArea(mode, snap, foreground);
            if (area is not PixelRect rect || rect.IsEmpty) return null;

            if (mode is CaptureKind.Region or CaptureKind.RepeatLast) _lastRegion = rect.Offset(snap.Bounds.X, snap.Bounds.Y);

            var image = ImageIO.Normalize(new CroppedBitmap(snap.Image, rect.ToInt32Rect()));
            var path = SaveToWorkspace(image);
            bool copied = Settings.CopyToClipboardAfterCapture && TryCopy(image);
            CaptureSaved?.Invoke(path);

            if (!quiet)
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

                if (Settings.OpenEditorAfterCapture)
                {
                    _app.OpenEditor(path);
                }
                else if (!panelShown)
                {
                    var name = Path.GetFileName(path);
                    var text = copied ? Loc.T("Capture.SavedAndCopied", name) : name;
                    _app.Tray?.ShowInfo(Loc.T("Capture.Saved"), text, () => _app.OpenEditor(path));
                }
            }
            return path;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture", ex);
            _app.Tray?.ShowError(Loc.T("Capture.Failed"), ex.Message);
            return null;
        }
        finally
        {
            _operation.End();
        }
    }

    /// <summary>The capture is saved even if the clipboard is unavailable (held by another application).</summary>
    private bool TryCopy(BitmapSource image)
    {
        try
        {
            ClipboardHelper.SetImage(image);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie de la capture", ex);
            return false;
        }
    }

    /// <summary>Select an area of the screen and read its text (OCR); nothing is saved in the workspace.</summary>
    /// <returns>The window showing the text, or null if cancelled or failed.</returns>
    public async Task<Window?> CaptureTextAsync(Window? hide = null, int settleMs = 0)
    {
        if (RegionSelectorWindow.IsOpen || !_operation.TryBegin(hide)) return null;
        try
        {
            await _operation.SettleAsync(settleMs);

            var snap = ScreenSnapshot.Take(includeCursor: false);
            if (RegionSelectorWindow.Pick(snap, Loc.T("Ocr.SelectTitle")) is not PixelRect rect || rect.IsEmpty) return null;

            var image = ImageIO.Normalize(new CroppedBitmap(snap.Image, rect.ToInt32Rect()));
            return _app.ShowOcr(image);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OCR", ex);
            _app.Tray?.ShowError(Loc.T("Ocr.Failed"), ex.Message);
            return null;
        }
        finally
        {
            _operation.End();
        }
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

            case CaptureKind.RepeatLast when _lastRegion is PixelRect last:
                var relative = last.Offset(-snap.Bounds.X, -snap.Bounds.Y).Intersect(full);
                return relative.IsEmpty ? RegionSelectorWindow.Pick(snap) : relative;

            default:
                return RegionSelectorWindow.Pick(snap);
        }
    }

    private async Task CountdownAsync(int seconds)
    {
        for (int remaining = seconds; remaining > 0; remaining--)
        {
            _app.Tray?.SetStatus(Loc.T("Capture.Countdown", remaining));
            await Task.Delay(1000);
        }
        _app.Tray?.SetStatus(null);
    }

    /// <summary>Saves into the active workspace (created again if it was deleted meanwhile).</summary>
    private string SaveToWorkspace(BitmapSource image)
    {
        var folder = Workspaces.ActiveFolder;
        Directory.CreateDirectory(folder);
        var extension = Settings.ImageFormat == "jpg" ? ".jpg" : ".png";
        var baseName = FileOps.SanitizeFileName(Settings.FileNamePrefix) + TimeStamp();
        var path = FileOps.UniquePath(folder, baseName, extension);
        ImageIO.Save(image, path, Settings.JpegQuality);
        return path;
    }

    /// <summary>Date and time for file names, identical in every language.</summary>
    internal static string TimeStamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", CultureInfo.InvariantCulture);
}

/// <summary>
/// One interactive screen operation at a time (capture, text capture, video), with an optional
/// window (quick panel, workspace) hidden while it runs so that it does not appear on the screenshot.
/// </summary>
internal sealed class ScreenOperation
{
    private Window? _hidden;

    public bool IsBusy { get; private set; }

    /// <summary>True until the hidden window is shown again.</summary>
    public bool HasHiddenWindow => _hidden != null;

    /// <summary>Marks the operation as running and hides the window.</summary>
    /// <returns>False if an operation is already running (nothing is done).</returns>
    public bool TryBegin(Window? hide)
    {
        if (IsBusy) return false;
        IsBusy = true;
        if (hide is { IsVisible: true })
        {
            hide.Hide();
            _hidden = hide;
        }
        return true;
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
        IsBusy = false;
    }
}
