using System.Printing;
using System.Printing.Interop;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Xps;
using Snaplume.Core;
using Snaplume.Imaging;
using WinForms = System.Windows.Forms;

namespace Snaplume.Platform.Windows.Shell;

/// <summary>Prints image files, one per page, through the XPS print path of WPF.</summary>
/// <remarks>
/// The print dialog is the WinForms one: unlike WPF's PrintDialog, it takes an owner window (here an Avalonia
/// window), so it stays in front of it and modal to it. Its choices (printer, paper, copies...) come back as a
/// DEVMODE, turned into a print ticket for the XPS writer. The pages are then spooled on a background STA thread:
/// nothing here depends on a WPF dispatcher being pumped by the Avalonia message loop.
/// </remarks>
internal static class ImagePrinter
{
    /// <summary>Margin kept around each image when printing, at least (1/96 inch units: 0.4 inch).</summary>
    private const double MinPrintMargin = 38.4;

    /// <summary>
    /// Prints image files, one per page, after the print dialog. Each page takes the orientation of its
    /// image; images are reduced to fit the printable area (never enlarged), keeping their ratio.
    /// Videos and unreadable files are left out.
    /// </summary>
    /// <returns>False if the user cancelled the dialog.</returns>
    /// <exception cref="InvalidOperationException">Nothing to print, or printing failed (message ready to show).</exception>
    public static async Task<bool> PrintAsync(IReadOnlyList<string> paths, string title, IntPtr owner)
    {
        var images = new List<(string Path, bool Landscape)>();
        foreach (var path in paths.Where(ImageIO.IsImage))
        {
            if (ProbeLandscape(path) is bool landscape) images.Add((path, landscape));
        }
        if (images.Count == 0) throw new InvalidOperationException(Loc.T("Share.Print.NoImage"));
        if (string.IsNullOrWhiteSpace(title))
            title = images.Count == 1 ? Path.GetFileName(images[0].Path) : Loc.T("Share.Print.JobName", images.Count);

        string printer;
        byte[] devMode;
        try
        {
            using var dialog = new WinForms.PrintDialog
            {
                UseEXDialog = true,
                AllowSomePages = false,
                AllowCurrentPage = false,
                AllowSelection = false,
            };
            var result = owner != IntPtr.Zero ? dialog.ShowDialog(new OwnerWindow(owner)) : dialog.ShowDialog();
            if (result != WinForms.DialogResult.OK) return false;
            printer = dialog.PrinterSettings.PrinterName;
            devMode = ReadDevMode(dialog.PrinterSettings);
        }
        catch (Exception ex)
        {
            // No printer installed, print spooler stopped...
            throw Failure(ex);
        }

        var landscapes = images.Select(i => i.Landscape).ToArray();
        var files = images.Select(i => i.Path).ToArray();
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                Spool(printer, devMode, title, landscapes, files);
                done.SetResult();
            }
            catch (Exception ex)
            {
                done.SetException(ex);
            }
        })
        {
            IsBackground = true,
            Name = "Snaplume printing",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        try
        {
            await done.Task;
        }
        catch (Exception ex)
        {
            throw Failure(ex);
        }
        return true;
    }

    private static InvalidOperationException Failure(Exception ex)
    {
        ErrorLog.Write("Impression", ex);
        return new InvalidOperationException(Loc.T("Share.Print.Failed", ex.Message), ex);
    }

    /// <summary>Copy of the printer settings chosen in the dialog (DEVMODE structure).</summary>
    private static byte[] ReadDevMode(System.Drawing.Printing.PrinterSettings settings)
    {
        var handle = settings.GetHdevmode(settings.DefaultPageSettings);
        try
        {
            var pointer = GlobalLock(handle);
            try
            {
                var data = new byte[(int)GlobalSize(handle)];
                Marshal.Copy(pointer, data, 0, data.Length);
                return data;
            }
            finally
            {
                GlobalUnlock(handle);
            }
        }
        finally
        {
            GlobalFree(handle);
        }
    }

    /// <summary>Writes the pages to the printer's queue (synchronously, on the calling STA thread).</summary>
    private static void Spool(string printer, byte[] devMode, string title, bool[] landscape, string[] files)
    {
        using var server = new LocalPrintServer();
        using var queue = FindQueue(server, printer);
        PrintTicket ticket;
        using (var converter = new PrintTicketConverter(queue.FullName, queue.ClientPrintSchemaVersion))
            ticket = converter.ConvertDevModeToPrintTicket(devMode);
        queue.CurrentJobSettings.Description = title;

        // Page size of the ticket, as portrait (1/96 inch units).
        var capabilities = queue.GetPrintCapabilities(ticket);
        double mediaWidth = ticket.PageMediaSize?.Width ?? capabilities.OrientedPageMediaWidth ?? 816;
        double mediaHeight = ticket.PageMediaSize?.Height ?? capabilities.OrientedPageMediaHeight ?? 1056;
        var portrait = new Size(Math.Min(mediaWidth, mediaHeight), Math.Max(mediaWidth, mediaHeight));
        double margin = PrintMargin(capabilities, portrait);

        var paginator = new ImagePaginator(landscape, index => Load(files[index]), portrait, margin);
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
        writer.Write(paginator, ticket);
    }

    /// <summary>The queue of a local printer or of a printer connection (\\server\printer).</summary>
    private static PrintQueue FindQueue(LocalPrintServer server, string printer)
    {
        var queues = server.GetPrintQueues(new[] { EnumeratedPrintQueueTypes.Local, EnumeratedPrintQueueTypes.Connections });
        var match = queues.FirstOrDefault(q => string.Equals(q.FullName, printer, StringComparison.OrdinalIgnoreCase)
                                               || string.Equals(q.Name, printer, StringComparison.OrdinalIgnoreCase));
        return match ?? new PrintQueue(server, printer);
    }

    /// <summary>Largest of the printer's unprintable margins, and at least <see cref="MinPrintMargin"/>.</summary>
    private static double PrintMargin(PrintCapabilities capabilities, Size portrait)
    {
        double margin = MinPrintMargin;
        try
        {
            if (capabilities.PageImageableArea is { } area)
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

    /// <summary>Decodes an image file without locking it (first frame of a GIF).</summary>
    private static BitmapSource Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        var frame = BitmapDecoder.Create(stream, BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.OnLoad).Frames[0];
        ImageIO.CheckSize(frame.PixelWidth, frame.PixelHeight);
        frame.Freeze();
        return frame;
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

    /// <summary>A native window (Avalonia's) as the owner of a WinForms dialog.</summary>
    private sealed class OwnerWindow(IntPtr handle) : WinForms.IWin32Window
    {
        public IntPtr Handle { get; } = handle;
    }

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr handle);
}
