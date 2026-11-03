namespace Snaplume.Platform;

/// <summary>
/// Optional capability of an <see cref="IVideoPlayer"/>: playback speed. <see cref="IVideoPlayer"/> itself has no
/// speed, so the player window offers the speed controls (button, [ ] { } and Backspace keys) only when the platform's
/// player also implements this interface.
/// </summary>
public interface IVideoPlayerSpeed
{
    /// <summary>1 = normal speed; the player window keeps it between 0.1 and 8.</summary>
    double Speed { get; set; }
}
