using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Platform.Windows.Native;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Tray;

/// <summary>
/// Notification-area icon (Shell_NotifyIcon) with its menu (a native popup menu) and balloon notifications, which
/// Windows 10 and 11 show as toasts.
/// </summary>
/// <remarks>
/// Created on the Avalonia UI thread, whose message loop also serves the icon's hidden window and the menu. The icon
/// uses the original callback protocol (as WinForms' NotifyIcon did): the mouse message or balloon event arrives in
/// lParam. Clicks are posted to the Avalonia dispatcher rather than run inside the window procedure, so that their
/// exceptions reach the application's handler, and the menu is closed first.
/// </remarks>
public sealed class TrayIcon : ITray
{
    private const string AppName = "Snaplume";
    /// <summary>Longest tooltip Windows accepts.</summary>
    private const int MaxTextLength = 127;
    private const uint IconId = 1;
    private const uint CallbackMessage = WM_APP + 1;

    private readonly HiddenWindow _window;
    private readonly IntPtr _appIcon;
    private readonly bool _ownsIcon;
    /// <summary>Sent to every top-level window when Explorer (the task bar) starts again: the icon must be added again.</summary>
    private readonly uint _taskbarCreated;
    private string _tooltip = AppName;
    private Action? _balloonAction;
    private bool _added;
    private bool _menuOpen;
    private bool _doubleClicked;

    public TrayIcon(string tooltip)
    {
        _taskbarCreated = RegisterWindowMessage("TaskbarCreated");
        // A top-level window (hidden): message-only windows get no broadcast, and cannot take the foreground for the menu.
        _window = new HiddenWindow("Tray", OnMessage, messageOnly: false);
        (_appIcon, _ownsIcon) = LoadAppIcon();
        _tooltip = Shorten(tooltip);
        Add();
    }

    public event Action? Activated;

    public Func<IReadOnlyList<TrayMenuItem>>? MenuBuilder { get; set; }

    public void SetTooltip(string text)
    {
        _tooltip = Shorten(text);
        if (!_added) return;
        var data = NewData(NIF_TIP);
        data.szTip = _tooltip;
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    public void ShowInfo(string title, string text, Action? onClick = null) => Show(title, text, NIIF_INFO, onClick);

    public void ShowWarning(string title, string text, Action? onClick = null) => Show(title, text, NIIF_WARNING, onClick);

    public void ShowError(string title, string text) => Show(title, text, NIIF_ERROR, null);

    private static string Shorten(string text)
    {
        if (string.IsNullOrEmpty(text)) return AppName;
        return text.Length > MaxTextLength ? text[..(MaxTextLength - 1)] + "…" : text;
    }

    private void Show(string title, string text, uint kind, Action? onClick)
    {
        // Windows refuses a balloon without text (e.g. an exception with an empty message).
        if (string.IsNullOrWhiteSpace(text)) text = string.IsNullOrWhiteSpace(title) ? AppName : title;
        _balloonAction = onClick;
        var data = NewData(NIF_INFO);
        data.szInfoTitle = Truncate(title ?? "", 63);
        data.szInfo = Truncate(text, 255);
        data.dwInfoFlags = kind;
        data.uTimeoutOrVersion = 4000;
        if (!Shell_NotifyIcon(NIM_MODIFY, ref data))
            ErrorLog.Write("Notification", "Shell_NotifyIcon a refusé la notification");
    }

    private static string Truncate(string text, int length) => text.Length > length ? text[..(length - 1)] + "…" : text;

    private NOTIFYICONDATA NewData(uint flags) => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _window.Handle,
        uID = IconId,
        uFlags = flags,
        szTip = "",
        szInfo = "",
        szInfoTitle = "",
    };

    /// <summary>Puts the icon in the notification area (again after an Explorer restart).</summary>
    private void Add()
    {
        var data = NewData(NIF_MESSAGE | NIF_ICON | NIF_TIP);
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _appIcon;
        data.szTip = _tooltip;
        // Fails while the task bar is not there yet (session start): TaskbarCreated comes once it is. Also fails when
        // the icon is still there (TaskbarCreated sent without an Explorer restart, e.g. a DPI change): it is updated.
        _added = Shell_NotifyIcon(NIM_ADD, ref data) || Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private IntPtr? OnMessage(uint message, IntPtr wParam, IntPtr lParam)
    {
        if (message == _taskbarCreated && _taskbarCreated != 0)
        {
            Add();
            return null;
        }
        if (message != CallbackMessage) return null;
        switch ((uint)lParam.ToInt64() & 0xFFFF)
        {
            case WM_LBUTTONDBLCLK:
                // The double click's second button-up does not toggle the panel back (as WinForms' MouseClick).
                _doubleClicked = true;
                break;
            case WM_LBUTTONUP:
                if (!_doubleClicked && Activated is { } activated) Dispatcher.UIThread.Post(activated);
                _doubleClicked = false;
                break;
            case WM_RBUTTONUP:
                ShowMenu();
                break;
            case NIN_BALLOONUSERCLICK:
                var action = _balloonAction;
                _balloonAction = null;
                if (action != null) Dispatcher.UIThread.Post(action);
                break;
            case NIN_BALLOONHIDE or NIN_BALLOONTIMEOUT:
                _balloonAction = null;
                break;
        }
        return IntPtr.Zero;
    }

    /// <summary>Builds the menu each time it opens: the entries depend on the state (recording, workspaces...).</summary>
    private void ShowMenu()
    {
        if (_menuOpen) return;
        IReadOnlyList<TrayMenuItem> items;
        try
        {
            items = MenuBuilder?.Invoke() ?? Array.Empty<TrayMenuItem>();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Menu de l'icône de notification", ex);
            return;
        }
        if (items.Count == 0) return;

        var commands = new List<Action?>();
        var menu = BuildMenu(items, commands);
        if (menu == IntPtr.Zero) return;
        _menuOpen = true;
        try
        {
            GetCursorPos(out var point);
            // Without the foreground, the menu would not close when clicking elsewhere.
            SetForegroundWindow(_window.Handle);
            uint flags = TPM_RETURNCMD | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN
                         | (GetSystemMetrics(SM_MENUDROPALIGNMENT) != 0 ? TPM_RIGHTALIGN : TPM_LEFTALIGN);
            if (Loc.IsRightToLeft) flags |= TPM_LAYOUTRTL;
            int command = TrackPopupMenuEx(menu, flags, point.X, point.Y, _window.Handle, IntPtr.Zero);
            // Documented workaround: lets the menu close properly the next time it is opened.
            PostMessage(_window.Handle, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (command > 0 && command <= commands.Count && commands[command - 1] is { } click) Dispatcher.UIThread.Post(click);
        }
        finally
        {
            _menuOpen = false;
            // Also frees the submenus.
            DestroyMenu(menu);
        }
    }

    /// <summary>Native menu of the items; <paramref name="commands"/>[id - 1] is the action of the entry with that id.</summary>
    private static IntPtr BuildMenu(IReadOnlyList<TrayMenuItem> items, List<Action?> commands)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero)
        {
            ErrorLog.Write("Menu de l'icône de notification", new Win32Exception(Marshal.GetLastPInvokeError()));
            return IntPtr.Zero;
        }
        bool hasDefault = false;
        foreach (var item in items)
        {
            if (item.Text == null)
            {
                AppendMenu(menu, MF_SEPARATOR, IntPtr.Zero, null);
                continue;
            }
            // "&" would underline the next letter (mnemonic); the shortcut is shown right-aligned after a tab.
            var text = item.Text.Replace("&", "&&");
            if (!string.IsNullOrEmpty(item.Shortcut)) text += "\t" + item.Shortcut;
            uint flags = MF_STRING;
            if (!item.Enabled) flags |= MF_GRAYED;
            if (item.Checked) flags |= MF_CHECKED;
            if (item.Children is { Count: > 0 } children)
            {
                var submenu = BuildMenu(children, commands);
                if (submenu == IntPtr.Zero) continue;
                AppendMenu(menu, flags | MF_POPUP, submenu, text);
                continue;
            }
            commands.Add(item.Click);
            AppendMenu(menu, flags, commands.Count, text);
            // The default entry is drawn in bold (one per menu).
            if (item.Bold && !hasDefault) hasDefault = SetMenuDefaultItem(menu, (uint)commands.Count, 0);
        }
        return menu;
    }

    /// <summary>
    /// The application's icon, embedded from Snaplume.App/Assets/app.ico, at the size of the notification area
    /// (the image of the .ico closest to it); the system's application icon if it cannot be read.
    /// </summary>
    private static (IntPtr Icon, bool Owned) LoadAppIcon()
    {
        try
        {
            using var stream = typeof(TrayIcon).Assembly.GetManifestResourceStream("Snaplume.Platform.Windows.app.ico");
            if (stream != null)
            {
                var file = new byte[stream.Length];
                stream.ReadExactly(file);
                int size = GetSystemMetrics(SM_CXSMICON);
                if (FindIconImage(file, size) is var (offset, length))
                {
                    var icon = CreateIconFromResourceEx(ref file[offset], (uint)length, true, 0x00030000, size, size, 0);
                    if (icon != IntPtr.Zero) return (icon, true);
                    ErrorLog.Write("Icône de notification", new Win32Exception(Marshal.GetLastPInvokeError()));
                }
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Icône de notification", ex);
        }
        return (LoadIcon(IntPtr.Zero, IDI_APPLICATION), false);
    }

    /// <summary>
    /// The image of an .ico file to use at <paramref name="size"/> pixels: the smallest one at least that big, or else
    /// the biggest one (offset and length in the file).
    /// </summary>
    private static (int Offset, int Length)? FindIconImage(byte[] file, int size)
    {
        if (file.Length < 6 || BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(2)) != 1) return null;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(4));
        (int Offset, int Length, int Size, int Bits)? best = null;
        for (int i = 0; i < count; i++)
        {
            int entry = 6 + i * 16;
            if (entry + 16 > file.Length) break;
            // 0 means 256 pixels.
            int width = file[entry] == 0 ? 256 : file[entry];
            int bits = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(entry + 6));
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(entry + 8));
            int offset = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(entry + 12));
            if (offset < 0 || length <= 0 || (long)offset + length > file.Length) continue;
            bool better = best is not { } b || IsBetter((width, bits), (b.Size, b.Bits), size);
            if (better) best = (offset, length, width, bits);
        }
        return best is { } found ? (found.Offset, found.Length) : null;
    }

    private static bool IsBetter((int Size, int Bits) candidate, (int Size, int Bits) current, int size)
    {
        bool candidateFits = candidate.Size >= size, currentFits = current.Size >= size;
        if (candidateFits != currentFits) return candidateFits;
        if (candidate.Size != current.Size) return candidateFits ? candidate.Size < current.Size : candidate.Size > current.Size;
        return candidate.Bits > current.Bits;
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData(0);
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }
        _window.Dispose();
        if (_ownsIcon) DestroyIcon(_appIcon);
    }

    // ---- Win32 ----

    private const uint NIM_ADD = 0;
    private const uint NIM_MODIFY = 1;
    private const uint NIM_DELETE = 2;
    private const uint NIF_MESSAGE = 0x1;
    private const uint NIF_ICON = 0x2;
    private const uint NIF_TIP = 0x4;
    private const uint NIF_INFO = 0x10;
    private const uint NIIF_INFO = 1;
    private const uint NIIF_WARNING = 2;
    private const uint NIIF_ERROR = 3;
    private const uint NIN_BALLOONHIDE = 0x403;
    private const uint NIN_BALLOONTIMEOUT = 0x404;
    private const uint NIN_BALLOONUSERCLICK = 0x405;
    private const uint WM_NULL = 0;
    private const uint WM_LBUTTONUP = 0x202;
    private const uint WM_LBUTTONDBLCLK = 0x203;
    private const uint WM_RBUTTONUP = 0x205;
    private const uint MF_STRING = 0x0;
    private const uint MF_GRAYED = 0x1;
    private const uint MF_CHECKED = 0x8;
    private const uint MF_POPUP = 0x10;
    private const uint MF_SEPARATOR = 0x800;
    private const uint TPM_LEFTALIGN = 0x0;
    private const uint TPM_RIGHTBUTTON = 0x2;
    private const uint TPM_RIGHTALIGN = 0x8;
    private const uint TPM_BOTTOMALIGN = 0x20;
    private const uint TPM_RETURNCMD = 0x100;
    private const uint TPM_LAYOUTRTL = 0x8000;
    private const int SM_CXSMICON = 49;
    private const int SM_MENUDROPALIGNMENT = 40;
    private static readonly IntPtr IDI_APPLICATION = new(32512);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "Shell_NotifyIconW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
    private static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "AppendMenuW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr idOrSubmenu, string? text);

    private static bool AppendMenu(IntPtr menu, uint flags, int id, string text) => AppendMenu(menu, flags, new IntPtr(id), text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMenuDefaultItem(IntPtr menu, uint item, uint byPosition);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y, IntPtr hWnd, IntPtr parameters);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessage(IntPtr hWnd, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr CreateIconFromResourceEx(ref byte data, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon,
        uint version, int width, int height, uint flags);

    [DllImport("user32.dll", EntryPoint = "LoadIconW")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr name);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);
}
