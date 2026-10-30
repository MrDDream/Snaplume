using Avalonia.Controls;
using Snaplume.Imaging;

namespace Snaplume.Core;

/// <summary>Placeholder with the public members of the capture service; replaced by the capture interface work.</summary>
public sealed class CaptureService
{
    public CaptureService(App app)
    {
    }

    public event Action<string>? CaptureSaved;

    /// <summary>True while a capture (or a text or code capture) is in progress.</summary>
    public bool IsBusy => false;

    /// <summary>Announces a file added to the workspace by another feature (e.g. a video).</summary>
    public void NotifySaved(string path) => CaptureSaved?.Invoke(path);

    /// <summary>Ends the capture in progress as soon as possible, then runs the action once the service is idle.</summary>
    public void StopThen(Action action) => action();

    /// <param name="hide">Window to hide during the capture (e.g. the workspace).</param>
    /// <param name="quiet">Only save (and copy): no editor, no workspace, no notification.</param>
    /// <returns>Path of the saved file, or null if cancelled.</returns>
    public Task<string?> CaptureAsync(CaptureKind mode, int delaySeconds = 0, Window? hide = null, int settleMs = 0, bool quiet = false) =>
        Task.FromResult<string?>(null);

    /// <summary>Select an area of the screen and read its text (OCR); nothing is saved in the workspace.</summary>
    public Task<Window?> CaptureTextAsync(Window? hide = null, int settleMs = 0) => Task.FromResult<Window?>(null);

    /// <summary>Select an area of the screen and read the QR codes and barcodes in it.</summary>
    public Task<Window?> CaptureQrAsync(Window? hide = null, int settleMs = 0) => Task.FromResult<Window?>(null);

    /// <summary>Select an area of the screen and return its image (to insert it into a capture being edited).</summary>
    public Task<PixelBuffer?> PickImageAsync(Window? hide) => Task.FromResult<PixelBuffer?>(null);

    /// <summary>Date and time for file names, identical in every language.</summary>
    internal static string TimeStamp() => DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture);
}
