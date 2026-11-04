using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Shapes;
using Avalonia.Interactivity;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Snaplume.Core;
using Snaplume.Imaging;
using IconPath = Avalonia.Controls.Shapes.Path;

namespace Snaplume.Editor;

/// <summary>
/// New size of the image, in pixels or in percent: the image stretched to it, or the canvas changed around the image
/// (space added or cut off, like "Canvas size" in Paint) with the image at an anchor and a background for the new
/// parts. Checked against the limits before closing.
/// </summary>
public partial class ResizeDialog : Window
{
    private static readonly int[] Percents = { 25, 50, 75, 150, 200 };

    /// <summary>Backgrounds offered for the canvas; the last swatch picks any other color.</summary>
    private static readonly (Color Color, string Name)[] Backgrounds =
    {
        (Colors.White, Loc.T("Editor.Color.White")),
        (Colors.Transparent, Loc.T("Effects.Transparent")),
        (Colors.Black, Loc.T("Editor.Color.Black")),
    };

    /// <summary>The colors of the custom background swatch until a color is picked.</summary>
    private static readonly IBrush Rainbow = new LinearGradientBrush
    {
        StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
        EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
        GradientStops =
        {
            new GradientStop(Color.FromRgb(0xE5, 0x39, 0x35), 0), new GradientStop(Color.FromRgb(0xFD, 0xD8, 0x35), 0.35),
            new GradientStop(Color.FromRgb(0x43, 0xA0, 0x47), 0.65), new GradientStop(Color.FromRgb(0x1E, 0x88, 0xE5), 1),
        },
    }.ToImmutable();

    private readonly EditorDocument? _document;
    private readonly int _originalWidth;
    private readonly int _originalHeight;
    private readonly List<ToggleButton> _anchorButtons = new();
    private readonly List<Button> _backgroundButtons = new();
    private Button? _customButton;
    /// <summary>The color picked for the custom swatch, null until one is.</summary>
    private Color? _customColor;
    private bool _syncing;
    private bool _accepted;

    public int NewWidth { get; private set; }
    public int NewHeight { get; private set; }
    /// <summary>The canvas changes size around the image (<see cref="Anchor"/>, <see cref="CanvasBackground"/>), instead of the image being stretched.</summary>
    public bool ExtendsCanvas { get; private set; }
    /// <summary>Where the image stays on the new canvas.</summary>
    public CanvasAnchor Anchor { get; private set; } = CanvasAnchor.Center;
    /// <summary>Color of the space added around the image; remembered for the next time (see <see cref="AppSettings.EditorCanvasBackground"/>).</summary>
    public Color CanvasBackground { get; private set; }

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
        WidthBox.Text = CanvasWidthBox.Text = width.ToString(CultureInfo.InvariantCulture);
        HeightBox.Text = CanvasHeightBox.Text = height.ToString(CultureInfo.InvariantCulture);
        _syncing = false;
        BuildAnchorGrid();
        BuildBackgroundSwatches(CanvasSize.BackgroundFrom(AppServices.Settings.EditorCanvasBackground));

        WidthBox.TextChanged += OnWidthChanged;
        HeightBox.TextChanged += OnHeightChanged;
        CanvasModeButton.IsCheckedChanged += (_, _) => ShowMode();
        OkButton.Click += OnOk;
        CancelButton.Click += (_, _) => Close();
        Opened += (_, _) => FocusWidth();
    }

    /// <summary>Shows the dialog; true when a valid size was chosen (<see cref="NewWidth"/>, <see cref="NewHeight"/>).</summary>
    public async Task<bool> ShowAsync(Window owner)
    {
        await ShowDialog(owner);
        return _accepted;
    }

    private bool CanvasMode => CanvasModeButton.IsChecked == true;

    private void ShowMode()
    {
        StretchPanel.IsVisible = !CanvasMode;
        CanvasPanel.IsVisible = CanvasMode;
        ErrorText.IsVisible = false;
        FocusWidth();
    }

    private void FocusWidth()
    {
        var box = CanvasMode ? CanvasWidthBox : WidthBox;
        box.Focus();
        box.SelectAll();
    }

    // ---- Canvas size ----

    /// <summary>Nine buttons laid out as the image is: the chosen one shows the image, its neighbours where the space goes.</summary>
    private void BuildAnchorGrid()
    {
        foreach (var anchor in Enum.GetValues<CanvasAnchor>())
        {
            var button = new ToggleButton { Tag = anchor };
            button.Classes.Add("anchor");
            ToolTip.SetTip(button, Loc.T("Canvas.AnchorTip"));
            Grid.SetColumn(button, (int)anchor % 3);
            Grid.SetRow(button, (int)anchor / 3);
            button.Click += (_, _) => SetAnchor(anchor);
            _anchorButtons.Add(button);
            AnchorGrid.Children.Add(button);
        }
        SetAnchor(Anchor);
    }

    private void SetAnchor(CanvasAnchor anchor)
    {
        Anchor = anchor;
        int column = (int)anchor % 3, row = (int)anchor / 3;
        foreach (var button in _anchorButtons)
        {
            var cell = (CanvasAnchor)button.Tag!;
            int dx = (int)cell % 3 - column, dy = (int)cell / 3 - row;
            button.IsChecked = cell == anchor;
            button.Content = cell == anchor ? ImageMark() : Math.Abs(dx) <= 1 && Math.Abs(dy) <= 1 ? Arrow(dx, dy) : null;
        }
    }

    /// <summary>The image, in the cell of the anchor (checked: on the accent color).</summary>
    private static Control ImageMark()
    {
        var mark = new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(2) };
        mark[!Border.BackgroundProperty] = new DynamicResourceExtension("OnAccentBrush");
        return mark;
    }

    /// <summary>An arrow from the image towards this cell: the space goes that way.</summary>
    private static Control Arrow(int dx, int dy)
    {
        var arrow = new IconPath
        {
            Data = Geometry.Parse("M1,6 H11 M7,2 L11,6 L7,10"),
            Width = 12,
            Height = 12,
            StrokeThickness = 1.6,
            StrokeLineCap = PenLineCap.Round,
            StrokeJoin = PenLineJoin.Round,
            RenderTransform = new RotateTransform(Math.Atan2(dy, dx) * 180 / Math.PI),
        };
        arrow[!Shape.StrokeProperty] = new DynamicResourceExtension("IconBrush");
        return arrow;
    }

    /// <summary>White, transparent and black swatches, then a round one for any other color.</summary>
    private void BuildBackgroundSwatches(Color current)
    {
        foreach (var (color, name) in Backgrounds)
        {
            var swatch = Swatch(EditorWindow.ColorBrush(color), name);
            swatch.Click += (_, _) => SetBackground(color);
            _backgroundButtons.Add(swatch);
        }
        _customButton = Swatch(Rainbow, Loc.T("Canvas.CustomColor"));
        _customButton.CornerRadius = new CornerRadius(12);
        _customButton.Click += async (_, _) =>
        {
            if (await ColorPickerDialog.PickAsync(this, _customColor ?? CanvasBackground) is Color color) SetBackground(color);
        };
        foreach (var button in _backgroundButtons.Append(_customButton)) BackgroundSwatches.Children.Add(button);
        SetBackground(current);
    }

    private static Button Swatch(IBrush brush, string name)
    {
        var swatch = new Button { Width = 24, Height = 24, Margin = new Thickness(0, 0, 6, 0), Background = brush };
        swatch.Classes.Add("swatch");
        ToolTip.SetTip(swatch, name);
        return swatch;
    }

    /// <summary>Index of the swatch of this color, -1 for another one; every transparent color is "transparent".</summary>
    private static int BackgroundIndex(Color color) =>
        color.A == 0 ? 1 : Array.FindIndex(Backgrounds, b => b.Color == color);

    private void SetBackground(Color color)
    {
        int index = BackgroundIndex(color);
        CanvasBackground = index >= 0 ? Backgrounds[index].Color : color;
        if (index < 0)
        {
            _customColor = color;
            _customButton!.Background = new ImmutableSolidColorBrush(color);
        }
        for (int i = 0; i < _backgroundButtons.Count; i++) _backgroundButtons[i].Classes.Set("selected", i == index);
        _customButton!.Classes.Set("selected", index < 0);
        BackgroundName.Text = index >= 0 ? Backgrounds[index].Name : Loc.T("Canvas.CustomColor");
    }

    /// <summary>The background comes back the next time, also after a restart.</summary>
    private void RememberBackground()
    {
        var settings = AppServices.Settings;
        settings.EditorCanvasBackground = HexColor.Format(CanvasBackground);
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // The choice still applies until Snaplume quits.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
    }

    // ---- Stretch ----

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
        bool canvas = CanvasMode;
        if (!TryRead(canvas ? CanvasWidthBox : WidthBox, out var w) || !TryRead(canvas ? CanvasHeightBox : HeightBox, out var h) ||
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
        // The saved image also has the margins of the shadow: they follow a stretched image, not a bigger canvas.
        var (outputWidth, outputHeight) = EditorDocument.OutputSizeOf(w, h, canvas ? _document.Effects : _document.ResizedEffects(w, h));
        if ((long)outputWidth * outputHeight > ImageIO.MaxPixels)
        {
            ShowError(Loc.T("Resize.TooManyPixelsWithShadow", ImageIO.MaxPixels / 1_000_000));
            return;
        }
        NewWidth = w;
        NewHeight = h;
        ExtendsCanvas = canvas;
        if (canvas) RememberBackground();
        _accepted = true;
        Close();
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
}
