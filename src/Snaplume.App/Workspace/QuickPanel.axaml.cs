using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Workspace;

/// <summary>
/// Compact panel docked near the notification area: recent captures and
/// the main actions, opened from the tray icon.
/// </summary>
public partial class QuickPanel : CaptureHostWindow
{
    /// <summary>The user moved the panel: it is shown where they left it, not docked.</summary>
    private bool _movedByUser;
    /// <summary>The title bar is being dragged: a change of position comes from the user.</summary>
    private bool _dragging;

    public QuickPanel()
    {
        InitializeComponent();
        Host(Gallery, WorkspaceList);
        TitleBar.PointerPressed += OnTitlePointerPressed;
        ResizeGrip.PointerPressed += OnGripPointerPressed;
        PositionChanged += (_, _) =>
        {
            if (_dragging) _movedByUser = true;
        };
        IsWorkspacePaneOpen = AppServices.Settings.PanelWorkspacesOpen;
        ShowWorkspacePane(IsWorkspacePaneOpen, animate: false);
        // Scrolling capture needs the wheel to be turned for the user (not possible on every desktop).
        ScrollingMenuItem.IsVisible = Has(PlatformFeatures.ScrollingCapture);
        UpdateState();
        OnWorkspacesChanged();
        if (Avalonia.Application.Current is App { Videos: { } videos }) OnRecordingChanged(videos.IsBusy);
    }

    protected override TextBlock StatusLine => SummaryText;

    /// <summary>
    /// Shows the panel against the bottom-right corner of the main screen's work area (on the taskbar and the screen
    /// edge), unless the user moved it somewhere still on a screen.
    /// </summary>
    public void ShowDocked()
    {
        _dragging = false;
        // Moved onto a screen unplugged since, or out of reach: docked again.
        if (_movedByUser && !IsOnAScreen()) _movedByUser = false;
        if (!_movedByUser && Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            Position = new PixelPoint(
                area.Right - (int)Math.Round(Width * scale),
                area.Bottom - (int)Math.Round(Height * scale));
        }
        // A drive plugged in or out since last time: its workspace is shown as it is now.
        Workspaces.ForgetAvailability();
        WorkspaceList.Refresh();
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Gallery.FocusList();
    }

    /// <summary>The title bar (where the panel is dragged from) is at least partly in the work area of a screen.</summary>
    private bool IsOnAScreen()
    {
        double scale = Screens.ScreenFromWindow(this)?.Scaling ?? Screens.Primary?.Scaling ?? 1;
        var titleBar = new PixelRect(Position, new PixelSize((int)Math.Round(Width * scale), (int)Math.Round(TitleBar.Height * scale)));
        return Screens.All.Any(s => s.WorkingArea.Intersects(titleBar));
    }

    protected override void UpdateState()
    {
        EditButton.IsEnabled = CopyButton.IsEnabled = Gallery.SelectedCount > 0;
        ReadTextButton.IsEnabled = ReadQrButton.IsEnabled = Gallery.SelectedPaths.Any(ImageIO.IsImage);
        if (!IsShowingMessage) SummaryText.Text = Gallery.SummaryText;
    }

    protected override void OnRecordingChanged(bool recording)
    {
        VideoButtonText.Text = Loc.T(recording ? "Panel.StopRecording" : "Panel.RecordVideo");
        AutomationProperties.SetName(VideoButton, VideoButtonText.Text);
        ToolTip.SetTip(VideoButton, Loc.T(recording ? "Panel.StopRecordingTip" : "Panel.RecordVideoTip"));
        RecordDot.IsVisible = !recording;
        StopSquare.IsVisible = recording;
    }

    protected override void OnWorkspacesChanged() =>
        WorkspaceName.Text = Workspaces.DisplayName(Workspaces.ActiveName);

    // ---- Side panel of the workspaces ----

    /// <summary>Width of the side panel of the workspaces, when open.</summary>
    public const double WorkspacePaneWidth = 200;

    /// <summary>Time the panel takes to slide (its transition in the styles).</summary>
    private static readonly TimeSpan PaneSlide = TimeSpan.FromMilliseconds(160);

    /// <summary>The side panel of the workspaces is open (remembered in the settings).</summary>
    public bool IsWorkspacePaneOpen
    {
        get => WorkspaceButton.IsChecked == true;
        set => WorkspaceButton.IsChecked = value;
    }

    /// <summary>The workspace button was toggled (click, Space, Enter): the panel opens or closes, the gallery shrinks or grows.</summary>
    private void OnWorkspaceButtonChecked(object? sender, RoutedEventArgs e)
    {
        bool open = IsWorkspacePaneOpen;
        ShowWorkspacePane(open, animate: IsVisible);
        var settings = AppServices.Settings;
        if (settings.PanelWorkspacesOpen == open) return;
        settings.PanelWorkspacesOpen = open;
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // The choice still applies until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    private void ShowWorkspacePane(bool open, bool animate)
    {
        if (open)
        {
            WorkspacePane.IsVisible = true;
            WorkspacePane.Width = WorkspacePaneWidth;
            return;
        }
        // The focus leaves the panel with it.
        if (WorkspacePane.IsKeyboardFocusWithin) WorkspaceButton.Focus(NavigationMethod.Tab);
        WorkspacePane.Width = 0;
        if (!animate)
        {
            WorkspacePane.IsVisible = false;
            return;
        }
        // Hidden once it has slid away: out of the Tab order and of the screen readers.
        DispatcherTimer.RunOnce(() =>
        {
            if (!IsWorkspacePaneOpen) WorkspacePane.IsVisible = false;
        }, PaneSlide + TimeSpan.FromMilliseconds(40));
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only on the bar itself: its buttons handle their own clicks.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed
            || e.Source is Visual source && source.FindAncestorOfType<Button>(includeSelf: true) != null) return;
        // Moved by the user only if the panel actually moves (a click on the bar leaves it docked). The drag may
        // return at once and move the window afterwards (Linux): PositionChanged tells then.
        var before = Position;
        _dragging = true;
        BeginMoveDrag(e);
        if (Position != before) _movedByUser = true;
    }

    private void OnGripPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        BeginResizeDrag(FlowDirection == Avalonia.Media.FlowDirection.RightToLeft ? WindowEdge.SouthWest : WindowEdge.SouthEast, e);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnReadText(object? sender, RoutedEventArgs e) => Gallery.ReadTextOfSelection();

    /// <summary>QR codes and barcodes of the first selected image.</summary>
    private void OnReadQrOfSelection(object? sender, RoutedEventArgs e) => Gallery.ReadQrOfSelection();

    private void OnOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance.ShowWorkspace();
    }

    private void OnHide(object? sender, RoutedEventArgs e) => Hide();
}
