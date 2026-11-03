using System.Runtime.InteropServices;

namespace Snaplume.Platform.Linux.Interop;

/// <summary>
/// The few libc calls .NET does not expose (64-bit Linux, glibc or musl). Only values common to every Linux
/// architecture belong here: open() flags, for one, differ between x64 and arm64.
/// </summary>
internal static unsafe class LibC
{
    private const string Lib = "libc";

    public const short POLLIN = 1;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport(Lib)]
    public static extern uint getuid();

    [DllImport(Lib, SetLastError = true)]
    public static extern int poll(PollFd* fds, nuint count, int timeout);
}
