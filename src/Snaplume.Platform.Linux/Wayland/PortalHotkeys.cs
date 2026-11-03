using Snaplume.Core;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Util;
using Snaplume.Platform.Linux.X11;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Wayland;

/// <summary>
/// Global shortcuts under Wayland through the GlobalShortcuts portal (KDE Plasma, GNOME 48+). Snaplume proposes
/// a trigger per shortcut; the desktop shows them to the user, who confirms or changes them, and reports each
/// press with the Activated signal. Shortcuts are bound together once registration calls stop arriving,
/// because a portal session binds its list only once: a change means a new session.
/// </summary>
internal sealed class PortalHotkeys : IGlobalHotkeys
{
    private readonly object _gate = new();
    private readonly Dictionary<string, (string Description, string? Trigger, Action Action)> _shortcuts = new();
    private readonly Timer _debounce;
    private readonly SemaphoreSlim _bindGate = new(1, 1);
    private string? _session;
    private IDisposable? _activatedWatch;
    private int _generation;
    private volatile bool _disposed;

    public PortalHotkeys() => _debounce = new Timer(_ => _ = BindAsync(), null, Timeout.Infinite, Timeout.Infinite);

    public bool IsSupported => true;

    /// <returns>True: the desktop decides later, and may show its own conflict dialog.</returns>
    public bool Register(string id, string description, Hotkey hotkey, Action action)
    {
        lock (_gate)
        {
            _shortcuts[id] = (description, KeySyms.PortalTrigger(hotkey), action);
            _generation++;
        }
        _debounce.Change(500, Timeout.Infinite);
        return true;
    }

    public void UnregisterAll()
    {
        string? session;
        lock (_gate)
        {
            _shortcuts.Clear();
            _generation++;
            session = _session;
            _session = null;
        }
        if (session != null) _ = Portal.CloseAsync(session);
    }

    /// <summary>One binding at a time: a debounce firing while the desktop's dialog is open waits for it to close.</summary>
    private async Task BindAsync()
    {
        await _bindGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await BindNowAsync().ConfigureAwait(false);
        }
        finally
        {
            _bindGate.Release();
        }
    }

    private async Task BindNowAsync()
    {
        List<(string Id, Dictionary<string, VariantValue> Properties)> list;
        int generation;
        string? previous;
        lock (_gate)
        {
            if (_shortcuts.Count == 0) return;
            generation = _generation;
            previous = _session;
            _session = null;
            list = _shortcuts.Select(s => (s.Key, ShortcutProperties(s.Value.Description, s.Value.Trigger))).ToList();
        }
        if (previous != null) await Portal.CloseAsync(previous).ConfigureAwait(false);
        string? session = null;
        try
        {
            await EnsureActivatedWatchAsync().ConfigureAwait(false);
            session = await Portal.CreateSessionAsync(Portal.GlobalShortcutsInterface, CancellationToken.None).ConfigureAwait(false);
            lock (_gate)
            {
                // Newer registrations arrived meanwhile: they schedule their own binding.
                if (generation != _generation)
                {
                    _ = Portal.CloseAsync(session);
                    return;
                }
                _session = session;
            }
            await Portal.RequestAsync(Portal.GlobalShortcutsInterface, "BindShortcuts",
                token => new DArg[] { new DPath(session), new DShortcuts(list), new DString(""), new DOptions(Portal.Options(token)) }).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The user dismissed the desktop's dialog: the shortcuts stay unbound until the next change.
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Portail GlobalShortcuts", ex);
        }
    }

    /// <summary>Description shown by the desktop, and the trigger Snaplume suggests (the user may change it).</summary>
    internal static Dictionary<string, VariantValue> ShortcutProperties(string description, string? trigger)
    {
        var properties = new Dictionary<string, VariantValue> { ["description"] = VariantValue.String(description) };
        if (trigger != null) properties["preferred_trigger"] = VariantValue.String(trigger);
        return properties;
    }

    /// <summary>Subscribes once; called by <see cref="BindNowAsync"/> only, so never twice at the same time.</summary>
    private async Task EnsureActivatedWatchAsync()
    {
        if (_activatedWatch != null) return;
        var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
        _activatedWatch = await DBusCalls.WatchAsync(connection, Portal.Service, Portal.ObjectPath, Portal.GlobalShortcutsInterface, "Activated",
            (message, _) =>
            {
                var reader = message.GetBodyReader();
                var session = reader.ReadObjectPathAsString();
                var id = reader.ReadString();
                return (Session: session, Id: id);
            },
            activation =>
            {
                Action? action = null;
                lock (_gate)
                {
                    if (activation.Session == _session && _shortcuts.TryGetValue(activation.Id, out var shortcut)) action = shortcut.Action;
                }
                if (action != null) UiActions.Post(action, "Raccourci clavier");
            }).ConfigureAwait(false);
        if (_disposed) _activatedWatch.Dispose();
    }

    public void Dispose()
    {
        _disposed = true;
        _debounce.Dispose();
        UnregisterAll();
        _activatedWatch?.Dispose();
    }
}
