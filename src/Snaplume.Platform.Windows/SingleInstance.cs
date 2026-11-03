using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Snaplume.Core;

namespace Snaplume.Platform.Windows;

/// <summary>
/// One Snaplume per user: a named mutex, and a named pipe through which a second launch hands its command line
/// (a JSON array of texts) to the first one. When the pipe cannot be reached, a named event asks the first instance
/// to show itself; <see cref="Listen"/> reports it as an empty command line.
/// </summary>
/// <remarks>
/// Names are per user: two accounts in the same session each get their own instance. The Local\ names live in the
/// session's namespace: another user logged on to the same session (e.g. "run as" another account) could create the
/// mutex or event first and keep Snaplume from starting for this user. That is only a local denial of service; the
/// pipe, which carries data, is protected by its owner and access rules (see <see cref="CreatePipeServer"/>), and the
/// client only connects to a pipe owned by the current user.
/// </remarks>
public sealed class SingleInstance : ISingleInstance
{
    /// <summary>Largest command line accepted from another instance.</summary>
    private const int MaxForwardedBytes = 64 * 1024;

    private static readonly SecurityIdentifier? User = CurrentUser();

    private readonly string _mutexName;
    private readonly string _showEventName;
    private readonly string _pipeName;
    private Mutex? _mutex;
    private bool _owned;
    /// <summary>A first <see cref="TryAcquire"/> was made: only that one waits for the previous instance.</summary>
    private bool _tried;
    private EventWaitHandle? _showEvent;
    private RegisteredWaitHandle? _showWait;
    private CancellationTokenSource? _pipeCancel;
    /// <summary>Pipe instance waiting for a client: closed on exit, so that a restarted instance can create its own.</summary>
    private NamedPipeServerStream? _pipeServer;

    /// <param name="suffix">Added to every name, so that tests never meet the Snaplume of the user.</param>
    public SingleInstance(string? suffix = null)
    {
        var id = (User?.Value ?? Environment.UserName) + suffix;
        _mutexName = $@"Local\Snaplume.SingleInstance.{id}";
        _showEventName = $@"Local\Snaplume.Show.{id}";
        _pipeName = $"Snaplume.CommandLine.{id}";
    }

    private static SecurityIdentifier? CurrentUser()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User;
    }

    /// <summary>
    /// Takes the mutex. After a restart (language change: "--restarted" on the command line), the first call waits up
    /// to 10 s for the previous instance to quit; later calls (the caller's own retries) do not wait. Call it on the
    /// thread that will dispose this object (a mutex belongs to a thread).
    /// </summary>
    public bool TryAcquire()
    {
        if (_owned) return true;
        _mutex ??= new Mutex(false, _mutexName);
        bool wait = !_tried && Environment.GetCommandLineArgs().Contains("--restarted");
        _tried = true;
        try
        {
            _owned = _mutex.WaitOne(wait ? TimeSpan.FromSeconds(10) : TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            // The previous instance crashed: the mutex is ours now.
            _owned = true;
        }
        if (!_owned)
        {
            _mutex.Dispose();
            _mutex = null;
        }
        return _owned;
    }

    /// <summary>Sends the command line to the running instance, else asks it to show itself.</summary>
    /// <returns>False if neither the pipe nor the event could be reached.</returns>
    public async Task<bool> ForwardAsync(string[] args, CancellationToken cancel = default)
    {
        try
        {
            // CurrentUserOnly: the pipe must belong to this user, not to another session's impostor.
            await using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out,
                PipeOptions.CurrentUserOnly | PipeOptions.Asynchronous);
            await client.ConnectAsync(3000, cancel);
            await client.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(CommandLine.WithFullPaths(args)), cancel);
            await client.FlushAsync(cancel);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancel.IsCancellationRequested)
        {
            ErrorLog.Write("Transmission de la ligne de commande", ex);
        }
        try
        {
            using var show = EventWaitHandle.OpenExisting(_showEventName);
            show.Set();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or UnauthorizedAccessException or IOException)
        {
            ErrorLog.Write("Affichage de l'instance existante", ex);
            return false;
        }
    }

    /// <summary>Receives the command lines of later launches, on a thread-pool thread.</summary>
    public void Listen(Action<string[]> received)
    {
        if (_pipeCancel != null) throw new InvalidOperationException("Already listening");
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, _showEventName);
        _showWait = ThreadPool.RegisterWaitForSingleObject(_showEvent, (_, _) => received(Array.Empty<string>()), null, -1, false);
        _pipeCancel = new CancellationTokenSource();
        _ = ListenForCommandLinesAsync(received, _pipeCancel.Token);
    }

    private async Task ListenForCommandLinesAsync(Action<string[]> received, CancellationToken token)
    {
        int failures = 0;
        // The instance awaiting the next client: created while the previous one is still open (see below), else
        // a first instance of the pipe.
        NamedPipeServerStream? server = null;
        while (!token.IsCancellationRequested)
        {
            try
            {
                server ??= CreatePipeServer(firstInstance: true);
                _pipeServer = server;
                await server.WaitForConnectionAsync(token);
                failures = 0;
            }
            catch (Exception ex)
            {
                ClosePipeServer(server);
                server = null;
                if (token.IsCancellationRequested) return;
                // The pipe cannot be created (name still held by the previous instance after a restart, or taken by
                // another process): tried again, less and less often; the Show event works meanwhile.
                if (++failures <= 3) ErrorLog.Write("Réception d'une ligne de commande", ex);
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 1 << Math.Min(failures - 1, 5))), token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                continue;
            }

            // The next instance exists before this one is closed: the name is never free for another process to
            // take, and a client arriving meanwhile finds an instance to connect to.
            NamedPipeServerStream? next = null;
            try
            {
                next = CreatePipeServer(firstInstance: false);
                _pipeServer = next;
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // A first instance is created again once this one is closed.
                ErrorLog.Write("Réception d'une ligne de commande", ex);
            }
            catch (Exception)
            {
                // Stopping.
            }

            try
            {
                var args = await ReadForwardedArgumentsAsync(server, token);
                if (args != null) received(args);
            }
            catch (Exception ex) when (!token.IsCancellationRequested)
            {
                // One bad client (malformed or too long data, too slow, gone before the end): it is
                // ignored and the next one is awaited; the pipe itself works.
                ErrorLog.Write("Réception d'une ligne de commande", ex);
            }
            catch (Exception)
            {
                // Stopping: the loop ends below.
            }
            finally
            {
                ClosePipeServer(server);
            }
            server = next;
        }
        ClosePipeServer(server);
    }

    private void ClosePipeServer(NamedPipeServerStream? server)
    {
        if (server == null) return;
        Interlocked.CompareExchange(ref _pipeServer, null, server);
        server.Dispose();
    }

    /// <summary>
    /// Pipe only this user can open. A first instance (FirstPipeInstance) fails if another process already took the
    /// name; the second one, created while the first is open, joins this process's pipe (at most two instances).
    /// </summary>
    private NamedPipeServerStream CreatePipeServer(bool firstInstance)
    {
        var user = User ?? throw new InvalidOperationException("No user SID");
        var security = new PipeSecurity();
        security.SetOwner(user);
        // CreateNewInstance: needed to add the second instance to the pipe.
        security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance,
            AccessControlType.Allow));
        var options = PipeOptions.Asynchronous | (firstInstance ? PipeOptions.FirstPipeInstance : PipeOptions.None);
        return NamedPipeServerStreamAcl.Create(_pipeName, PipeDirection.In, 2, PipeTransmissionMode.Byte, options, 0, 0, security);
    }

    /// <returns>The arguments, or null if there are none or they are not a list of texts (e.g. "null", [null]).</returns>
    /// <exception cref="Exception">Invalid JSON, too much data, or the client too slow (5 s).</exception>
    private static async Task<string[]?> ReadForwardedArgumentsAsync(Stream stream, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));
        using var data = new MemoryStream();
        var chunk = new byte[4096];
        int read;
        while ((read = await stream.ReadAsync(chunk, timeout.Token)) > 0)
        {
            data.Write(chunk, 0, read);
            if (data.Length > MaxForwardedBytes) throw new InvalidDataException("Forwarded command line too long");
        }
        if (data.Length == 0) return null;
        var args = JsonSerializer.Deserialize<string?[]>(data.ToArray());
        if (args == null || args.Any(a => a == null))
        {
            ErrorLog.Write("Réception d'une ligne de commande", "Arguments invalides, ignorés");
            return null;
        }
        return Array.ConvertAll(args, a => a!);
    }

    /// <summary>Stops listening and releases the mutex (on the thread that acquired it).</summary>
    public void Dispose()
    {
        _pipeCancel?.Cancel();
        // Closed now, not when the listener wakes up (it may never run again): a restarted instance
        // must be able to create the pipe while this process ends.
        ClosePipeServer(_pipeServer);
        _showWait?.Unregister(null);
        _showWait = null;
        _showEvent?.Dispose();
        _showEvent = null;
        if (_mutex != null)
        {
            if (_owned)
            {
                try
                {
                    _mutex.ReleaseMutex();
                }
                catch (ApplicationException)
                {
                    // Released from another thread: the system frees it when the process ends.
                }
            }
            _mutex.Dispose();
            _mutex = null;
        }
        _owned = false;
    }
}
