using System.Runtime.InteropServices;

namespace Snaplume.Platform.Linux.Interop;

/// <summary>The few libc calls .NET does not expose (64-bit Linux, glibc or musl: same values on x64 and arm64).</summary>
internal static unsafe class LibC
{
    private const string Lib = "libc";

    public const int O_RDWR = 0x2;
    public const int O_CREAT = 0x40;
    public const int O_NOFOLLOW = 0x20000;
    public const int O_CLOEXEC = 0x80000;
    public const int LOCK_EX = 2;
    public const int LOCK_NB = 4;
    public const int LOCK_UN = 8;
    public const short POLLIN = 1;
    public const int EFD_CLOEXEC = 0x80000;
    public const int EFD_NONBLOCK = 0x800;

    [StructLayout(LayoutKind.Sequential)]
    public struct PollFd
    {
        public int Fd;
        public short Events;
        public short Revents;
    }

    [DllImport(Lib, SetLastError = true)]
    public static extern int open([MarshalAs(UnmanagedType.LPUTF8Str)] string path, int flags, uint mode);

    [DllImport(Lib, SetLastError = true)]
    public static extern int close(int fd);

    [DllImport(Lib, SetLastError = true)]
    public static extern int flock(int fd, int operation);

    [DllImport(Lib)]
    public static extern uint getuid();

    [DllImport(Lib, SetLastError = true)]
    public static extern int poll(PollFd* fds, nuint count, int timeout);
}
