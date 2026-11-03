using Microsoft.Win32.SafeHandles;
using Snaplume.Platform.Linux.DBus;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Wayland;

/// <summary>
/// A ScreenCast portal session for recording under Wayland: the user picks a monitor or a window in the
/// desktop's dialog (once: the restore token is kept), then OpenPipeWireRemote hands over a PipeWire file
/// descriptor that GStreamer's pipewiresrc reads in this process.
/// </summary>
internal sealed class ScreenCastSession : IDisposable
{
    private const string TokenName = "screencast";
    private const uint SourceMonitor = 1, SourceWindow = 2;
    private const uint CursorHidden = 1, CursorEmbedded = 2;

    private ScreenCastSession(string handle, PortalStream stream, SafeFileHandle pipeWire)
    {
        Handle = handle;
        Stream = stream;
        PipeWire = pipeWire;
    }

    public string Handle { get; }
    public PortalStream Stream { get; }

    /// <summary>Open PipeWire connection; pipewiresrc duplicates it, this copy is closed with the session.</summary>
    public SafeFileHandle PipeWire { get; }

    public int FileDescriptor => (int)PipeWire.DangerousGetHandle();

    /// <exception cref="OperationCanceledException">The user cancelled the desktop's dialog.</exception>
    public static async Task<ScreenCastSession> StartAsync(bool showCursor, CancellationToken cancel)
    {
        var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
        uint version = await Portal.VersionAsync(Portal.ScreenCastInterface).ConfigureAwait(false) ?? 1;
        uint sources = DBusCalls.AsUInt(await DBusCalls.GetPropertyAsync(connection, Portal.Service, Portal.ObjectPath,
            Portal.ScreenCastInterface, "AvailableSourceTypes").ConfigureAwait(false)) ?? SourceMonitor;
        uint cursors = version >= 2
            ? DBusCalls.AsUInt(await DBusCalls.GetPropertyAsync(connection, Portal.Service, Portal.ObjectPath,
                Portal.ScreenCastInterface, "AvailableCursorModes").ConfigureAwait(false)) ?? 0
            : 0;

        var session = await Portal.CreateSessionAsync(Portal.ScreenCastInterface, cancel).ConfigureAwait(false);
        try
        {
            var options = SelectOptions(version, sources, cursors, showCursor, Portal.LoadToken(TokenName));
            await Portal.RequestAsync(Portal.ScreenCastInterface, "SelectSources",
                token => new DArg[] { new DPath(session), new DOptions(Portal.Options(token, options)) }, cancel).ConfigureAwait(false);
            var results = await Portal.RequestAsync(Portal.ScreenCastInterface, "Start",
                token => new DArg[] { new DPath(session), new DString(""), new DOptions(Portal.Options(token)) }, cancel).ConfigureAwait(false);
            if (results.GetString("restore_token") is { } restore) Portal.SaveToken(TokenName, restore);
            var stream = PortalStreams.Parse(results).FirstOrDefault();
            if (stream.Node == 0) throw new InvalidOperationException("ScreenCast: no stream");
            var fd = await DBusCalls.CallAsync(connection, (m, _) => m.GetBodyReader().ReadHandle<SafeFileHandle>(),
                Portal.Service, Portal.ObjectPath, Portal.ScreenCastInterface, "OpenPipeWireRemote",
                new DPath(session), new DOptions(new())).WaitAsync(Portal.NonInteractiveTimeout).ConfigureAwait(false);
            return new ScreenCastSession(session, stream, fd ?? throw new InvalidOperationException("ScreenCast: no PipeWire descriptor"));
        }
        catch
        {
            await Portal.CloseAsync(session).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>SelectSources options for what this portal version and desktop support.</summary>
    internal static (string, VariantValue)[] SelectOptions(uint version, uint availableSources, uint availableCursors, bool showCursor, string? restoreToken)
    {
        uint types = availableSources & (SourceMonitor | SourceWindow);
        if (types == 0) types = SourceMonitor;
        var options = new List<(string, VariantValue)>
        {
            ("types", VariantValue.UInt32(types)),
            ("multiple", VariantValue.Bool(false)),
        };
        uint wanted = showCursor ? CursorEmbedded : CursorHidden;
        if (version >= 2 && (availableCursors & wanted) != 0) options.Add(("cursor_mode", VariantValue.UInt32(wanted)));
        if (version >= 4)
        {
            options.Add(("persist_mode", VariantValue.UInt32(2)));
            if (restoreToken != null) options.Add(("restore_token", VariantValue.String(restoreToken)));
        }
        return options.ToArray();
    }

    public void Dispose()
    {
        PipeWire.Dispose();
        _ = Portal.CloseAsync(Handle);
    }
}
