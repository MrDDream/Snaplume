using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Snaplume.Core;
using WinForms = System.Windows.Forms;

namespace Snaplume.Editor;

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
    };

    private readonly EditorDocument _document;
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
    private double _zoom = 1;

    private TextBox? _textEditor;
    private TextAnnotation? _editingText;
    /// <summary>Document before the text edit, recorded as one undo step if the text or its style changed.</summary>
    private EditorDocument.Snapshot? _textCheckpoint;
    private (string Text, Color Color, double FontSize, bool HasBackground) _textBefore;

    // Arrow-key moves of the same annotation, with nothing else in between, make one undo step.
    private Annotation? _nudged;
    private long _nudgeVersion = -1;

    public string FilePath => _document.FilePath;

    public EditorWindow(string path)
    {
        InitializeComponent();
        _document = EditorDocument.Load(path);
        AnnotCanvas.Document = _document;

        foreach (var (color, name) in Palette)
        {
            var swatch = new Button { Style = (Style)FindResource("SwatchButton"), Background = new SolidColorBrush(color), ToolTip = name };
            swatch.Click += (_, _) => SetCurrentColor(color, applyToSelection: true);
            PalettePanel.Children.Add(swatch);
        }
        foreach (var t in Thicknesses) ThicknessBox.Items.Add(t);
        foreach (var f in FontSizes) FontSizeBox.Items.Add(f);
        SetCurrentColor(Palette[0].Color, applyToSelection: false);
        StampSizeText.Text = StampSizeSlider.Value.ToString("P0", Loc.Culture);
        BuildStampCatalog();
        SetCurrentStamp(AnnotCanvas.CurrentStampId);

        AnnotCanvas.SelectionChanged += OnSelectionChanged;
        AnnotCanvas.TextEditRequested += BeginTextEdit;
        _document.Changed += UpdateUi;

        Loaded += (_, _) =>
        {
            ZoomToFit(onlyShrink: true);
            AnnotCanvas.Focus();
        };
        DpiChanged += (_, _) => ApplyZoom();
        // The preset popup stays open until the user draws, so that the first click on the image is not lost.
        AnnotCanvas.PreviewMouseDown += (_, _) => PresetPopup.IsOpen = false;
        Deactivated += (_, _) => PresetPopup.IsOpen = false;
        LocationChanged += (_, _) => PresetPopup.IsOpen = false;

        _ready = true;
        UpdateUi();
        UpdateContext();
    }

    // ---- Tools and style ----

    private void OnToolChecked(object sender, RoutedEventArgs e)
    {
        if (!_ready || sender is not RadioButton { Tag: string tag }) return;
        EndTextEdit();
        var tool = Enum.Parse<EditorTool>(tag);
        // A tool chosen with the keyboard: the presets of the previous one no longer apply.
        if (tool != _presetTool) PresetPopup.IsOpen = false;
        AnnotCanvas.Tool = tool;
        if (tool is not (EditorTool.Select or EditorTool.Crop or EditorTool.Stamp))
            SetCurrentColor(_toolColors.GetValueOrDefault(tool, Palette[0].Color), applyToSelection: false);
        UpdateContext();
        if (tool == EditorTool.Stamp && IsLoaded) StampPickerButton.IsChecked = true;
    }

    private void SelectTool(EditorTool tool)
    {
        var button = ToolButtons.Children.OfType<RadioButton>().FirstOrDefault(b => (string)b.Tag == tool.ToString());
        if (button != null) button.IsChecked = true;
    }

    private void OnCropTool(object sender, RoutedEventArgs e) => SelectTool(EditorTool.Crop);

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

        Show(ColorSection, kind is not (null or EditorTool.Pixelate or EditorTool.Spotlight or EditorTool.Stamp));
        Show(ThicknessSection, kind is not (null or EditorTool.Text or EditorTool.Spotlight or EditorTool.Highlight or EditorTool.Stamp));
        ThicknessLabel.Text = kind switch
        {
            EditorTool.Pixelate => Loc.T("Editor.PixelSize"),
            EditorTool.Step => Loc.T("Editor.Size"),
            _ => Loc.T("Editor.Thickness"),
        };
        Show(FontSection, kind == EditorTool.Text);
        Show(FillSection, kind is EditorTool.Rectangle or EditorTool.Ellipse or EditorTool.Text or EditorTool.Step);
        FillBox.Content = kind switch
        {
            EditorTool.Step => Loc.T("Editor.Letters"),
            EditorTool.Text => Loc.T("Editor.WhiteBackground"),
            _ => Loc.T("Editor.Fill"),
        };
        Show(VariantSection, kind is EditorTool.Arrow or EditorTool.Step);
        if (kind != _variantKind && kind is EditorTool.Arrow or EditorTool.Step)
        {
            _syncingUi = true;
            try
            {
                _variantKind = kind;
                VariantLabel.Text = kind == EditorTool.Arrow ? Loc.T("Editor.ArrowHeads") : Loc.T("Editor.Shape");
                VariantBox.ItemsSource = kind == EditorTool.Arrow ? ArrowHeadNames : StepShapeNames;
            }
            finally
            {
                _syncingUi = false;
            }
        }
        ShowStyle();
        Show(StampSection, kind == EditorTool.Stamp && selected == null);
        Show(DeleteSection, selected != null);
    }

    /// <summary>Shows the current style (the selection's when there is one) in the properties bar.</summary>
    private void ShowStyle()
    {
        var style = AnnotCanvas.CurrentStyle;
        _syncingUi = true;
        try
        {
            CurrentColorSwatch.Background = new SolidColorBrush(style.Color);
            ThicknessBox.SelectedItem = Thicknesses.Contains(style.Thickness) ? style.Thickness : null;
            FontSizeBox.SelectedItem = FontSizes.Contains(style.FontSize) ? style.FontSize : null;
            FillBox.IsChecked = _contextKind == EditorTool.Step ? style.Letters : style.Fill;
            if (_contextKind == EditorTool.Arrow) VariantBox.SelectedIndex = (int)style.Heads;
            else if (_contextKind == EditorTool.Step) VariantBox.SelectedIndex = (int)style.Shape;
        }
        finally
        {
            _syncingUi = false;
        }
    }

    private static void Show(UIElement element, bool visible) =>
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;

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
            _textEditor.Foreground = new SolidColorBrush(_editingText.Color);
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
        CurrentColorSwatch.Background = new SolidColorBrush(color);
    }

    private void OnCustomColor(object sender, RoutedEventArgs e)
    {
        var current = AnnotCanvas.CurrentStyle.Color;
        using var dialog = new WinForms.ColorDialog
        {
            FullOpen = true,
            Color = System.Drawing.Color.FromArgb(current.R, current.G, current.B),
        };
        if (dialog.ShowDialog() == WinForms.DialogResult.OK)
            SetCurrentColor(Color.FromRgb(dialog.Color.R, dialog.Color.G, dialog.Color.B), applyToSelection: true);
    }

    private void OnThicknessChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || ThicknessBox.SelectedItem is not double thickness) return;
        ChangeStyle(s => s with { Thickness = thickness });
        AnnotCanvas.Focus();
    }

    private void OnFontSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || FontSizeBox.SelectedItem is not double size) return;
        ChangeStyle(s => s with { FontSize = size });
        if (_textEditor != null) _textEditor.Focus();
        else AnnotCanvas.Focus();
    }

    private void OnVariantChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _syncingUi || VariantBox.SelectedIndex < 0) return;
        int index = VariantBox.SelectedIndex;
        if (_contextKind == EditorTool.Arrow) ChangeStyle(s => s with { Heads = (ArrowHeads)index });
        else if (_contextKind == EditorTool.Step) ChangeStyle(s => s with { Shape = (StepShape)index });
        AnnotCanvas.Focus();
    }

    private void OnFillChanged(object sender, RoutedEventArgs e)
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
                FontWeight = FontWeights.SemiBold,
                FontSize = 13,
                Margin = new Thickness(2, StampCatalog.Children.Count == 0 ? 4 : 14, 0, 6),
            });
            var panel = new WrapPanel();
            foreach (var stamp in StampLibrary.All.Where(s => s.Category == category))
            {
                double height = stamp.Category == StampLibrary.TextStamps ? 30 : 40;
                var button = new Button
                {
                    Style = (Style)FindResource("FlatButton"),
                    Padding = new Thickness(5),
                    Margin = new Thickness(1),
                    ToolTip = stamp.Name,
                    Content = new Image { Source = stamp.Preview, Height = height, Stretch = Stretch.Uniform, FlowDirection = FlowDirection.LeftToRight },
                };
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
        CurrentStampImage.Source = StampLibrary.Find(id)?.Preview;
    }

    private void OnStampPickerOpen(object sender, RoutedEventArgs e) => StampPopup.IsOpen = true;

    private void OnStampPickerClosed(object? sender, EventArgs e) => StampPickerButton.IsChecked = false;

    private void OnStampSizeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_ready) return;
        AnnotCanvas.CurrentStampScale = e.NewValue;
        StampSizeText.Text = e.NewValue.ToString("P0", Loc.Culture);
    }

    // ---- Presets ----

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
    };

    /// <summary>A mouse click on a tool button shows its presets (keyboard shortcuts do not).</summary>
    private void OnToolClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not RadioButton { Tag: string tag } button) return;
        var tool = Enum.Parse<EditorTool>(tag);
        if (!ToolPresets.Supports(tool) || (PresetPopup.IsOpen && _presetTool == tool))
        {
            PresetPopup.IsOpen = false;
            return;
        }
        _presetTool = tool;
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
            empty.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
            PresetPanel.Children.Add(empty);
        }
    }

    private FrameworkElement CreatePresetTile(ToolPreset preset, int number)
    {
        var numberText = new TextBlock
        {
            Text = number.ToString(),
            FontSize = 10.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 6, 3),
        };
        // Theme colors by reference: they follow a switch to the light or dark theme.
        numberText.SetResourceReference(TextBlock.ForegroundProperty, "MutedTextBrush");
        var apply = new Button
        {
            Style = (Style)FindResource("FlatButton"),
            Width = 124,
            Height = 58,
            Padding = new Thickness(0),
            ToolTip = preset.Description,
            Content = new Grid
            {
                Children =
                {
                    new Image
                    {
                        Source = preset.CreatePreview(), Width = 105, Height = 42, Stretch = Stretch.Uniform,
                        FlowDirection = FlowDirection.LeftToRight,
                    },
                    numberText,
                },
            },
        };
        apply.SetResourceReference(BackgroundProperty, "TileBrush");
        apply.Click += (_, _) => ApplyPreset(preset);

        var remove = new Button
        {
            Style = (Style)FindResource("FlatButton"),
            Padding = new Thickness(3),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 2, 2, 0),
            ToolTip = Loc.T("Preset.RemoveTip"),
            Content = new System.Windows.Shapes.Path
            {
                Style = (Style)FindResource("Icon"),
                Data = (Geometry)FindResource("IconClose"),
                Width = 8,
                Height = 8,
                Stretch = Stretch.Uniform,
                StrokeThickness = 2,
            },
        };
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

    private void OnPresetAdd(object sender, RoutedEventArgs e)
    {
        // Only the settings of this tool, so that identical presets compare equal.
        var preset = new ToolPreset(_presetTool, DrawingStyle.Default.Merge(_presetTool, AnnotCanvas.CurrentStyle));
        if (!ToolPresets.Add(preset)) InfoText.Text = Loc.T("Preset.AlreadyExists");
        BuildPresetPanel();
    }

    private void OnPresetReset(object sender, RoutedEventArgs e)
    {
        ToolPresets.Reset(_presetTool);
        BuildPresetPanel();
    }

    private void OnPresetPopupClose(object sender, RoutedEventArgs e) => PresetPopup.IsOpen = false;

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
        InfoText.Text = Loc.T("Editor.Info.Size", image.PixelWidth, image.PixelHeight) + "   •   " + annotations
                        + (_document.IsDirty ? "   •   " + Loc.T("Editor.Info.Modified") : "");
        Title = (_document.IsDirty ? "• " : "") + Loc.T("Editor.WindowTitle", Path.GetFileName(_document.FilePath));
        bool deleteShown = DeleteSection.Visibility == Visibility.Visible;
        if (deleteShown != hasSelection) UpdateContext();
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
            FontWeight = FontWeights.SemiBold,
            FontSize = text.FontSize,
            Foreground = new SolidColorBrush(text.Color),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(0),
            AcceptsReturn = true,
            MinWidth = 80,
        };
        box.SetResourceReference(BorderBrushProperty, "AccentBrush");
        Canvas.SetLeft(box, text.Position.X + TextAnnotation.Pad - 3);
        Canvas.SetTop(box, text.Position.Y + TextAnnotation.Pad - 1);
        box.LostKeyboardFocus += (_, _) => EndTextEdit();
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape || (e.Key == Key.Enter && Keyboard.Modifiers == ModifierKeys.Control))
            {
                e.Handled = true;
                EndTextEdit();
                AnnotCanvas.Focus();
            }
        };
        TextLayer.Children.Add(box);
        _textEditor = box;
        Dispatcher.BeginInvoke(DispatcherPriority.Input, () =>
        {
            box.Focus();
            box.CaretIndex = box.Text.Length;
        });
    }

    private void EndTextEdit()
    {
        if (_textEditor is not { } box || _editingText is not { } text || _textCheckpoint is not { } checkpoint) return;
        _textEditor = null;
        _editingText = null;
        _textCheckpoint = null;
        TextLayer.Children.Remove(box);
        AnnotCanvas.SetEditingText(null);

        var value = box.Text.TrimEnd();
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

    // ---- Commands ----

    private void OnSave(object sender, RoutedEventArgs e) => Save();

    private bool Save()
    {
        EndTextEdit();
        try
        {
            _document.Save(App.Instance.Settings.JpegQuality);
            InfoText.Text = Loc.T("Editor.Saved");
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Enregistrement", ex);
            MessageBox.Show(this, Loc.T("Editor.SaveError", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
            return false;
        }
    }

    private void OnCopy(object sender, RoutedEventArgs e) => Copy();

    private void Copy()
    {
        EndTextEdit();
        try
        {
            ClipboardHelper.SetImage(_document.Flatten());
            InfoText.Text = Loc.T("Editor.Copied");
        }
        catch (ExternalException ex)
        {
            // The clipboard stayed locked by another application.
            ErrorLog.Write("Copie dans le presse-papiers", ex);
            MessageBox.Show(this, Loc.T("Editor.CopyError"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void OnExport(object sender, RoutedEventArgs e) => Export();

    private void Export()
    {
        EndTextEdit();
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("Editor.ExportTitle"),
            Filter = $"{Loc.T("Editor.ImageFormat", "PNG")}|*.png|{Loc.T("Editor.ImageFormat", "JPEG")}|*.jpg|{Loc.T("Editor.ImageFormat", "BMP")}|*.bmp",
            FileName = Path.GetFileNameWithoutExtension(_document.FilePath),
        };
        if (dialog.ShowDialog(this) != true) return;

        var path = dialog.FileName;
        var extension = Path.GetExtension(path).ToLowerInvariant();
        if (!ExportExtensions.Contains(extension) && extension != ".jpeg")
        {
            // Unsupported extension typed by hand: the format chosen in the list is added.
            path += ExportExtensions[Math.Clamp(dialog.FilterIndex - 1, 0, ExportExtensions.Length - 1)];
            if (File.Exists(path) && MessageBox.Show(this, Loc.T("Editor.ExportOverwrite", Path.GetFileName(path)), "Snaplume",
                    MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                return;
        }
        if (string.Equals(Path.GetFullPath(path), Path.GetFullPath(_document.FilePath), StringComparison.OrdinalIgnoreCase))
        {
            // Overwriting the capture would bypass its editable annotations: that is what Save is for.
            MessageBox.Show(this, Loc.T("Editor.ExportSameFile"), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            ImageIO.Save(_document.Flatten(), path, App.Instance.Settings.JpegQuality);
            InfoText.Text = Loc.T("Editor.Exported", Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Export", ex);
            MessageBox.Show(this, Loc.T("Editor.ExportError", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>Text of the image as it is now, annotations included.</summary>
    private void OnReadText(object sender, RoutedEventArgs e)
    {
        EndTextEdit();
        App.Instance.ShowOcr(_document.Flatten());
    }

    private void OnResize(object sender, RoutedEventArgs e) => Resize();

    private void Resize()
    {
        EndTextEdit();
        AnnotCanvas.CancelOperation();
        var image = _document.BaseImage;
        var dialog = new ResizeDialog(this, image.PixelWidth, image.PixelHeight);
        if (dialog.ShowDialog() != true) return;
        try
        {
            _document.Resize(dialog.NewWidth, dialog.NewHeight);
        }
        catch (Exception ex)
        {
            // Mostly not enough memory for a huge image; the document is left as it was.
            ErrorLog.Write("Redimensionnement", ex);
            MessageBox.Show(this, Loc.T("Editor.ResizeError", ex.Message), "Snaplume", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        ZoomToFit(onlyShrink: true);
        InfoText.Text = Loc.T("Editor.Resized", dialog.NewWidth, dialog.NewHeight);
    }

    private void OnUndo(object sender, RoutedEventArgs e) => Undo();

    private void OnRedo(object sender, RoutedEventArgs e) => Redo();

    private void Undo()
    {
        EndTextEdit();
        // During a drawing or a drag, undo only cancels it.
        if (AnnotCanvas.CancelOperation()) return;
        _document.Undo();
    }

    private void Redo()
    {
        EndTextEdit();
        AnnotCanvas.CancelOperation();
        _document.Redo();
    }

    private void OnDeleteSelected(object sender, RoutedEventArgs e) => DeleteSelected();

    private void DeleteSelected()
    {
        EndTextEdit();
        if (AnnotCanvas.IsDragging || AnnotCanvas.Selected is not { } selected) return;
        _document.PushUndo();
        _document.Annotations.Remove(selected);
        _document.NotifyChanged();
    }

    private void Nudge(Vector offset)
    {
        EndTextEdit();
        if (AnnotCanvas.IsDragging || AnnotCanvas.Selected is not { } selected) return;
        bool sameStep = ReferenceEquals(selected, _nudged) && _document.Version == _nudgeVersion;
        if (!sameStep) _document.PushUndo();
        selected.Translate(offset);
        if (sameStep) _document.AmendLastChange();
        else _document.NotifyChanged();
        _nudged = selected;
        _nudgeVersion = _document.Version;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (e.Handled) return;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;

        // Saving works even while a text is being typed (the edit is validated first).
        if (key == Key.S && (modifiers == ModifierKeys.Control || modifiers == (ModifierKeys.Control | ModifierKeys.Shift)))
        {
            e.Handled = true;
            bool typing = _textEditor != null;
            if (modifiers == ModifierKeys.Control) Save();
            else Export();
            if (typing) AnnotCanvas.Focus();
            return;
        }
        if (_textEditor != null || e.OriginalSource is TextBox) return;

        if (modifiers == ModifierKeys.Control)
        {
            switch (key)
            {
                case Key.Z: Undo(); break;
                case Key.Y: Redo(); break;
                case Key.C: Copy(); break;
                case Key.R: Resize(); break;
                case Key.D0 or Key.NumPad0: SetZoom(1); break;
                case Key.OemPlus or Key.Add: SetZoom(_zoom * 1.25); break;
                case Key.OemMinus or Key.Subtract: SetZoom(_zoom / 1.25); break;
                default: return;
            }
            e.Handled = true;
            return;
        }
        if (modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            if (key != Key.Z) return;
            Redo();
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None && modifiers != ModifierKeys.Shift) return;

        double step = modifiers == ModifierKeys.Shift ? 10 : 1;
        bool inCombo = e.OriginalSource is ComboBox or ComboBoxItem;
        switch (key)
        {
            case Key.Delete or Key.Back: DeleteSelected(); break;
            case Key.Escape:
                if (PresetPopup.IsOpen)
                {
                    PresetPopup.IsOpen = false;
                    break;
                }
                if (AnnotCanvas.CancelOperation()) break;
                if (AnnotCanvas.Selected != null) AnnotCanvas.Select(null);
                else SelectTool(EditorTool.Select);
                break;
            case Key.Left when !inCombo && AnnotCanvas.Selected != null: Nudge(new Vector(-step, 0)); break;
            case Key.Right when !inCombo && AnnotCanvas.Selected != null: Nudge(new Vector(step, 0)); break;
            case Key.Up when !inCombo && AnnotCanvas.Selected != null: Nudge(new Vector(0, -step)); break;
            case Key.Down when !inCombo && AnnotCanvas.Selected != null: Nudge(new Vector(0, step)); break;
            default:
                if (modifiers != ModifierKeys.None || inCombo || !ToolKeys.TryGetValue(key, out var tool)) return;
                SelectTool(tool);
                break;
        }
        e.Handled = true;
    }

    // ---- Zoom ----

    private void ApplyZoom()
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        double scale = _zoom / dpi; // 100 % = one image pixel per physical pixel
        ZoomTransform.ScaleX = ZoomTransform.ScaleY = scale;
        AnnotCanvas.ViewScale = scale;
        RenderOptions.SetBitmapScalingMode(AnnotCanvas, _zoom >= 2 ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
        ZoomText.Text = _zoom.ToString("P0", Loc.Culture);
    }

    private void SetZoom(double zoom)
    {
        _zoom = Math.Clamp(zoom, 0.05, 16);
        ApplyZoom();
    }

    private void ZoomToFit(bool onlyShrink)
    {
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var image = _document.BaseImage;
        double availableWidth = Math.Max(50, Scroller.ActualWidth - 72);
        double availableHeight = Math.Max(50, Scroller.ActualHeight - 72);
        double fit = Math.Min(availableWidth * dpi / image.PixelWidth, availableHeight * dpi / image.PixelHeight);
        SetZoom(onlyShrink ? Math.Min(1, fit) : fit);
    }

    private void OnScrollerWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        SetZoom(_zoom * (e.Delta > 0 ? 1.25 : 0.8));
        e.Handled = true;
    }

    private void OnZoomIn(object sender, RoutedEventArgs e) => SetZoom(_zoom * 1.25);
    private void OnZoomOut(object sender, RoutedEventArgs e) => SetZoom(_zoom * 0.8);
    private void OnZoomActual(object sender, RoutedEventArgs e) => SetZoom(1);
    private void OnZoomFit(object sender, RoutedEventArgs e) => ZoomToFit(onlyShrink: false);

    // ---- Closing ----

    protected override void OnClosing(CancelEventArgs e)
    {
        EndTextEdit();
        if (_document.IsDirty)
        {
            var answer = MessageBox.Show(this, Loc.T("Editor.SaveChangesQuestion"), "Snaplume",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (answer == MessageBoxResult.Cancel || (answer == MessageBoxResult.Yes && !Save()))
            {
                e.Cancel = true;
                return;
            }
        }
        base.OnClosing(e);
    }
}
