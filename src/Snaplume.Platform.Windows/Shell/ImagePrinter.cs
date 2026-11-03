using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Windows.Native;

namespace Snaplume.Platform.Windows.Shell;

/// <summary>Prints image files, one per page, through the Windows print dialog and GDI.</summary>
/// <remarks>
/// The dialog (PrintDlgEx) takes an owner window (here an Avalonia window), so it stays in front of it and modal to
/// it. It gives back a device context of the chosen printer with the chosen settings (paper, copies...); the pages
/// are then drawn into it on a background thread, each one in the orientation of its image (ResetDC between pages).
/// </remarks>
internal static class ImagePrinter
{
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
            if (ImageIO.ReadSize(path) is var (width, height)) images.Add((path, width > height));
            else ErrorLog.Write($"Impression de {path}", Loc.T("App.ImageUnreadable"));
        }
        if (images.Count == 0) throw new InvalidOperationException(Loc.T("Share.Print.NoImage"));
        if (string.IsNullOrWhiteSpace(title))
            title = images.Count == 1 ? Path.GetFileName(images[0].Path) : Loc.T("Share.Print.JobName", images.Count);

        PrintJob? job;
        try
        {
            job = AskPrinter(owner);
        }
        catch (Exception ex)
        {
            // No printer installed, print spooler stopped...
            throw Failure(ex);
        }
        if (job == null) return false;

        try
        {
            // Some drivers take a while per page (and big images are decoded one at a time): never on the UI thread.
            await Task.Run(() => Spool(job, title, images));
        }
        catch (Exception ex)
        {
            throw Failure(ex);
        }
        finally
        {
            job.Dispose();
        }
        return true;
    }

    private static InvalidOperationException Failure(Exception ex)
    {
        ErrorLog.Write("Impression", ex);
        return new InvalidOperationException(Loc.T("Share.Print.Failed", ex.Message), ex);
    }

    /// <summary>The print dialog; null if cancelled.</summary>
    private static PrintJob? AskPrinter(IntPtr owner)
    {
        // PrintDlgEx refuses to run without an owner window.
        using var fallbackOwner = owner == IntPtr.Zero ? new HiddenWindow("Print", (_, _, _) => null, messageOnly: false) : null;
        var dialog = new PRINTDLGEX
        {
            lStructSize = Marshal.SizeOf<PRINTDLGEX>(),
            hwndOwner = fallbackOwner?.Handle ?? owner,
            // No page range, selection or current page (one image per page, all printed); printing to a file was
            // never offered either.
            Flags = PD_RETURNDC | PD_NOPAGENUMS | PD_NOSELECTION | PD_NOCURRENTPAGE | PD_HIDEPRINTTOFILE
                    | PD_USEDEVMODECOPIESANDCOLLATE,
            nCopies = 1,
            nStartPage = START_PAGE_GENERAL,
        };
        int result = PrintDlgEx(ref dialog);
        // An HRESULT (e.g. E_OUTOFMEMORY, or the error of a missing print spooler).
        if (result != 0) throw Marshal.GetExceptionForHR(result) ?? new Win32Exception(result);
        var job = new PrintJob(dialog.hDC, dialog.hDevMode, dialog.hDevNames, (int)Math.Max(1, dialog.nCopies),
            (dialog.Flags & PD_COLLATE) != 0);
        if (dialog.dwResultAction == PD_RESULT_PRINT && dialog.hDC != IntPtr.Zero) return job;
        job.Dispose();
        return null;
    }

    /// <summary>Draws the pages into the printer's device context (synchronously).</summary>
    /// <param name="output">A file instead of the printer (to check the pages with a PDF printer).</param>
    private static void Spool(PrintJob job, string title, List<(string Path, bool Landscape)> images, string? output = null)
    {
        var hdc = job.Hdc;
        var document = new DOCINFO { cbSize = Marshal.SizeOf<DOCINFO>(), lpszDocName = title, lpszOutput = output };
        if (StartDoc(hdc, ref document) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        try
        {
            // Copies the driver cannot make itself are printed here: whole documents when collated.
            int documents = job.Collate ? job.Copies : 1, pages = job.Collate ? 1 : job.Copies;
            for (int copy = 0; copy < documents; copy++)
            {
                foreach (var (path, landscape) in images)
                {
                    job.SetOrientation(landscape);
                    PixelBuffer? image = null;
                    try
                    {
                        image = ImageIO.Load(path);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // Deleted or damaged meanwhile: a blank page keeps the others in order.
                        ErrorLog.Write("Impression", ex);
                    }
                    var dib = image == null ? null : DibEncoder.Encode(image);
                    for (int i = 0; i < pages; i++)
                    {
                        if (StartPage(hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                        if (image != null && dib != null) DrawImage(hdc, image.Width, image.Height, dib);
                        if (EndPage(hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
                    }
                }
            }
            if (EndDoc(hdc) <= 0) throw new Win32Exception(Marshal.GetLastPInvokeError());
        }
        catch
        {
            AbortDoc(hdc);
            throw;
        }
    }

    /// <summary>Draws the image on the page, 1 pixel = 1/96 inch, reduced to fit inside the margins, centred.</summary>
    private static void DrawImage(IntPtr hdc, int width, int height, byte[] dib)
    {
        double dpiX = Math.Max(1, GetDeviceCaps(hdc, LOGPIXELSX)), dpiY = Math.Max(1, GetDeviceCaps(hdc, LOGPIXELSY));
        // Whole sheet and printable area, in device pixels; the device context's origin is the printable area's corner.
        int physicalWidth = GetDeviceCaps(hdc, PHYSICALWIDTH), physicalHeight = GetDeviceCaps(hdc, PHYSICALHEIGHT);
        int offsetX = GetDeviceCaps(hdc, PHYSICALOFFSETX), offsetY = GetDeviceCaps(hdc, PHYSICALOFFSETY);
        int printableWidth = GetDeviceCaps(hdc, HORZRES), printableHeight = GetDeviceCaps(hdc, VERTRES);
        if (physicalWidth <= 0 || physicalHeight <= 0)
        {
            (physicalWidth, physicalHeight, offsetX, offsetY) = (printableWidth, printableHeight, 0, 0);
        }

        // To 1/96 inch, the unit of the layout.
        double pageWidth = physicalWidth * 96 / dpiX, pageHeight = physicalHeight * 96 / dpiY;
        double margin = PrintLayout.Margin(pageWidth, pageHeight, offsetX * 96 / dpiX, offsetY * 96 / dpiY,
            (physicalWidth - offsetX - printableWidth) * 96 / dpiX, (physicalHeight - offsetY - printableHeight) * 96 / dpiY);
        var (x, y, w, h) = PrintLayout.Fit(width, height, pageWidth, pageHeight, margin);

        int left = (int)Math.Round(x * dpiX / 96) - offsetX, top = (int)Math.Round(y * dpiY / 96) - offsetY;
        int right = (int)Math.Round((x + w) * dpiX / 96) - offsetX, bottom = (int)Math.Round((y + h) * dpiY / 96) - offsetY;
        // Smooth reduction (the brush origin must be set again after choosing HALFTONE).
        SetStretchBltMode(hdc, HALFTONE);
        SetBrushOrgEx(hdc, 0, 0, IntPtr.Zero);
        unsafe
        {
            fixed (byte* bits = dib)
            {
                if (StretchDIBits(hdc, left, top, Math.Max(1, right - left), Math.Max(1, bottom - top), 0, 0, width, height,
                        bits + DibEncoder.HeaderSize, bits, DIB_RGB_COLORS, NativeMethods.SRCCOPY) == 0)
                    throw new Win32Exception(Marshal.GetLastPInvokeError());
            }
        }
    }

    /// <summary>The printer chosen in the dialog: its device context and settings (DEVMODE), freed together.</summary>
    private sealed class PrintJob(IntPtr hdc, IntPtr devMode, IntPtr devNames, int copies, bool collate) : IDisposable
    {
        /// <summary>Offsets in DEVMODEW: dmFields after the 32-character name and four 16-bit fields, then dmOrientation.</summary>
        private const int FieldsOffset = 72, OrientationOffset = 76;
        private const int DM_ORIENTATION = 0x1;
        private const short DMORIENT_PORTRAIT = 1, DMORIENT_LANDSCAPE = 2;

        public IntPtr Hdc { get; } = hdc;
        /// <summary>Copies the program prints itself (when the driver cannot).</summary>
        public int Copies { get; } = copies;
        public bool Collate { get; } = collate;

        /// <summary>Changes the orientation of the next page (between pages only).</summary>
        public void SetOrientation(bool landscape)
        {
            if (devMode == IntPtr.Zero) return;
            var pointer = GlobalLock(devMode);
            if (pointer == IntPtr.Zero) return;
            try
            {
                if ((long)GlobalSize(devMode) < OrientationOffset + 2) return;
                Marshal.WriteInt32(pointer, FieldsOffset, Marshal.ReadInt32(pointer, FieldsOffset) | DM_ORIENTATION);
                Marshal.WriteInt16(pointer, OrientationOffset, landscape ? DMORIENT_LANDSCAPE : DMORIENT_PORTRAIT);
                // Some drivers cannot: the page keeps the previous orientation, the image is still fitted to it.
                if (ResetDC(Hdc, pointer) == IntPtr.Zero) ErrorLog.Write("Impression", "ResetDC a échoué (orientation de la page)");
            }
            finally
            {
                GlobalUnlock(devMode);
            }
        }

        public void Dispose()
        {
            if (Hdc != IntPtr.Zero) DeleteDC(Hdc);
            if (devMode != IntPtr.Zero) GlobalFree(devMode);
            if (devNames != IntPtr.Zero) GlobalFree(devNames);
        }
    }

    // ---- Win32 ----

    private const uint PD_NOSELECTION = 0x4;
    private const uint PD_NOPAGENUMS = 0x8;
    private const uint PD_COLLATE = 0x10;
    private const uint PD_RETURNDC = 0x100;
    private const uint PD_USEDEVMODECOPIESANDCOLLATE = 0x40000;
    private const uint PD_HIDEPRINTTOFILE = 0x100000;
    private const uint PD_NOCURRENTPAGE = 0x800000;
    private const uint START_PAGE_GENERAL = 0xFFFFFFFF;
    private const uint PD_RESULT_PRINT = 1;
    private const int HORZRES = 8;
    private const int VERTRES = 10;
    private const int LOGPIXELSX = 88;
    private const int LOGPIXELSY = 90;
    private const int PHYSICALWIDTH = 110;
    private const int PHYSICALHEIGHT = 111;
    private const int PHYSICALOFFSETX = 112;
    private const int PHYSICALOFFSETY = 113;
    private const int HALFTONE = 4;
    private const uint DIB_RGB_COLORS = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PRINTDLGEX
    {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hDevMode;
        public IntPtr hDevNames;
        public IntPtr hDC;
        public uint Flags;
        public uint Flags2;
        public uint ExclusionFlags;
        public uint nPageRanges;
        public uint nMaxPageRanges;
        public IntPtr lpPageRanges;
        public uint nMinPage;
        public uint nMaxPage;
        public uint nCopies;
        public IntPtr hInstance;
        public IntPtr lpPrintTemplateName;
        public IntPtr lpCallback;
        public uint nPropertyPages;
        public IntPtr lphPropertyPages;
        public uint nStartPage;
        public uint dwResultAction;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DOCINFO
    {
        public int cbSize;
        public string lpszDocName;
        public string? lpszOutput;
        public string? lpszDatatype;
        public uint fwType;
    }

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode, EntryPoint = "PrintDlgExW")]
    private static extern int PrintDlgEx(ref PRINTDLGEX dialog);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode, EntryPoint = "StartDocW", SetLastError = true)]
    private static extern int StartDoc(IntPtr hdc, ref DOCINFO document);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int StartPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndPage(IntPtr hdc);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern int EndDoc(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int AbortDoc(IntPtr hdc);

    [DllImport("gdi32.dll", EntryPoint = "ResetDCW")]
    private static extern IntPtr ResetDC(IntPtr hdc, IntPtr devMode);

    [DllImport("gdi32.dll")]
    private static extern int GetDeviceCaps(IntPtr hdc, int index);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr previous);

    [DllImport("gdi32.dll", SetLastError = true)]
    private static extern unsafe int StretchDIBits(IntPtr hdc, int x, int y, int width, int height, int sourceX, int sourceY,
        int sourceWidth, int sourceHeight, byte* bits, byte* info, uint usage, uint operation);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteDC(IntPtr hdc);

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
