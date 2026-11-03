using System.Runtime.InteropServices;
using Snaplume.Imaging;
using Snaplume.Platform.Windows.Native;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Video;

/// <summary>
/// Thumbnails from the Windows shell (the ones Explorer shows, e.g. a video frame). Without
/// SIIGBF_THUMBNAILONLY the shell falls back to the file type's icon when it has no thumbnail
/// (e.g. no video codec), which is fine for the gallery.
/// </summary>
internal static class ShellThumbnail
{
    private const int SIIGBF_BIGGERSIZEOK = 0x01;

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx, cy;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        string path, IntPtr bindContext, [MarshalAs(UnmanagedType.LPStruct)] Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

    /// <returns>The thumbnail (or the file type's icon), straight alpha; null if the shell failed.</returns>
    public static PixelBuffer? Get(string path, int size)
    {
        IShellItemImageFactory? factory = null;
        IntPtr hBitmap = IntPtr.Zero;
        try
        {
            SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
            if (factory.GetImage(new SIZE { cx = size, cy = size }, SIIGBF_BIGGERSIZEOK, out hBitmap) != 0) return null;
            return ToPixelBuffer(hBitmap);
        }
        catch (Exception)
        {
            // Missing file, no thumbnail handler...: the caller shows a placeholder.
            return null;
        }
        finally
        {
            if (hBitmap != IntPtr.Zero) NativeMethods.DeleteObject(hBitmap);
            if (factory != null) Marshal.ReleaseComObject(factory);
        }
    }

    /// <summary>Copies a 32-bit shell bitmap (premultiplied, or with no alpha at all) into a straight BGRA buffer.</summary>
    private static PixelBuffer? ToPixelBuffer(IntPtr hBitmap)
    {
        if (GetObject(hBitmap, Marshal.SizeOf<BITMAP>(), out var bm) == 0 || bm.bmWidth <= 0 || bm.bmHeight == 0) return null;
        int width = bm.bmWidth, height = Math.Abs(bm.bmHeight);
        var image = new PixelBuffer(width, height);
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height, // top-down rows
            biPlanes = 1,
            biBitCount = 32,
        };
        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hBitmap, 0, (uint)height, image.Pixels, ref header, DIB_RGB_COLORS) != height) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        var pixels = image.Pixels;
        bool hasAlpha = false;
        for (int i = 3; i < pixels.Length && !hasAlpha; i += 4) hasAlpha = pixels[i] != 0;
        if (!hasAlpha) return image.MakeOpaque(); // e.g. a video frame: the 4th byte is unused

        for (int i = 0; i < pixels.Length; i += 4)
        {
            int a = pixels[i + 3];
            if (a is 0 or 255) continue;
            pixels[i] = (byte)Math.Min(255, pixels[i] * 255 / a);
            pixels[i + 1] = (byte)Math.Min(255, pixels[i + 1] * 255 / a);
            pixels[i + 2] = (byte)Math.Min(255, pixels[i + 2] * 255 / a);
        }
        return image;
    }
}
