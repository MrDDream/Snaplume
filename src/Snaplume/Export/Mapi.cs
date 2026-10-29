using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Snaplume.Export;

/// <summary>
/// Simple MAPI: hands a new message with attachments to the default mail client (Outlook,
/// Thunderbird...). Always with MAPI_DIALOG: the client shows the message, the user sends it.
/// </summary>
internal static class Mapi
{
    public const uint Success = 0;
    public const uint UserAbort = 1;
    public const uint Failure = 2;

    private const uint MAPI_LOGON_UI = 0x00000001;
    private const uint MAPI_DIALOG = 0x00000008;
    /// <summary>nPosition: the attachment is not placed in the text of the message.</summary>
    private const uint NoPosition = 0xFFFFFFFF;

    // Layouts of mapi.h with the natural x64 alignment (no #pragma pack in the header):
    // MapiMessageW is 96 bytes, MapiFileDescW 40 bytes. Strings are passed as pointers allocated (and
    // freed) here. No recipient: the user chooses them in the mail client.

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MapiMessageW
    {
        public uint ulReserved;
        public IntPtr lpszSubject;
        public IntPtr lpszNoteText;
        public IntPtr lpszMessageType;
        public IntPtr lpszDateReceived;
        public IntPtr lpszConversationID;
        public uint flFlags;
        public IntPtr lpOriginator;
        public uint nRecipCount;
        public IntPtr lpRecips;
        public uint nFileCount;
        public IntPtr lpFiles;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MapiFileDescW
    {
        public uint ulReserved;
        public uint flFlags;
        public uint nPosition;
        public IntPtr lpszPathName;
        public IntPtr lpszFileName;
        public IntPtr lpFileType;
    }

    [DllImport("mapi32.dll", EntryPoint = "MAPISendMailW", ExactSpelling = true)]
    private static extern uint MAPISendMailW(IntPtr session, IntPtr uiParam, ref MapiMessageW message, uint flags, uint reserved);

    /// <summary>
    /// A mail client is registered as the default one (Settings › Default apps). Without one, the
    /// mapi32 stub of Windows would only show its own error.
    /// </summary>
    public static bool HasMailClient()
    {
        foreach (var root in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            try
            {
                using var key = root.OpenSubKey(@"Software\Clients\Mail");
                if (key?.GetValue(null) is string name && name.Trim().Length > 0) return true;
            }
            catch (Exception)
            {
                // Not readable: look at the other one.
            }
        }
        return false;
    }

    /// <summary>
    /// Opens a new message with the files attached, in the mail client's window. Blocks until the client
    /// returns (some only after the message is sent or closed): call it from a background STA thread.
    /// </summary>
    /// <returns>MAPI result: <see cref="Success"/>, <see cref="UserAbort"/> or an error code.</returns>
    /// <exception cref="DllNotFoundException">No MAPI on this computer.</exception>
    /// <remarks>
    /// Unicode entry point only: the mapi32 stub of Windows 10 and later always exports MAPISendMailW
    /// (and converts for clients that only implement the ANSI version).
    /// </remarks>
    public static uint SendMail(IReadOnlyList<string> paths, string subject)
    {
        var allocations = new List<IntPtr>();
        IntPtr files = IntPtr.Zero;
        try
        {
            int size = Marshal.SizeOf<MapiFileDescW>();
            files = Marshal.AllocHGlobal(size * paths.Count);
            for (int i = 0; i < paths.Count; i++)
            {
                var file = new MapiFileDescW
                {
                    nPosition = NoPosition,
                    lpszPathName = Unicode(paths[i], allocations),
                    lpszFileName = Unicode(Path.GetFileName(paths[i]), allocations),
                };
                Marshal.StructureToPtr(file, files + i * size, false);
            }
            var message = new MapiMessageW
            {
                lpszSubject = Unicode(subject, allocations),
                nFileCount = (uint)paths.Count,
                lpFiles = files,
            };
            return MAPISendMailW(IntPtr.Zero, IntPtr.Zero, ref message, MAPI_LOGON_UI | MAPI_DIALOG, 0);
        }
        finally
        {
            if (files != IntPtr.Zero) Marshal.FreeHGlobal(files);
            foreach (var pointer in allocations) Marshal.FreeHGlobal(pointer);
        }
    }

    private static IntPtr Unicode(string text, List<IntPtr> allocations)
    {
        var pointer = Marshal.StringToHGlobalUni(text);
        allocations.Add(pointer);
        return pointer;
    }
}
