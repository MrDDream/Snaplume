using Avalonia.Controls;
using Path = Avalonia.Controls.Shapes.Path;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Workspace;

/// <summary>
/// Common part of the quick panel and the workspace window, which both host a capture gallery:
/// capture and recording commands, short messages over the summary, and closing that only hides.
/// </summary>
public class CaptureHostWindow : Window
{
    private readonly DispatcherTimer _messageTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    public CaptureHostWindow()
    {
        _messageTimer.Tick += (_, _) =>
        {
            _messageTimer.Stop();
            UpdateState();
        };
        // The designer has no running application.
        if (Avalonia.Application.Current is not App app || app.Videos == null) return;
        app.Videos.StateChanged += OnVideoStateChanged;
        Workspaces.Changed += OnWorkspacesChanged;
    }

    /// <summary>Text line showing the summary of the gallery, or a short message.</summary>
    protected virtual TextBlock? StatusLine => null;

    /// <summary>A message is displayed: the summary waits until it times out.</summary>
    protected bool IsShowingMessage => _messageTimer.IsEnabled;

    /// <summary>The platform offers this feature (commands it lacks are hidden).</summary>
    protected static bool Has(PlatformFeatures feature) => AppServices.HasPlatform && AppServices.Platform.Has(feature);

    /// <summary>Shows a short feedback (e.g. "Image copied") for a few seconds instead of the summary.</summary>
    protected void ShowMessage(string message)
    {
        if (StatusLine is not { } line) return;
        line.Text = message;
        _messageTimer.Stop();
        _messageTimer.Start();
    }

    /// <summary>Updates the buttons and the summary from the gallery.</summary>
    protected virtual void UpdateState()
    {
    }

    /// <summary>A recording started or ended: the video button becomes "Stop" while recording.</summary>
    protected virtual void OnRecordingChanged(bool recording)
    {
    }

    /// <summary>The list of workspaces or the active one changed.</summary>
    protected virtual void OnWorkspacesChanged()
    {
    }

    /// <summary>Raised on the recorder's thread maybe: handled on the UI thread.</summary>
    private void OnVideoStateChanged() => Dispatcher.UIThread.Post(() => OnRecordingChanged(App.Instance.Videos.IsBusy));

    // ---- Commands ----

    protected void OnCaptureRegion(object? sender, RoutedEventArgs e) => Capture(CaptureKind.Region);
    protected void OnCaptureScreen(object? sender, RoutedEventArgs e) => Capture(CaptureKind.Screen);
    protected void OnCaptureAllScreens(object? sender, RoutedEventArgs e) => Capture(CaptureKind.AllScreens);
    protected void OnCaptureRepeat(object? sender, RoutedEventArgs e) => Capture(CaptureKind.RepeatLast);

    /// <summary>Menu item whose Tag is the delay in seconds.</summary>
    protected void OnDelayedCapture(object? sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out var seconds)) Capture(CaptureKind.Region, seconds);
    }

    private void Capture(CaptureKind kind, int delay = 0) =>
        _ = App.Instance.Captures.CaptureAsync(kind, delay, hide: this);

    protected void OnCaptureText(object? sender, RoutedEventArgs e) => _ = App.Instance.Captures.CaptureTextAsync(hide: this);

    /// <summary>Scrolls a window or a control and stitches it into one tall capture.</summary>
    protected void OnCaptureScrolling(object? sender, RoutedEventArgs e) => Capture(CaptureKind.Scrolling);

    /// <summary>Reads the QR codes and barcodes of an area of the screen.</summary>
    protected void OnReadQr(object? sender, RoutedEventArgs e) => _ = App.Instance.Captures.CaptureQrAsync(hide: this);

    /// <summary>Starts recording an area, or stops the recording in progress.</summary>
    protected void OnRecordVideo(object? sender, RoutedEventArgs e) => _ = App.Instance.Videos.ToggleAsync(hide: this);

    protected void OnRecordScreen(object? sender, RoutedEventArgs e) => _ = App.Instance.Videos.ToggleAsync(fullScreen: true, hide: this);

    protected void OnOpenFolder(object? sender, RoutedEventArgs e) => WorkspaceCommands.OpenFolder(this, Workspaces.ActiveName);

    protected void OnSettings(object? sender, RoutedEventArgs e) => App.Instance.ShowSettings();

    /// <summary>Small line icon (Icons.axaml) for a menu item.</summary>
    protected Path MenuIcon(string geometry)
    {
        var icon = new Path { Classes = { "icon" }, Width = 15.6, Height = 15.6, RenderTransform = new ScaleTransform(0.65, 0.65) };
        if (this.TryFindResource(geometry, out var data) && data is Geometry g) icon.Data = g;
        return icon;
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        // Closing (or Alt+F4) only hides: the application keeps running in the notification area. When the
        // application or the session ends, the window really closes.
        if (Avalonia.Application.Current is App { IsExiting: false }
            && e.CloseReason is not (WindowCloseReason.ApplicationShutdown or WindowCloseReason.OSShutdown))
        {
            e.Cancel = true;
            Hide();
        }
        base.OnClosing(e);
    }

    protected override void OnClosed(EventArgs e)
    {
        _messageTimer.Stop();
        if (Avalonia.Application.Current is App { Videos: { } videos }) videos.StateChanged -= OnVideoStateChanged;
        Workspaces.Changed -= OnWorkspacesChanged;
        base.OnClosed(e);
    }
}
