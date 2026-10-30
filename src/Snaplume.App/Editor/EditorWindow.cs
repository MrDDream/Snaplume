using Avalonia.Controls;

namespace Snaplume.Editor;

/// <summary>Placeholder of the image editor window; replaced by the editor work (EditorWindow.axaml).</summary>
public class EditorWindow : Window
{
    public EditorWindow(string path)
    {
        FilePath = path;
        Title = Path.GetFileName(path);
    }

    /// <summary>The capture being edited.</summary>
    public string FilePath { get; }
}
