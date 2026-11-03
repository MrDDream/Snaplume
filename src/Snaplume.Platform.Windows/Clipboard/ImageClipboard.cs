using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Windows.Native;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Clipboard;

/// <summary>
/// The Windows clipboard (Win32 API), with several flavours at once (PNG + bitmap), reading those of other
/// applications (PNG, files copied in Explorer, bitmap).
/// </summary>
/// <remarks>
/// The clipboard is opened on the Avalonia UI thread, by a hidden window of that thread (a clipboard without an owner
/// window takes no data). Another process may hold the clipboard open for a moment, so it is asked again for about
/// half a second.
/// </remarks>
public sealed class ImageClipboard : IImageClipboard
{
    private const int Attempts = 10;
    private const uint CF_BITMAP = 2;
    private const uint CF_DIB = 8;
    private const uint CF_UNICODETEXT = 13;
    private const uint CF_HDROP = 15;

    /// <summary>The format Office, browsers and most editors use for images with transparency.</summary>
    private static readonly uint PngFormat = RegisterClipboardFormat("PNG");
    private HiddenWindow? _owner;

    /// <param name="png">The image already encoded (e.g. in the background); it keeps transparency and exact colours.</param>
    public async Task<bool> SetImageAsync(PixelBuffer image, byte[] png)
    {
        // Bitmap flavour for applications without PNG support (Paint, older Office): no alpha channel there, so the
        // transparent parts are put on white. Prepared off the UI thread (tens of milliseconds for a big image).
        var dib = await Task.Run(() => DibEncoder.Encode(image));
        return await OnUiAsync(() => SetAsync(new[] { (PngFormat, png), (CF_DIB, dib) }, "Copie de l'image"));
    }

    /// <summary>The PNG flavour first (it keeps transparency), then the first image file copied in Explorer, then the bitmap.</summary>
    /// <exception cref="InvalidDataException">The image is not readable or too big.</exception>
    public async Task<PixelBuffer?> GetImageAsync()
    {
        var content = await OnUiAsync(ReadAsync);
        // Decoding a big image takes a while: off the UI thread, once the clipboard is closed again.
        return content switch
        {
            byte[] png => await Task.Run(() => ImageIO.Decode(png)),
            string file => await Task.Run(() => ImageIO.Load(file)),
            PixelBuffer bitmap => bitmap,
            _ => null,
        };
    }

    public Task<bool> SetTextAsync(string text) =>
        OnUiAsync(() => SetAsync(new[] { (CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0")) }, "Copie du texte"));

    public Task<bool> SetFilesAsync(IReadOnlyList<string> paths)
    {
        // DROPFILES (20 bytes, wide names) then the full paths, each ended by a null character, and a final null.
        var names = Encoding.Unicode.GetBytes(string.Concat(paths.Select(p => Path.GetFullPath(p) + "\0")) + "\0");
        var data = new byte[20 + names.Length];
        BitConverter.TryWriteBytes(data.AsSpan(0), 20); // pFiles
        BitConverter.TryWriteBytes(data.AsSpan(16), 1); // fWide
        names.CopyTo(data, 20);
        return OnUiAsync(() => SetAsync(new[] { (CF_HDROP, data) }, "Copie des fichiers"));
    }

    /// <summary>Replaces the clipboard content by these formats, all at once.</summary>
    private async Task<bool> SetAsync((uint Format, byte[] Data)[] formats, string context)
    {
        if (!await OpenAsync(context)) return false;
        try
        {
            if (!EmptyClipboard())
            {
                ErrorLog.Write(context, new Win32Exception(Marshal.GetLastPInvokeError()));
                return false;
            }
            foreach (var (format, data) in formats)
            {
                var memory = ToGlobal(data);
                if (memory == IntPtr.Zero || SetClipboardData(format, memory) == IntPtr.Zero)
                {
                    var error = new Win32Exception(Marshal.GetLastPInvokeError());
                    // Only freed when the clipboard did not take it.
                    if (memory != IntPtr.Zero) GlobalFree(memory);
                    ErrorLog.Write(context, error);
                    return false;
                }
            }
            return true;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>The PNG bytes, the path of an image file, a bitmap read as opaque pixels, or null.</summary>
    private async Task<object?> ReadAsync()
    {
        if (!await OpenAsync("Lecture du presse-papiers")) return null;
        try
        {
            if (PngFormat != 0 && IsClipboardFormatAvailable(PngFormat) && ReadGlobal(GetClipboardData(PngFormat)) is { Length: > 0 } png)
                return png;
            if (IsClipboardFormatAvailable(CF_HDROP) && ReadFiles(GetClipboardData(CF_HDROP)).FirstOrDefault(ImageIO.IsImage) is { } file)
                return file;
            // CF_BITMAP is also offered by the system for any CF_DIB / CF_DIBV5, in every bitmap format: GDI converts it.
            if (IsClipboardFormatAvailable(CF_BITMAP) && GetClipboardData(CF_BITMAP) is var bitmap && bitmap != IntPtr.Zero)
                return ReadBitmap(bitmap);
            return null;
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Opens the clipboard for the hidden window, waiting a little while another application holds it.</summary>
    private async Task<bool> OpenAsync(string context)
    {
        try
        {
            _owner ??= new HiddenWindow("Clipboard", (_, _, _) => null, messageOnly: true);
        }
        catch (Win32Exception ex)
        {
            ErrorLog.Write(context, ex);
            return false;
        }
        for (int attempt = 1; ; attempt++)
        {
            if (OpenClipboard(_owner.Handle)) return true;
            if (attempt >= Attempts)
            {
                ErrorLog.Write(context, new Win32Exception(Marshal.GetLastPInvokeError()));
                return false;
            }
            await Task.Delay(50);
        }
    }

    private static IntPtr ToGlobal(byte[] data)
    {
        var memory = GlobalAlloc(GMEM_MOVEABLE, (UIntPtr)Math.Max(1, data.Length));
        if (memory == IntPtr.Zero) return IntPtr.Zero;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero)
        {
            GlobalFree(memory);
            return IntPtr.Zero;
        }
        Marshal.Copy(data, 0, pointer, data.Length);
        GlobalUnlock(memory);
        return memory;
    }

    private static byte[]? ReadGlobal(IntPtr memory)
    {
        if (memory == IntPtr.Zero) return null;
        var pointer = GlobalLock(memory);
        if (pointer == IntPtr.Zero) return null;
        try
        {
            var data = new byte[(int)GlobalSize(memory)];
            Marshal.Copy(pointer, data, 0, data.Length);
            return data;
        }
        finally
        {
            GlobalUnlock(memory);
        }
    }

    private static List<string> ReadFiles(IntPtr drop)
    {
        var files = new List<string>();
        if (drop == IntPtr.Zero) return files;
        uint count = DragQueryFile(drop, uint.MaxValue, null, 0);
        for (uint i = 0; i < count; i++)
        {
            var name = new StringBuilder((int)DragQueryFile(drop, i, null, 0) + 1);
            if (DragQueryFile(drop, i, name, (uint)name.Capacity) > 0) files.Add(name.ToString());
        }
        return files;
    }

    /// <summary>The bitmap flavour has no reliable alpha (often 0 everywhere): read as opaque.</summary>
    private static PixelBuffer? ReadBitmap(IntPtr bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<BITMAP>(), out var info) == 0 || info.bmWidth <= 0 || info.bmHeight == 0) return null;
        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        ImageIO.CheckSize(width, height);
        var image = new PixelBuffer(width, height);
        // Negative height: top-down rows, as PixelBuffer.
        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,
            biPlanes = 1,
            biBitCount = 32,
        };
        var screen = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(screen, bitmap, 0, (uint)height, image.Pixels, ref header, DIB_RGB_COLORS) != height) return null;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, screen);
        }
        return image.MakeOpaque();
    }

    private static Task<T> OnUiAsync<T>(Func<Task<T>> action) =>
        Dispatcher.UIThread.CheckAccess() ? action() : Dispatcher.UIThread.InvokeAsync(action);

    // ---- Win32 (the GDI and global memory functions are in NativeMethods) ----

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClipboardFormatW")]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr owner);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetClipboardData(uint format, IntPtr memory);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "DragQueryFileW")]
    private static extern uint DragQueryFile(IntPtr drop, uint index, StringBuilder? file, uint size);
}
