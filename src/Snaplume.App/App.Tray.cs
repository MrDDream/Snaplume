using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Platform;
using Snaplume.Ui;
using Snaplume.Workspace;

namespace Snaplume;

/// <summary>Notification-area icon: its menu (rebuilt each time it opens), tooltip and click.</summary>
public partial class App
{
    private const string AppName = "Snaplume";
    /// <summary>Longest tooltip Windows accepts (others are shortened the same way).</summary>
    private const int MaxTooltipLength = 127;
    /// <summary>Status shown instead of the workspace name (e.g. the timer countdown), or null.</summary>
    private string? _trayStatus;

    /// <summary>
    /// The action, run on the UI thread: the platform may call tray menu items and notification clicks from its own thread.
    /// </summary>
    public static Action OnUi(Action action) => () => Dispatcher.UIThread.Post(action);

    private void CreateTray()
    {
        try
        {
            Tray = Platform.CreateTray(AppName);
        }
        catch (Exception ex)
        {
            // No notification area (some desktops): the windows and shortcuts still work.
            ErrorLog.Write("Icône de notification", ex);
            return;
        }
        Tray.MenuBuilder = BuildTrayMenu;
        // The platform may raise it on another thread.
        Tray.Activated += () => Dispatcher.UIThread.Post(ToggleQuickPanel);
        Workspaces.Changed += UpdateTrayTooltip;
        UpdateTrayTooltip();
    }

    /// <summary>
    /// Tooltip of the tray icon, e.g. the timer countdown ("Capture in 3 s"); null restores the default text
    /// (the application and its active workspace).
    /// </summary>
    public void SetTrayStatus(string? text)
    {
        _trayStatus = text;
        UpdateTrayTooltip();
    }

    private void UpdateTrayTooltip()
    {
        if (Tray == null) return;
        var text = _trayStatus;
        if (string.IsNullOrEmpty(text))
        {
            var workspace = Workspaces.ActiveName;
            text = workspace.Length == 0 ? AppName : Loc.T("Tray.TooltipWorkspace", Workspaces.DisplayName(workspace));
        }
        if (text.Length > MaxTooltipLength) text = text[..(MaxTooltipLength - 1)] + "…";
        try
        {
            Tray.SetTooltip(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Icône de notification", ex);
        }
    }

    /// <summary>
    /// The tray menu, built when it opens: the recording state and the workspaces are read then. Click actions are
    /// posted to the UI thread (some systems call them from their own thread).
    /// </summary>
    private IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        void Capture(CaptureKind mode, int delay = 0) => _ = Captures.CaptureAsync(mode, delay, settleMs: 200);
        bool recording = Videos.IsBusy;

        var items = new List<TrayMenuItem>
        {
            new(Loc.T("Tray.MyCaptures"), OnUi(ShowQuickPanel), Bold: true),
            new(Loc.T("Tray.Workspace"), OnUi(ShowWorkspace)),
            new(Loc.T("Tray.Workspaces"), Children: BuildWorkspacesMenu()),
            TrayMenuItem.Separator,
            new(Loc.T(Platform.Has(PlatformFeatures.WindowDetection) ? "Tray.CaptureRegion" : "Tray.CaptureArea"),
                OnUi(() => Capture(CaptureKind.Region))),
            new(Loc.T("Tray.CaptureScreen"), OnUi(() => Capture(CaptureKind.Screen))),
            new(Loc.T("Tray.CaptureAllScreens"), OnUi(() => Capture(CaptureKind.AllScreens))),
            new(Loc.T("Tray.RepeatLast"), OnUi(() => Capture(CaptureKind.RepeatLast))),
        };
        if (Platform.Has(PlatformFeatures.ScrollingCapture))
            items.Add(new(Loc.T("Tray.CaptureScrolling"), OnUi(() => Capture(CaptureKind.Scrolling))));
        items.Add(new(Loc.T("Tray.CaptureDelayed"), Children: new[] { 3, 5, 10 }
            .Select(seconds => new TrayMenuItem(Loc.T("Common.Seconds", seconds), OnUi(() => Capture(CaptureKind.Region, seconds))))
            .ToList()));
        items.Add(new(Loc.T("Ocr.CaptureText"), OnUi(() => _ = Captures.CaptureTextAsync(settleMs: 200))));
        items.Add(new(Loc.T("Tray.ReadQr"), OnUi(() => _ = Captures.CaptureQrAsync(settleMs: 200))));

        items.Add(TrayMenuItem.Separator);
        // During a recording the first entry stops it.
        items.Add(new(Loc.T(recording ? "Tray.StopVideo" : "Tray.RecordVideo"), OnUi(() => _ = Videos.ToggleAsync(settleMs: 200))));
        if (!recording)
            items.Add(new(Loc.T("Tray.RecordScreenVideo"), OnUi(() => _ = Videos.ToggleAsync(fullScreen: true, settleMs: 200))));

        items.Add(TrayMenuItem.Separator);
        items.Add(new(Loc.T("Tray.OpenFolder"), OnUi(OpenActiveFolder)));
        items.Add(new(Loc.T("Tray.Settings"), OnUi(ShowSettings)));
        items.Add(TrayMenuItem.Separator);
        items.Add(new(Loc.T("Tray.Quit"), OnUi(ExitApp)));
        return items;
    }

    /// <summary>The workspaces (the active one checked), then "New...".</summary>
    private List<TrayMenuItem> BuildWorkspacesMenu()
    {
        var active = Workspaces.ActiveName;
        var items = Workspaces.List()
            .Select(name => new TrayMenuItem(Workspaces.DisplayName(name), () => Dispatcher.UIThread.Post(() => SelectWorkspaceFromTray(name)),
                Enabled: Workspaces.IsAvailable(name), Checked: string.Equals(name, active, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        items.Add(TrayMenuItem.Separator);
        items.Add(new(Loc.T("Tray.NewWorkspace"), () => Dispatcher.UIThread.Post(() => _ = CreateWorkspaceFromTrayAsync())));
        return items;
    }

    private static void SelectWorkspaceFromTray(string name)
    {
        try
        {
            Workspaces.Select(name);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Choix d'un espace de travail", ex);
        }
    }

    /// <summary>Asks for a name and creates the workspace (it becomes the active one).</summary>
    private static async Task CreateWorkspaceFromTrayAsync()
    {
        var name = await InputDialog.AskAsync(null, Loc.T("Tray.NewWorkspaceTitle"), Loc.T("Tray.NewWorkspaceLabel"), "", isFileName: false);
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            Workspaces.Create(name);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Création d'un espace de travail", ex);
            await Dialogs.ShowAsync(null, Loc.T("Tray.NewWorkspaceFailed", ex.Message), AppName, DialogButtons.Ok, DialogIcon.Warning);
        }
    }

    private static void OpenActiveFolder()
    {
        try
        {
            FileOps.OpenFolder(Workspaces.ActiveFolder);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Ouverture du dossier des captures", ex);
        }
    }
}
