using System.Security.Cryptography;
using Snaplume.Core;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.DBus;

/// <summary>What a portal request answered: 0 = success, 1 = cancelled by the user, 2 = failed.</summary>
internal sealed record PortalResponse(uint Code, Dictionary<string, VariantValue> Results);

/// <summary>
/// xdg-desktop-portal (org.freedesktop.portal.Desktop). Most methods return at once with the path of a Request
/// object and answer later with its Response signal. The path is predictable from our unique bus name and the
/// handle_token we pass, so the signal is subscribed to before the call: a fast portal cannot answer before
/// Snaplume listens.
/// </summary>
internal static class Portal
{
    public const string Service = "org.freedesktop.portal.Desktop";
    public const string ObjectPath = "/org/freedesktop/portal/desktop";
    public const string ScreenshotInterface = "org.freedesktop.portal.Screenshot";
    public const string ScreenCastInterface = "org.freedesktop.portal.ScreenCast";
    public const string RemoteDesktopInterface = "org.freedesktop.portal.RemoteDesktop";
    public const string GlobalShortcutsInterface = "org.freedesktop.portal.GlobalShortcuts";

    /// <summary>A token valid as an object path element: letters, digits and underscores.</summary>
    public static string NewToken() => "snaplume_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    /// <summary>":1.42" becomes "1_42", as the portal builds request and session paths.</summary>
    public static string SenderPart(string uniqueName) => uniqueName.TrimStart(':').Replace('.', '_');

    public static string RequestPath(string uniqueName, string token) => $"{ObjectPath}/request/{SenderPart(uniqueName)}/{token}";

    public static string SessionPath(string uniqueName, string token) => $"{ObjectPath}/session/{SenderPart(uniqueName)}/{token}";

    /// <summary>Options with the handle_token of the request.</summary>
    public static Dictionary<string, VariantValue> Options(string token, params (string Key, VariantValue Value)[] entries)
    {
        var options = new Dictionary<string, VariantValue> { ["handle_token"] = VariantValue.String(token) };
        foreach (var (key, value) in entries) options[key] = value;
        return options;
    }

    /// <summary>Version of a portal interface; null when this desktop does not offer it.</summary>
    public static async Task<uint?> VersionAsync(string @interface)
    {
        try
        {
            var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
            return DBusCalls.AsUInt(await DBusCalls.GetPropertyAsync(connection, Service, ObjectPath, @interface, "version").ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Calls a portal method that answers through a Request, and waits for the answer.
    /// </summary>
    /// <param name="args">Builds the arguments from the handle token (it belongs in the options).</param>
    /// <exception cref="OperationCanceledException">The user cancelled, or <paramref name="cancel"/> fired.</exception>
    /// <exception cref="InvalidOperationException">The portal reported a failure.</exception>
    public static async Task<Dictionary<string, VariantValue>> RequestAsync(string @interface, string member,
        Func<string, DArg[]> args, CancellationToken cancel = default)
    {
        var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
        var token = NewToken();
        var expected = RequestPath(connection.UniqueName ?? throw new InvalidOperationException("Not connected"), token);
        var answer = new TaskCompletionSource<PortalResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var watch = await WatchResponseAsync(connection, expected, answer).ConfigureAwait(false);
        var handle = await DBusCalls.CallAsync(connection, (m, _) => m.GetBodyReader().ReadObjectPathAsString(),
            Service, ObjectPath, @interface, member, args(token)).ConfigureAwait(false);
        // Portals older than 0.9 ignore handle_token and choose the path themselves.
        using var other = handle != expected ? await WatchResponseAsync(connection, handle, answer).ConfigureAwait(false) : null;
        PortalResponse response;
        using (cancel.Register(() =>
               {
                   answer.TrySetCanceled(cancel);
                   _ = CloseAsync(handle, "org.freedesktop.portal.Request");
               }))
        {
            response = await answer.Task.ConfigureAwait(false);
        }
        return response.Code switch
        {
            0 => response.Results,
            1 => throw new OperationCanceledException($"{@interface}.{member}: cancelled by the user"),
            _ => throw new InvalidOperationException($"{@interface}.{member}: the portal failed (response {response.Code})"),
        };
    }

    private static async Task<IDisposable> WatchResponseAsync(DBusConnection connection, string path, TaskCompletionSource<PortalResponse> answer) =>
        await connection.WatchSignalAsync(null, path, "org.freedesktop.portal.Request", "Response",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                uint code = reader.ReadUInt32();
                return new PortalResponse(code, reader.ReadDictionaryOfStringToVariantValue());
            },
            (exception, response) =>
            {
                if (exception != null) answer.TrySetException(exception);
                else answer.TrySetResult(response);
            },
            null, false, ObserverFlags.None).ConfigureAwait(false);

    /// <summary>Closes a Request or a Session object (both have a Close method without arguments).</summary>
    public static async Task CloseAsync(string path, string @interface = "org.freedesktop.portal.Session")
    {
        try
        {
            var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
            await DBusCalls.CallAsync(connection, Service, path, @interface, "Close").ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is DBusExceptionBase or InvalidOperationException)
        {
            // Already closed by the portal or the user.
        }
    }

    /// <summary>Creates a session of a portal (ScreenCast, RemoteDesktop, GlobalShortcuts) and returns its handle.</summary>
    public static async Task<string> CreateSessionAsync(string @interface, CancellationToken cancel)
    {
        var results = await RequestAsync(@interface, "CreateSession",
            token => new DArg[] { new DOptions(Options(token, ("session_handle_token", VariantValue.String(NewToken())))) },
            cancel).ConfigureAwait(false);
        return results.GetString("session_handle") ?? throw new InvalidOperationException($"{@interface}.CreateSession: no session handle");
    }

    /// <summary>Restore tokens let the portal reuse the user's last choice without asking again.</summary>
    public static string? LoadToken(string name)
    {
        try
        {
            var path = TokenPath(name);
            return File.Exists(path) ? File.ReadAllText(path).Trim() is { Length: > 0 } token ? token : null : null;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecture du jeton du portail", ex);
            return null;
        }
    }

    public static void SaveToken(string name, string? token)
    {
        try
        {
            var path = TokenPath(name);
            if (string.IsNullOrEmpty(token))
            {
                File.Delete(path);
                return;
            }
            Directory.CreateDirectory(AppSettings.SettingsFolder);
            File.WriteAllText(path, token);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement du jeton du portail", ex);
        }
    }

    private static string TokenPath(string name) => Path.Combine(AppSettings.SettingsFolder, $"portal-{name}.token");
}
