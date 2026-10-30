using Avalonia.Controls;

namespace Snaplume.Capturing;

/// <summary>
/// A screen operation (capture, text or code capture, video) with an optional window (quick panel,
/// workspace) hidden while it runs so that it does not appear on the screenshot. Each service runs one
/// at a time; across services, only one may be in its interactive part (countdown, area selection,
/// scrolling) at a time: e.g. no video selection opens while a scrolling capture turns the wheel.
/// A video that is recording is past that part: screenshots may still be taken meanwhile.
/// </summary>
internal sealed class ScreenOperation
{
    /// <summary>
    /// At least this long between hiding a window and the screenshot: the window must be gone from the screen,
    /// including the fade-out animations of the desktop.
    /// </summary>
    private const int HiddenWindowSettleMs = 250;

    /// <summary>The operation in its interactive part, whatever the service.</summary>
    private static ScreenOperation? _interactive;

    private Window? _hidden;

    public bool IsBusy { get; private set; }

    /// <summary>True until the hidden window is shown again.</summary>
    public bool HasHiddenWindow => _hidden != null;

    /// <summary>True while any operation is in its interactive part.</summary>
    public static bool AnyInteractive => _interactive != null;

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
        if (_hidden != null) settleMs = Math.Max(settleMs, HiddenWindowSettleMs);
        return settleMs > 0 ? Task.Delay(settleMs) : Task.CompletedTask;
    }

    /// <summary>Shows the hidden window again, if any.</summary>
    public void RestoreWindow(bool activate)
    {
        var window = _hidden;
        _hidden = null;
        if (window == null) return;
        try
        {
            window.Show();
            if (activate) window.Activate();
        }
        catch (InvalidOperationException)
        {
            // Closed meanwhile (e.g. the workspace window was closed from the tray).
        }
    }

    /// <summary>Ends the operation; the hidden window comes back if it was not restored yet.</summary>
    public void End()
    {
        RestoreWindow(activate: false);
        EndInteractive();
        IsBusy = false;
    }
}
