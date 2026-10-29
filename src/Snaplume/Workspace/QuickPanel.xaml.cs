using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>
/// Compact panel docked above the notification area: recent captures and
/// the main actions, opened from the tray icon.
/// </summary>
public partial class QuickPanel : CaptureHostWindow
{
    private const double ScreenMargin = 12;
    private bool _movedByUser;

    public QuickPanel()
    {
        InitializeComponent();
        Gallery.SelectionChanged += UpdateState;
        Gallery.Message += ShowMessage;
        UpdateState();
        OnWorkspacesChanged();
        OnRecordingChanged(App.Instance.Videos.IsBusy);
    }

    public void Reload() => Gallery.Reload();

    protected override TextBlock StatusLine => SummaryText;

    /// <summary>Shows the panel in the bottom-right corner, unless the user moved it.</summary>
    public void ShowDocked()
    {
        if (!_movedByUser)
        {
            var area = SystemParameters.WorkArea;
            Left = area.Right - Width - ScreenMargin;
            Top = area.Bottom - Height - ScreenMargin;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Gallery.FocusList();
    }

    protected override void UpdateState()
    {
        EditButton.IsEnabled = CopyButton.IsEnabled = Gallery.SelectedCount > 0;
        ReadTextButton.IsEnabled = Gallery.SelectedPaths.Any(ImageIO.IsImage);
        if (!IsShowingMessage) SummaryText.Text = Gallery.SummaryText;
    }

    protected override void OnRecordingChanged(bool recording)
    {
        VideoButtonText.Text = Loc.T(recording ? "Panel.StopRecording" : "Panel.RecordVideo");
        VideoButton.ToolTip = Loc.T(recording ? "Panel.StopRecordingTip" : "Panel.RecordVideoTip");
        RecordDot.Visibility = recording ? Visibility.Collapsed : Visibility.Visible;
        StopSquare.Visibility = recording ? Visibility.Visible : Visibility.Collapsed;
    }

    protected override void OnWorkspacesChanged() =>
        WorkspaceName.Text = Workspaces.DisplayName(Workspaces.ActiveName);

    /// <summary>Drop-down list of the workspaces (the active one checked), then "New...".</summary>
    private void OnWorkspaceMenu(object sender, RoutedEventArgs e)
    {
        var menu = WorkspaceButton.ContextMenu!;
        menu.Items.Clear();
        foreach (var name in Workspaces.List())
        {
            // A TextBlock, so that an underscore in the name is not taken for an access key.
            var item = new MenuItem
            {
                Header = new TextBlock { Text = Workspaces.DisplayName(name) },
                IsCheckable = true,
                IsChecked = WorkspaceCommands.IsActive(name),
            };
            item.Click += (_, _) => WorkspaceCommands.Select(this, name);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var create = new MenuItem { Header = Loc.T("Workspaces.New"), Icon = MenuIcon("IconAdd") };
        create.Click += (_, _) => WorkspaceCommands.Create(this);
        menu.Items.Add(create);
        OpenMenu(WorkspaceButton, PlacementMode.Bottom);
    }

    private void OnTitleMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        DragMove();
        _movedByUser = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            Hide();
            e.Handled = true;
        }
    }

    private void OnCaptureMenu(object sender, RoutedEventArgs e) => OpenMenu(CaptureMenuButton, PlacementMode.Top);
    private void OnVideoMenu(object sender, RoutedEventArgs e) => OpenMenu(VideoMenuButton, PlacementMode.Top);

    private void OnEdit(object sender, RoutedEventArgs e) => Gallery.EditSelection();
    private void OnCopy(object sender, RoutedEventArgs e) => Gallery.CopySelection();
    private void OnReadText(object sender, RoutedEventArgs e) => Gallery.ReadTextOfSelection();

    private void OnOpenWorkspace(object sender, RoutedEventArgs e)
    {
        Hide();
        App.Instance.ShowWorkspace();
    }

    private void OnHide(object sender, RoutedEventArgs e) => Hide();

    protected override void OnClosed(EventArgs e)
    {
        Gallery.Detach();
        base.OnClosed(e);
    }
}
