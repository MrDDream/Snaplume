using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Snaplume.Core;

namespace Snaplume.Editor;

/// <summary>
/// Choice of a color (opaque): basic colors, a saturation/brightness area with a hue bar, and the hexadecimal
/// code. Replaces the Windows color dialog of older versions, the same on every system.
/// </summary>
public sealed class ColorPickerDialog : Window
{
    /// <summary>The basic colors of the classic color dialog, by columns of shades.</summary>
    private static readonly string[] BasicColors =
    {
        "#FF8080", "#FFFF80", "#80FF80", "#00FF80", "#80FFFF", "#0080FF", "#FF80C0", "#FF80FF",
        "#FF0000", "#FFFF00", "#80FF00", "#00FF40", "#00FFFF", "#0080C0", "#8080C0", "#FF00FF",
        "#804040", "#FF8040", "#00FF00", "#008080", "#004080", "#8080FF", "#800040", "#FF0080",
        "#800000", "#FF8000", "#008000", "#008040", "#0000FF", "#0000A0", "#800080", "#8000FF",
        "#400000", "#804000", "#004000", "#004040", "#000080", "#000040", "#400040", "#400080",
        "#000000", "#808000", "#808040", "#808080", "#408080", "#C0C0C0", "#E0E0E0", "#FFFFFF",
    };

    private readonly ColorSpectrum _spectrum = new();
    private readonly HueBar _hue = new();
    private readonly Border _newSwatch = new() { Width = 56, Height = 28, CornerRadius = new CornerRadius(0, 4, 4, 0) };
    private readonly TextBox _hexBox = new() { Width = 96, MaxLength = 7 };
    private bool _syncing;
    private Color _color;
    private bool _accepted;

    private ColorPickerDialog(Color current)
    {
        Title = Loc.T("Editor.ColorPicker.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        ShowInTaskbar = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = Loc.IsRightToLeft ? FlowDirection.RightToLeft : FlowDirection.LeftToRight;

        var basic = new WrapPanel { Width = 8 * 26, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var hex in BasicColors)
        {
            var color = HexColor.Parse(hex);
            var swatch = new Button
            {
                Width = 22,
                Height = 22,
                Margin = new Thickness(0, 0, 4, 4),
                Padding = default,
                Focusable = false,
                Background = new ImmutableSolidColorBrush(color),
                BorderBrush = new ImmutableSolidColorBrush(Color.FromArgb(0x40, 0, 0, 0)),
                BorderThickness = new Thickness(1),
            };
            swatch.Classes.Add("swatch");
            ToolTip.SetTip(swatch, hex);
            swatch.Click += (_, _) => SetColor(color, fromSpectrum: false);
            basic.Children.Add(swatch);
        }

        _spectrum.Width = 220;
        _spectrum.Height = 160;
        _hue.Width = 220;
        _hue.Height = 16;
        _hue.Margin = new Thickness(0, 10, 0, 0);
        ToolTip.SetTip(_hue, Loc.T("Editor.ColorPicker.Hue"));
        _spectrum.Changed += () => SetColor(new HsvColor(1, _hue.Hue, _spectrum.Saturation, _spectrum.Value).ToRgb(), fromSpectrum: true);
        _hue.Changed += () =>
        {
            _spectrum.Hue = _hue.Hue;
            SetColor(new HsvColor(1, _hue.Hue, _spectrum.Saturation, _spectrum.Value).ToRgb(), fromSpectrum: true);
        };

        var currentSwatch = new Border
        {
            Width = 56,
            Height = 28,
            CornerRadius = new CornerRadius(4, 0, 0, 4),
            Background = new ImmutableSolidColorBrush(Color.FromRgb(current.R, current.G, current.B)),
        };
        ToolTip.SetTip(currentSwatch, Loc.T("Editor.ColorPicker.Current"));
        ToolTip.SetTip(_newSwatch, Loc.T("Editor.ColorPicker.New"));
        _hexBox.TextChanged += (_, _) =>
        {
            if (_syncing) return;
            var text = _hexBox.Text?.Trim() ?? "";
            if (!text.StartsWith('#')) text = "#" + text;
            if (text.Length == 7 && HexColor.TryParse(text, out var typed)) SetColor(typed, fromSpectrum: false, fromText: true);
        };
        ToolTip.SetTip(_hexBox, Loc.T("Editor.ColorPicker.HexTip"));

        var ok = new Button { Content = Loc.T("Dialog.Ok"), MinWidth = 88, IsDefault = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        var cancel = new Button { Content = Loc.T("Dialog.Cancel"), MinWidth = 88, IsCancel = true, HorizontalContentAlignment = HorizontalAlignment.Center };
        cancel.Click += (_, _) => Close();

        var left = new StackPanel
        {
            Children =
            {
                Label(Loc.T("Editor.ColorPicker.Basic")),
                basic,
            },
        };
        var right = new StackPanel
        {
            Margin = new Thickness(20, 0, 0, 0),
            Children =
            {
                Label(Loc.T("Editor.ColorPicker.Custom")),
                // The color area is a picture of colors: never mirrored.
                new Border { FlowDirection = FlowDirection.LeftToRight, Margin = new Thickness(0, 4, 0, 0), Child = new StackPanel { Children = { _spectrum, _hue } } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 12, 0, 0),
                    Spacing = 10,
                    Children =
                    {
                        new StackPanel { Orientation = Orientation.Horizontal, Children = { currentSwatch, _newSwatch } },
                        new TextBlock { Text = Loc.T("Editor.ColorPicker.Hex"), VerticalAlignment = VerticalAlignment.Center },
                        _hexBox,
                    },
                },
            },
        };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 8,
            Margin = new Thickness(0, 18, 0, 0),
            Children = { ok, cancel },
        };
        var body = new DockPanel { Margin = new Thickness(20, 16, 20, 18) };
        DockPanel.SetDock(buttons, Dock.Bottom);
        body.Children.Add(buttons);
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { left, right } });
        Content = body;

        SetColor(Color.FromRgb(current.R, current.G, current.B), fromSpectrum: false);
        Opened += (_, _) => _hexBox.Focus();
    }

    private static TextBlock Label(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 0, 4) };

    /// <summary>Asks for a color; null when cancelled.</summary>
    public static async Task<Color?> PickAsync(Window owner, Color current)
    {
        var dialog = new ColorPickerDialog(current);
        await dialog.ShowDialog(owner);
        return dialog._accepted ? dialog._color : null;
    }

    private void SetColor(Color color, bool fromSpectrum, bool fromText = false)
    {
        _color = Color.FromRgb(color.R, color.G, color.B);
        _newSwatch.Background = new ImmutableSolidColorBrush(_color);
        if (!fromSpectrum)
        {
            var hsv = _color.ToHsv();
            // A gray has no hue: the bar keeps the one chosen before.
            if (hsv.S > 0) _hue.Hue = _spectrum.Hue = hsv.H;
            _spectrum.SetPosition(hsv.S, hsv.V);
        }
        if (!fromText)
        {
            _syncing = true;
            _hexBox.Text = HexColor.Format(_color)[3..].Insert(0, "#");
            _syncing = false;
        }
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key != Key.Escape || e.Handled) return;
        e.Handled = true;
        Close();
    }

    /// <summary>Saturation from left to right, brightness from top to bottom, for one hue.</summary>
    private sealed class ColorSpectrum : Control
    {
        private static readonly IPen Ring = new ImmutablePen(Brushes.White.ToImmutable(), 2);
        private static readonly IPen RingShadow = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 4);
        /// <summary>Brightness: transparent at the top, black at the bottom, whatever the hue.</summary>
        private static readonly IBrush ValueBrush = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
            GradientStops = { new GradientStop(Color.FromArgb(0, 0, 0, 0), 0), new GradientStop(Colors.Black, 1) },
        }.ToImmutable();
        private double _hue;
        /// <summary>Saturation, from white to the hue: made again only when the hue changes.</summary>
        private IBrush? _saturationBrush;

        public event Action? Changed;

        public ColorSpectrum() => Cursor = new Cursor(StandardCursorType.Cross);

        public double Saturation { get; private set; }
        public double Value { get; private set; } = 1;

        public double Hue
        {
            get => _hue;
            set
            {
                if (value != _hue) _saturationBrush = null;
                _hue = value;
                InvalidateVisual();
            }
        }

        public void SetPosition(double saturation, double value)
        {
            Saturation = Math.Clamp(saturation, 0, 1);
            Value = Math.Clamp(value, 0, 1);
            InvalidateVisual();
        }

        public override void Render(DrawingContext context)
        {
            var area = new Rect(Bounds.Size);
            _saturationBrush ??= new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
                GradientStops = { new GradientStop(Colors.White, 0), new GradientStop(new HsvColor(1, _hue, 1, 1).ToRgb(), 1) },
            }.ToImmutable();
            context.DrawRectangle(_saturationBrush, null, area, 4, 4);
            context.DrawRectangle(ValueBrush, null, area, 4, 4);
            var marker = new Point(Saturation * area.Width, (1 - Value) * area.Height);
            context.DrawEllipse(null, RingShadow, marker, 6, 6);
            context.DrawEllipse(null, Ring, marker, 6, 6);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            Pick(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured == this) Pick(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void Pick(Point p)
        {
            if (Bounds.Width <= 0 || Bounds.Height <= 0) return;
            SetPosition(p.X / Bounds.Width, 1 - p.Y / Bounds.Height);
            Changed?.Invoke();
        }
    }

    /// <summary>Every hue from left to right.</summary>
    private sealed class HueBar : Control
    {
        private static readonly IPen Marker = new ImmutablePen(Brushes.White.ToImmutable(), 2);
        private static readonly IPen MarkerShadow = new ImmutablePen(new ImmutableSolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)), 4);
        /// <summary>Every hue, from red to red again.</summary>
        private static readonly IBrush Hues = CreateHues();
        private double _hue;

        public event Action? Changed;

        public HueBar() => Cursor = new Cursor(StandardCursorType.Hand);

        private static IBrush CreateHues()
        {
            var brush = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(1, 0, RelativeUnit.Relative),
            };
            for (int i = 0; i <= 6; i++) brush.GradientStops.Add(new GradientStop(new HsvColor(1, i * 60, 1, 1).ToRgb(), i / 6.0));
            return brush.ToImmutable();
        }

        public double Hue
        {
            get => _hue;
            set
            {
                _hue = Math.Clamp(value, 0, 360);
                InvalidateVisual();
            }
        }

        public override void Render(DrawingContext context)
        {
            var area = new Rect(Bounds.Size);
            context.DrawRectangle(Hues, null, area, 4, 4);
            double x = _hue / 360 * area.Width;
            var marker = new Rect(x - 3, -2, 6, area.Height + 4);
            context.DrawRectangle(null, MarkerShadow, marker, 2, 2);
            context.DrawRectangle(null, Marker, marker, 2, 2);
        }

        protected override void OnPointerPressed(PointerPressedEventArgs e)
        {
            base.OnPointerPressed(e);
            e.Pointer.Capture(this);
            Pick(e.GetPosition(this));
            e.Handled = true;
        }

        protected override void OnPointerMoved(PointerEventArgs e)
        {
            base.OnPointerMoved(e);
            if (e.Pointer.Captured == this) Pick(e.GetPosition(this));
        }

        protected override void OnPointerReleased(PointerReleasedEventArgs e)
        {
            base.OnPointerReleased(e);
            e.Pointer.Capture(null);
        }

        private void Pick(Point p)
        {
            if (Bounds.Width <= 0) return;
            Hue = Math.Clamp(p.X / Bounds.Width, 0, 1) * 360;
            Changed?.Invoke();
        }
    }
}
