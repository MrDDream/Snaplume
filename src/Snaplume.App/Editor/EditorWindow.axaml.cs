using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Ui;
using IconPath = Avalonia.Controls.Shapes.Path;
using Path = System.IO.Path;

namespace Snaplume.Editor;

/// <summary>
/// The image editor: tools rail, properties bar, the capture with its annotations and effects, and the commands
/// (save, export, copy, print, e-mail, OCR, insert an image, crop, resize). Closing asks to save the changes.
/// </summary>
public partial class EditorWindow : Window
{
    private static readonly (Color Color, string Name)[] Palette =
    {
        (Color.FromRgb(0xE5, 0x39, 0x35), Loc.T("Editor.Color.Red")),
        (Color.FromRgb(0xFB, 0x8C, 0x00), Loc.T("Editor.Color.Orange")),
        (Color.FromRgb(0xFD, 0xD8, 0x35), Loc.T("Editor.Color.Yellow")),
        (Color.FromRgb(0x43, 0xA0, 0x47), Loc.T("Editor.Color.Green")),
        (Color.FromRgb(0x1E, 0x88, 0xE5), Loc.T("Editor.Color.Blue")),
        (Color.FromRgb(0x8E, 0x24, 0xAA), Loc.T("Editor.Color.Purple")),
        (Color.FromRgb(0x21, 0x21, 0x21), Loc.T("Editor.Color.Black")),
        (Color.FromRgb(0xFF, 0xFF, 0xFF), Loc.T("Editor.Color.White")),
    };

    private static readonly double[] Thicknesses = { 1, 2, 3, 4, 6, 8, 12, 16, 24 };
    private static readonly double[] FontSizes = { 10, 12, 14, 16, 20, 24, 32, 40, 48, 64, 80 };
    /// <summary>Zoom factors of the magnifier offered in the properties bar.</summary>
    private static readonly double[] LensZooms = { 1.5, 2, 2.5, 3, 4 };

    /// <summary>Colors offered for the background of the image effects (files have no transparency).</summary>
    private static readonly (Color Color, string Name)[] EffectBackgrounds =
    {
        (Colors.White, Loc.T("Editor.Color.White")),
        (Color.FromRgb(0xF3, 0xF4, 0xF6), Loc.T("Effects.LightGray")),
        (Color.FromRgb(0x1F, 0x29, 0x37), Loc.T("Effects.DarkGray")),
        (Colors.Black, Loc.T("Editor.Color.Black")),
    };

    /// <summary>Formats the export can write, the first one being the default.</summary>
    private static readonly string[] ExportExtensions = { ".png", ".jpg", ".bmp" };

    private static readonly Dictionary<Key, EditorTool> ToolKeys = new()
    {
        [Key.V] = EditorTool.Select,
        [Key.A] = EditorTool.Arrow,
        [Key.L] = EditorTool.Line,
        [Key.R] = EditorTool.Rectangle,
        [Key.E] = EditorTool.Ellipse,
        [Key.T] = EditorTool.Text,
        [Key.H] = EditorTool.Highlight,
        [Key.B] = EditorTool.Pixelate,
        [Key.O] = EditorTool.Spotlight,
        [Key.N] = EditorTool.Step,
        [Key.S] = EditorTool.Stamp,
        [Key.P] = EditorTool.Pen,
        [Key.C] = EditorTool.Crop,
        [Key.M] = EditorTool.Magnifier,
    };

    /// <summary>Name and usage hint shown in the properties bar.</summary>
    private static readonly Dictionary<EditorTool, (string Name, string Hint)> ToolInfo = new()
    {
        [EditorTool.Select] = (Loc.T("Editor.Tool.Select"), Loc.T("Editor.Hint.Select")),
        [EditorTool.Arrow] = (Loc.T("Editor.Tool.Arrow"), Loc.T("Editor.Hint.Arrow")),
        [EditorTool.Line] = (Loc.T("Editor.Tool.Line"), Loc.T("Editor.Hint.Line")),
        [EditorTool.Rectangle] = (Loc.T("Editor.Tool.Rectangle"), Loc.T("Editor.Hint.Rectangle")),
        [EditorTool.Ellipse] = (Loc.T("Editor.Tool.Ellipse"), Loc.T("Editor.Hint.Ellipse")),
        [EditorTool.Pen] = (Loc.T("Editor.Tool.Pen"), Loc.T("Editor.Hint.Pen")),
        [EditorTool.Text] = (Loc.T("Editor.Tool.Text"), Loc.T("Editor.Hint.Text")),
        [EditorTool.Step] = (Loc.T("Editor.Tool.Step"), Loc.T("Editor.Hint.Step")),
        [EditorTool.Stamp] = (Loc.T("Editor.Tool.Stamp"), Loc.T("Editor.Hint.Stamp")),
        [EditorTool.Highlight] = (Loc.T("Editor.Tool.Highlight"), Loc.T("Editor.Hint.Highlight")),
        [EditorTool.Spotlight] = (Loc.T("Editor.Tool.Spotlight"), Loc.T("Editor.Hint.Spotlight")),
        [EditorTool.Pixelate] = (Loc.T("Editor.Tool.Pixelate"), Loc.T("Editor.Hint.Pixelate")),
        [EditorTool.Crop] = (Loc.T("Editor.Tool.Crop"), Loc.T("Editor.Hint.Crop")),
        [EditorTool.Magnifier] = (Loc.T("Editor.Tool.Magnifier"), Loc.T("Editor.Hint.Magnifier")),
    };

    // Same order as the ArrowHeads and StepShape enums.
    private static readonly string[] ArrowHeadNames =
    {
        "→  " + Loc.T("Editor.ArrowHead.End"), "↔  " + Loc.T("Editor.ArrowHead.Both"), "—  " + Loc.T("Editor.ArrowHead.None"),
    };
    private static readonly string[] StepShapeNames =
    {
        Loc.T("Editor.StepShape.Circle"), Loc.T("Editor.StepShape.Leader"), Loc.T("Editor.StepShape.Tail"),
        Loc.T("Editor.StepShape.Pin"), Loc.T("Editor.StepShape.Dark"),
    };
    // Same order as the MagnifierShape enum.
    private static readonly string[] LensShapeNames = { Loc.T("Editor.Lens.Circle"), Loc.T("Editor.Lens.RoundedRect") };

    private static readonly Dictionary<EditorTool, string> PresetTitles = new()
    {
        [EditorTool.Arrow] = Loc.T("Preset.Title.Arrow"),
        [EditorTool.Line] = Loc.T("Preset.Title.Line"),
        [EditorTool.Rectangle] = Loc.T("Preset.Title.Rectangle"),
        [EditorTool.Ellipse] = Loc.T("Preset.Title.Ellipse"),
        [EditorTool.Pen] = Loc.T("Preset.Title.Pen"),
        [EditorTool.Text] = Loc.T("Preset.Title.Text"),
        [EditorTool.Step] = Loc.T("Preset.Title.Step"),
        [EditorTool.Highlight] = Loc.T("Preset.Title.Highlight"),
        [EditorTool.Magnifier] = Loc.T("Preset.Title.Magnifier"),
    };

    private readonly EditorDocument _document = null!;
    /// <summary>Last color used with each tool (yellow highlighter, red arrows...).</summary>
    private readonly Dictionary<EditorTool, Color> _toolColors = new() { [EditorTool.Highlight] = Palette[2].Color };
    private bool _ready;
    private bool _syncingUi;
    /// <summary>Kind of annotation the properties bar currently edits (selection or next drawn).</summary>
    private EditorTool? _contextKind;
    /// <summary>Tool whose presets are shown in the preset popup.</summary>
    private EditorTool _presetTool;
    /// <summary>Tool whose choices fill the variant box (arrow heads or bullet shapes).</summary>
    private EditorTool? _variantKind;
    private double _zoom = 1;

    private TextBox? _textEditor;
    private TextAnnotation? _editingText;
    /// <summary>Document before the text edit, recorded as one undo step if the text or its style changed.</summary>
    private EditorDocument.Snapshot? _textCheckpoint;
    private (string Text, Color Color, double FontSize, bool HasBackground) _textBefore;

    // Arrow-key moves of the same annotation, with nothing else in between, make one undo step.
    private Annotation? _nudged;
    private long _nudgeVersion = -1;

    // Moves of the same effect slider, with nothing else in between, make one undo step (see ChangeEffects).
    private bool _syncingEffects;
    private string? _effectsKey;
    private long _effectsVersion = -1;
    /// <summary>Effects shown in the panel (see <see cref="ShowEffects"/>); null when it must be filled again.</summary>
    private ImageEffects? _shownEffects;
    /// <summary>Last slider move not applied yet: the moves are applied once per idle moment, not at each tick.</summary>
    private (string Slider, Func<ImageEffects, ImageEffects> Change)? _pendingSlider;
    private readonly List<(Border Dot, bool IsBorder)> _effectColorDots = new();

    /// <summary>Size of the capture when the image was inserted: the view is fitted again if placing it made the capture grow.</summary>
    private (int Width, int Height) _sizeBeforeInsert;

    // Closing: the question is asked after cancelling the first close, then the window closes for good.
    private bool _closeConfirmed;
    private bool _closeAsking;
    /// <summary>A command waiting for a dialog (save before e-mail...): a second one waits for it to end.</summary>
    private bool _busy;

    /// <summary>For the XAML previewer only.</summary>
    public EditorWindow() => InitializeComponent();

    /// <summary>Opens the capture (and its annotations) for editing.</summary>
    /// <exception cref="InvalidDataException">Not a readable image, or too large.</exception>
    public EditorWindow(string path)
    {
        InitializeComponent();
        FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;
        _document = EditorDocument.Load(path);
        AnnotCanvas.Document = _document;
        Backdrop.Document = _document;

        foreach (var (color, name) in Palette)
            PalettePanel.Children.Add(Swatch(color, name, () => SetCurrentColor(color, applyToSelection: true)));
        ThicknessBox.ItemsSource = Thicknesses.Select(t => Loc.T("Effects.Pixels", t)).ToArray();
        FontSizeBox.ItemsSource = FontSizes.Select(f => f.ToString("0", Loc.Culture) + " pt").ToArray();
        LensZoomBox.ItemsSource = LensZooms.Select(z => z.ToString("0.#", Loc.Culture) + " ×").ToArray();
        BuildEffectSwatches();
        SetCurrentColor(Palette[0].Color, applyToSelection: false);
        StampSizeText.Text = StampSizeSlider.Value.ToString("P0", Loc.Culture);
        BuildStampCatalog();
        SetCurrentStamp(AnnotCanvas.CurrentStampId);
        WireEvents();

        AnnotCanvas.SelectionChanged += OnSelectionChanged;
        AnnotCanvas.TextEditRequested += BeginTextEdit;
        AnnotCanvas.FloatingImageChanged += OnFloatingImageChanged;
        AnnotCanvas.FloatingImageFailed += ex => _ = ShowInsertErrorAsync(ex);
        _document.Changed += UpdateUi;

        Opened += (_, _) =>
        {
            ApplyZoom();
            // Once laid out: the size of the view is known.
            Dispatcher.UIThread.Post(() =>
            {
                ZoomToFit(onlyShrink: true);
                AnnotCanvas.Focus();
            }, DispatcherPriority.Background);
        };
        ScalingChanged += (_, _) => ApplyZoom();
        // The preset popup stays open until the user draws, so that the first click on the image is not lost.
        AnnotCanvas.AddHandler(PointerPressedEvent, (_, _) => PresetPopup.IsOpen = false, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => PresetPopup.IsOpen = false;
        PositionChanged += (_, _) => PresetPopup.IsOpen = false;

        _ready = true;
        UpdateUi();
        UpdateContext();
    }

    /// <summary>The capture being edited.</summary>
    public string FilePath => _document.FilePath;

    /// <summary>The document being edited (tests, and the windows that open the editor).</summary>
    internal EditorDocument Document => _document;

    private void WireEvents()
    {
        SaveButton.Click += (_, _) => Save();
        CopyButton.Click += (_, _) => _ = CopyAsync();
        ExportButton.Click += (_, _) => _ = ExportAsync();
        PrintButton.Click += (_, _) => _ = PrintAsync();
        EmailButton.Click += (_, _) => _ = EmailAsync();
        UndoButton.Click += (_, _) => Undo();
        RedoButton.Click += (_, _) => Redo();
        ReadTextButton.Click += (_, _) => ReadText();
        ResizeButton.Click += (_, _) => _ = ResizeAsync();
        CropButton.Click += (_, _) => SelectTool(EditorTool.Crop);
        InsertPasteItem.Click += (_, _) => _ = PasteImageAsync();
        InsertFileItem.Click += (_, _) => _ = InsertFromFileAsync();
        InsertCaptureItem.Click += (_, _) => _ = InsertCaptureAsync();
        DeleteButton.Click += (_, _) => DeleteSelected();
        CustomColorButton.Click += async (_, _) =>
        {
            if (await ColorPickerDialog.PickAsync(this, AnnotCanvas.CurrentStyle.Color) is Color color) SetCurrentColor(color, applyToSelection: true);
        };
        ThicknessBox.SelectionChanged += (_, _) => OnThicknessChanged();
        FontSizeBox.SelectionChanged += (_, _) => OnFontSizeChanged();
        VariantBox.SelectionChanged += (_, _) => OnVariantChanged();
        LensZoomBox.SelectionChanged += (_, _) => OnLensZoomChanged();
        FillBox.IsCheckedChanged += (_, _) => OnFillChanged();
        StampSizeSlider.ValueChanged += (_, e) =>
        {
            if (!_ready) return;
            AnnotCanvas.CurrentStampScale = e.NewValue;
            StampSizeText.Text = e.NewValue.ToString("P0", Loc.Culture);
        };

        foreach (var button in ToolButtons.Children.OfType<RadioButton>())
        {
            button.IsCheckedChanged += OnToolChecked;
            button.Click += OnToolClicked;
        }

        // Toggle buttons opening a popup: the popup lets them take the click that closes it.
        StampPopup.PlacementTarget = StampPickerButton;
        StampPopup.OverlayInputPassThroughElement = StampPickerButton;
        StampPickerButton.IsCheckedChanged += (_, _) => StampPopup.IsOpen = StampPickerButton.IsChecked == true;
        StampPopup.Closed += (_, _) => StampPickerButton.IsChecked = false;
        EffectsPopup.PlacementTarget = EffectsButton;
        EffectsPopup.OverlayInputPassThroughElement = EffectsButton;
        EffectsButton.IsCheckedChanged += (_, _) =>
        {
            if (EffectsButton.IsChecked == true) OpenEffects();
            else EffectsPopup.IsOpen = false;
        };
        EffectsPopup.Closed += (_, _) => OnEffectsClosed();
        // A slider released: its last move is applied, its next move starts a new undo step.
        EffectsPanel.AddHandler(PointerReleasedEvent, (_, _) =>
        {
            ApplyPendingSlider();
            _effectsKey = null;
        }, RoutingStrategies.Tunnel, handledEventsToo: true);
        foreach (var slider in new[] { ShadowBlurSlider, ShadowOpacitySlider, ShadowOffsetSlider, BorderWidthSlider, CornerSlider, TornSizeSlider })
            slider.ValueChanged += (_, e) => OnEffectsSlider(slider, e.NewValue);
        foreach (var check in new[] { ShadowCheck, BorderCheck, TornTopCheck, TornBottomCheck, TornLeftCheck, TornRightCheck })
            check.IsCheckedChanged += (_, _) => OnEffectsCheck();
        TornReseedButton.Click += (_, _) => ChangeEffects(null, fx => fx with
        {
            TornSeed = fx.TornSeed == int.MaxValue ? 1 : fx.TornSeed + 1,
            TornEdges = fx.TornEdges == TornEdges.None ? TornEdges.Bottom : fx.TornEdges,
        });
        // "None": every effect off, their settings kept for this session.
        EffectsNoneButton.Click += (_, _) =>
            ChangeEffects(null, fx => fx with { Shadow = false, Border = false, CornerRadius = 0, TornEdges = TornEdges.None });

        PresetCloseButton.Click += (_, _) => PresetPopup.IsOpen = false;
        PresetAddButton.Click += (_, _) => OnPresetAdd();
        PresetResetButton.Click += (_, _) =>
        {
            ToolPresets.Reset(_presetTool);
            BuildPresetPanel();
        };

        ZoomInButton.Click += (_, _) => SetZoom(_zoom * 1.25);
        ZoomOutButton.Click += (_, _) => SetZoom(_zoom * 0.8);
        ZoomActualButton.Click += (_, _) => SetZoom(1);
        ZoomFitButton.Click += (_, _) => ZoomToFit(onlyShrink: false);
        Scroller.AddHandler(PointerWheelChangedEvent, OnScrollerWheel, RoutingStrategies.Tunnel);
        Scroller.AddHandler(DragDrop.DragOverEvent, OnImageDragOver);
        Scroller.AddHandler(DragDrop.DropEvent, OnImageDrop);
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    /// <summary>Square color button of the palettes.</summary>
    private static Button Swatch(Color color, string name, Action click)
    {
        var swatch = new Button
        {
            Width = 20,
            Height = 20,
            Margin = new Thickness(2, 0),
            Padding = default,
            Focusable = false,
            Background = new ImmutableSolidColorBrush(color),
        };
        swatch.Classes.Add("swatch");
        ToolTip.SetTip(swatch, name);
        swatch.Click += (_, _) => click();
        return swatch;
    }

    // ---- Tools and style ----

    private void OnToolChecked(object? sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { IsChecked: true, Tag: string tag }) return;
        EndPendingEdits();
        var tool = Enum.Parse<EditorTool>(tag);
        // A tool chosen with the keyboard: the presets of the previous one no longer apply.
        if (tool != _presetTool) PresetPopup.IsOpen = false;
        AnnotCanvas.Tool = tool;
        if (tool is not (EditorTool.Select or EditorTool.Crop or EditorTool.Stamp))
            SetCurrentColor(_toolColors.GetValueOrDefault(tool, Palette[0].Color), applyToSelection: false);
        UpdateContext();
        if (tool == EditorTool.Stamp && IsVisible) StampPickerButton.IsChecked = true;
    }

    private void SelectTool(EditorTool tool)
    {
        var button = ToolButtons.Children.OfType<RadioButton>().FirstOrDefault(b => (string?)b.Tag == tool.ToString());
        if (button != null) button.IsChecked = true;
    }

    /// <summary>Shows only the properties that apply to the current tool, or to the selection.</summary>
    private void UpdateContext()
    {
        var tool = AnnotCanvas.Tool;
        var selected = AnnotCanvas.Selected;
        var info = ToolInfo[tool];
        ToolName.Text = selected != null && tool == EditorTool.Select ? SelectionName(selected) : info.Name;
        ToolHint.Text = info.Hint;

        // What the properties apply to: the selection when there is one, else the next annotation drawn.
        var kind = selected?.Kind ?? (tool is EditorTool.Select or EditorTool.Crop ? null : tool);
        _contextKind = kind;

        ColorSection.IsVisible = kind is not (null or EditorTool.Pixelate or EditorTool.Spotlight or EditorTool.Stamp);
        ThicknessSection.IsVisible = kind is not (null or EditorTool.Text or EditorTool.Spotlight or EditorTool.Highlight or EditorTool.Stamp);
        ThicknessLabel.Text = kind switch
        {
            EditorTool.Pixelate => Loc.T("Editor.PixelSize"),
            EditorTool.Step => Loc.T("Editor.Size"),
            _ => Loc.T("Editor.Thickness"),
        };
        FontSection.IsVisible = kind == EditorTool.Text;
        LensZoomSection.IsVisible = kind == EditorTool.Magnifier;
        FillSection.IsVisible = kind is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Text or EditorTool.Step or EditorTool.Magnifier;
        FillBox.Content = kind switch
        {
            EditorTool.Step => Loc.T("Editor.Letters"),
            EditorTool.Text => Loc.T("Editor.WhiteBackground"),
            EditorTool.Magnifier => Loc.T("Editor.Connector"),
            _ => Loc.T("Editor.Fill"),
        };
        VariantSection.IsVisible = kind is EditorTool.Arrow or EditorTool.Step or EditorTool.Magnifier;
        if (kind != _variantKind && kind is EditorTool.Arrow or EditorTool.Step or EditorTool.Magnifier)
        {
            _syncingUi = true;
            try
            {
                _variantKind = kind;
                VariantLabel.Text = kind == EditorTool.Arrow ? Loc.T("Editor.ArrowHeads") : Loc.T("Editor.Shape");
                VariantBox.ItemsSource = kind switch
                {
                    EditorTool.Arrow => ArrowHeadNames,
                    EditorTool.Magnifier => LensShapeNames,
                    _ => StepShapeNames,
                };
            }
            finally
            {
                _syncingUi = false;
            }
        }
        ShowStyle();
        StampSection.IsVisible = kind == EditorTool.Stamp && selected == null;
        DeleteSection.IsVisible = selected != null;
        if (AnnotCanvas.HasFloatingImage)
        {
            ToolName.Text = Loc.T("Editor.Insert.Floating");
            ToolHint.Text = Loc.T("Editor.Insert.FloatingHint");
        }
    }

    /// <summary>Shows the current style (the selection's when there is one) in the properties bar.</summary>
    private void ShowStyle()
    {
        var style = AnnotCanvas.CurrentStyle;
        _syncingUi = true;
        try
        {
            CurrentColorSwatch.Background = new ImmutableSolidColorBrush(style.Color);
            ThicknessBox.SelectedIndex = Array.IndexOf(Thicknesses, style.Thickness);
            FontSizeBox.SelectedIndex = Array.IndexOf(FontSizes, style.FontSize);
            FillBox.IsChecked = _contextKind == EditorTool.Step ? style.Letters : style.Fill;
            if (_contextKind == EditorTool.Arrow) VariantBox.SelectedIndex = (int)style.Heads;
            else if (_contextKind == EditorTool.Step) VariantBox.SelectedIndex = (int)style.Shape;
            else if (_contextKind == EditorTool.Magnifier) VariantBox.SelectedIndex = (int)style.Lens;
            LensZoomBox.SelectedIndex = Array.IndexOf(LensZooms, style.Zoom);
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private static string SelectionName(Annotation annotation) => annotation switch
    {
        PixelateAnnotation => Loc.T("Editor.Selection.Pixelate"),
        StepAnnotation => Loc.T("Editor.Selection.Step"),
        StampAnnotation stamp => StampLibrary.Find(stamp.StampId)?.Name ?? ToolInfo[EditorTool.Stamp].Name,
        _ => ToolInfo[annotation.Kind].Name,
    };

    /// <summary>
    /// Changes the style of the next annotations and of the text being typed, or else of the
    /// selection (one undo step, only when the selection actually changes).
    /// </summary>
    private void ChangeStyle(Func<DrawingStyle, DrawingStyle> change)
    {
        AnnotCanvas.CurrentStyle = change(AnnotCanvas.CurrentStyle);
        var target = (Annotation?)_editingText ?? AnnotCanvas.Selected;
        if (target == null || DrawingStyle.From(target, AnnotCanvas.CurrentStyle) is not { } own) return;
        var updated = change(own);
        if (updated == own) return;
        if (_editingText != null && _textEditor != null)
        {
            // Recorded with the text itself when the edit ends.
            updated.ApplyTo(_editingText);
            _textEditor.Foreground = new ImmutableSolidColorBrush(_editingText.Color);
            _textEditor.FontSize = _editingText.FontSize;
        }
        else
        {
            AnnotCanvas.ApplyToSelection(a => updated.ApplyTo(a, AnnotCanvas.ImageArea));
        }
    }

    private void SetCurrentColor(Color color, bool applyToSelection)
    {
        _toolColors[AnnotCanvas.Tool] = color;
        if (applyToSelection) ChangeStyle(s => s with { Color = color });
        else AnnotCanvas.CurrentStyle = AnnotCanvas.CurrentStyle with { Color = color };
        CurrentColorSwatch.Background = new ImmutableSolidColorBrush(color);
    }

    private void OnThicknessChanged()
    {
        if (!_ready || _syncingUi || ThicknessBox.SelectedIndex < 0) return;
        double thickness = Thicknesses[ThicknessBox.SelectedIndex];
        ChangeStyle(s => s with { Thickness = thickness });
        AnnotCanvas.Focus();
    }

    private void OnFontSizeChanged()
    {
        if (!_ready || _syncingUi || FontSizeBox.SelectedIndex < 0) return;
        double size = FontSizes[FontSizeBox.SelectedIndex];
        ChangeStyle(s => s with { FontSize = size });
        if (_textEditor != null) _textEditor.Focus();
        else AnnotCanvas.Focus();
    }

    private void OnVariantChanged()
    {
        if (!_ready || _syncingUi || VariantBox.SelectedIndex < 0) return;
        int index = VariantBox.SelectedIndex;
        if (_contextKind == EditorTool.Arrow) ChangeStyle(s => s with { Heads = (ArrowHeads)index });
        else if (_contextKind == EditorTool.Step) ChangeStyle(s => s with { Shape = (StepShape)index });
        else if (_contextKind == EditorTool.Magnifier) ChangeStyle(s => s with { Lens = (MagnifierShape)index });
        AnnotCanvas.Focus();
    }

    private void OnLensZoomChanged()
    {
        if (!_ready || _syncingUi || LensZoomBox.SelectedIndex < 0) return;
        double zoom = LensZooms[LensZoomBox.SelectedIndex];
        ChangeStyle(s => s with { Zoom = zoom });
        AnnotCanvas.Focus();
    }

    private void OnFillChanged()
    {
        if (!_ready || _syncingUi) return;
        bool fill = FillBox.IsChecked == true;
        // Letters for the bullets; fill for the shapes, white background for the texts (also the one being typed).
        if (_contextKind == EditorTool.Step) ChangeStyle(s => s with { Letters = fill });
        else ChangeStyle(s => s with { Fill = fill });
    }

    // ---- Stamps ----

    private void BuildStampCatalog()
    {
        foreach (var category in StampLibrary.Categories)
        {
            StampCatalog.Children.Add(new TextBlock
            {
                Text = StampLibrary.CategoryName(category),
                FontWeight = FontWeight.SemiBold,
                FontSize = 13,
                Margin = new Thickness(2, StampCatalog.Children.Count == 0 ? 4 : 14, 0, 6),
            });
            var panel = new WrapPanel();
            foreach (var stamp in StampLibrary.All.Where(s => s.Category == category))
            {
                double height = stamp.Category == StampLibrary.TextStamps ? 30 : 40;
                var button = new Button
                {
                    Padding = new Thickness(5),
                    Margin = new Thickness(1),
                    Content = DrawingPreview.ForStamp(stamp, height),
                };
                button.Classes.Add("flat");
                ToolTip.SetTip(button, stamp.Name);
                var id = stamp.Id;
                button.Click += (_, _) =>
                {
                    SetCurrentStamp(id);
                    StampPopup.IsOpen = false;
                    AnnotCanvas.Focus();
                };
                panel.Children.Add(button);
            }
            StampCatalog.Children.Add(panel);
        }
    }

    private void SetCurrentStamp(string id)
    {
        AnnotCanvas.CurrentStampId = id;
        CurrentStampImage.SetStamp(StampLibrary.Find(id));
    }

    // ---- Presets ----

    /// <summary>A mouse click on a tool button shows its presets (keyboard shortcuts do not).</summary>
    private void OnToolClicked(object? sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } button) return;
        var tool = Enum.Parse<EditorTool>(tag);
        if (!ToolPresets.Supports(tool) || (PresetPopup.IsOpen && _presetTool == tool))
        {
            PresetPopup.IsOpen = false;
            return;
        }
        _presetTool = tool;
        PresetPopup.IsOpen = false;
        PresetPopup.PlacementTarget = button;
        BuildPresetPanel();
        PresetPopup.IsOpen = true;
    }

    private void BuildPresetPanel()
    {
        PresetTitle.Text = PresetTitles[_presetTool];
        PresetPanel.Children.Clear();
        var presets = ToolPresets.For(_presetTool);
        for (int i = 0; i < presets.Count; i++)
            PresetPanel.Children.Add(CreatePresetTile(presets[i], i + 1));
        if (presets.Count == 0)
        {
            var empty = new TextBlock { Text = Loc.T("Preset.Empty"), Margin = new Thickness(2, 4, 0, 4) };
            empty.Classes.Add("muted");
            PresetPanel.Children.Add(empty);
        }
    }

    private Control CreatePresetTile(ToolPreset preset, int number)
    {
        var numberText = new TextBlock
        {
            Text = number.ToString(Loc.Culture),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 3),
        };
        numberText.Classes.Add("muted");
        var sample = new DrawingPreview { Width = 105, Height = 42 };
        sample.SetDrawing(preset.RenderPreview, new Size(ToolPreset.PreviewWidth, ToolPreset.PreviewHeight));
        var apply = new Button
        {
            Width = 124,
            Height = 58,
            Padding = default,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            Content = new Grid { Children = { sample, numberText } },
        };
        apply.Classes.Add("flat");
        apply.Classes.Add("tile");
        apply[!BackgroundProperty] = new DynamicResourceExtension("TileBrush");
        ToolTip.SetTip(apply, preset.Description);
        apply.Click += (_, _) => ApplyPreset(preset);

        var remove = new Button
        {
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            Content = new IconPath { Width = 8, Height = 8, Stretch = Stretch.Uniform, StrokeThickness = 2, Classes = { "icon" } },
        };
        ((IconPath)remove.Content)[!IconPath.DataProperty] = new DynamicResourceExtension("IconClose");
        remove.Classes.Add("flat");
        ToolTip.SetTip(remove, Loc.T("Preset.RemoveTip"));
        remove.Click += (_, _) =>
        {
            ToolPresets.Remove(preset);
            BuildPresetPanel();
        };

        return new Grid { Margin = new Thickness(0, 0, 6, 6), Children = { apply, remove } };
    }

    /// <summary>Selects the preset's tool with its style, and restyles the selection if it is of that kind.</summary>
    private void ApplyPreset(ToolPreset preset)
    {
        PresetPopup.IsOpen = false;
        SelectTool(preset.Tool);
        _toolColors[preset.Tool] = preset.Style.Color;
        AnnotCanvas.CurrentStyle = AnnotCanvas.CurrentStyle.Merge(preset.Tool, preset.Style);

        if (AnnotCanvas.Selected is { } selected && selected.Kind == preset.Tool)
        {
            if (DrawingStyle.From(selected, preset.Style) != preset.Style)
                AnnotCanvas.ApplyToSelection(a => preset.Style.ApplyTo(a, AnnotCanvas.ImageArea));
            OnSelectionChanged();
        }
        else
        {
            AnnotCanvas.Select(null);
            UpdateContext();
        }
        AnnotCanvas.Focus();
    }

    private void OnPresetAdd()
    {
        // Only the settings of this tool, so that identical presets compare equal.
        var preset = new ToolPreset(_presetTool, DrawingStyle.Default.Merge(_presetTool, AnnotCanvas.CurrentStyle));
        if (!ToolPresets.Add(preset)) InfoText.Text = Loc.T("Preset.AlreadyExists");
        BuildPresetPanel();
    }

    /// <summary>Shows the style of the selected annotation in the toolbar.</summary>
    private void OnSelectionChanged()
    {
        if (AnnotCanvas.Selected is { } selected && DrawingStyle.From(selected, AnnotCanvas.CurrentStyle) is { } style)
            AnnotCanvas.CurrentStyle = style;
        UpdateUi();
        UpdateContext();
    }

    private void UpdateUi()
    {
        UndoButton.IsEnabled = _document.CanUndo;
        RedoButton.IsEnabled = _document.CanRedo;
        bool hasSelection = AnnotCanvas.Selected != null;
        DeleteButton.IsEnabled = hasSelection;
        var image = _document.BaseImage;
        int count = _document.Annotations.Count;
        var annotations = count switch
        {
            0 => Loc.T("Editor.Info.NoAnnotations"),
            1 => Loc.T("Editor.Info.OneAnnotation"),
            _ => Loc.T("Editor.Info.Annotations", count),
        };
        InfoText.Text = Loc.T("Editor.Info.Size", image.Width, image.Height) + "   •   " + annotations
                        + (_document.IsDirty ? "   •   " + Loc.T("Editor.Info.Modified") : "")
                        + (_document.PreviewShadowFailed ? "   •   " + Loc.T("Effects.ShadowUnavailable") : "");
        Title = (_document.IsDirty ? "• " : "") + Loc.T("Editor.WindowTitle", Path.GetFileName(_document.FilePath));
        // The image keeps its coordinates; the effects add margins around it.
        var margin = Backdrop.ImageMargin;
        if (ImageLayer.Margin != margin) ImageLayer.Margin = margin;
        // The shadow of the effects replaces the decorative one of the page.
        ImagePage.IsVisible = _document.PreviewLayout?.Effects.Shadow != true;
        if (EffectsPopup.IsOpen) ShowEffects();
        if (DeleteSection.IsVisible != hasSelection) UpdateContext();
    }

    // ---- Text editing ----

    private void BeginTextEdit(TextAnnotation text, bool isNew)
    {
        EndTextEdit();
        _editingText = text;
        _textCheckpoint = _document.BeginChange();
        _textBefore = (text.Text, text.Color, text.FontSize, text.HasBackground);
        AnnotCanvas.SetEditingText(text);

        var box = new TextBox
        {
            Text = text.Text,
            FontFamily = TextAnnotation.Font,
            FontWeight = FontWeight.SemiBold,
            FontSize = text.FontSize,
            Foreground = new ImmutableSolidColorBrush(text.Color),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            // Arabic or Hebrew text is typed right to left, as it will be drawn (see TextAnnotation.DirectionOf).
            FlowDirection = TextAnnotation.DirectionOf(text.Text),
        };
        box.Classes.Add("annotation-text");
        box.TextChanged += (_, _) =>
        {
            var direction = TextAnnotation.DirectionOf(box.Text);
            if (box.FlowDirection != direction) box.FlowDirection = direction;
        };
        Canvas.SetLeft(box, text.Position.X + TextAnnotation.Pad - 3);
        Canvas.SetTop(box, text.Position.Y + TextAnnotation.Pad - 1);
        box.LostFocus += (_, _) => EndTextEdit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && e.KeyModifiers == KeyModifiers.Control))
            {
                e.Handled = true;
                EndTextEdit();
                AnnotCanvas.Focus();
            }
        };
        TextLayer.Children.Add(box);
        _textEditor = box;
        Dispatcher.UIThread.Post(() =>
        {
            if (_textEditor != box) return;
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }, DispatcherPriority.Input);
    }

    private void EndTextEdit()
    {
        if (_textEditor is not { } box || _editingText is not { } text || _textCheckpoint is not { } checkpoint) return;
        _textEditor = null;
        _editingText = null;
        _textCheckpoint = null;
        TextLayer.Children.Remove(box);
        AnnotCanvas.SetEditingText(null);

        var value = (box.Text ?? "").TrimEnd();
        bool exists = _document.Annotations.Contains(text);
        bool changed;
        if (string.IsNullOrWhiteSpace(value))
        {
            changed = exists;
            if (exists) _document.Annotations.Remove(text);
        }
        else if (!exists)
        {
            changed = true;
            text.Text = value;
            _document.Annotations.Add(text);
            AnnotCanvas.Select(text);
        }
        else
        {
            text.Text = value;
            // The color, size or background may have been changed while typing.
            changed = (text.Text, text.Color, text.FontSize, text.HasBackground) != _textBefore;
        }
        if (changed) _document.CommitChange(checkpoint);
        else _document.NotifyChanged();
    }

    // ---- Image effects ----

    private void BuildEffectSwatches()
    {
        foreach (var (color, name) in Palette.Append((Color.FromRgb(0x9C, 0xA3, 0xAF), Loc.T("Effects.Gray"))))
            BorderColors.Children.Add(Swatch(color, name, () => ChangeEffects(null, e => e with { Border = true, BorderColor = color })));
        BorderColors.Children.Add(CreateCustomColorButton(isBorder: true));
        foreach (var (color, name) in EffectBackgrounds)
            BackgroundColors.Children.Add(Swatch(color, name, () => ChangeEffects(null, e => e with { Background = color })));
        BackgroundColors.Children.Add(CreateCustomColorButton(isBorder: false));
    }

    /// <summary>Round button showing the current color of the border or of the background; it opens the color dialog.</summary>
    private Button CreateCustomColorButton(bool isBorder)
    {
        var dot = new Border { Width = 20, Height = 20, CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1.5) };
        dot[!Border.BorderBrushProperty] = new DynamicResourceExtension("MutedTextBrush");
        _effectColorDots.Add((dot, isBorder));
        var button = new Button { Padding = new Thickness(2), Margin = new Thickness(4, 0, 0, 0), Focusable = false, Content = dot };
        button.Classes.Add("flat");
        ToolTip.SetTip(button, Loc.T("Effects.CustomColorTip"));
        button.Click += async (_, _) =>
        {
            var current = isBorder ? _document.Effects.BorderColor : _document.Effects.Background;
            // The dialog closes the panel: it is shown again with the new color.
            if (await ColorPickerDialog.PickAsync(this, current) is Color color)
                ChangeEffects(null, e => isBorder ? e with { Border = true, BorderColor = color } : e with { Background = color });
            EffectsButton.IsChecked = true;
        };
        return button;
    }

    private void OpenEffects()
    {
        EndPendingEdits();
        AnnotCanvas.CancelOperation();
        PresetPopup.IsOpen = false;
        ShowEffects();
        EffectsPopup.IsOpen = true;
    }

    private void OnEffectsClosed()
    {
        ApplyPendingSlider();
        EffectsButton.IsChecked = false;
        _effectsKey = null;
    }

    /// <summary>Shows the effects of the document in the panel (also after an undo); nothing to do when they are already shown.</summary>
    private void ShowEffects()
    {
        var effects = _document.Effects;
        if (effects == _shownEffects) return;
        var shown = _shownEffects;
        _shownEffects = effects;
        _syncingEffects = true;
        try
        {
            ShadowCheck.IsChecked = effects.Shadow;
            ShadowBlurSlider.Value = effects.ShadowBlur;
            ShadowOpacitySlider.Value = effects.ShadowOpacity * 100;
            ShadowOffsetSlider.Value = (effects.ShadowOffsetX + effects.ShadowOffsetY) / 2;
            BorderCheck.IsChecked = effects.Border;
            BorderWidthSlider.Value = effects.BorderWidth;
            CornerSlider.Value = effects.CornerRadius;
            TornTopCheck.IsChecked = effects.TornEdges.HasFlag(TornEdges.Top);
            TornBottomCheck.IsChecked = effects.TornEdges.HasFlag(TornEdges.Bottom);
            TornLeftCheck.IsChecked = effects.TornEdges.HasFlag(TornEdges.Left);
            TornRightCheck.IsChecked = effects.TornEdges.HasFlag(TornEdges.Right);
            TornSizeSlider.Value = effects.TornSize;

            ShadowBlurText.Text = Pixels(effects.ShadowBlur);
            ShadowOpacityText.Text = effects.ShadowOpacity.ToString("P0", Loc.Culture);
            ShadowOffsetText.Text = Pixels((effects.ShadowOffsetX + effects.ShadowOffsetY) / 2);
            BorderWidthText.Text = Pixels(effects.BorderWidth);
            CornerText.Text = Pixels(effects.CornerRadius);
            TornSizeText.Text = Pixels(effects.TornSize);
            if (shown == null || shown.BorderColor != effects.BorderColor || shown.Background != effects.Background)
            {
                foreach (var (dot, isBorder) in _effectColorDots)
                    dot.Background = new ImmutableSolidColorBrush(isBorder ? effects.BorderColor : effects.Background);
            }
        }
        finally
        {
            _syncingEffects = false;
        }
    }

    private static string Pixels(double value) => Loc.T("Effects.Pixels", Math.Round(value));

    /// <summary>
    /// Applies a change of the effects to the document, shown at once. One undo step per change; the moves of
    /// the same slider during one drag (<paramref name="sliderKey"/>) amend the same step.
    /// </summary>
    private void ChangeEffects(string? sliderKey, Func<ImageEffects, ImageEffects> change)
    {
        if (!_ready || _syncingEffects) return;
        // A slider move still waiting comes first: changes are applied in the order they were made.
        if (sliderKey == null) ApplyPendingSlider();
        EndTextEdit();
        AnnotCanvas.CancelOperation();
        var current = _document.Effects;
        var updated = change(current);
        if (updated == current) return;
        bool amend = sliderKey != null && sliderKey == _effectsKey && _document.Version == _effectsVersion;
        _document.SetEffects(updated, amend);
        _effectsKey = sliderKey;
        _effectsVersion = _document.Version;
    }

    private void OnEffectsSlider(Slider slider, double value)
    {
        if (!_ready || _syncingEffects) return;
        double v = Math.Round(value);
        Func<ImageEffects, ImageEffects>? change = slider.Name switch
        {
            nameof(ShadowBlurSlider) => fx => fx with { Shadow = true, ShadowBlur = v },
            nameof(ShadowOpacitySlider) => fx => fx with { Shadow = true, ShadowOpacity = v / 100 },
            nameof(ShadowOffsetSlider) => fx => fx with { Shadow = true, ShadowOffsetX = v, ShadowOffsetY = v },
            nameof(BorderWidthSlider) => fx => fx with { Border = true, BorderWidth = v },
            nameof(CornerSlider) => fx => fx with { CornerRadius = v },
            nameof(TornSizeSlider) => fx => fx with { TornSize = v },
            _ => null,
        };
        if (change == null || slider.Name == null) return;
        // A drag sends many ticks: only the last one waiting is applied, once the window has drawn the previous one.
        if (_pendingSlider is { } pending && pending.Slider != slider.Name) ApplyPendingSlider();
        bool scheduled = _pendingSlider != null;
        _pendingSlider = (slider.Name, change);
        if (!scheduled) Dispatcher.UIThread.Post(ApplyPendingSlider, DispatcherPriority.Background);
    }

    private void ApplyPendingSlider()
    {
        if (_pendingSlider is not { } pending) return;
        _pendingSlider = null;
        ChangeEffects(pending.Slider, pending.Change);
    }

    private void OnEffectsCheck()
    {
        if (!_ready || _syncingEffects) return;
        var torn = (TornTopCheck.IsChecked == true ? TornEdges.Top : TornEdges.None)
                   | (TornBottomCheck.IsChecked == true ? TornEdges.Bottom : TornEdges.None)
                   | (TornLeftCheck.IsChecked == true ? TornEdges.Left : TornEdges.None)
                   | (TornRightCheck.IsChecked == true ? TornEdges.Right : TornEdges.None);
        bool shadow = ShadowCheck.IsChecked == true, border = BorderCheck.IsChecked == true;
        ChangeEffects(null, fx => fx with { Shadow = shadow, Border = border, TornEdges = torn });
    }

    // ---- Inserting an image ----

    /// <summary>
    /// Validates what is in progress before a command: the text being typed, the image being inserted.
    /// Returns false when the inserted image could not be burnt in (it is still floating).
    /// </summary>
    private bool EndPendingEdits()
    {
        EndTextEdit();
        AnnotCanvas.CommitFloatingImage();
        return !AnnotCanvas.HasFloatingImage;
    }

    private async Task InsertFromFileAsync()
    {
        var folder = await StorageProvider.TryGetFolderFromPathAsync(new Uri(Path.GetDirectoryName(Path.GetFullPath(_document.FilePath))!));
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Editor.Insert.FileTitle"),
            AllowMultiple = false,
            SuggestedStartLocation = folder,
            FileTypeFilter = new[]
            {
                new FilePickerFileType(Loc.T("Editor.Insert.FileFilter")) { Patterns = ImageIO.Extensions.Select(x => "*" + x).ToArray() },
            },
        });
        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path) await InsertFileAsync(path);
    }

    /// <summary>A new capture of an area of the screen, this window hidden meanwhile.</summary>
    private async Task InsertCaptureAsync()
    {
        if (!EndPendingEdits()) return;
        AnnotCanvas.CancelOperation();
        var image = await App.Instance.Captures.PickImageAsync(this);
        Activate();
        if (image != null) await InsertImageAsync(image);
    }

    private void OnImageDragOver(object? sender, DragEventArgs e)
    {
        e.DragEffects = DroppedImage(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnImageDrop(object? sender, DragEventArgs e)
    {
        if (DroppedImage(e) is not { } file) return;
        e.Handled = true;
        Activate();
        _ = InsertFileAsync(file);
    }

    /// <summary>First image file dragged from the file manager or from the workspace, if any.</summary>
    private static string? DroppedImage(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).FirstOrDefault(p => p != null && ImageIO.IsImage(p));

    private async Task InsertFileAsync(string path)
    {
        PixelBuffer image;
        try
        {
            image = ImageIO.Load(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Lecture de l'image à insérer", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.Insert.ReadError", ex.Message), Loc.T("Editor.Insert.Title"), DialogButtons.Ok, DialogIcon.Error);
            return;
        }
        await InsertImageAsync(image);
    }

    /// <summary>Ctrl+V: the image of the clipboard, or the first image file copied in the file manager.</summary>
    private async Task PasteImageAsync()
    {
        PixelBuffer? image;
        try
        {
            image = await App.Instance.Platform.Clipboard.GetImageAsync();
        }
        catch (Exception ex)
        {
            // Clipboard held by another application, unreadable or too big image...
            ErrorLog.Write("Collage d'une image", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.Insert.ReadError", ex.Message), Loc.T("Editor.Insert.Title"), DialogButtons.Ok, DialogIcon.Error);
            return;
        }
        if (image == null)
        {
            InfoText.Text = Loc.T("Editor.Insert.NoImage");
            return;
        }
        await InsertImageAsync(image);
    }

    /// <summary>
    /// Shows <paramref name="image"/> floating over the capture, in the middle of what is visible, to be moved
    /// and resized before it is burnt in (see <see cref="AnnotationCanvas.FloatImage"/>); the capture grows when the
    /// image is placed past its edges. An image bigger than the capture is kept whole at its top left corner (the
    /// capture grows) or shrunk to fit, as the user chooses.
    /// </summary>
    internal async Task InsertImageAsync(PixelBuffer image)
    {
        if (!EndPendingEdits()) return;
        AnnotCanvas.CancelOperation();
        var area = AnnotCanvas.ImageArea;
        double width = image.Width, height = image.Height;
        if (width > area.Width || height > area.Height)
        {
            var answer = await Dialogs.ShowAsync(this, Loc.T("Editor.Insert.Larger", image.Width, image.Height, (int)area.Width, (int)area.Height),
                Loc.T("Editor.Insert.Title"), DialogButtons.YesNoCancel, DialogIcon.Question, DialogResult.Yes);
            if (answer == DialogResult.Cancel) return;
            if (answer == DialogResult.No)
            {
                double scale = Math.Min(area.Width / width, area.Height / height);
                width = Math.Max(1, width * scale);
                height = Math.Max(1, height * scale);
            }
            // The document may have changed while the question was asked (a second paste...).
            if (!EndPendingEdits()) return;
            area = AnnotCanvas.ImageArea;
        }

        _sizeBeforeInsert = (_document.BaseImage.Width, _document.BaseImage.Height);
        UpdateLayout();
        var viewport = Scroller.Viewport;
        var center = Scroller.TranslatePoint(new Point(viewport.Width / 2, viewport.Height / 2), AnnotCanvas) ?? area.Center;
        double x = Math.Clamp(center.X - width / 2, 0, Math.Max(0, area.Width - width));
        double y = Math.Clamp(center.Y - height / 2, 0, Math.Max(0, area.Height - height));
        AnnotCanvas.FloatImage(image, new Rect(Math.Round(x), Math.Round(y), width, height));
        AnnotCanvas.Focus();
    }

    private void OnFloatingImageChanged()
    {
        UpdateContext();
        if (!AnnotCanvas.HasFloatingImage && _sizeBeforeInsert != (_document.BaseImage.Width, _document.BaseImage.Height))
            ZoomToFit(onlyShrink: true);
    }

    private Task ShowInsertErrorAsync(Exception ex)
    {
        ErrorLog.Write("Insertion d'une image", ex);
        return Dialogs.ShowAsync(this, Loc.T("Editor.Insert.Error", ex.Message), Loc.T("Editor.Insert.Title"), DialogButtons.Ok, DialogIcon.Error);
    }

    // ---- Commands ----

    /// <summary>Saves the capture and its annotations; the error, if any, is shown without waiting.</summary>
    private bool Save()
    {
        ApplyPendingSlider();
        if (!EndPendingEdits()) return false;
        try
        {
            _document.Save(AppServices.Settings.JpegQuality);
            InfoText.Text = Loc.T("Editor.Saved");
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement", ex);
            _ = Dialogs.ShowAsync(this, Loc.T("Editor.SaveError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return false;
        }
    }

    private async Task CopyAsync()
    {
        if (!EndPendingEdits()) return;
        try
        {
            var image = _document.Flatten();
            if (!await App.Instance.Platform.Clipboard.SetImageAsync(image, ImageIO.EncodePng(image)))
            {
                // The clipboard stayed locked by another application.
                ErrorLog.Write("Copie dans le presse-papiers", "Presse-papiers occupé");
                await Dialogs.ShowAsync(this, Loc.T("Editor.CopyError"), "Snaplume", DialogButtons.Ok, DialogIcon.Warning);
                return;
            }
            InfoText.Text = Loc.T("Editor.Copied");
        }
        catch (Exception ex)
        {
            // Image too big with its margins, not enough memory...
            ErrorLog.Write("Copie dans le presse-papiers", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.CopyFailed", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
        }
    }

    private async Task ExportAsync()
    {
        if (!EndPendingEdits()) return;
        var choices = new[]
        {
            new FilePickerFileType(Loc.T("Editor.ImageFormat", "PNG")) { Patterns = new[] { "*.png" } },
            new FilePickerFileType(Loc.T("Editor.ImageFormat", "JPEG")) { Patterns = new[] { "*.jpg", "*.jpeg" } },
            new FilePickerFileType(Loc.T("Editor.ImageFormat", "BMP")) { Patterns = new[] { "*.bmp" } },
        };
        var result = await StorageProvider.SaveFilePickerWithResultAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Editor.ExportTitle"),
            SuggestedFileName = Path.GetFileNameWithoutExtension(_document.FilePath),
            FileTypeChoices = choices,
            DefaultExtension = "png",
            ShowOverwritePrompt = true,
        });
        if (result.File?.TryGetLocalPath() is not { } path) return;

        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!ExportExtensions.Contains(extension) && extension != ".jpeg")
        {
            // Unsupported extension typed by hand: the format chosen in the list is added.
            int index = Math.Max(0, Array.FindIndex(choices, c => c.Name == result.SelectedFileType?.Name));
            path += ExportExtensions[Math.Clamp(index, 0, ExportExtensions.Length - 1)];
            if (File.Exists(path) && await Dialogs.ShowAsync(this, Loc.T("Editor.ExportOverwrite", Path.GetFileName(path)), "Snaplume",
                    DialogButtons.YesNo, DialogIcon.Warning, DialogResult.No) != DialogResult.Yes)
                return;
        }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            // Overwriting the capture would bypass its editable annotations: that is what Save is for.
            await Dialogs.ShowAsync(this, Loc.T("Editor.ExportSameFile"), "Snaplume", DialogButtons.Ok, DialogIcon.Warning);
            return;
        }
        try
        {
            ImageIO.Save(_document.Flatten(), path, AppServices.Settings.JpegQuality);
            InfoText.Text = Loc.T("Editor.Exported", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Export", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.ExportError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
        }
    }

    /// <summary>Prints the image as it is now, annotations and effects included (a temporary PNG handed to the system).</summary>
    private async Task PrintAsync()
    {
        if (_busy || !EndPendingEdits()) return;
        AnnotCanvas.CancelOperation();
        var folder = Path.Combine(Path.GetTempPath(), "Snaplume");
        var temp = Path.Combine(folder, $"print-{Guid.NewGuid():N}.png");
        _busy = true;
        try
        {
            Directory.CreateDirectory(folder);
            // Rendered before the print dialog, even if it is cancelled: the orientation of the page comes from the image.
            ImageIO.Save(_document.Flatten(), temp);
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            await App.Instance.Platform.Shell.PrintImagesAsync(new[] { temp }, Path.GetFileName(_document.FilePath), handle);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Impression", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.PrintError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
        }
        finally
        {
            _busy = false;
            try { File.Delete(temp); }
            catch (Exception ex) { ErrorLog.Write("Suppression de l'image imprimée", ex); }
        }
    }

    /// <summary>Sends the capture file by e-mail, saved first if the user wants the changes in it.</summary>
    private async Task EmailAsync()
    {
        if (_busy || !EndPendingEdits()) return;
        AnnotCanvas.CancelOperation();
        _busy = true;
        try
        {
            if (_document.IsDirty)
            {
                var answer = await Dialogs.ShowAsync(this, Loc.T("Editor.SaveBeforeEmailQuestion"), "Snaplume",
                    DialogButtons.YesNoCancel, DialogIcon.Question, DialogResult.Yes);
                if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save())) return;
            }
            var shell = App.Instance.Platform.Shell;
            var file = Path.GetFullPath(_document.FilePath);
            if (!shell.CanEmail)
            {
                await ShowFilesForWebmailAsync(file, Loc.T("Share.Email.NoClient"));
                return;
            }
            try
            {
                shell.Email(new[] { file }, Path.GetFileName(file));
            }
            catch (InvalidOperationException ex)
            {
                // No mail application after all: the message says what to do.
                ErrorLog.Write("Envoi par e-mail", ex);
                await ShowFilesForWebmailAsync(file, ex.Message);
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Envoi par e-mail", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.EmailError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
        }
        finally
        {
            _busy = false;
        }
    }

    /// <summary>Without a mail application: explains how to attach the file to a webmail and shows it in the file manager.</summary>
    private async Task ShowFilesForWebmailAsync(string file, string message)
    {
        await Dialogs.ShowAsync(this, message, Loc.T("Share.Email.Title"), DialogButtons.Ok, DialogIcon.Information);
        try
        {
            App.Instance.Platform.Shell.Reveal(file);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage de la capture à envoyer", ex);
        }
    }

    /// <summary>Text of the image as it is now, annotations included.</summary>
    private void ReadText()
    {
        if (!EndPendingEdits()) return;
        try
        {
            App.Instance.ShowOcr(_document.Flatten());
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OCR de l'image modifiée", ex);
            _ = Dialogs.ShowAsync(this, Loc.T("Editor.ReadTextError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
        }
    }

    private async Task ResizeAsync()
    {
        if (_busy || !EndPendingEdits()) return;
        AnnotCanvas.CancelOperation();
        var dialog = new ResizeDialog(_document);
        _busy = true;
        bool accepted;
        try
        {
            accepted = await dialog.ShowAsync(this);
        }
        finally
        {
            _busy = false;
        }
        if (!accepted) return;
        try
        {
            _document.Resize(dialog.NewWidth, dialog.NewHeight);
        }
        catch (Exception ex)
        {
            // Mostly not enough memory for a huge image; the document is left as it was.
            ErrorLog.Write("Redimensionnement", ex);
            await Dialogs.ShowAsync(this, Loc.T("Editor.ResizeError", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return;
        }
        ZoomToFit(onlyShrink: true);
        InfoText.Text = Loc.T("Editor.Resized", dialog.NewWidth, dialog.NewHeight);
    }

    private void Undo()
    {
        ApplyPendingSlider();
        EndTextEdit();
        // During a drawing or a drag, undo only cancels it; an image being inserted goes away, like in Paint.
        if (AnnotCanvas.CancelOperation()) return;
        if (AnnotCanvas.HasFloatingImage)
        {
            AnnotCanvas.CancelFloatingImage();
            return;
        }
        _document.Undo();
    }

    private void Redo()
    {
        ApplyPendingSlider();
        EndTextEdit();
        AnnotCanvas.CancelOperation();
        // Placing the image would end the history that can be redone.
        if (AnnotCanvas.HasFloatingImage) return;
        _document.Redo();
    }

    private void DeleteSelected()
    {
        EndTextEdit();
        if (AnnotCanvas.HasFloatingImage && !AnnotCanvas.IsDragging)
        {
            AnnotCanvas.CancelFloatingImage();
            return;
        }
        if (AnnotCanvas.IsDragging || AnnotCanvas.Selected is not { } selected) return;
        _document.PushUndo();
        _document.Annotations.Remove(selected);
        _document.NotifyChanged();
    }

    private void Nudge(Vector offset)
    {
        EndTextEdit();
        if (AnnotCanvas.HasFloatingImage)
        {
            AnnotCanvas.NudgeFloatingImage(offset);
            return;
        }
        if (AnnotCanvas.IsDragging || AnnotCanvas.Selected is not { } selected) return;
        bool sameStep = ReferenceEquals(selected, _nudged) && _document.Version == _nudgeVersion;
        if (!sameStep) _document.PushUndo();
        selected.Translate(offset);
        if (sameStep) _document.AmendLastChange();
        else _document.NotifyChanged();
        _nudged = selected;
        _nudgeVersion = _document.Version;
    }

    /// <summary>The keyboard shortcuts of the editor, seen before the focused control (tunnel).</summary>
    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        var key = e.Key;
        var modifiers = e.KeyModifiers & ~KeyModifiers.Meta;

        // Saving works even while a text is being typed (the edit is validated first).
        if (key == Key.S && (modifiers == KeyModifiers.Control || modifiers == (KeyModifiers.Control | KeyModifiers.Shift)))
        {
            e.Handled = true;
            bool typing = _textEditor != null;
            if (modifiers == KeyModifiers.Control) Save();
            else _ = ExportAsync();
            if (typing) AnnotCanvas.Focus();
            return;
        }
        if (_textEditor != null || e.Source is TextBox) return;

        if (modifiers == KeyModifiers.Control)
        {
            switch (key)
            {
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: _ = CopyAsync(); break;
                case Key.V: _ = PasteImageAsync(); break;
                case Key.R: _ = ResizeAsync(); break;
                case Key.P: _ = PrintAsync(); break;
                case Key.D0 or Key.NumPad0: SetZoom(1); break;
                case Key.OemPlus or Key.Add: SetZoom(_zoom * 1.25); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / 1.25); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (modifiers == (KeyModifiers.Control | KeyModifiers.Shift))
        {
            if (key != Key.Z) return;
            Redo();
            e.Handled = true;
            return;
        }
        if (modifiers != KeyModifiers.None && modifiers != KeyModifiers.Shift) return;

        double step = modifiers == KeyModifiers.Shift ? 10 : 1;
        bool inCombo = e.Source is ComboBox or ComboBoxItem || (e.Source as Visual)?.FindAncestorOfType<ComboBox>() != null;
        bool canNudge = !inCombo && (AnnotCanvas.Selected != null || AnnotCanvas.HasFloatingImage);
        switch (key)
        {
            case Key.Delete or Key.Back: DeleteSelected(); break;
            case Key.Enter when AnnotCanvas.HasFloatingImage && !inCombo: AnnotCanvas.CommitFloatingImage(); break;
            case Key.Escape:
                if (PresetPopup.IsOpen)
                {
                    PresetPopup.IsOpen = false;
                    break;
                }
                if (EffectsPopup.IsOpen)
                {
                    EffectsPopup.IsOpen = false;
                    break;
                }
                if (AnnotCanvas.CancelOperation()) break;
                if (AnnotCanvas.HasFloatingImage) AnnotCanvas.CancelFloatingImage();
                else if (AnnotCanvas.Selected != null) AnnotCanvas.Select(null);
                else SelectTool(EditorTool.Select);
                break;
            case Key.Left when canNudge: Nudge(new Vector(-step, 0)); break;
            case Key.Right when canNudge: Nudge(new Vector(step, 0)); break;
            case Key.Up when canNudge: Nudge(new Vector(0, -step)); break;
            case Key.Down when canNudge: Nudge(new Vector(0, step)); break;
            default:
                if (modifiers != KeyModifiers.None || inCombo || !ToolKeys.TryGetValue(key, out var tool)) return;
                SelectTool(tool);
                break;
        }
        e.Handled = true;
    }

    // ---- Zoom ----

    private void ApplyZoom()
    {
        double dpi = RenderScaling > 0 ? RenderScaling : 1;
        double scale = _zoom / dpi; // 100 % = one image pixel per physical pixel
        ZoomHost.LayoutTransform = new ScaleTransform(scale, scale);
        AnnotCanvas.ViewScale = scale;
        AnnotCanvas.Interpolation = _zoom >= 2 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        ZoomText.Text = _zoom.ToString("P0", Loc.Culture);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 16);
        ApplyZoom();
    }

    private void ZoomToFit(bool onlyShrink)
    {
        double dpi = RenderScaling > 0 ? RenderScaling : 1;
        // The whole result, margins of the effects included.
        var size = _document.OutputSize;
        double availableWidth = Math.Max(50, Scroller.Bounds.Width - 72);
        double availableHeight = Math.Max(50, Scroller.Bounds.Height - 72);
        double fit = Math.Min(availableWidth * dpi / size.Width, availableHeight * dpi / size.Height);
        SetZoom(onlyShrink ? Math.Min(1, fit) : fit);
    }

    private void OnScrollerWheel(object? sender, PointerWheelEventArgs e)
    {
        if ((e.KeyModifiers & ~KeyModifiers.Meta) != KeyModifiers.Control || e.Delta.Y == 0) return;
        SetZoom(_zoom * (e.Delta.Y > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    // ---- Closing ----

    /// <summary>
    /// Message boxes cannot block here as in WPF: when there are unsaved changes, the close is cancelled, the question
    /// asked, then the window closed again (or left open).
    /// </summary>
    protected override void OnClosing(WindowClosingEventArgs e)
    {
        if (_closeConfirmed)
        {
            base.OnClosing(e);
            return;
        }
        // An image still being placed becomes a change to save (or to discard) like the others.
        if (_closeAsking || !EndPendingEdits())
        {
            e.Cancel = true;
            return;
        }
        if (!_document.IsDirty)
        {
            base.OnClosing(e);
            return;
        }
        e.Cancel = true;
        _ = AskThenCloseAsync();
    }

    private async Task AskThenCloseAsync()
    {
        _closeAsking = true;
        try
        {
            var answer = await Dialogs.ShowAsync(this, Loc.T("Editor.SaveChangesQuestion"), "Snaplume",
                DialogButtons.YesNoCancel, DialogIcon.Question, DialogResult.Yes);
            if (answer == DialogResult.Cancel || (answer == DialogResult.Yes && !Save())) return;
        }
        finally
        {
            _closeAsking = false;
        }
        _closeConfirmed = true;
        Close();
    }
}
