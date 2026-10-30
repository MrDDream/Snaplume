using System.Collections.Specialized;
using System.Runtime.InteropServices;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Imaging;
using Wpf = System.Windows;
using WpfImaging = System.Windows.Media.Imaging;
using WpfMedia = System.Windows.Media;

namespace Snaplume.Platform.Windows.Clipboard;

/// <summary>
/// The Windows clipboard through WPF's OLE clipboard, which offers several flavours at once (PNG + bitmap) and reads
/// those of other applications (PNG, files copied in Explorer, DIB).
/// </summary>
/// <remarks>
/// OLE needs an STA thread: calls made elsewhere are moved to the Avalonia UI thread. Another process may hold the
/// clipboard open for a moment, so writes are retried for about half a second.
/// </remarks>
public sealed class ImageClipboard : IImageClipboard
{
    private const int Attempts = 10;

    /// <param name="png">The image already encoded (e.g. in the background); it keeps transparency and exact colours.</param>
    public Task<bool> SetImageAsync(PixelBuffer image, byte[] png) => OnStaAsync(async () =>
    {
        var data = new Wpf.DataObject();
        // Bitmap flavour for applications without PNG support (Paint, older Office): no alpha channel there.
        var source = WpfImaging.BitmapSource.Create(image.Width, image.Height, 96, 96, WpfMedia.PixelFormats.Bgra32, null,
            image.Pixels, image.Stride);
        data.SetImage(new WpfImaging.FormatConvertedBitmap(source, WpfMedia.PixelFormats.Bgr32, null, 0));
        data.SetData("PNG", new MemoryStream(png, writable: false), false);
        return await SetWithRetryAsync(data, "Copie de l'image");
    });

    /// <summary>The PNG flavour first (it keeps transparency), then the first image file copied in Explorer, then the bitmap.</summary>
    /// <exception cref="InvalidDataException">The image is not readable or too big.</exception>
    public Task<PixelBuffer?> GetImageAsync() => OnStaAsync(() =>
    {
        Wpf.IDataObject? data;
        try
        {
            data = Wpf.Clipboard.GetDataObject();
        }
        catch (COMException ex)
        {
            ErrorLog.Write("Lecture du presse-papiers", ex);
            return Task.FromResult<PixelBuffer?>(null);
        }
        if (data == null) return Task.FromResult<PixelBuffer?>(null);

        if (data.GetDataPresent("PNG") && data.GetData("PNG") is MemoryStream png)
        {
            using (png) return Task.FromResult<PixelBuffer?>(ImageIO.Decode(png.ToArray()));
        }
        if (data.GetDataPresent(Wpf.DataFormats.FileDrop) && data.GetData(Wpf.DataFormats.FileDrop) is string[] files
            && files.FirstOrDefault(ImageIO.IsImage) is { } file)
        {
            return Task.FromResult<PixelBuffer?>(ImageIO.Load(file));
        }
        if (data.GetDataPresent(Wpf.DataFormats.Bitmap) && Wpf.Clipboard.GetImage() is { } bitmap)
            return Task.FromResult<PixelBuffer?>(ToPixelBuffer(bitmap));
        return Task.FromResult<PixelBuffer?>(null);
    });

    public Task<bool> SetTextAsync(string text) => OnStaAsync(async () =>
    {
        var data = new Wpf.DataObject();
        data.SetText(text, Wpf.TextDataFormat.UnicodeText);
        return await SetWithRetryAsync(data, "Copie du texte");
    });

    public Task<bool> SetFilesAsync(IReadOnlyList<string> paths) => OnStaAsync(async () =>
    {
        var files = new StringCollection();
        files.AddRange(paths.Select(Path.GetFullPath).ToArray());
        var data = new Wpf.DataObject();
        data.SetFileDropList(files);
        return await SetWithRetryAsync(data, "Copie des fichiers");
    });

    /// <summary>The bitmap flavour has no reliable alpha (often 0 everywhere): read as opaque.</summary>
    private static PixelBuffer ToPixelBuffer(WpfImaging.BitmapSource bitmap)
    {
        ImageIO.CheckSize(bitmap.PixelWidth, bitmap.PixelHeight);
        var opaque = new WpfImaging.FormatConvertedBitmap(bitmap, WpfMedia.PixelFormats.Bgr32, null, 0);
        var image = new PixelBuffer(opaque.PixelWidth, opaque.PixelHeight);
        opaque.CopyPixels(image.Pixels, image.Stride, 0);
        return image.MakeOpaque();
    }

    private static async Task<bool> SetWithRetryAsync(Wpf.DataObject data, string context)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                Wpf.Clipboard.SetDataObject(data, copy: true);
                return true;
            }
            catch (COMException ex)
            {
                if (attempt >= Attempts)
                {
                    ErrorLog.Write(context, ex);
                    return false;
                }
            }
            catch (Exception ex) when (ex is ExternalException or InvalidOperationException)
            {
                ErrorLog.Write(context, ex);
                return false;
            }
            await Task.Delay(50);
        }
    }

    private static Task<T> OnStaAsync<T>(Func<Task<T>> action)
    {
        if (Thread.CurrentThread.GetApartmentState() == ApartmentState.STA) return action();
        return Dispatcher.UIThread.InvokeAsync(action);
    }
}
