using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Snaplume.Core;
using Snaplume.Imaging;

namespace Snaplume.Editor;

/// <summary>New size of the image, in pixels or in percent; checked against the limits before closing.</summary>
public partial class ResizeDialog : Window
{
    private static readonly int[] Percents = { 25, 50, 75, 150, 200 };

    private readonly EditorDocument? _document;
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private bool _syncing;
    private bool _accepted;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }

    /// <summary>For the XAML previewer only.</summary>
    public ResizeDialog() => InitializeComponent();

    public ResizeDialog(EditorDocument document)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _document = document;
        int width = document.BaseImage.Width, height = document.BaseImage.Height;
        _originalWidth = width;
        _originalHeight = height;
        CurrentSizeText.Text = Loc.T("Resize.CurrentSize", width, height);

        foreach (var percent in Percents)
        {
            var button = new Button
            {
                Content = (percent / 100.0).ToString("P0", Loc.Culture),
                Padding = new Thickness(10, 3, 10, 3),
                Margin = new Thickness(0, 0, 6, 6),
            };
            button.Click += (_, _) =>
            {
                _syncing = true;
                WidthBox.Text = Math.Max(1, (int)Math.Round(_originalWidth * percent / 100.0)).ToString(CultureInfo.InvariantCulture);
                HeightBox.Text = Math.Max(1, (int)Math.Round(_originalHeight * percent / 100.0)).ToString(CultureInfo.InvariantCulture);
                _syncing = false;
            };
            PercentButtons.Children.Add(button);
        }

        _syncing = true;
        WidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
        WidthBox.TextChanged += OnWidthChanged;
        HeightBox.TextChanged += OnHeightChanged;
        OkButton.Click += OnOk;
        CancelButton.Click += (_, _) => Close();
        Opened += (_, _) =>
        {
            WidthBox.Focus();
            WidthBox.SelectAll();
        };
    }

    /// <summary>Shows the dialog; true when a valid size was chosen (<see cref="NewWidth"/>, <see cref="NewHeight"/>).</summary>
    public async Task<bool> ShowAsync(Window owner)
    {
        await ShowDialog(owner);
        return _accepted;
    }

    private static bool TryRead(TextBox box, out int value) =>
        int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.CurrentCulture, out value)
        || int.TryParse(box.Text?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private void OnWidthChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing || KeepRatioCheck.IsChecked != true || !TryRead(WidthBox, out var w) || w <= 0) return;
        _syncing = true;
        HeightBox.Text = Math.Max(1, (int)Math.Round((double)w * _originalHeight / _originalWidth)).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void OnHeightChanged(object? sender, TextChangedEventArgs e)
    {
        if (_syncing || KeepRatioCheck.IsChecked != true || !TryRead(HeightBox, out var h) || h <= 0) return;
        _syncing = true;
        WidthBox.Text = Math.Max(1, (int)Math.Round((double)h * _originalWidth / _originalHeight)).ToString(CultureInfo.InvariantCulture);
        _syncing = false;
    }

    private void OnOk(object? sender, RoutedEventArgs e)
    {
        if (_document == null) return;
        if (!TryRead(WidthBox, out var w) || !TryRead(HeightBox, out var h) ||
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
        _accepted = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
