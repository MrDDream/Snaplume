using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// One Snaplume per user: an flock on snaplume.lock decides who is first (the kernel releases it when the
/// process ends, even after a crash), and the first one listens on the Unix socket snaplume.sock for the
/// command lines of later launches. Both live in a folder only the user can open ($XDG_RUNTIME_DIR/snaplume,
/// or /tmp/snaplume-uid checked for ownership), and the socket itself is 0600.
/// </summary>
internal sealed class UnixSingleInstance : ISingleInstance
{
    /// <summary>Largest command line accepted from another launch.</summary>
    internal const int MaxMessageBytes = 64 * 1024;

    private readonly string _folder;
    private int _lockFd = -1;
    private Socket? _listener;
    private volatile bool _disposed;

    public UnixSingleInstance() => _folder = PrivateFolder();

    private string LockPath => Path.Combine(_folder, "snaplume.lock");
    private string SocketPath => Path.Combine(_folder, "snaplume.sock");

    public bool TryAcquire()
    {
        try
        {
            Directory.CreateDirectory(_folder);
            EnsurePrivate(_folder);
            int fd = LibC.open(LockPath, LibC.O_RDWR | LibC.O_CREAT | LibC.O_CLOEXEC | LibC.O_NOFOLLOW, 0b110_000_000);
            if (fd < 0) throw new IOException($"open({LockPath}) failed, errno {System.Runtime.InteropServices.Marshal.GetLastPInvokeError()}");
            if (LibC.flock(fd, LibC.LOCK_EX | LibC.LOCK_NB) != 0)
            {
                LibC.close(fd);
                return false;
            }
            _lockFd = fd;
            return true;
        }
        catch (Exception ex)
        {
            // Better two instances than none.
            ErrorLog.Write("Instance unique (verrou)", ex);
            return true;
        }
    }

    public async Task<bool> ForwardAsync(string[] args, CancellationToken cancel = default)
    {
        var message = Encode(args);
        if (message.Length > MaxMessageBytes || !IsFolderPrivate()) return false;
        // The first instance may still be starting: its socket appears a moment after its lock.
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try
            {
                using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                await socket.ConnectAsync(new UnixDomainSocketEndPoint(SocketPath), cancel).ConfigureAwait(false);
                await socket.SendAsync(message, SocketFlags.None, cancel).ConfigureAwait(false);
                socket.Shutdown(SocketShutdown.Send);
                return true;
            }
            catch (SocketException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(200, cancel).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                ErrorLog.Write("Instance unique (transfert)", ex);
                return false;
            }
        }
    }

    public void Listen(Action<string[]> received)
    {
        if (OperatingSystem.IsWindows() || _lockFd < 0 || !IsFolderPrivate()) return;
        try
        {
            // This process holds the lock: a socket file left there belongs to a dead instance.
            if (File.Exists(SocketPath)) File.Delete(SocketPath);
            var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            listener.Bind(new UnixDomainSocketEndPoint(SocketPath));
            File.SetUnixFileMode(SocketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(8);
            _listener = listener;
            var thread = new Thread(() => AcceptLoop(listener, received)) { IsBackground = true, Name = "Snaplume single instance" };
            thread.Start();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Instance unique (écoute)", ex);
        }
    }

    private void AcceptLoop(Socket listener, Action<string[]> received)
    {
        while (!_disposed)
        {
            try
            {
                using var client = listener.Accept();
                client.ReceiveTimeout = 2000;
                var buffer = new MemoryStream();
                var chunk = new byte[8192];
                int read;
                while ((read = client.Receive(chunk)) > 0)
                {
                    buffer.Write(chunk, 0, read);
                    if (buffer.Length > MaxMessageBytes) break;
                }
                if (Decode(buffer.ToArray()) is { } args) received(args);
                else ErrorLog.Write("Instance unique", "Message refusé (trop long ou illisible)");
            }
            catch (Exception ex) when (!_disposed)
            {
                ErrorLog.Write("Instance unique (réception)", ex);
            }
            catch (Exception)
            {
                return;
            }
        }
    }

    /// <summary>The arguments as a JSON array of strings (UTF-8).</summary>
    internal static byte[] Encode(string[] args) => JsonSerializer.SerializeToUtf8Bytes(args);

    /// <summary>The arguments, or null if the message is too long or not a JSON array of strings.</summary>
    internal static string[]? Decode(byte[] message)
    {
        if (message.Length == 0 || message.Length > MaxMessageBytes) return null;
        try
        {
            var args = JsonSerializer.Deserialize<string?[]>(message);
            return args == null || args.Any(a => a == null) ? null : args.Select(a => a!).ToArray();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>$XDG_RUNTIME_DIR/snaplume, else /tmp/snaplume-uid.</summary>
    internal static string PrivateFolder() =>
        Xdg.RuntimeDir is { } runtime ? Path.Combine(runtime, "snaplume") : Path.Combine("/tmp", "snaplume-" + LibC.getuid());

    private bool IsFolderPrivate()
    {
        try
        {
            EnsurePrivate(_folder);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Instance unique (dossier)", ex);
            return false;
        }
    }

    /// <summary>
    /// 0700 and owned by the user. In /tmp, another user could have created the folder first (or a link in its
    /// place) to receive Snaplume's command lines: such a folder is refused.
    /// </summary>
    private static void EnsurePrivate(string folder)
    {
        if (OperatingSystem.IsWindows()) return;
        var info = new DirectoryInfo(folder);
        if (info.LinkTarget != null) throw new IOException($"{folder} is a symbolic link");
        File.SetUnixFileMode(folder, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // SetUnixFileMode succeeds only for the owner (or root): reading the mode back confirms it applied.
        if ((File.GetUnixFileMode(folder) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute
                                             | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new IOException($"{folder} is not private");
    }

    public void Dispose()
    {
        _disposed = true;
        if (_listener != null)
        {
            try
            {
                _listener.Dispose();
                File.Delete(SocketPath);
            }
            catch (Exception)
            {
                // Removed at the next start anyway.
            }
        }
        if (_lockFd >= 0)
        {
            LibC.flock(_lockFd, LibC.LOCK_UN);
            LibC.close(_lockFd);
            _lockFd = -1;
        }
    }
}
