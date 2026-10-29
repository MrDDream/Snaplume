using System.Collections.Specialized;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snaplume.Core;

public static class ImageIO
{
    public static readonly string[] Extensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif" };

    public static readonly string[] VideoExtensions = { ".mp4" };

    public static bool IsImage(string path) =>
        Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static bool IsVideo(string path) =>
        VideoExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    /// <summary>Files shown in the workspace: images and videos.</summary>
    public static bool IsCapture(string path) => IsImage(path) || IsVideo(path);

    /// <summary>Loads an image without locking the file, as 96 DPI Bgra32.</summary>
    public static BitmapSource Load(string path)
    {
        using var stream = new MemoryStream(File.ReadAllBytes(path));
        // Decoded on demand (while the stream is open), so the size is checked before the pixels are read.
        var decoder = BitmapDecoder.Create(stream,
            BitmapCreateOptions.PreservePixelFormat | BitmapCreateOptions.IgnoreColorProfile, BitmapCacheOption.None);
        return Normalize(decoder.Frames[0]);
    }

    /// <summary>
    /// Materializes an image as 96 DPI Bgra32 so that 1 unit = 1 pixel everywhere in the editor.
    /// </summary>
    public static BitmapSource Normalize(BitmapSource source)
    {
        CheckSize(source.PixelWidth, source.PixelHeight);
        BitmapSource converted = source.Format == PixelFormats.Bgra32
            ? source
            : new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        // Below the size limit, the stride and the buffer length fit in an int.
        int stride = converted.PixelWidth * 4;
        var buffer = new byte[(long)stride * converted.PixelHeight];
        converted.CopyPixels(buffer, stride, 0);
        var result = BitmapSource.Create(converted.PixelWidth, converted.PixelHeight, 96, 96, PixelFormats.Bgra32, null, buffer, stride);
        result.Freeze();
        return result;
    }

    /// <summary>Largest image handled (250 megapixels, 1 GB in memory as Bgra32).</summary>
    public const long MaxPixels = 250_000_000;

    /// <summary>Refuses images too big to edit, before allocating anything.</summary>
    /// <exception cref="InvalidDataException">The image exceeds <see cref="MaxPixels"/>.</exception>
    public static void CheckSize(int width, int height)
    {
        if ((long)width * height > MaxPixels)
            throw new InvalidDataException(Loc.T("App.ImageTooLarge", width, height));
    }

    /// <summary>Saves atomically; the format follows the extension. Keeps the original creation date.</summary>
    public static void Save(BitmapSource image, string path, int jpegQuality = 90)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        BitmapEncoder encoder = ext switch
        {
            ".jpg" or ".jpeg" => new JpegBitmapEncoder { QualityLevel = Math.Clamp(jpegQuality, 10, 100) },
            ".bmp" => new BmpBitmapEncoder(),
            ".gif" => new GifBitmapEncoder(),
            _ => new PngBitmapEncoder(),
        };
        // Screenshots are opaque: drop the alpha channel for smaller and more compatible files.
        encoder.Frames.Add(BitmapFrame.Create(new FormatConvertedBitmap(image, PixelFormats.Bgr24, null, 0)));

        DateTime? created = File.Exists(path) ? File.GetCreationTimeUtc(path) : null;
        var temp = path + ".tmp";
        try
        {
            using (var stream = File.Create(temp)) encoder.Save(stream);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try
            {
                File.Delete(temp);
            }
            catch (Exception)
            {
                // The original error matters more.
            }
            throw;
        }
        if (created is DateTime c) File.SetCreationTimeUtc(path, c);
    }

    /// <summary>Renders the flattened image to PNG, as a stream positioned at its start.</summary>
    public static MemoryStream ToPng(BitmapSource image)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        var stream = new MemoryStream();
        encoder.Save(stream);
        stream.Position = 0;
        return stream;
    }
}

public static class ClipboardHelper
{
    public static void SetImage(BitmapSource image)
    {
        var data = new DataObject();
        data.SetImage(new FormatConvertedBitmap(image, PixelFormats.Bgr32, null, 0));
        // PNG flavour for applications that support it (keeps exact colors).
        data.SetData("PNG", ImageIO.ToPng(image), false);
        SetWithRetry(data);
    }

    public static void SetFiles(IEnumerable<string> paths)
    {
        var files = new StringCollection();
        files.AddRange(paths.ToArray());
        var data = new DataObject();
        data.SetFileDropList(files);
        SetWithRetry(data);
    }

    public static void SetText(string text)
    {
        var data = new DataObject();
        data.SetText(text, TextDataFormat.UnicodeText);
        SetWithRetry(data);
    }

    private static void SetWithRetry(DataObject data)
    {
        // Another process may hold the clipboard open for a short time.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(data, copy: true);
                return;
            }
            catch (COMException) when (attempt < 10)
            {
                Thread.Sleep(50);
            }
        }
    }
}
