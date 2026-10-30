namespace Snaplume.Platform.Linux;

/// <summary>Kind of graphical session Snaplume runs in.</summary>
internal enum SessionKind
{
    X11,
    Wayland,
}

/// <summary>Session detection and the features each session allows. Pure, unit-tested.</summary>
internal static class SessionInfo
{
    /// <summary>
    /// XDG_SESSION_TYPE first (set by the login manager), then the display variables. SNAPLUME_SESSION=x11|wayland
    /// overrides the detection, for testing one backend inside the other session.
    /// </summary>
    public static SessionKind Detect(Func<string, string?> environment)
    {
        static bool Is(string? value, string expected) => string.Equals(value?.Trim(), expected, StringComparison.OrdinalIgnoreCase);
        var forced = environment("SNAPLUME_SESSION");
        if (Is(forced, "x11")) return SessionKind.X11;
        if (Is(forced, "wayland")) return SessionKind.Wayland;
        var type = environment("XDG_SESSION_TYPE");
        if (Is(type, "wayland")) return SessionKind.Wayland;
        if (Is(type, "x11")) return SessionKind.X11;
        if (!string.IsNullOrEmpty(environment("WAYLAND_DISPLAY"))) return SessionKind.Wayland;
        return SessionKind.X11;
    }

    /// <param name="globalShortcutsPortal">The GlobalShortcuts portal answers (KDE Plasma, GNOME 48+).</param>
    /// <param name="remoteDesktopPortal">The RemoteDesktop portal answers (needed to turn the wheel under Wayland).</param>
    public static PlatformFeatures Features(SessionKind session, bool globalShortcutsPortal, bool remoteDesktopPortal)
    {
        if (session == SessionKind.X11)
            return PlatformFeatures.WindowDetection | PlatformFeatures.ScrollingCapture | PlatformFeatures.GlobalHotkeys
                   | PlatformFeatures.ClickHighlight | PlatformFeatures.RecordAnyArea | PlatformFeatures.StartWithSession
                   | PlatformFeatures.ActiveWindow;
        var features = PlatformFeatures.StartWithSession;
        if (globalShortcutsPortal) features |= PlatformFeatures.GlobalHotkeys;
        if (remoteDesktopPortal) features |= PlatformFeatures.ScrollingCapture;
        return features;
    }
}
