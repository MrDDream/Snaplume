using Avalonia.Controls;

namespace Snaplume.Recording;

/// <summary>Placeholder with the public members of the video service; replaced by the recording work.</summary>
public sealed class VideoService
{
    public VideoService(App app)
    {
    }

    /// <summary>True while an area is being chosen or a video is being recorded.</summary>
    public bool IsBusy => false;

    /// <summary>Recording started, paused, resumed or stopped.</summary>
    public event Action? StateChanged;

    /// <summary>Starts a recording (area selection first, unless <paramref name="fullScreen"/>), or stops the one in progress.</summary>
    /// <returns>Path of the saved video, or null.</returns>
    public Task<string?> ToggleAsync(bool fullScreen = false, Window? hide = null, int settleMs = 0, int durationSeconds = 0)
    {
        StateChanged?.Invoke();
        return Task.FromResult<string?>(null);
    }

    public void Stop()
    {
    }

    public void Cancel()
    {
    }

    public void TogglePause()
    {
    }

    /// <summary>Stops the recording in progress (the video is kept), then runs the action.</summary>
    public void StopThen(Action action) => action();

    /// <summary>Deletes temporary videos left by a crash (older than a day).</summary>
    public static void DeleteStaleTempFiles()
    {
    }
}
