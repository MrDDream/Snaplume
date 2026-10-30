namespace Snaplume.Platform.Linux.Util;

/// <summary>XDG base directories (freedesktop basedir spec), with the defaults the spec gives.</summary>
internal static class Xdg
{
    public static string Home => Environment.GetEnvironmentVariable("HOME") is { Length: > 0 } home && home.StartsWith('/')
        ? home
        : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    public static string ConfigHome => Absolute("XDG_CONFIG_HOME") ?? Path.Combine(Home, ".config");

    public static string DataHome => Absolute("XDG_DATA_HOME") ?? Path.Combine(Home, ".local", "share");

    /// <summary>$XDG_RUNTIME_DIR (private, 0700, cleared at logout), or null if unset.</summary>
    public static string? RuntimeDir => Absolute("XDG_RUNTIME_DIR");

    /// <summary>Variables must be absolute paths; relative values are invalid and ignored (spec).</summary>
    private static string? Absolute(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value && value.StartsWith('/') ? value : null;

    /// <summary>Desktop file id of Snaplume, also its icon name and AppStream id.</summary>
    public const string AppId = "io.github.mrddream.Snaplume";
}
