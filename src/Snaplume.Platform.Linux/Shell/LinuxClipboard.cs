using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// Clipboard. Avalonia 12 writes and reads any MIME type ("platform formats"), so PNG goes through it under X11.
/// Under Wayland, Avalonia is an XWayland client and the compositor only syncs its selection while one of its
/// windows has the focus, which a capture taken from a global shortcut does not have: wl-copy / wl-paste
/// (wl-clipboard) go first there. xclip is the last resort when no Avalonia window exists yet.
/// </summary>
internal sealed class LinuxClipboard : IImageClipboard
{
    private static readonly DataFormat<byte[]> Png = DataFormat.CreateBytesPlatformFormat("image/png");
    private static readonly DataFormat<byte[]> UriList = DataFormat.CreateBytesPlatformFormat("text/uri-list");
    private static readonly DataFormat<byte[]> GnomeCopiedFiles = DataFormat.CreateBytesPlatformFormat("x-special/gnome-copied-files");
    private static readonly string[] ImageTypes = { "image/png", "image/jpeg", "image/bmp", "image/gif" };
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(5);

    private readonly bool _wayland;

    public LinuxClipboard(bool wayland) => _wayland = wayland;

    public async Task<bool> SetImageAsync(PixelBuffer image, byte[] png)
    {
        if (_wayland && await ToolCopyAsync(wayland: true, "image/png", png).ConfigureAwait(false)) return true;
        if (await WithAvaloniaAsync(async clipboard =>
            {
                var item = new DataTransferItem();
                item.Set(Png, png);
                var transfer = new DataTransfer();
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
                return true;
            }).ConfigureAwait(false)) return true;
        return await ToolCopyAsync(wayland: false, "image/png", png).ConfigureAwait(false);
    }

    public async Task<PixelBuffer?> GetImageAsync()
    {
        try
        {
            if (_wayland && ExternalTools.Find("wl-paste") is { } wlPaste)
            {
                var types = await ExternalTools.RunAsync(wlPaste, new[] { "--list-types" }, ToolTimeout).ConfigureAwait(false);
                var offered = types.Succeeded ? types.Text.Split('\n', StringSplitOptions.TrimEntries) : Array.Empty<string>();
                if (ImageTypes.FirstOrDefault(offered.Contains) is { } type)
                {
                    var data = await ExternalTools.RunAsync(wlPaste, new[] { "--no-newline", "--type", type }, ToolTimeout).ConfigureAwait(false);
                    if (data.Succeeded && data.Output.Length > 0) return ImageIO.Decode(data.Output);
                }
                if (offered.Contains("text/uri-list"))
                {
                    var data = await ExternalTools.RunAsync(wlPaste, new[] { "--no-newline", "--type", "text/uri-list" }, ToolTimeout).ConfigureAwait(false);
                    if (FirstImageFile(FileUris.ParseUriList(data.Text)) is { } file) return ImageIO.Load(file);
                }
            }

            var fromAvalonia = await WithAvaloniaAsync<PixelBuffer?>(async clipboard =>
            {
                if (await clipboard.TryGetValueAsync(Png) is { Length: > 0 } bytes) return ImageIO.Decode(bytes);
                if (await clipboard.TryGetFilesAsync() is { } items
                    && FirstImageFile(items.Select(i => i.TryGetLocalPath()).OfType<string>()) is { } file)
                    return ImageIO.Load(file);
                if (await clipboard.TryGetBitmapAsync() is { } bitmap)
                {
                    using var stream = new MemoryStream();
#pragma warning disable CS0618 // The replacement needs encoder options Avalonia offers for JPEG only; PNG is the default here.
                    bitmap.Save(stream);
#pragma warning restore CS0618
                    return ImageIO.Decode(stream.ToArray());
                }
                return null;
            }).ConfigureAwait(false);
            if (fromAvalonia != null) return fromAvalonia;

            if (!_wayland && ExternalTools.Find("xclip") is { } xclip)
            {
                var data = await ExternalTools.RunAsync(xclip, new[] { "-selection", "clipboard", "-t", "image/png", "-o" }, ToolTimeout).ConfigureAwait(false);
                if (data.Succeeded && data.Output.Length > 0) return ImageIO.Decode(data.Output);
            }
        }
        catch (Exception ex) when (ex is InvalidDataException or TimeoutException or IOException or InvalidOperationException)
        {
            ErrorLog.Write("Lecture de l'image du presse-papiers", ex);
        }
        return null;
    }

    public async Task<bool> SetTextAsync(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (_wayland && await ToolCopyAsync(wayland: true, "text/plain;charset=utf-8", bytes).ConfigureAwait(false)) return true;
        if (await WithAvaloniaAsync(async clipboard =>
            {
                await clipboard.SetTextAsync(text);
                return true;
            }).ConfigureAwait(false)) return true;
        return await ToolCopyAsync(wayland: false, "UTF8_STRING", bytes).ConfigureAwait(false);
    }

    /// <summary>
    /// Files as the file managers paste them: text/uri-list, plus GNOME's own list (Nautilus, Nemo, Caja need it
    /// to paste files rather than their paths as text).
    /// </summary>
    public async Task<bool> SetFilesAsync(IReadOnlyList<string> paths)
    {
        var uriList = Encoding.UTF8.GetBytes(FileUris.UriList(paths));
        var gnome = Encoding.UTF8.GetBytes("copy\n" + string.Join("\n", paths.Select(FileUris.FromPath)));
        if (_wayland && await ToolCopyAsync(wayland: true, "text/uri-list", uriList).ConfigureAwait(false)) return true;
        if (await WithAvaloniaAsync(async clipboard =>
            {
                var item = new DataTransferItem();
                item.Set(UriList, uriList);
                item.Set(GnomeCopiedFiles, gnome);
                var transfer = new DataTransfer();
                transfer.Add(item);
                await clipboard.SetDataAsync(transfer);
                return true;
            }).ConfigureAwait(false)) return true;
        return await ToolCopyAsync(wayland: false, "text/uri-list", uriList).ConfigureAwait(false);
    }

    private static string? FirstImageFile(IEnumerable<string> paths) => paths.FirstOrDefault(p => ImageIO.IsImage(p) && File.Exists(p));

    /// <summary>Runs on the UI thread with the clipboard of an open Avalonia window; default(T) when there is none.</summary>
    private static async Task<T> WithAvaloniaAsync<T>(Func<IClipboard, Task<T>> action)
    {
        try
        {
            return await Dispatcher.UIThread.InvokeAsync(async () =>
            {
                var clipboard = AvaloniaClipboard();
                return clipboard == null ? default! : await action(clipboard);
            });
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Presse-papiers Avalonia", ex);
            return default!;
        }
    }

    private static IClipboard? AvaloniaClipboard() =>
        Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop
            ? desktop.Windows.Select(w => TopLevel.GetTopLevel(w)?.Clipboard).FirstOrDefault(c => c != null)
            : null;

    /// <summary>
    /// Copies with wl-copy (Wayland) or xclip (X11), which stay in the background to serve the data, like a
    /// clipboard owner. False when the tool is missing or failed.
    /// </summary>
    private static async Task<bool> ToolCopyAsync(bool wayland, string type, byte[] data)
    {
        var (tool, arguments) = wayland
            ? ("wl-copy", new[] { "--type", type })
            : ("xclip", new[] { "-selection", "clipboard", "-t", type, "-i" });
        if (ExternalTools.Find(tool) is not { } path) return false;
        try
        {
            return await ExternalTools.LaunchAsync(path, arguments, ToolTimeout, data).ConfigureAwait(false) == 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Presse-papiers ({tool})", ex);
            return false;
        }
    }
}
