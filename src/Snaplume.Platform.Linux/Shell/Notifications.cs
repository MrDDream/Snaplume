using System.Collections.Concurrent;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Util;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// Desktop notifications (org.freedesktop.Notifications). A click on the notification invokes its "default"
/// action, reported by the ActionInvoked signal.
/// </summary>
internal sealed class Notifications : IDisposable
{
    public enum Urgency : byte { Low = 0, Normal = 1, Critical = 2 }

    private const string Service = "org.freedesktop.Notifications";
    private const string ObjectPath = "/org/freedesktop/Notifications";

    private readonly ConcurrentDictionary<uint, Action> _clicks = new();
    private IDisposable? _actionWatch;
    private IDisposable? _closedWatch;

    public async Task ShowAsync(string title, string text, Urgency urgency, Action? onClick)
    {
        try
        {
            var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
            await EnsureWatchesAsync(connection).ConfigureAwait(false);
            var hints = new Dictionary<string, VariantValue>
            {
                ["urgency"] = VariantValue.Byte((byte)urgency),
                ["desktop-entry"] = VariantValue.String(Xdg.AppId),
            };
            var actions = onClick != null ? new[] { "default", Loc.T("Linux.NotificationOpen") } : Array.Empty<string>();
            var id = await DBusCalls.CallAsync(connection, (m, _) => m.GetBodyReader().ReadUInt32(), Service, ObjectPath, Service, "Notify",
                new DString("Snaplume"), new DUInt(0), new DString(Xdg.AppId), new DString(title), new DString(EscapeBody(text)),
                new DStrings(actions), new DOptions(hints), new DInt(urgency == Urgency.Critical ? 0 : -1)).ConfigureAwait(false);
            if (onClick != null) _clicks[id] = onClick;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Notification « {title} »", ex);
        }
    }

    /// <summary>Servers that support body markup interpret &amp;, &lt; and &gt;: file names must not become tags.</summary>
    internal static string EscapeBody(string text) =>
        text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private async Task EnsureWatchesAsync(DBusConnection connection)
    {
        if (_actionWatch != null) return;
        _actionWatch = await DBusCalls.WatchAsync(connection, ObjectPath, Service, "ActionInvoked",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                return (Id: reader.ReadUInt32(), Action: reader.ReadString());
            },
            invoked =>
            {
                if (invoked.Action != "default") return;
                if (_clicks.TryRemove(invoked.Id, out var action)) Dispatcher.UIThread.Post(() => Run(action));
            }).ConfigureAwait(false);
        _closedWatch = await DBusCalls.WatchAsync(connection, ObjectPath, Service, "NotificationClosed",
            (message, _) => message.GetBodyReader().ReadUInt32(),
            // Some servers send ActionInvoked after NotificationClosed: the action is forgotten a little later.
            id => _ = Task.Delay(2000).ContinueWith(_ => _clicks.TryRemove(id, out Action? _), TaskScheduler.Default)).ConfigureAwait(false);
    }

    private static void Run(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Clic sur une notification", ex);
        }
    }

    public void Dispose()
    {
        _actionWatch?.Dispose();
        _closedWatch?.Dispose();
    }
}
