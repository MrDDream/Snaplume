using System.Collections.Concurrent;
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
    private readonly SemaphoreSlim _watchGate = new(1, 1);
    private IDisposable? _actionWatch;
    private IDisposable? _closedWatch;
    private volatile bool _disposed;

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

    /// <summary>Subscribes once, however many notifications are shown at the same time.</summary>
    private async Task EnsureWatchesAsync(DBusConnection connection)
    {
        if (_closedWatch != null) return;
        await _watchGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_closedWatch != null || _disposed) return;
            _actionWatch = await DBusCalls.WatchAsync(connection, Service, ObjectPath, Service, "ActionInvoked",
                (message, _) =>
                {
                    var reader = message.GetBodyReader();
                    return (Id: reader.ReadUInt32(), Action: reader.ReadString());
                },
                invoked =>
                {
                    if (invoked.Action != "default") return;
                    if (_clicks.TryRemove(invoked.Id, out var action)) UiActions.Post(action, "Clic sur une notification");
                }).ConfigureAwait(false);
            _closedWatch = await DBusCalls.WatchAsync(connection, Service, ObjectPath, Service, "NotificationClosed",
                (message, _) => message.GetBodyReader().ReadUInt32(),
                // Some servers send ActionInvoked after NotificationClosed: the action is forgotten a little later.
                id => _ = Task.Delay(2000).ContinueWith(_ => _clicks.TryRemove(id, out Action? _), TaskScheduler.Default)).ConfigureAwait(false);
            if (_disposed) DisposeWatches();
        }
        finally
        {
            _watchGate.Release();
        }
    }

    private void DisposeWatches()
    {
        _actionWatch?.Dispose();
        _closedWatch?.Dispose();
    }

    public void Dispose()
    {
        // Watches still being created are disposed by EnsureWatchesAsync.
        _disposed = true;
        DisposeWatches();
    }
}
