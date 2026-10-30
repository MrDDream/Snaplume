using Snaplume.Core;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// The home trash of the freedesktop Trash specification ($XDG_DATA_HOME/Trash), used when "gio trash" is not
/// installed: an info file is written first, then the item is moved into files/. A file on another file system
/// (USB drive, network share) is copied there then deleted, as .NET's File.Move does; a folder on another file
/// system cannot be moved and is refused. (gio trash uses the drive's own .Trash-uid folder instead.)
/// </summary>
internal static class HomeTrash
{
    public static bool TryMove(string path)
    {
        var trash = Path.Combine(Xdg.DataHome, "Trash");
        var files = Path.Combine(trash, "files");
        var info = Path.Combine(trash, "info");
        string? infoPath = null;
        try
        {
            Directory.CreateDirectory(files);
            Directory.CreateDirectory(info);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(trash, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            var full = Path.GetFullPath(path);
            var name = DesktopFiles.UniqueTrashName(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)),
                n => File.Exists(Path.Combine(info, n + ".trashinfo")) || Path.Exists(Path.Combine(files, n)));
            infoPath = Path.Combine(info, name + ".trashinfo");
            // CreateNew: two Snaplume windows deleting the same name cannot overwrite each other's info file.
            using (var stream = new FileStream(infoPath, FileMode.CreateNew, FileAccess.Write))
            using (var writer = new StreamWriter(stream))
                writer.Write(DesktopFiles.TrashInfo(full, DateTime.Now));
            var target = Path.Combine(files, name);
            if (Directory.Exists(full)) Directory.Move(full, target);
            else File.Move(full, target);
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Corbeille (spécification freedesktop) : {path}", ex);
            if (infoPath != null)
            {
                try
                {
                    File.Delete(infoPath);
                }
                catch (Exception)
                {
                    // Left behind: harmless, the file manager ignores info files without an item.
                }
            }
            return false;
        }
    }
}
