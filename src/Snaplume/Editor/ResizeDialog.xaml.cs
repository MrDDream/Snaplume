using System.Windows;
using System.Windows.Controls;
using Snaplume.Core;

namespace Snaplume.Editor;

public partial class ResizeDialog : Window
{
    private static readonly int[] Percents = { 25, 50, 75, 150, 200 };

    private readonly EditorDocument _document;
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private bool _syncing;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }

    public ResizeDialog(Window owner, EditorDocument document)
    {
        InitializeComponent();
        Owner = owner;
        _document = document;
        int width = document.BaseImage.PixelWidth, height = document.BaseImage.PixelHeight;
        _originalWidth = width;
        _originalHeight = height;
        CurrentSizeText.Text = Loc.T("Resize.CurrentSize", width, height);

        foreach (var percent in Percents)
        {
            var button = new Button { Content = (percent / 100.0).ToString("P0", Loc.Culture), Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 6, 6) };
            button.Click += (_, _) =>
            {
                _syncing = true;
                WidthBox.Text = Math.Max(1, (int)Math.Round(_originalWidth * percent / 100.0)).ToString();
                HeightBox.Text = Math.Max(1, (int)Math.Round(_originalHeight * percent / 100.0)).ToString();
                _syncing = false;
            };
            PercentButtons.Children.Add(button);
        }

        _syncing = true;
        WidthBox.Text = width.ToString();
        HeightBox.Text = height.ToString();
        _syncing = false;
        Loaded += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    private void OnWidthChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || KeepRatioCheck.IsChecked != true || !int.TryParse(WidthBox.Text, out var w) || w <= 0) return;
        _syncing = true;
        HeightBox.Text = Math.Max(1, (int)Math.Round((double)w * _originalHeight / _originalWidth)).ToString();
        _syncing = false;
    }

    private void OnHeightChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing || KeepRatioCheck.IsChecked != true || !int.TryParse(HeightBox.Text, out var h) || h <= 0) return;
        _syncing = true;
        WidthBox.Text = Math.Max(1, (int)Math.Round((double)h * _originalWidth / _originalHeight)).ToString();
        _syncing = false;
    }

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(WidthBox.Text, out var w) || !int.TryParse(HeightBox.Text, out var h) ||
            w < 1 || h < 1 || w > EditorDocument.MaxDimension || h > EditorDocument.MaxDimension)
        {
            ShowError(Loc.T("Resize.Error", 1, EditorDocument.MaxDimension));
            return;
        }
        if ((long)w * h > ImageIO.MaxPixels)
        {
            ShowError(Loc.T("Resize.TooManyPixels", ImageIO.MaxPixels / 1_000_000));
            return;
        }
        // The saved image also has the margins of the shadow, whose size follows the image.
        var (outputWidth, outputHeight) = EditorDocument.OutputSizeOf(w, h, _document.ResizedEffects(w, h));
        if ((long)outputWidth * outputHeight > ImageIO.MaxPixels)
        {
            ShowError(Loc.T("Resize.TooManyPixelsWithShadow", ImageIO.MaxPixels / 1_000_000));
            return;
        }
        NewWidth = w;
        NewHeight = h;
        DialogResult = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
