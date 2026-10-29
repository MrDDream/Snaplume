using System.Windows;
using Snaplume.Workspace;
using Gdi = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Snaplume.Core;

/// <summary>Notification-area icon with the capture menu.</summary>
public sealed class TrayIcon : IDisposable
{
    private const string AppName = "Snaplume";
    /// <summary>Longest tooltip Windows accepts.</summary>
    private const int MaxTextLength = 127;

    private readonly App _app;
    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Gdi.Font _boldFont;
    private readonly Gdi.Icon _appIcon;
    private Action? _balloonAction;
    private string? _status;

    public TrayIcon(App app)
    {
        _app = app;
        var menu = _menu = new WinForms.ContextMenuStrip();
        if (Loc.IsRightToLeft) menu.RightToLeft = WinForms.RightToLeft.Yes;

        _boldFont = new Gdi.Font(menu.Font, Gdi.FontStyle.Bold);
        menu.Items.Add(new WinForms.ToolStripMenuItem(Loc.T("Tray.MyCaptures"), null, (_, _) => app.ShowQuickPanel()) { Font = _boldFont });
        menu.Items.Add(Loc.T("Tray.Workspace"), null, (_, _) => app.ShowWorkspace());
        menu.Items.Add(BuildWorkspacesMenu());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.CaptureRegion"), null, (_, _) => Capture(CaptureKind.Region));
        menu.Items.Add(Loc.T("Tray.CaptureScreen"), null, (_, _) => Capture(CaptureKind.Screen));
        menu.Items.Add(Loc.T("Tray.CaptureAllScreens"), null, (_, _) => Capture(CaptureKind.AllScreens));
        menu.Items.Add(Loc.T("Tray.RepeatLast"), null, (_, _) => Capture(CaptureKind.RepeatLast));

        var delayed = new WinForms.ToolStripMenuItem(Loc.T("Tray.CaptureDelayed"));
        foreach (var seconds in new[] { 3, 5, 10 })
            delayed.DropDownItems.Add(Loc.T("Common.Seconds", seconds), null, (_, _) => Capture(CaptureKind.Region, seconds));
        menu.Items.Add(delayed);
        menu.Items.Add(Loc.T("Ocr.CaptureText"), null, (_, _) => _ = app.Captures.CaptureTextAsync(settleMs: 200));

        menu.Items.Add(new WinForms.ToolStripSeparator());
        var video = new WinForms.ToolStripMenuItem(Loc.T("Tray.RecordVideo"), null, (_, _) => _ = app.Videos.ToggleAsync(settleMs: 200));
        var videoScreen = new WinForms.ToolStripMenuItem(Loc.T("Tray.RecordScreenVideo"), null, (_, _) => _ = app.Videos.ToggleAsync(fullScreen: true, settleMs: 200));
        menu.Items.Add(video);
        menu.Items.Add(videoScreen);
        menu.Opening += (_, _) =>
        {
            // During a recording the first entry stops it.
            video.Text = Loc.T(app.Videos.IsBusy ? "Tray.StopVideo" : "Tray.RecordVideo");
            videoScreen.Visible = !app.Videos.IsBusy;
        };

        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.OpenFolder"), null, (_, _) => FileOps.OpenFolder(Workspaces.ActiveFolder));
        menu.Items.Add(Loc.T("Tray.Settings"), null, (_, _) => app.ShowSettings());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(Loc.T("Tray.Quit"), null, (_, _) => app.ExitApp());

        _appIcon = LoadIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = menu,
            Visible = true,
        };
        UpdateText();
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left) app.ToggleQuickPanel();
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            action?.Invoke();
        };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
        Workspaces.Changed += UpdateText;
    }

    /// <summary>Submenu listing the workspaces (the active one checked), filled when it opens.</summary>
    private WinForms.ToolStripMenuItem BuildWorkspacesMenu()
    {
        var item = new WinForms.ToolStripMenuItem(Loc.T("Tray.Workspaces"));
        // A placeholder so that the arrow is shown before the first opening.
        item.DropDownItems.Add(new WinForms.ToolStripSeparator());
        item.DropDownOpening += (_, _) =>
        {
            item.DropDownItems.Clear();
            var active = Workspaces.ActiveName;
            foreach (var name in Workspaces.List())
            {
                // "&" would underline the next letter (mnemonic).
                var entry = new WinForms.ToolStripMenuItem(Workspaces.DisplayName(name).Replace("&", "&&"), null, (_, _) => Workspaces.Select(name))
                {
                    Checked = string.Equals(name, active, StringComparison.OrdinalIgnoreCase),
                };
                item.DropDownItems.Add(entry);
            }
            item.DropDownItems.Add(new WinForms.ToolStripSeparator());
            item.DropDownItems.Add(Loc.T("Tray.NewWorkspace"), null, (_, _) => CreateWorkspace());
        };
        return item;
    }

    /// <summary>Asks for a name and creates the workspace (it becomes the active one).</summary>
    private static void CreateWorkspace()
    {
        // No owner window here: InputDialog accepts none, it is then centered on the screen.
        var dialog = new InputDialog(null!, Loc.T("Tray.NewWorkspaceTitle"), Loc.T("Tray.NewWorkspaceLabel"), "")
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = true,
            Topmost = true,
        };
        if (dialog.ShowDialog() != true) return;
        try
        {
            Workspaces.Create(dialog.Value);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Création d'un espace de travail", ex);
            MessageBox.Show(Loc.T("Tray.NewWorkspaceFailed", ex.Message), AppName, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Capture(CaptureKind mode, int delay = 0) =>
        _ = _app.Captures.CaptureAsync(mode, delay, settleMs: 200);

    public void ShowInfo(string title, string text, Action? onClick = null) =>
        Show(title, text, WinForms.ToolTipIcon.Info, onClick);

    public void ShowWarning(string title, string text, Action? onClick = null) =>
        Show(title, text, WinForms.ToolTipIcon.Warning, onClick);

    public void ShowError(string title, string text) =>
        Show(title, text, WinForms.ToolTipIcon.Error, null);

    private void Show(string title, string text, WinForms.ToolTipIcon kind, Action? onClick)
    {
        // Windows refuses a balloon without text (e.g. an exception with an empty message).
        if (string.IsNullOrWhiteSpace(text)) text = string.IsNullOrWhiteSpace(title) ? AppName : title;
        _balloonAction = onClick;
        try
        {
            _icon.ShowBalloonTip(4000, title ?? "", text, kind);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Notification", ex);
        }
    }

    /// <summary>Tooltip text, e.g. the timer countdown. Null restores the default.</summary>
    public void SetStatus(string? text)
    {
        _status = text;
        UpdateText();
    }

    /// <summary>The status, else the name of the application and of the active workspace.</summary>
    private void UpdateText()
    {
        var text = _status;
        if (string.IsNullOrEmpty(text))
        {
            var workspace = Workspaces.ActiveName;
            text = workspace.Length == 0 ? AppName : Loc.T("Tray.TooltipWorkspace", Workspaces.DisplayName(workspace));
        }
        _icon.Text = text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

    private static Gdi.Icon LoadIcon()
    {
        var resource = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico"));
        if (resource == null) return (Gdi.Icon)Gdi.SystemIcons.Application.Clone();
        using var stream = resource.Stream;
        return new Gdi.Icon(stream, WinForms.SystemInformation.SmallIconSize);
    }

    public void Dispose()
    {
        Workspaces.Changed -= UpdateText;
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Dispose();
        _boldFont.Dispose();
        _appIcon.Dispose();
    }
}
