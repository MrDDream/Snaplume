using System.Windows;
using System.Windows.Interop;
using Microsoft.Win32;
using static Snaplume.Native.NativeMethods;

namespace Snaplume.Core;

/// <summary>
/// Light or dark interface. "auto" follows the Windows app mode, even when it changes while running.
/// Colors are swapped live: windows use them through DynamicResource.
/// </summary>
public static class ThemeManager
{
    public const string Auto = "auto";
    public const string Light = "light";
    public const string Dark = "dark";

    private static string _mode = Auto;

    public static bool IsDark { get; private set; }

    public static void Initialize(string? mode)
    {
        _mode = Normalize(mode);
        Apply();
        SystemEvents.UserPreferenceChanged += (_, e) =>
        {
            if (_mode == Auto && e.Category == UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(Apply);
        };
        // Title bars are drawn by Windows: tell it each window's mode.
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ApplyTitleBar((Window)sender)));
    }

    public static void SetMode(string? mode)
    {
        _mode = Normalize(mode);
        Apply();
    }

    private static string Normalize(string? mode) => mode is Light or Dark ? mode : Auto;

    /// <summary>Windows setting "Choose your default app mode".</summary>
    private static bool SystemUsesDarkMode()
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
    }

    private static void Apply()
    {
        bool dark = _mode == Dark || (_mode == Auto && SystemUsesDarkMode());
        var dictionaries = Application.Current.Resources.MergedDictionaries;
        // The color theme is the first merged dictionary (see App.xaml): nothing to do if it is already the right one.
        var file = dark ? "Dark.xaml" : "Light.xaml";
        if (dictionaries.Count > 0 && dictionaries[0].Source?.OriginalString.EndsWith(file, StringComparison.Ordinal) == true && IsDark == dark)
            return;

        var theme = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{file}") };
        if (dictionaries.Count > 0) dictionaries[0] = theme;
        else dictionaries.Add(theme);

        IsDark = dark;
        foreach (Window window in Application.Current.Windows) ApplyTitleBar(window);
    }

    private static void ApplyTitleBar(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        int dark = IsDark ? 1 : 0;
        if (DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int)) != 0)
            _ = DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE_OLD, ref dark, sizeof(int));
    }
}
