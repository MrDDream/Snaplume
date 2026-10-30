using Avalonia.Threading;
using Snaplume.Core;
using Gdi = System.Drawing;
using WinForms = System.Windows.Forms;

namespace Snaplume.Platform.Windows.Tray;

/// <summary>
/// Notification-area icon (WinForms NotifyIcon) with its menu and balloon notifications, as in Snaplume 1.1.
/// </summary>
/// <remarks>
/// Created on the Avalonia UI thread, whose message loop also serves the icon's hidden window and the menu.
/// Clicks are posted to the Avalonia dispatcher rather than run inside WinForms' event handlers, so that their
/// exceptions reach the application's handler instead of WinForms' error dialog, and the menu is closed first.
/// </remarks>
public sealed class TrayIcon : ITray
{
    private const string AppName = "Snaplume";
    /// <summary>Longest tooltip Windows accepts.</summary>
    private const int MaxTextLength = 127;

    private readonly WinForms.NotifyIcon _icon;
    private readonly WinForms.ContextMenuStrip _menu;
    private readonly Gdi.Font _boldFont;
    private readonly Gdi.Icon _appIcon;
    private Action? _balloonAction;

    public TrayIcon(string tooltip)
    {
        _menu = new WinForms.ContextMenuStrip();
        if (Loc.IsRightToLeft) _menu.RightToLeft = WinForms.RightToLeft.Yes;
        _boldFont = new Gdi.Font(_menu.Font, Gdi.FontStyle.Bold);
        _menu.Opening += OnMenuOpening;

        _appIcon = LoadIcon();
        _icon = new WinForms.NotifyIcon
        {
            Icon = _appIcon,
            ContextMenuStrip = _menu,
        };
        SetTooltip(tooltip);
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left && Activated is { } activated) Dispatcher.UIThread.Post(activated);
        };
        _icon.BalloonTipClicked += (_, _) =>
        {
            var action = _balloonAction;
            _balloonAction = null;
            if (action != null) Dispatcher.UIThread.Post(action);
        };
        _icon.BalloonTipClosed += (_, _) => _balloonAction = null;
        _icon.Visible = true;
    }

    public event Action? Activated;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuBuilder { get; set; }

    public void SetTooltip(string text)
    {
        if (string.IsNullOrEmpty(text)) text = AppName;
        _icon.Text = text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

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

    /// <summary>Rebuilds the menu each time it opens: the entries depend on the state (recording, workspaces...).</summary>
    private void OnMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        IReadOnlyList<TrayMenuItem> items;
        try
        {
            items = MenuBuilder?.Invoke() ?? Array.Empty<TrayMenuItem>();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Menu de l'icône de notification", ex);
            items = Array.Empty<TrayMenuItem>();
        }
        _menu.SuspendLayout();
        DisposeItems(_menu.Items);
        AddItems(_menu.Items, items);
        _menu.ResumeLayout();
        // An empty menu is opened with Cancel set: it must be cleared once the items are there.
        e.Cancel = _menu.Items.Count == 0;
    }

    private void AddItems(WinForms.ToolStripItemCollection target, IReadOnlyList<TrayMenuItem> items)
    {
        foreach (var item in items)
        {
            if (item.Text == null)
            {
                target.Add(new WinForms.ToolStripSeparator());
                continue;
            }
            // "&" would underline the next letter (mnemonic).
            var entry = new WinForms.ToolStripMenuItem(item.Text.Replace("&", "&&"))
            {
                Enabled = item.Enabled,
                Checked = item.Checked,
            };
            if (item.Bold) entry.Font = _boldFont;
            if (!string.IsNullOrEmpty(item.Shortcut))
            {
                entry.ShortcutKeyDisplayString = item.Shortcut;
                entry.ShowShortcutKeys = true;
            }
            if (item.Click is { } click) entry.Click += (_, _) => Dispatcher.UIThread.Post(click);
            if (item.Children is { Count: > 0 } children) AddItems(entry.DropDownItems, children);
            target.Add(entry);
        }
    }

    /// <summary>Removes and frees the previous entries (submenus own native windows).</summary>
    private static void DisposeItems(WinForms.ToolStripItemCollection items)
    {
        var old = items.Cast<WinForms.ToolStripItem>().ToList();
        items.Clear();
        foreach (var item in old) item.Dispose();
    }

    /// <summary>The application's icon, embedded from Snaplume.App/Assets/app.ico, at the size of the notification area.</summary>
    private static Gdi.Icon LoadIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Snaplume.Platform.Windows.app.ico");
            if (stream != null) return new Gdi.Icon(stream, WinForms.SystemInformation.SmallIconSize);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Icône de notification", ex);
        }
        return (Gdi.Icon)Gdi.SystemIcons.Application.Clone();
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _menu.Opening -= OnMenuOpening;
        DisposeItems(_menu.Items);
        _menu.Dispose();
        _boldFont.Dispose();
        _appIcon.Dispose();
    }
}
