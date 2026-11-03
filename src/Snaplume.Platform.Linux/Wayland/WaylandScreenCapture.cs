using Snaplume.Capturing;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Util;
using Snaplume.Platform.Linux.X11;
using Tmds.DBus.Protocol;

namespace Snaplume.Platform.Linux.Wayland;

/// <summary>
/// Screen capture under Wayland, where no client may read the screen: the Screenshot portal takes the picture
/// (non-interactive; the desktop asks the user once) and writes it to a file. Monitors come from XWayland's
/// XRandR, in logical pixels, scaled to the size of the picture.
/// </summary>
internal sealed class WaylandScreenCapture : IScreenCapture
{
    private readonly X11Connection? _xwayland;
    private IReadOnlyList<MonitorInfo>? _lastMonitors;

    public WaylandScreenCapture(X11Connection? xwayland) => _xwayland = xwayland;

    /// <summary>Picture pixels per logical pixel, from the last capture (1 until then).</summary>
    public double LastScale { get; private set; } = 1;

    public IReadOnlyList<MonitorInfo> Monitors => _lastMonitors ?? LogicalMonitors() ?? new[] { new MonitorInfo(new PixelRect(0, 0, 1920, 1080), 1, true, "Screen") };

    public async Task<DesktopImage> CaptureDesktopAsync(bool includeCursor, CancellationToken cancel = default)
    {
        // The portal draws no pointer in non-interactive screenshots: includeCursor cannot be honoured.
        var image = await TakeScreenshotAsync(cancel).ConfigureAwait(false);
        var bounds = new PixelRect(0, 0, image.Width, image.Height);
        var logical = LogicalMonitors();
        var monitors = logical == null
            ? new[] { new MonitorInfo(bounds, 1, true, "Screen") }
            : ScaleMonitors(logical, image.Width, image.Height);
        if (logical != null)
        {
            var union = logical.Select(m => m.Bounds).Aggregate((a, b) => a.Union(b));
            LastScale = union.Width > 0 ? (double)image.Width / union.Width : 1;
        }
        _lastMonitors = monitors;
        return new DesktopImage(image, bounds, monitors, null);
    }

    public IScreenGrabber CreateGrabber(PixelRect area) => new Grabber(area);

    /// <summary>
    /// One screenshot through the portal, decoded; the portal's file is then deleted when it is a temporary one.
    /// A file that could not be read is left where it is (it may not be a picture of Snaplume's request).
    /// </summary>
    /// <exception cref="OperationCanceledException">The user refused.</exception>
    public static async Task<PixelBuffer> TakeScreenshotAsync(CancellationToken cancel)
    {
        var requested = DateTime.UtcNow;
        var results = await Portal.RequestAsync(Portal.ScreenshotInterface, "Screenshot",
            token => new DArg[] { new DString(""), new DOptions(Portal.Options(token, ("interactive", VariantValue.Bool(false)))) },
            cancel).ConfigureAwait(false);
        var path = FileUris.ToPath(results.GetString("uri")) ?? throw new InvalidOperationException("Screenshot portal: no local file");
        var image = ImageIO.Load(path);
        if (ShouldDelete(path, requested, Path.GetTempPath(), Xdg.RuntimeDir) && IsRegularFile(path))
        {
            try
            {
                File.Delete(path);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Suppression de la capture du portail", ex);
            }
        }
        return image;
    }

    /// <summary>A plain file: not a link (which another user could have put in a shared temporary folder), not a folder.</summary>
    internal static bool IsRegularFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.LinkTarget == null && (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The portal's file is Snaplume's to delete when it lies in a temporary folder, or when it is the
    /// "Screenshot…" file a desktop (GNOME) saved in the pictures folder for this very request: otherwise every
    /// step of a scrolling capture would leave a picture behind.
    /// </summary>
    internal static bool ShouldDelete(string path, DateTime requestedUtc, string tempFolder, string? runtimeFolder)
    {
        // Unix paths compared as text: the portal returns absolute, normalised paths.
        if (!path.StartsWith('/') || path.Contains("/../", StringComparison.Ordinal)) return false;
        var full = path;
        bool Under(string? folder) => !string.IsNullOrEmpty(folder) && folder.StartsWith('/')
            && full.StartsWith(folder.TrimEnd('/') + "/", StringComparison.Ordinal);
        if (Under("/tmp") || Under("/var/tmp") || Under(tempFolder) || Under(runtimeFolder)) return true;
        if (!full[(full.LastIndexOf('/') + 1)..].StartsWith("Screenshot", StringComparison.OrdinalIgnoreCase)) return false;
        try
        {
            return File.Exists(full) && File.GetLastWriteTimeUtc(full) >= requestedUtc.AddSeconds(-2);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private IReadOnlyList<MonitorInfo>? LogicalMonitors() =>
        _xwayland == null ? null : X11ScreenCapture.ReadMonitors(_xwayland);

    /// <summary>
    /// Monitors in picture pixels: XWayland reports logical positions, the portal returns physical pixels, so
    /// everything is stretched by the ratio between the picture and the logical desktop.
    /// </summary>
    internal static IReadOnlyList<MonitorInfo> ScaleMonitors(IReadOnlyList<MonitorInfo> logical, int imageWidth, int imageHeight)
    {
        if (logical.Count == 0) return new[] { new MonitorInfo(new PixelRect(0, 0, imageWidth, imageHeight), 1, true, "Screen") };
        var union = logical.Select(m => m.Bounds).Aggregate((a, b) => a.Union(b));
        double sx = (double)imageWidth / union.Width, sy = (double)imageHeight / union.Height;
        return logical.Select(m =>
        {
            int left = (int)Math.Round((m.Bounds.X - union.X) * sx), top = (int)Math.Round((m.Bounds.Y - union.Y) * sy);
            int right = (int)Math.Round((m.Bounds.Right - union.X) * sx), bottom = (int)Math.Round((m.Bounds.Bottom - union.Y) * sy);
            return m with { Bounds = PixelRect.FromLTRB(left, top, Math.Min(right, imageWidth), Math.Min(bottom, imageHeight)), Scale = Math.Round(sx, 2) };
        }).ToList();
    }

    /// <summary>Scrolling capture under Wayland: a full screenshot per step, cropped. Slow, but the only way.</summary>
    private sealed class Grabber(PixelRect area) : IScreenGrabber
    {
        public PixelRect Area { get; } = area;

        public async Task GrabAsync(byte[] target, CancellationToken cancel = default)
        {
            var image = await TakeScreenshotAsync(cancel).ConfigureAwait(false);
            var crop = image.Crop(Area.X, Area.Y, Area.Width, Area.Height);
            Array.Clear(target);
            for (int y = 0; y < crop.Height; y++)
                Buffer.BlockCopy(crop.Pixels, y * crop.Stride, target, y * Area.Width * 4, crop.Stride);
        }

        public void Dispose()
        {
        }
    }
}
