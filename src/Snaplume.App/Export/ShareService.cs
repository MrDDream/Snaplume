using Avalonia.Controls;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Ui;

namespace Snaplume.Export;

/// <summary>
/// Print and e-mail captures (gallery and editor). The work is done by the platform (print dialog and spooler,
/// mail client); this class chooses the files and explains what went wrong.
/// </summary>
public static class ShareService
{
    // ---- Printing ----

    /// <summary>
    /// Prints image files, one per page, after the print dialog. Each page takes the orientation of its image;
    /// images are reduced to fit the page, keeping their ratio. Videos and unreadable files are left out.
    /// </summary>
    public static void Print(Window owner, IReadOnlyList<string> paths) => _ = PrintAsync(owner, paths);

    /// <inheritdoc cref="Print(Window, IReadOnlyList{string})"/>
    public static async Task PrintAsync(Window owner, IReadOnlyList<string> paths)
    {
        var images = await Task.Run(() => paths.Where(p => ImageIO.IsImage(p) && ImageIO.ReadSize(p) != null).ToList());
        if (images.Count == 0)
        {
            await Dialogs.ShowAsync(VisibleOwner(owner), Loc.T("Share.Print.NoImage"), "Snaplume", DialogButtons.Ok, DialogIcon.Information);
            return;
        }
        var title = images.Count == 1 ? Path.GetFileName(images[0]) : Loc.T("Share.Print.JobName", images.Count);
        // Not every print path reads WebP (CUPS, Windows without its WebP codec): those go as temporary PNGs.
        var temps = new List<string>();
        try
        {
            var files = await Task.Run(() => images.Select(path =>
            {
                if (!Path.GetExtension(path).Equals(".webp", StringComparison.OrdinalIgnoreCase)) return path;
                var temp = Path.Combine(Path.GetTempPath(), $"Snaplume-print-{Guid.NewGuid():N}.png");
                temps.Add(temp);
                File.WriteAllBytes(temp, ImageIO.EncodePng(ImageIO.Load(path)));
                return temp;
            }).ToList());
            await PrintFilesAsync(owner, files, title);
        }
        catch (Exception ex)
        {
            await ShowPrintErrorAsync(owner, ex);
        }
        finally
        {
            foreach (var temp in temps)
            {
                try
                {
                    File.Delete(temp);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Suppression du fichier temporaire d'impression", ex);
                }
            }
        }
    }

    /// <summary>Prints one image held in memory (e.g. the editor's image with its annotations).</summary>
    public static void Print(Window owner, PixelBuffer image, string title) => _ = PrintAsync(owner, image, title);

    /// <inheritdoc cref="Print(Window, PixelBuffer, string)"/>
    public static async Task PrintAsync(Window owner, PixelBuffer image, string title)
    {
        // The platform prints files: a temporary PNG, deleted once the job is handed over.
        var temp = Path.Combine(Path.GetTempPath(), $"Snaplume-print-{Guid.NewGuid():N}.png");
        try
        {
            var png = await Task.Run(() => ImageIO.EncodePng(image));
            await File.WriteAllBytesAsync(temp, png);
            await PrintFilesAsync(owner, new[] { temp }, string.IsNullOrWhiteSpace(title) ? "Snaplume" : title);
        }
        catch (Exception ex)
        {
            await ShowPrintErrorAsync(owner, ex);
        }
        finally
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Suppression du fichier temporaire d'impression", ex);
            }
        }
    }

    private static async Task PrintFilesAsync(Window owner, IReadOnlyList<string> files, string title)
    {
        try
        {
            var handle = owner.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await AppServices.Platform.Shell.PrintImagesAsync(files, title, handle);
        }
        catch (Exception ex)
        {
            // No printer installed, print service stopped...
            await ShowPrintErrorAsync(owner, ex);
        }
    }

    private static Task ShowPrintErrorAsync(Window owner, Exception ex)
    {
        ErrorLog.Write("Impression", ex);
        return Dialogs.ShowAsync(VisibleOwner(owner), Loc.T("Share.Print.Failed", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
    }

    // ---- E-mail ----

    /// <summary>
    /// Opens a new message of the mail application with the files attached. Nothing is ever sent automatically:
    /// the application shows the message and the user sends it. Without a mail application, explains how to attach
    /// the files to a webmail and shows them in the file manager.
    /// </summary>
    /// <returns>True when the files were handed to the mail application.</returns>
    public static bool Email(Window owner, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return false;
        var shell = AppServices.Platform.Shell;
        if (!shell.CanEmail)
        {
            _ = ShowFilesForWebmailAsync(owner, files, Loc.T("Share.Email.NoClientSystem"));
            return false;
        }
        var subject = files.Count == 1 ? Path.GetFileName(files[0]) : Loc.T("Share.Email.Subject", files.Count);
        try
        {
            shell.Email(files, subject);
            return true;
        }
        catch (InvalidOperationException ex)
        {
            // No mail application after all: the message says so.
            ErrorLog.Write("Envoi par e-mail", ex);
            _ = ShowFilesForWebmailAsync(owner, files, ex.Message);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Envoi par e-mail", ex);
            _ = ShowFilesForWebmailAsync(owner, files, Loc.T("Share.Email.FailedSystem"));
        }
        return false;
    }

    private static async Task ShowFilesForWebmailAsync(Window owner, IReadOnlyList<string> files, string message)
    {
        await Dialogs.ShowAsync(VisibleOwner(owner), message, Loc.T("Share.Email.Title"), DialogButtons.Ok, DialogIcon.Information);
        try
        {
            FileOps.Reveal(files[0]);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans le gestionnaire de fichiers", ex);
        }
    }

    /// <summary>The owner if it can still own a message box (it may have been closed meanwhile).</summary>
    private static Window? VisibleOwner(Window? owner) => owner is { IsVisible: true } ? owner : Dialogs.ActiveWindow;
}
