using Snaplume.Platform;

namespace Snaplume.Core;

/// <summary>
/// Application-wide services, set once at startup by the application (and by the tests): the settings and the
/// services of the operating system. Code shared by every platform reaches them here instead of the App class.
/// </summary>
public static class AppServices
{
    private static IPlatform? _platform;

    public static AppSettings Settings { get; set; } = new();

    public static IPlatform Platform
    {
        get => _platform ?? throw new InvalidOperationException("The platform services are not set");
        set => _platform = value;
    }

    public static bool HasPlatform => _platform != null;
}
