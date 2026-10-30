using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Snaplume.Core;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// Notification-area icon through Avalonia's TrayIcon (StatusNotifierItem over D-Bus on Linux: KDE, Cinnamon,
/// XFCE; GNOME needs the AppIndicator extension), with notifications from org.freedesktop.Notifications.
/// </summary>
internal sealed class LinuxTray : ITray
{
    private readonly TrayIcon _icon;
    private readonly NativeMenu _menu = new();
    private readonly Notifications _notifications = new();
    private Func<IReadOnlyList<TrayMenuItem>>? _menuBuilder;

    public LinuxTray(string tooltip)
    {
        _icon = new TrayIcon
        {
            Icon = LoadIcon(),
            ToolTipText = tooltip,
            Menu = _menu,
            IsVisible = true,
        };
        _icon.Clicked += (_, _) => Activated?.Invoke();
        // StatusNotifierItem hosts fetch the menu when it opens: rebuilt then, with the current state.
        _menu.Opening += (_, _) => Rebuild();
        if (Application.Current is { } app)
        {
            var icons = TrayIcon.GetIcons(app) ?? new TrayIcons();
            icons.Add(_icon);
            TrayIcon.SetIcons(app, icons);
        }
    }

    public event Action? Activated;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuBuilder
    {
        get => _menuBuilder;
        set
        {
            _menuBuilder = value;
            Rebuild();
        }
    }

    public void SetTooltip(string text) => _icon.ToolTipText = text;

    public void ShowInfo(string title, string text, Action? onClick = null) =>
        _ = _notifications.ShowAsync(title, text, Notifications.Urgency.Normal, onClick);

    public void ShowWarning(string title, string text, Action? onClick = null) =>
        _ = _notifications.ShowAsync(title, text, Notifications.Urgency.Normal, onClick);

    public void ShowError(string title, string text) =>
        _ = _notifications.ShowAsync(title, text, Notifications.Urgency.Critical, null);

    private void Rebuild()
    {
        try
        {
            var items = _menuBuilder?.Invoke() ?? Array.Empty<TrayMenuItem>();
            _menu.Items.Clear();
            foreach (var item in items) _menu.Items.Add(ToNative(item));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Menu de l'icône de notification", ex);
        }
    }

    private NativeMenuItemBase ToNative(TrayMenuItem item)
    {
        if (item.Text == null) return new NativeMenuItemSeparator();
        var native = new NativeMenuItem(item.Text)
        {
            IsEnabled = item.Enabled,
            ToggleType = item.Checked ? MenuItemToggleType.CheckBox : MenuItemToggleType.None,
            IsChecked = item.Checked,
        };
        if (item.Shortcut != null && KeyGestureOrNull(item.Shortcut) is { } gesture) native.Gesture = gesture;
        if (item.Children is { Count: > 0 } children)
        {
            var submenu = new NativeMenu();
            foreach (var child in children) submenu.Items.Add(ToNative(child));
            native.Menu = submenu;
        }
        else if (item.Click is { } click)
        {
            native.Click += (_, _) =>
            {
                try
                {
                    click();
                }
                catch (Exception ex)
                {
                    ErrorLog.Write($"Menu « {item.Text} »", ex);
                }
            };
        }
        return native;
    }

    /// <summary>The menu shows the shortcut when Avalonia can parse it ("Ctrl+Shift+PrintScreen" is not a KeyGesture).</summary>
    private static KeyGesture? KeyGestureOrNull(string text)
    {
        try
        {
            return KeyGesture.Parse(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>The icon embedded in this assembly (rendered from docs/branding/snaplume.svg).</summary>
    private static WindowIcon? LoadIcon()
    {
        try
        {
            using var stream = typeof(LinuxTray).Assembly.GetManifestResourceStream("Snaplume.Platform.Linux.tray.png");
            return stream == null ? null : new WindowIcon(stream);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Icône de notification", ex);
            return null;
        }
    }

    public void Dispose()
    {
        _notifications.Dispose();
        _icon.IsVisible = false;
        if (Application.Current is { } app && TrayIcon.GetIcons(app) is { } icons) icons.Remove(_icon);
        _icon.Dispose();
    }
}
