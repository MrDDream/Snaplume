using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Shell;

/// <summary>Explorer, Recycle Bin, e-mail (Simple MAPI), printing, Run key and folder checks.</summary>
public sealed class DesktopShell : IDesktopShell
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Snaplume";
    /// <summary>Value written by the versions named LibreCapture, removed whenever the setting is saved.</summary>
    private const string LegacyRunValue = "LibreCapture";
    private static readonly Guid FolderIdDownloads = new("374DE290-123F-4565-9164-39C4925E467B");

    /// <summary>Explorer by its full path, never an explorer.exe found in the current folder.</summary>
    private static string ExplorerPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    /// <summary>Folders open in Explorer (as before), files and URIs with their default application.</summary>
    public void Open(string target)
    {
        if (Path.IsPathFullyQualified(target) && Directory.Exists(target))
            Process.Start(ExplorerPath, $"\"{Path.GetFullPath(target)}\"")?.Dispose();
        else
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true })?.Dispose();
    }

    public void Reveal(string path) =>
        Process.Start(ExplorerPath, $"/select,\"{Path.GetFullPath(path)}\"")?.Dispose();

    /// <summary>
    /// Sends the items to the Recycle Bin. When an item cannot be recycled (e.g. on a network share), Windows asks
    /// before deleting it permanently.
    /// </summary>
    public bool MoveToTrash(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;
        var op = new SHFILEOPSTRUCT
        {
            wFunc = FO_DELETE,
            pFrom = string.Join('\0', paths.Select(Path.GetFullPath)) + "\0\0",
            fFlags = FOF_ALLOWUNDO | FOF_NOCONFIRMATION | FOF_SILENT | FOF_NOERRORUI | FOF_WANTNUKEWARNING,
        };
        return SHFileOperation(ref op) == 0 && !op.fAnyOperationsAborted;
    }

    public bool CanEmail => Mapi.HasMailClient();

    /// <summary>
    /// Opens a new message of the default mail client with the files attached (Simple MAPI). Nothing is
    /// ever sent automatically: the client shows the message and the user sends it.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// No mail client: the files are shown selected in Explorer, ready to be dragged into a webmail, and the message
    /// says so.
    /// </exception>
    public void Email(IReadOnlyList<string> paths, string subject)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return;
        if (!Mapi.HasMailClient())
        {
            ShowFilesForWebmail(files);
            throw new InvalidOperationException(Loc.T("Share.Email.NoClient"));
        }
        if (string.IsNullOrWhiteSpace(subject))
            subject = files.Count == 1 ? Path.GetFileName(files[0]) : Loc.T("Share.Email.Subject", files.Count);

        // Some clients only return once the message is sent or closed: never on the UI thread.
        var thread = new Thread(() =>
        {
            uint result;
            try
            {
                // Lets the mail client bring its window to the front. ASFW_ANY because its process is not
                // known beforehand: Simple MAPI loads the client's DLL in this process, which then hands over
                // to an exe that may not be running yet (Outlook, Thunderbird...). The right is limited: it
                // only works while Snaplume is in the foreground (the user just clicked "E-mail"), and it is
                // used up by the next foreground change or the user's next input.
                _ = AllowSetForegroundWindow(-1);
                result = Mapi.SendMail(files, subject);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Envoi par e-mail", ex);
                result = Mapi.Failure;
            }
            if (result is Mapi.Success or Mapi.UserAbort) return;
            ErrorLog.Write("Envoi par e-mail", $"MAPISendMail : {result}");
            // Shown from this thread: the platform has no window of its own to own the message.
            uint options = MB_OK | MB_ICONINFORMATION | (Loc.IsRightToLeft ? MB_RTLREADING | MB_RIGHT : 0);
            MessageBox(IntPtr.Zero, Loc.T("Share.Email.Failed"), Loc.T("Share.Email.Title"), options);
            ShowFilesForWebmail(files);
        })
        {
            IsBackground = true,
            Name = "Snaplume e-mail",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }

    /// <summary>The files selected in Explorer, ready to be dragged into a message.</summary>
    private void ShowFilesForWebmail(IReadOnlyList<string> files)
    {
        try
        {
            if (!SelectInExplorer(files)) Reveal(files[0]);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans l'Explorateur", ex);
        }
    }

    /// <summary>Opens the folder with all the files selected (same folder only).</summary>
    private static bool SelectInExplorer(IReadOnlyList<string> files)
    {
        var folder = Path.GetDirectoryName(files[0]);
        if (files.Count < 2 || folder == null ||
            files.Any(f => !string.Equals(Path.GetDirectoryName(f), folder, StringComparison.OrdinalIgnoreCase)))
            return false;

        IntPtr folderId = ILCreateFromPathW(folder);
        var items = files.Select(ILCreateFromPathW).ToArray();
        try
        {
            if (folderId == IntPtr.Zero || items.Any(i => i == IntPtr.Zero)) return false;
            return SHOpenFolderAndSelectItems(folderId, (uint)items.Length, items, 0) == 0;
        }
        finally
        {
            if (folderId != IntPtr.Zero) ILFree(folderId);
            foreach (var item in items.Where(i => i != IntPtr.Zero)) ILFree(item);
        }
    }

    /// <inheritdoc cref="ImagePrinter.PrintAsync"/>
    public Task<bool> PrintImagesAsync(IReadOnlyList<string> paths, string title, IntPtr owner) =>
        ImagePrinter.PrintAsync(paths, title, owner);

    /// <summary>The Downloads folder (wherever the user moved it).</summary>
    public string DownloadsFolder
    {
        get
        {
            try
            {
                if (SHGetKnownFolderPath(FolderIdDownloads, 0, IntPtr.Zero, out var pointer) == 0)
                {
                    try
                    {
                        if (Marshal.PtrToStringUni(pointer) is { Length: > 0 } path) return path;
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pointer);
                    }
                }
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Dossier Téléchargements", ex);
            }
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            return profile.Length > 0 ? Path.Combine(profile, "Downloads") : "";
        }
    }

    /// <summary>
    /// True when the "Snaplume" value of the Run key starts this executable (e.g. created by the
    /// installer). A value starting another copy of Snaplume does not count, nor does one whose executable no longer
    /// exists (left by an uninstalled copy: the MSI package cannot clean the Run key of every user); saving the
    /// setting turned off then removes it (<see cref="SetStartWithSession"/>).
    /// </summary>
    public bool StartsWithSession
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                if (key?.GetValue(RunValue) is not string command || Environment.ProcessPath is not { } exe) return false;
                return CommandExecutable(command) is { } target && File.Exists(Environment.ExpandEnvironmentVariables(target))
                       && IsSameFile(target, exe);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Démarrage avec Windows", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// Writes or removes the Run value (this executable, started in the tray). Turned off, the value is removed
    /// whatever it starts, an orphan included. Throws on failure.
    /// </summary>
    public void SetStartWithSession(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key == null) return;
        key.DeleteValue(LegacyRunValue, throwOnMissingValue: false);
        if (enabled && Environment.ProcessPath is { } exe) key.SetValue(RunValue, $"\"{exe}\" --tray");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Executable of a command line: the quoted path, or the text up to the first space.</summary>
    internal static string? CommandExecutable(string command)
    {
        command = command.Trim();
        if (command.StartsWith('"'))
        {
            int end = command.IndexOf('"', 1);
            return end > 1 ? command[1..end] : null;
        }
        int space = command.IndexOf(' ');
        return space < 0 ? command : command[..space];
    }

    private static bool IsSameFile(string a, string b)
    {
        try
        {
            a = Path.GetFullPath(Environment.ExpandEnvironmentVariables(a));
            b = Path.GetFullPath(b);
            return string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// True when Windows 11 routes the Print Screen key to the Snipping Tool,
    /// which prevents other applications from using it.
    /// </summary>
    public bool PrintScreenTakenBySystem
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Keyboard");
                return key?.GetValue("PrintScreenKeyForSnippingEnabled") is int value && value == 1;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Touche Impr. écran", ex);
                return false;
            }
        }
    }

    /// <summary>
    /// A drive root, a personal folder (profile, Desktop, Documents, Pictures, Videos, Music, Downloads), a
    /// folder holding one of them (C:\Users) or a system folder: each sub-folder of the captures folder
    /// becomes a workspace, and deleting a workspace sends its whole folder to the Recycle Bin.
    /// </summary>
    /// <returns>The explanation to show (the folder is refused), or null.</returns>
    public string? CheckWorkspaceFolder(string folder)
    {
        try
        {
            return IsTooBroad(Path.GetFullPath(folder))
                ? Loc.T("Settings.FolderTooBroad", Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder)), AppSettings.DefaultWorkspaceFolder)
                : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            // Not a valid path: the caller's own checks (creating the folder) report it.
            return null;
        }
    }

    /// <summary>A UNC path or a mapped network drive: deleted captures do not go to the Recycle Bin there.</summary>
    public bool IsNetworkFolder(string folder)
    {
        if (folder.StartsWith(@"\\", StringComparison.Ordinal)) return true; // UNC path (\\server\share)
        try
        {
            return Path.GetPathRoot(folder) is { Length: > 0 } root && new DriveInfo(root).DriveType == DriveType.Network;
        }
        catch (Exception)
        {
            return false;
        }
    }

    internal bool IsTooBroad(string folder) => PathRules.IsTooBroad(folder, DownloadsFolder);
}
