using Snaplume.Core;
using Snaplume.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Wayland;

/// <summary>
/// Pointer control under Wayland through the RemoteDesktop portal: one session with a pointer device and a
/// monitor stream (absolute motion needs a stream), started once per scrolling capture. The user's consent is
/// remembered with a restore token (persist_mode 2), so later captures start without a dialog.
/// </summary>
internal sealed class WaylandPointer : IPointerControl
{
    private const string TokenName = "remote-desktop";
    private const uint DevicePointer = 2;
    private const uint SourceMonitor = 1;

    private readonly WaylandScreenCapture _screen;
    private readonly IPointerControl? _xwayland;
    private string? _session;
    private PortalStream? _stream;

    public WaylandPointer(WaylandScreenCapture screen, IPointerControl? xwayland)
    {
        _screen = screen;
        _xwayland = xwayland;
    }

    public async Task<bool> PrepareAsync(CancellationToken cancel = default)
    {
        if (_session != null) return true;
        string? session = null;
        try
        {
            session = await Portal.CreateSessionAsync(Portal.RemoteDesktopInterface, cancel).ConfigureAwait(false);
            var version = await Portal.VersionAsync(Portal.RemoteDesktopInterface).ConfigureAwait(false) ?? 1;
            var deviceOptions = new List<(string, VariantValue)> { ("types", VariantValue.UInt32(DevicePointer)) };
            if (version >= 2)
            {
                deviceOptions.Add(("persist_mode", VariantValue.UInt32(2)));
                if (Portal.LoadToken(TokenName) is { } restore) deviceOptions.Add(("restore_token", VariantValue.String(restore)));
            }
            await Portal.RequestAsync(Portal.RemoteDesktopInterface, "SelectDevices",
                token => new DArg[] { new DPath(session), new DOptions(Portal.Options(token, deviceOptions.ToArray())) }, cancel).ConfigureAwait(false);
            await Portal.RequestAsync(Portal.ScreenCastInterface, "SelectSources",
                token => new DArg[]
                {
                    new DPath(session),
                    new DOptions(Portal.Options(token, ("types", VariantValue.UInt32(SourceMonitor)), ("multiple", VariantValue.Bool(false)))),
                }, cancel).ConfigureAwait(false);
            var results = await Portal.RequestAsync(Portal.RemoteDesktopInterface, "Start",
                token => new DArg[] { new DPath(session), new DString(""), new DOptions(Portal.Options(token)) }, cancel).ConfigureAwait(false);
            if (results.GetUInt("devices") is { } devices && (devices & DevicePointer) == 0)
                throw new InvalidOperationException("RemoteDesktop: pointer not granted");
            if (results.GetString("restore_token") is { } newToken) Portal.SaveToken(TokenName, newToken);
            _stream = PortalStreams.Parse(results).FirstOrDefault() is { Node: not 0 } s ? s : null;
            _session = session;
            return true;
        }
        catch (OperationCanceledException)
        {
            if (session != null) await Portal.CloseAsync(session).ConfigureAwait(false);
            return false;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Portail RemoteDesktop", ex);
            if (session != null) await Portal.CloseAsync(session).ConfigureAwait(false);
            return false;
        }
    }

    /// <summary>Wayland does not tell clients where the pointer is.</summary>
    public bool TryGetPosition(out int x, out int y)
    {
        x = y = 0;
        return false;
    }

    public bool SetPosition(int x, int y)
    {
        if (_session is not { } session || _stream is not { } stream) return false;
        var (sx, sy) = PortalStreams.ToStream(stream, x, y, _screen.LastScale);
        return Notify("NotifyPointerMotionAbsolute", new DPath(session), new DOptions(new()), new DUInt(stream.Node), new DDouble(sx), new DDouble(sy));
    }

    /// <summary>Discrete wheel steps on the vertical axis (0); positive steps scroll down, the opposite of <paramref name="notches"/>.</summary>
    public bool Scroll(int notches)
    {
        if (_session is not { } session) return false;
        return Notify("NotifyPointerAxisDiscrete", new DPath(session), new DOptions(new()), new DUInt(0), new DInt(-notches));
    }

    /// <summary>Only seen when a Snaplume (XWayland) window has the keyboard focus, e.g. the stop bar.</summary>
    public bool EscapePressed() => _xwayland?.EscapePressed() ?? false;

    /// <summary>Wayland lets no client activate another one's window.</summary>
    public void ActivateWindowAt(int x, int y)
    {
    }

    public void Release()
    {
        if (_session is { } session) _ = Portal.CloseAsync(session);
        _session = null;
        _stream = null;
    }

    private static bool Notify(string member, params DArg[] args)
    {
        try
        {
            var call = Task.Run(async () =>
            {
                var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
                await DBusCalls.CallAsync(connection, Portal.Service, Portal.ObjectPath, Portal.RemoteDesktopInterface, member, args).ConfigureAwait(false);
            });
            return call.Wait(TimeSpan.FromSeconds(2));
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Portail RemoteDesktop ({member})", ex);
            return false;
        }
    }
}
