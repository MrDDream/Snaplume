using System.Printing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using Snaplume.Core;

namespace Snaplume.Export;

/// <summary>Print and e-mail captures (gallery and editor).</summary>
public static class ShareService
{
    /// <summary>Margin kept around each image when printing, at least (1/96 inch units: 0.4 inch).</summary>
    private const double MinPrintMargin = 38.4;

    /// <summary>Print jobs being spooled (kept alive until they complete).</summary>
    private static readonly HashSet<XpsDocumentWriter> Spooling = new();

    // ---- Printing ----

    /// <summary>
    /// Prints image files, one per page, after the print dialog. Each page takes the orientation of its
    /// image; images are reduced to fit the printable area (never enlarged), keeping their ratio.
    /// Videos and unreadable files are left out.
    /// </summary>
    public static void Print(Window owner, IReadOnlyList<string> paths)
    {
        var images = new List<(string Path, bool Landscape)>();
        foreach (var path in paths.Where(ImageIO.IsImage))
        {
            if (ProbeLandscape(path) is bool landscape) images.Add((path, landscape));
        }
        if (images.Count == 0)
        {
            MessageBox.Show(owner, Loc.T("Share.Print.NoImage"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var title = images.Count == 1 ? Path.GetFileName(images[0].Path) : Loc.T("Share.Print.JobName", images.Count);
        PrintPages(owner, title, images.Select(i => i.Landscape).ToArray(), index => ImageIO.Load(images[index].Path));
    }

    /// <summary>Prints one image held in memory (e.g. the editor's image with its annotations).</summary>
    public static void Print(Window owner, BitmapSource image, string title)
    {
        if (!image.IsFrozen && image.CanFreeze) image.Freeze();
        PrintPages(owner, string.IsNullOrWhiteSpace(title) ? "Snaplume" : title,
            new[] { image.PixelWidth > image.PixelHeight }, _ => image);
    }

    private static void PrintPages(Window owner, string title, bool[] landscape, Func<int, BitmapSource> load)
    {
        var dialog = new PrintDialog { UserPageRangeEnabled = false, CurrentPageEnabled = false };
        try
        {
            if (dialog.ShowDialog() != true) return;
        }
        catch (Exception ex)
        {
            // No printer installed, print spooler stopped...
            ShowPrintError(owner, ex);
            return;
        }

        try
        {
            var queue = dialog.PrintQueue;
            var ticket = dialog.PrintTicket;
            queue.CurrentJobSettings.Description = title;

            // Page size of the ticket, as portrait (1/96 inch units).
            double mediaWidth = ticket.PageMediaSize?.Width ?? dialog.PrintableAreaWidth;
            double mediaHeight = ticket.PageMediaSize?.Height ?? dialog.PrintableAreaHeight;
            var portrait = new Size(Math.Min(mediaWidth, mediaHeight), Math.Max(mediaWidth, mediaHeight));
            double margin = PrintMargin(queue, ticket, portrait);

            var paginator = new ImagePaginator(landscape, load, portrait, margin);
            var writer = PrintQueue.CreateXpsDocumentWriter(queue);
            // Each page tells the printer its own orientation (page tickets are asked for in page order).
            int page = 0;
            writer.WritingPrintTicketRequired += (_, e) =>
            {
                if (e.CurrentPrintTicketLevel != System.Windows.Xps.Serialization.PrintTicketLevel.FixedPagePrintTicket) return;
                int index = Math.Min(page++, landscape.Length - 1);
                var pageTicket = ticket.Clone();
                pageTicket.PageOrientation = landscape[index] ? PageOrientation.Landscape : PageOrientation.Portrait;
                e.CurrentPrintTicket = pageTicket;
            };
            writer.WritingCompleted += (_, e) =>
            {
                Spooling.Remove(writer);
                if (e.Error != null) ShowPrintError(owner, e.Error);
            };
            Spooling.Add(writer);
            // Spooled in the background of the UI thread, page by page: a long job does not freeze the window.
            writer.WriteAsync(paginator, ticket);
        }
        catch (Exception ex)
        {
            ShowPrintError(owner, ex);
        }
    }

    /// <summary>Largest of the printer's unprintable margins, and at least <see cref="MinPrintMargin"/>.</summary>
    private static double PrintMargin(PrintQueue queue, PrintTicket ticket, Size portrait)
    {
        double margin = MinPrintMargin;
        try
        {
            if (queue.GetPrintCapabilities(ticket).PageImageableArea is { } area)
            {
                margin = Math.Max(margin, Math.Max(area.OriginWidth, area.OriginHeight));
                margin = Math.Max(margin, portrait.Width - area.OriginWidth - area.ExtentWidth);
                margin = Math.Max(margin, portrait.Height - area.OriginHeight - area.ExtentHeight);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Marges de l'imprimante", ex);
        }
        // Never more than a quarter of the page.
        return Math.Min(margin, Math.Min(portrait.Width, portrait.Height) / 4);
    }

    /// <summary>Orientation of an image from its header, null if it is not readable.</summary>
    private static bool? ProbeLandscape(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.DelayCreation | BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.None).Frames[0];
            return frame.PixelWidth > frame.PixelHeight;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ErrorLog.Write($"Impression de {path}", ex);
            return null;
        }
    }

    private static void ShowPrintError(Window owner, Exception ex)
    {
        ErrorLog.Write("Impression", ex);
        MessageBoxShow(VisibleOwner(owner), Loc.T("Share.Print.Failed", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    /// <summary>One image per page, decoded only when the page is spooled.</summary>
    private sealed class ImagePaginator : DocumentPaginator
    {
        private readonly bool[] _landscape;
        private readonly Func<int, BitmapSource> _load;
        private readonly Size _portrait;
        private readonly double _margin;

        public ImagePaginator(bool[] landscape, Func<int, BitmapSource> load, Size portrait, double margin)
        {
            _landscape = landscape;
            _load = load;
            _portrait = portrait;
            _margin = margin;
        }

        public override DocumentPage GetPage(int pageNumber)
        {
            var size = _landscape[pageNumber] ? new Size(_portrait.Height, _portrait.Width) : _portrait;
            var area = new Rect(_margin, _margin, Math.Max(1, size.Width - 2 * _margin), Math.Max(1, size.Height - 2 * _margin));
            var visual = new DrawingVisual();
            try
            {
                var image = _load(pageNumber);
                // 1 pixel = 1/96 inch, reduced to fit, centred.
                double scale = Math.Min(1, Math.Min(area.Width / image.PixelWidth, area.Height / image.PixelHeight));
                double width = image.PixelWidth * scale, height = image.PixelHeight * scale;
                var rect = new Rect(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height);
                using var dc = visual.RenderOpen();
                dc.DrawImage(image, rect);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Deleted or damaged meanwhile: a blank page keeps the others in order.
                ErrorLog.Write("Impression", ex);
            }
            return new DocumentPage(visual, size, new Rect(size), area);
        }

        public override bool IsPageCountValid => true;
        public override int PageCount => _landscape.Length;
        public override Size PageSize { get => _portrait; set { } }
        public override IDocumentPaginatorSource? Source => null;
    }

    // ---- E-mail ----

    /// <summary>
    /// Opens a new message of the default mail client with the files attached (Simple MAPI). Nothing is
    /// ever sent automatically: the client shows the message and the user sends it. Without a mail
    /// client, explains how to attach the files to a webmail and shows them in Explorer.
    /// </summary>
    /// <returns>True when the files were handed to the mail client (it opens in the background).</returns>
    public static bool Email(Window owner, IReadOnlyList<string> paths)
    {
        var files = paths.Where(File.Exists).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (files.Count == 0) return false;
        if (!Mapi.HasMailClient())
        {
            ShowFilesForWebmail(owner, files, Loc.T("Share.Email.NoClient"));
            return false;
        }

        var subject = files.Count == 1 ? Path.GetFileName(files[0]) : Loc.T("Share.Email.Subject", files.Count);
        var dispatcher = owner.Dispatcher;
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
                _ = AllowSetForegroundWindow(AsfwAny);
                result = Mapi.SendMail(files, subject);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Envoi par e-mail", ex);
                result = Mapi.Failure;
            }
            if (result is Mapi.Success or Mapi.UserAbort) return;
            ErrorLog.Write("Envoi par e-mail", $"MAPISendMail : {result}");
            dispatcher.BeginInvoke(() => ShowFilesForWebmail(owner, files, Loc.T("Share.Email.Failed")));
        })
        {
            IsBackground = true,
            Name = "Snaplume e-mail",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return true;
    }

    private static void ShowFilesForWebmail(Window owner, IReadOnlyList<string> files, string message)
    {
        MessageBoxShow(VisibleOwner(owner), message, Loc.T("Share.Email.Title"), MessageBoxButton.OK, MessageBoxImage.Information);
        try
        {
            if (!SelectInExplorer(files)) FileOps.ShowInExplorer(files[0]);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage dans l'Explorateur", ex);
        }
    }

    /// <summary>Opens the folder with all the files selected, ready to be dragged (same folder only).</summary>
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

    /// <summary>The owner if it can still own a message box (it may have been closed meanwhile).</summary>
    private static Window? VisibleOwner(Window? owner) =>
        owner != null && owner.IsLoaded && owner.IsVisible ? owner
            : Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive);

    private static MessageBoxResult MessageBoxShow(Window? owner, string text, string caption, MessageBoxButton button, MessageBoxImage icon) =>
        owner != null ? MessageBox.Show(owner, text, caption, button, icon) : MessageBox.Show(text, caption, button, icon);

    private const int AsfwAny = -1;

    [DllImport("user32.dll")]
    private static extern bool AllowSetForegroundWindow(int processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern IntPtr ILCreateFromPathW(string path);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr itemIdList);

    [DllImport("shell32.dll")]
    private static extern int SHOpenFolderAndSelectItems(IntPtr folder, uint count, IntPtr[] items, uint flags);
}
