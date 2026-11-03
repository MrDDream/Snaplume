using Snaplume.Platform.Windows.Capturing;
using Snaplume.Platform.Windows.Clipboard;
using Snaplume.Platform.Windows.Hotkeys;
using Snaplume.Platform.Windows.Ocr;
using Snaplume.Platform.Windows.Recording;
using Snaplume.Platform.Windows.Shell;
using Snaplume.Platform.Windows.Tray;
using Snaplume.Platform.Windows.Video;

namespace Snaplume.Platform.Windows;

/// <summary>Windows services: Win32, WinRT (OCR, video), ScreenRecorderLib, MAPI, WPF printing and clipboard, WinForms tray icon.</summary>
public static class WindowsPlatform
{
    /// <summary>
    /// Creates the services. Call it on the Avalonia UI thread (STA, with the Win32 message loop that serves the
    /// hidden windows of the hotkeys and the tray icon).
    /// </summary>
    public static IPlatform Create() => new Services();

    private sealed class Services : IPlatform
    {
        private readonly Lazy<IGlobalHotkeys> _hotkeys = new(() => new GlobalHotkeys());

        public string Id => "windows";

        /// <summary>Everything is available on Windows 10 2004 and later.</summary>
        public PlatformFeatures Features =>
            PlatformFeatures.WindowDetection | PlatformFeatures.ScrollingCapture | PlatformFeatures.GlobalHotkeys
            | PlatformFeatures.ClickHighlight | PlatformFeatures.ExcludeFromCapture | PlatformFeatures.RecordAnyArea
            | PlatformFeatures.StartWithSession | PlatformFeatures.ActiveWindow | PlatformFeatures.ReplaceSystemCapture
            | PlatformFeatures.ClickZoom;

        public IScreenCapture Screen { get; } = new ScreenCapture();
        public IWindowLocator Windows { get; } = new WindowLocator();
        public IPointerControl Pointer { get; } = new PointerControl();
        /// <summary>Its message window is created on first use, on the thread asking (the UI thread).</summary>
        public IGlobalHotkeys Hotkeys => _hotkeys.Value;
        public IScreenRecorderFactory Recorder { get; } = new ScreenRecorderFactory();
        public IVideoTools Video { get; } = new VideoTools();
        public IOcrEngine Ocr { get; } = new WindowsOcr();
        public IDesktopShell Shell { get; } = new DesktopShell();
        public IImageClipboard Clipboard { get; } = new ImageClipboard();
        public INativeWindows NativeWindows { get; } = new NativeWindows();
        public ISingleInstance SingleInstance { get; } = new SingleInstance();
        public IUpdateInstaller Updates { get; } = new UpdateInstaller();

        public ITray CreateTray(string tooltip) => new TrayIcon(tooltip);
    }
}
