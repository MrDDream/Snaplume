using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media.Imaging;

namespace Snaplume.Workspace;

/// <summary>
/// One capture of the gallery. The same instance is kept while the file exists, so that the
/// selection survives refreshes; its file information is updated in place.
/// </summary>
public sealed class CaptureItem : INotifyPropertyChanged
{
    private BitmapSource? _thumbnail;
    private string _dimensions = "";

    public CaptureItem(string path)
    {
        Path = path;
        IsVideo = Core.ImageIO.IsVideo(path);
    }

    public string Path { get; }
    public string Name => System.IO.Path.GetFileName(Path);
    public bool IsVideo { get; }
    public DateTime Created { get; private set; }
    /// <summary>Used to detect that the thumbnail must be reloaded.</summary>
    public DateTime Modified { get; private set; }
    public long Length { get; private set; }

    /// <summary>Failed attempts to read the thumbnail (file locked by another program).</summary>
    internal int ThumbnailFailures { get; set; }

    /// <summary>Takes the current file information.</summary>
    /// <returns>True when the content changed: the thumbnail must be reloaded.</returns>
    public bool Update(DateTime created, DateTime modifiedUtc, long length)
    {
        bool contentChanged = modifiedUtc != Modified || length != Length;
        bool changed = contentChanged || created != Created;
        Created = created;
        Modified = modifiedUtc;
        Length = length;
        if (changed) OnPropertyChanged(nameof(Details));
        return contentChanged;
    }

    public string Details
    {
        get
        {
            var date = Created.ToString("g", Core.Loc.Culture);
            if (IsVideo) return $"{date}  •  {Core.Loc.T("Gallery.VideoSize", Length / (1024.0 * 1024))}";
            return string.IsNullOrEmpty(_dimensions) ? date : $"{date}  •  {_dimensions}";
        }
    }

    public BitmapSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            OnPropertyChanged();
        }
    }

    public string Dimensions
    {
        get => _dimensions;
        set
        {
            _dimensions = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Details));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
