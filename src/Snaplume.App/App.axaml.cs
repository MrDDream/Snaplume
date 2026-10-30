using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;
using Snaplume.Recording;

namespace Snaplume;

/// <summary>
/// The application: startup, single instance, command line, tray icon, hotkeys and the windows it opens.
/// Placeholder: the members below are the ones the rest of the interface calls.
/// </summary>
public partial class App : Application
{
    public static App Instance => (App)Current!;

    public AppSettings Settings => AppServices.Settings;
    public IPlatform Platform => AppServices.Platform;
    public CaptureService Captures { get; private set; } = null!;
    public VideoService Videos { get; private set; } = null!;
    public ITray? Tray { get; private set; }
    public bool IsExiting { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>Registers the global shortcuts of the settings; tells which ones are taken if asked.</summary>
    public void RegisterHotkeys(bool showWarnings)
    {
    }

    public void ShowQuickPanel()
    {
    }

    public void ToggleQuickPanel()
    {
    }

    public void ShowWorkspace()
    {
    }

    /// <summary>Opens an image in the editor (an animated GIF opens in the default application).</summary>
    public void OpenEditor(string path)
    {
    }

    /// <summary>Opens a video in the built-in player.</summary>
    public void OpenPlayer(string path)
    {
    }

    /// <summary>Recognizes the text of an image file and shows it.</summary>
    public void ShowOcr(string path)
    {
    }

    /// <summary>Recognizes the text of an image (a capture area, the image being edited) and shows it.</summary>
    public Window? ShowOcr(PixelBuffer image) => null;

    /// <summary>Reads the QR codes and barcodes of an image file and shows them.</summary>
    public void ShowQr(string path)
    {
    }

    /// <summary>Reads the QR codes and barcodes of an image and shows them.</summary>
    public Window? ShowQr(PixelBuffer image) => null;

    public void ShowSettings()
    {
    }

    public void ExitApp()
    {
    }

    public void Restart()
    {
    }
}
