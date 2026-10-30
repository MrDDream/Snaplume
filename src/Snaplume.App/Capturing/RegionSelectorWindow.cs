using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Capturing;

/// <summary>
/// Borderless topmost windows showing the frozen desktop image so the user can pick an area: one window per monitor,
/// so that each one gets the scaling of its own monitor (a single window across monitors of different DPI would be
/// scaled for one of them only). The windows share one <see cref="SelectionModel"/> in physical pixels: a drag may
/// cross monitors.
/// </summary>
public sealed class RegionSelectorWindow : Window
{
    /// <summary>The selection being shown: screenshots, text captures and videos share the screen.</summary>
    private static Session? _current;

    private readonly Session _session;
    private readonly PixelRect _monitor; // relative to the desktop image
    private readonly SelectionOverlay _overlay;

    /// <summary>True while a selection is on screen; another one must not be opened.</summary>
    public static bool IsOpen => _current != null;

    /// <summary>Closes the selection on screen, if any, as if the user had cancelled it.</summary>
    public static void CancelCurrent() => _current?.Model.Cancel();

    /// <summary>
    /// Lets the user pick an area of the desktop image: drag, or click a window or control (when the system can list
    /// them, see <see cref="PlatformFeatures.WindowDetection"/>).
    /// </summary>
    /// <param name="title">Optional prefix of the help line, e.g. "Video recording".</param>
    /// <returns>The area relative to the desktop image, or null if cancelled or if a selection is already open.</returns>
    public static async Task<PixelRect?> PickAsync(DesktopImage desktop, string? title = null)
    {
        if (_current != null) return null;
        var monitors = desktop.Monitors.Count > 0
            ? desktop.Monitors.Select(m => m.Bounds.Offset(-desktop.Bounds.X, -desktop.Bounds.Y)).ToList()
            : new List<PixelRect> { new(0, 0, desktop.Bounds.Width, desktop.Bounds.Height) };
        // List the windows under the mouse now: once shown, the selection would cover them all.
        var windows = ListWindows(desktop, monitors);
        var model = new SelectionModel(desktop.Image.Width, desktop.Image.Height, monitors, windows);
        var session = new Session(desktop, model, title);
        _current = session;
        Bitmap? bitmap = null;
        try
        {
            bitmap = AvaloniaImages.ToBitmap(desktop.Image);
            session.Bitmap = bitmap;
            var result = new TaskCompletionSource<PixelRect?>(TaskCreationOptions.RunContinuationsAsynchronously);
            model.Completed += rect => result.TrySetResult(rect);
            model.Cancelled += () => result.TrySetResult(null);
            model.Changed += () =>
            {
                foreach (var window in session.Windows) window._overlay.InvalidateVisual();
            };

            if (desktop.Cursor is (int cx, int cy)) model.MoveTo(cx - desktop.Bounds.X, cy - desktop.Bounds.Y);
            else if (AppServices.Platform.Pointer.TryGetPosition(out int px, out int py)) model.MoveTo(px - desktop.Bounds.X, py - desktop.Bounds.Y);

            foreach (var monitor in monitors)
            {
                var window = new RegionSelectorWindow(session, monitor);
                session.Windows.Add(window);
                // A window closed by the system (Alt+F4, another session) cancels the selection.
                window.Closed += (_, _) => model.Cancel();
                window.Show();
            }
            session.FocusWindowUnderCursor();

            var picked = await result.Task;
            return picked;
        }
        finally
        {
            _current = null;
            foreach (var window in session.Windows) window.CloseQuietly();
            session.Bitmap = null;
            bitmap?.Dispose();
        }
    }

    private static IWindowSnapshot ListWindows(DesktopImage desktop, IReadOnlyList<PixelRect> monitors)
    {
        var platform = AppServices.Platform;
        if (!platform.Has(PlatformFeatures.WindowDetection)) return EmptyWindowSnapshot.Instance;
        try
        {
            return platform.Windows.Snapshot(desktop.Bounds, monitors);
        }
        catch (Exception ex)
        {
            // Dragging still works.
            ErrorLog.Write("Détection des fenêtres", ex);
            return EmptyWindowSnapshot.Instance;
        }
    }

    private RegionSelectorWindow(Session session, PixelRect monitor)
    {
        _session = session;
        _monitor = monitor;

        WindowDecorations = WindowDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Title = Loc.T("Selection.WindowTitle");
        // The desktop image is drawn in pixel coordinates: never mirrored, even in Arabic.
        FlowDirection = FlowDirection.LeftToRight;

        var absolute = monitor.Offset(session.Desktop.Bounds.X, session.Desktop.Bounds.Y);
        Position = new PixelPoint(absolute.X, absolute.Y);
        double scaling = ScalingOf(absolute);
        Width = absolute.Width / scaling;
        Height = absolute.Height / scaling;

        _overlay = new SelectionOverlay(session, monitor);
        Content = _overlay;

        Opened += (_, _) =>
        {
            // The system may have placed or scaled the window differently: put it back over its monitor, in pixels.
            Position = new PixelPoint(absolute.X, absolute.Y);
            // Window managers keep their panels above ordinary windows, even topmost ones; not above a full-screen one.
            if (!OperatingSystem.IsWindows()) WindowState = WindowState.FullScreen;
        };
    }

    /// <summary>Scaling of the monitor, as Avalonia sees it (the desktop capture's value as a fallback).</summary>
    private double ScalingOf(PixelRect absolute)
    {
        try
        {
            var screen = Screens.ScreenFromBounds(new Avalonia.PixelRect(absolute.X, absolute.Y, absolute.Width, absolute.Height));
            if (screen != null && screen.Scaling > 0) return screen.Scaling;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Sélection de zone", ex);
        }
        var info = _session.Desktop.Monitors.FirstOrDefault(m => m.Bounds == absolute);
        return info is { Scale: > 0 } ? info.Scale : 1;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled) return;
        var key = e.Key switch
        {
            Key.Escape => SelectionKey.Escape,
            Key.Enter => SelectionKey.Enter,
            Key.F => SelectionKey.F,
            Key.A => SelectionKey.A,
            Key.Left => SelectionKey.Left,
            Key.Right => SelectionKey.Right,
            Key.Up => SelectionKey.Up,
            Key.Down => SelectionKey.Down,
            _ => (SelectionKey?)null,
        };
        if (key is not SelectionKey k) return;
        var model = _session.Model;
        if (!model.Key(k, e.KeyModifiers.HasFlag(KeyModifiers.Shift))) return;
        e.Handled = true;
        if (k is SelectionKey.Left or SelectionKey.Right or SelectionKey.Up or SelectionKey.Down)
        {
            // The mouse follows the crosshair when the system lets Snaplume move it (not under Wayland).
            try
            {
                AppServices.Platform.Pointer.SetPosition(model.X + _session.Desktop.Bounds.X, model.Y + _session.Desktop.Bounds.Y);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Sélection de zone", ex);
            }
        }
    }

    private void CloseQuietly()
    {
        try
        {
            Close();
        }
        catch (InvalidOperationException)
        {
            // Already closing.
        }
    }

    /// <summary>One selection on screen: the desktop image, its model and the windows showing it.</summary>
    internal sealed class Session
    {
        public Session(DesktopImage desktop, SelectionModel model, string? title)
        {
            Desktop = desktop;
            Model = model;
            Title = title;
        }

        public DesktopImage Desktop { get; }
        public SelectionModel Model { get; }
        public string? Title { get; }
        public Bitmap? Bitmap { get; set; }
        public List<RegionSelectorWindow> Windows { get; } = new();

        /// <summary>The keyboard goes to the monitor under the mouse.</summary>
        public void FocusWindowUnderCursor()
        {
            var monitor = Model.HasPosition ? Model.MonitorAt(Model.X, Model.Y) : Model.Monitors[0];
            var window = Windows.FirstOrDefault(w => w._monitor == monitor) ?? Windows.FirstOrDefault();
            if (window == null) return;
            window.Activate();
            window._overlay.Focus();
        }
    }
}
