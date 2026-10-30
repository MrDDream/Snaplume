using Avalonia;
using Avalonia.Controls;
using Snaplume.Core;

namespace Snaplume;

/// <summary>
/// Entry point. The single instance, the settings and the platform services are set up by <see cref="App"/> once
/// Avalonia runs (see App.Start); here only the errors that would escape it are logged.
/// </summary>
internal static class Program
{
    // STA on Windows: the clipboard and drag-and-drop go through OLE.
    [STAThread]
    public static int Main(string[] args)
    {
        AppDomain.CurrentDomain.UnhandledException += OnFatalException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;
        try
        {
            // The application lives in the notification area: it quits through "Quit", never when a window closes.
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, ShutdownMode.OnExplicitShutdown);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Erreur fatale", ex);
            return 1;
        }
    }

    /// <summary>Also used by the visual designer.</summary>
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().LogToTrace();

    /// <summary>Exception on another thread: the process ends, at least keep a trace.</summary>
    private static void OnFatalException(object sender, UnhandledExceptionEventArgs e) =>
        ErrorLog.Write("Erreur fatale", e.ExceptionObject as Exception ?? new Exception(e.ExceptionObject?.ToString()));

    private static void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        ErrorLog.Write("Tâche en échec non observée", e.Exception);
        e.SetObserved();
    }
}
