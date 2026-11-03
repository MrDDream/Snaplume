using Avalonia.Styling;

namespace Snaplume.Ui;

/// <summary>
/// Light or dark interface (<see cref="Core.AppSettings.Theme"/>). "auto" lets Avalonia follow the system mode,
/// even when it changes while Snaplume runs; the colors of Themes/Colors.axaml switch live (DynamicResource), and
/// the title bars follow on their own.
/// </summary>
public static class ThemeManager
{
    public const string Auto = "auto";
    public const string Light = "light";
    public const string Dark = "dark";

    /// <summary>Applies a mode of the settings; anything unknown is "auto".</summary>
    public static void Apply(string? mode)
    {
        if (Avalonia.Application.Current is not { } app) return;
        app.RequestedThemeVariant = mode switch
        {
            Light => ThemeVariant.Light,
            Dark => ThemeVariant.Dark,
            _ => ThemeVariant.Default,
        };
    }
}
