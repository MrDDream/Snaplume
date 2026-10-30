using Avalonia.Controls;

namespace Snaplume.Player;

/// <summary>Placeholder of the video player window; replaced by the recording work (PlayerWindow.axaml).</summary>
public class PlayerWindow : Window
{
    public PlayerWindow(string path)
    {
        FilePath = path;
        Title = Path.GetFileName(path);
    }

    /// <summary>The video being played.</summary>
    public string FilePath { get; }
}
