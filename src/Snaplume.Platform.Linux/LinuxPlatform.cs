using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Media;
using Snaplume.Platform.Linux.Ocr;
using Snaplume.Platform.Linux.Shell;
using Snaplume.Platform.Linux.Wayland;
using Snaplume.Platform.Linux.X11;

namespace Snaplume.Platform.Linux;

/// <summary>
/// Linux services. Avalonia runs on X11 in both sessions (XWayland under Wayland), so the X11 connection also
/// serves Wayland for what XWayland can do (window tweaks, monitor layout); what only the compositor can do
/// (screenshots, wheel, global shortcuts, screencasts) goes through xdg-desktop-portal.
/// </summary>
public static class LinuxPlatform
{
    /// <summary>Builds the services; called once on the Avalonia UI thread at startup.</summary>
    public static IPlatform Create()
    {
        var session = SessionInfo.Detect(Environment.GetEnvironmentVariable);
        var x11 = X11Connection.TryOpen();
        if (session == SessionKind.Wayland) return CreateWayland(x11);
        if (x11 == null) ErrorLog.Write("Plateforme Linux", "Aucune connexion X11 (DISPLAY) : capture et raccourcis indisponibles");
        return CreateX11(x11);
    }

    private static IPlatform CreateX11(X11Connection? x11)
    {
        IScreenCapture screen = x11 != null ? new X11ScreenCapture(x11) : new UnavailableScreenCapture();
        IWindowLocator windows = x11 != null ? new X11WindowLocator(x11) : new NoWindowLocator();
        IPointerControl pointer = x11 != null ? new X11Pointer(x11) : new NoPointerControl();
        var features = SessionInfo.Features(SessionKind.X11, false, false);
        return new LinuxServices("x11", features, screen, windows, pointer, new X11Hotkeys(), new GstScreenRecorderFactory(wayland: false),
            new LinuxClipboard(wayland: false), new X11NativeWindows(x11));
    }

    private static IPlatform CreateWayland(X11Connection? xwayland)
    {
        // Probed once, with a time limit: a broken session bus must not hold the startup.
        var probe = Task.Run(async () => (
            Shortcuts: await Portal.VersionAsync(Portal.GlobalShortcutsInterface).ConfigureAwait(false),
            Remote: await Portal.VersionAsync(Portal.RemoteDesktopInterface).ConfigureAwait(false)));
        bool shortcuts = false, remote = false;
        if (probe.Wait(TimeSpan.FromSeconds(3)))
        {
            shortcuts = probe.Result.Shortcuts != null;
            remote = probe.Result.Remote != null;
        }
        else
        {
            ErrorLog.Write("Plateforme Wayland", "Le portail xdg-desktop-portal ne répond pas");
        }
        var screen = new WaylandScreenCapture(xwayland);
        var pointer = new WaylandPointer(screen, xwayland != null ? new X11Pointer(xwayland) : null);
        IGlobalHotkeys hotkeys = shortcuts ? new PortalHotkeys() : new NoGlobalHotkeys();
        return new LinuxServices("wayland", SessionInfo.Features(SessionKind.Wayland, shortcuts, remote), screen, new NoWindowLocator(),
            pointer, hotkeys, new GstScreenRecorderFactory(wayland: true), new LinuxClipboard(wayland: true), new X11NativeWindows(xwayland));
    }

    private sealed class LinuxServices : IPlatform
    {
        public LinuxServices(string id, PlatformFeatures features, IScreenCapture screen, IWindowLocator windows, IPointerControl pointer,
            IGlobalHotkeys hotkeys, IScreenRecorderFactory recorder, IImageClipboard clipboard, INativeWindows nativeWindows)
        {
            Id = id;
            Features = features;
            Screen = screen;
            Windows = windows;
            Pointer = pointer;
            Hotkeys = hotkeys;
            Recorder = recorder;
            Clipboard = clipboard;
            NativeWindows = nativeWindows;
        }

        public string Id { get; }
        public PlatformFeatures Features { get; }
        public IScreenCapture Screen { get; }
        public IWindowLocator Windows { get; }
        public IPointerControl Pointer { get; }
        public IGlobalHotkeys Hotkeys { get; }
        public IScreenRecorderFactory Recorder { get; }
        public IVideoTools Video { get; } = new FfmpegVideoTools();
        public IOcrEngine Ocr { get; } = new TesseractOcr();
        public IDesktopShell Shell { get; } = new LinuxDesktopShell();
        public IImageClipboard Clipboard { get; }
        public INativeWindows NativeWindows { get; }
        public ISingleInstance SingleInstance { get; } = new UnixSingleInstance();
        public IUpdateInstaller Updates { get; } = new LinuxUpdateInstaller();

        public ITray CreateTray(string tooltip) => new LinuxTray(tooltip);
    }

    /// <summary>Wayland: no window list for clients; selection by dragging only.</summary>
    private sealed class NoWindowLocator : IWindowLocator
    {
        public IWindowSnapshot Snapshot(PixelRect virtualBounds, IReadOnlyList<PixelRect> monitors) => EmptyWindowSnapshot.Instance;
        public PixelRect? ForegroundWindowBounds() => null;
    }

    private sealed class NoGlobalHotkeys : IGlobalHotkeys
    {
        public bool IsSupported => false;
        public bool Register(string id, string description, Hotkey hotkey, Action action) => false;
        public void UnregisterAll()
        {
        }
        public void Dispose()
        {
        }
    }

    private sealed class NoPointerControl : IPointerControl
    {
        public Task<bool> PrepareAsync(CancellationToken cancel = default) => Task.FromResult(false);
        public bool TryGetPosition(out int x, out int y)
        {
            x = y = 0;
            return false;
        }
        public bool SetPosition(int x, int y) => false;
        public bool Scroll(int notches) => false;
        public bool EscapePressed() => false;
        public void ActivateWindowAt(int x, int y)
        {
        }
        public void Release()
        {
        }
    }

    /// <summary>No X server reachable (DISPLAY unset under X11): every capture explains it.</summary>
    private sealed class UnavailableScreenCapture : IScreenCapture
    {
        public IReadOnlyList<MonitorInfo> Monitors => new[] { new MonitorInfo(new PixelRect(0, 0, 1920, 1080), 1, true, "Screen") };
        public Task<DesktopImage> CaptureDesktopAsync(bool includeCursor, CancellationToken cancel = default) =>
            Task.FromException<DesktopImage>(new InvalidOperationException(Loc.T("Linux.NoDisplay")));
        public IScreenGrabber CreateGrabber(PixelRect area) => throw new InvalidOperationException(Loc.T("Linux.NoDisplay"));
    }
}
