using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
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
    private bool _movedByUser;

    public QuickPanel()
    {
        InitializeComponent();
        Gallery.SelectionChanged += UpdateState;
        Gallery.Message += ShowMessage;
        TitleBar.PointerPressed += OnTitlePointerPressed;
        ResizeGrip.PointerPressed += OnGripPointerPressed;
        if (WorkspaceButton.Flyout is MenuFlyout workspaceMenu) workspaceMenu.Opening += OnWorkspaceMenuOpening;
        // Scrolling capture needs the wheel to be turned for the user (not possible on every desktop).
        ScrollingMenuItem.IsVisible = Has(PlatformFeatures.ScrollingCapture);
        UpdateState();
        OnWorkspacesChanged();
        if (Avalonia.Application.Current is App { Videos: { } videos }) OnRecordingChanged(videos.IsBusy);
    }

    public void Reload() => Gallery.Reload();

    protected override TextBlock StatusLine => SummaryText;

    /// <summary>
    /// Shows the panel against the bottom-right corner of the main screen's work area (on the taskbar and the screen
    /// edge), unless the user moved it.
    /// </summary>
    public void ShowDocked()
    {
        if (!_movedByUser && Screens.Primary is { } screen)
        {
            var area = screen.WorkingArea;
            double scale = screen.Scaling;
            Position = new PixelPoint(
                area.Right - (int)Math.Round(Width * scale),
                area.Bottom - (int)Math.Round(Height * scale));
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Gallery.FocusList();
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
        ToolTip.SetTip(VideoButton, Loc.T(recording ? "Panel.StopRecordingTip" : "Panel.RecordVideoTip"));
        RecordDot.IsVisible = !recording;
        StopSquare.IsVisible = recording;
    }

    protected override void OnWorkspacesChanged() =>
        WorkspaceName.Text = Workspaces.DisplayName(Workspaces.ActiveName);

    /// <summary>Drop-down list of the workspaces (the active one checked), then "New...".</summary>
    private void OnWorkspaceMenuOpening(object? sender, EventArgs e)
    {
        if (sender is not MenuFlyout menu) return;
        menu.Items.Clear();
        foreach (var name in Workspaces.List())
        {
            // A TextBlock, so that an underscore in the name is not taken for an access key.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = Workspaces.DisplayName(name) },
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = WorkspaceCommands.IsActive(name),
            };
            var target = name;
            item.Click += (_, _) => WorkspaceCommands.Select(this, target);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = Loc.T("Workspaces.New"), Icon = MenuIcon("IconAdd") };
        create.Click += (_, _) => _ = WorkspaceCommands.CreateAsync(this);
        menu.Items.Add(create);
    }

    private void OnTitlePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Only on the bar itself: its buttons handle their own clicks.
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed || e.Source is Visual source && source.FindAncestorOfTypeOrSelf<Button>() != null) return;
        BeginMoveDrag(e);
        _movedByUser = true;
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

    private void OnEdit(object? sender, RoutedEventArgs e) => Gallery.EditSelection();
    private void OnCopy(object? sender, RoutedEventArgs e) => Gallery.CopySelection();
    private void OnReadText(object? sender, RoutedEventArgs e) => Gallery.ReadTextOfSelection();

    /// <summary>QR codes and barcodes of the first selected image.</summary>
    private void OnReadQrOfSelection(object? sender, RoutedEventArgs e) => Gallery.ReadQrOfSelection();

    private void OnOpenWorkspace(object? sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance.ShowWorkspace();
    }

    private void OnHide(object? sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        Gallery.Detach();
        base.OnClosed(e);
    }
}

internal static class VisualExtensions
{
    /// <summary>The visual or its nearest ancestor of the given type.</summary>
    public static T? FindAncestorOfTypeOrSelf<T>(this Visual visual) where T : class
    {
        for (Visual? current = visual; current != null; current = Avalonia.VisualTree.VisualExtensions.GetVisualParent(current))
        {
            if (current is T match) return match;
        }
        return null;
    }
}
