using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>
/// The editor window opened without a screen (Avalonia headless, Skia): layout, drawing with the mouse, keyboard
/// shortcuts, insertion and the close confirmation. The input goes to the headless window, never to the desktop.
/// </summary>
public class EditorWindowTests
{
    private static (EditorWindow Window, AnnotationCanvas Canvas, TempFolder Folder) Open(int width = 400, int height = 260)
    {
        var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(width, height, Color.FromRgb(0xDD, 0xE6, 0xF0)), path);
        var window = new EditorWindow(path) { Width = 1240, Height = 820 };
        window.Show();
        Dispatcher.UIThread.RunJobs();
        var canvas = window.FindControl<AnnotationCanvas>("AnnotCanvas")!;
        return (window, canvas, folder);
    }

    /// <summary>A point of the image, in the coordinates of the window.</summary>
    private static Point OnWindow(EditorWindow window, AnnotationCanvas canvas, double x, double y) =>
        canvas.TranslatePoint(new Point(x, y), window) ?? throw new InvalidOperationException("Canvas not laid out");

    [AvaloniaFact]
    public void Window_lays_out_and_shows_the_capture()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            Assert.Equal(400, canvas.Bounds.Width);
            Assert.Equal(260, canvas.Bounds.Height);
            Assert.EndsWith("capture.png", window.FilePath);
            Assert.Contains("capture.png", window.Title);

            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            var pixels = AvaloniaImages.ToPixelBuffer(frame!);
            // Kept with the test results to look at the layout; never committed.
            ImageIO.Save(pixels, Path.Combine(AppContext.BaseDirectory, "editor-window.png"));
            var center = OnWindow(window, canvas, 200, 130) * window.RenderScaling;
            Assert.True(Near(At(pixels, (int)center.X, (int)center.Y), Color.FromRgb(0xDD, 0xE6, 0xF0)));
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Annotations_and_effects_are_drawn_in_the_window()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            var document = canvas.Document!;
            document.PushUndo();
            document.Annotations.Add(new RectangleAnnotation { Start = new(20, 20), End = new(180, 120), Color = Colors.Red, Filled = true });
            document.Annotations.Add(new StepAnnotation { Center = new(300, 60), Shape = StepShape.Leader, Target = new(240, 100) });
            document.Annotations.Add(new TextAnnotation { Position = new(220, 150), Text = "Snaplume", HasBackground = true });
            document.Annotations.Add(new MagnifierAnnotation { Start = new(160, 100), End = new(200, 140), LensCenter = new(90, 200), Color = Colors.Black });
            document.SetEffects(ImageEffects.None with { Shadow = true, CornerRadius = 16, Border = true });
            Dispatcher.UIThread.RunJobs();

            var pixels = AvaloniaImages.ToPixelBuffer(window.CaptureRenderedFrame()!);
            // Kept with the test results to look at the drawing; never committed.
            ImageIO.Save(pixels, Path.Combine(AppContext.BaseDirectory, "editor-window-annotations.png"));
            var inRectangle = OnWindow(window, canvas, 100, 70) * window.RenderScaling;
            Assert.True(Near(At(pixels, (int)inRectangle.X, (int)inRectangle.Y), Colors.Red));
            // The rounded corner cuts the image: the background (and the edge of the shadow) shows there.
            var corner = OnWindow(window, canvas, 1, 1) * window.RenderScaling;
            Assert.False(Near(At(pixels, (int)corner.X, (int)corner.Y), Color.FromRgb(0xDD, 0xE6, 0xF0), 16));
            Assert.True(At(pixels, (int)corner.X, (int)corner.Y).R > 200);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Arrow_drawn_with_the_mouse_is_undone_with_ctrl_z()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Assert.Equal(EditorTool.Arrow, canvas.Tool);

            window.MouseDown(OnWindow(window, canvas, 50, 60), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 120, 90));
            window.MouseMove(OnWindow(window, canvas, 200, 120));
            window.MouseUp(OnWindow(window, canvas, 200, 120), MouseButton.Left);
            var arrow = Assert.IsType<ArrowAnnotation>(Assert.Single(canvas.Document!.Annotations));
            Assert.Equal(50, arrow.Start.X, 1);
            Assert.Equal(200, arrow.End.X, 1);
            Assert.Same(arrow, canvas.Selected);
            Assert.True(canvas.Document.IsDirty);

            // Arrow keys move the selection, as one undo step with the other moves.
            window.KeyPress(Key.Right, RawInputModifiers.Shift, PhysicalKey.ArrowRight, null);
            window.KeyPress(Key.Right, RawInputModifiers.None, PhysicalKey.ArrowRight, null);
            Assert.Equal(61, arrow.Start.X, 1);

            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal(50, ((ArrowAnnotation)canvas.Document.Annotations[0]).Start.X, 1);
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Empty(canvas.Document.Annotations);
            window.KeyPress(Key.Y, RawInputModifiers.Control, PhysicalKey.Y, "y");
            Assert.Single(canvas.Document.Annotations);

            // Delete removes the selection.
            canvas.Select(canvas.Document.Annotations[0]);
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Empty(canvas.Document.Annotations);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Inserted_image_floats_then_grows_the_capture_on_enter()
    {
        var (window, canvas, folder) = Open(200, 120);
        using (folder)
        {
            var insert = typeof(EditorWindow).GetMethod("InsertImageAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var task = (Task)insert.Invoke(window, new object[] { Solid(60, 40, Colors.Red) })!;
            Dispatcher.UIThread.RunJobs();
            Assert.True(task.IsCompletedSuccessfully);
            Assert.True(canvas.HasFloatingImage);
            Assert.Empty(canvas.Document!.Annotations);

            // Dragged past the right edge: the capture grows when the image is placed (Enter).
            var start = canvas.FloatingRect.Center;
            window.MouseDown(OnWindow(window, canvas, start.X, start.Y), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, start.X + 150, start.Y));
            window.MouseUp(OnWindow(window, canvas, start.X + 150, start.Y), MouseButton.Left);
            Assert.True(canvas.FloatingRect.Right > 200);
            // Still grabbed where it is outside the capture; Alt moves it without sticking to the edges.
            var outside = new Point(canvas.FloatingRect.Right - 5, canvas.FloatingRect.Center.Y);
            Assert.True(outside.X > 200);
            double left = canvas.FloatingRect.Left;
            window.MouseDown(OnWindow(window, canvas, outside.X, outside.Y), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, outside.X + 7, outside.Y), RawInputModifiers.Alt);
            window.MouseUp(OnWindow(window, canvas, outside.X + 7, outside.Y), MouseButton.Left, RawInputModifiers.Alt);
            Assert.Equal(left + 7, canvas.FloatingRect.Left, 1);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Assert.False(canvas.HasFloatingImage);
            Assert.True(canvas.Document.BaseImage.Width > 200);
            Assert.Equal(120, canvas.Document.BaseImage.Height);

            // One undo step puts the capture back.
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal(200, canvas.Document.BaseImage.Width);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Closing_with_changes_asks_first_and_closes_after_the_answer()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            canvas.Document!.PushUndo();
            canvas.Document.Annotations.Add(new LineAnnotation { Start = new(1, 1), End = new(50, 50) });
            canvas.Document.NotifyChanged();
            bool closed = false;
            window.Closed += (_, _) => closed = true;

            window.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.False(closed);
            var question = Assert.Single(window.OwnedWindows);
            // "No": the changes are dropped and the window closes.
            var no = question.GetLogicalDescendants().OfType<Button>().Single(b => (string?)b.Content == Loc.T("Dialog.No"));
            no.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
        }
    }

    [AvaloniaFact]
    public void Tool_shortcuts_and_properties_bar_follow_the_tool()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            var thickness = window.FindControl<StackPanel>("ThicknessSection")!;
            var font = window.FindControl<StackPanel>("FontSection")!;
            window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
            Assert.Equal(EditorTool.Text, canvas.Tool);
            Assert.True(font.IsVisible);
            Assert.False(thickness.IsVisible);
            window.KeyPress(Key.B, RawInputModifiers.None, PhysicalKey.B, "b");
            Assert.Equal(EditorTool.Pixelate, canvas.Tool);
            Assert.True(thickness.IsVisible);
            Assert.False(font.IsVisible);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Equal(EditorTool.Select, canvas.Tool);

            // A text typed in the editing box becomes an annotation when Escape ends the edit.
            window.KeyPress(Key.T, RawInputModifiers.None, PhysicalKey.T, "t");
            window.MouseDown(OnWindow(window, canvas, 40, 40), MouseButton.Left);
            window.MouseUp(OnWindow(window, canvas, 40, 40), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("Bonjour");
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            var text = Assert.IsType<TextAnnotation>(Assert.Single(canvas.Document!.Annotations));
            Assert.Equal("Bonjour", text.Text);

            // Ctrl+Enter also ends the edit (the text box would take it as a new line).
            window.MouseDown(OnWindow(window, canvas, 40, 150), MouseButton.Left);
            window.MouseUp(OnWindow(window, canvas, 40, 150), MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            window.KeyTextInput("Ligne");
            window.KeyPress(Key.Enter, RawInputModifiers.Control, PhysicalKey.Enter, null);
            Assert.Equal(2, canvas.Document.Annotations.Count);
            Assert.Equal("Ligne", ((TextAnnotation)canvas.Document.Annotations[1]).Text);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Border_preset_applies_in_one_undo_step_and_shows_as_selected()
    {
        // The application's themes, so that the gallery is drawn as in the application.
        var app = Application.Current!;
        var colors = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        var icons = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(colors);
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        var (window, canvas, folder) = Open();
        try
        {
            using (folder)
            {
                var document = canvas.Document!;
                document.Annotations.Add(new ArrowAnnotation { Start = new(40, 200), End = new(300, 60), Color = Colors.Red, Thickness = 6 });
                var before = ImageEffects.None with { Background = Color.FromRgb(0x1F, 0x29, 0x37), Shadow = true, ShadowBlur = 30 };
                document.SetEffects(before);
                Dispatcher.UIThread.RunJobs();

                var button = window.FindControl<ToggleButton>("BordersButton")!;
                var popup = window.FindControl<Popup>("BordersPopup")!;
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), window)!.Value;
                window.MouseDown(center, MouseButton.Left);
                window.MouseUp(center, MouseButton.Left);
                Dispatcher.UIThread.RunJobs();
                Assert.True(popup.IsOpen);
                Assert.True(button.IsChecked);

                var tiles = window.FindControl<WrapPanel>("BordersPanel")!.Children.OfType<Button>().ToList();
                Assert.Equal(BorderPresets.All.Count, tiles.Count);
                // Custom effects: no preset is outlined; every sample is drawn.
                Assert.DoesNotContain(tiles, t => t.Classes.Contains("selected"));
                Assert.All(tiles, t => Assert.True(t.GetVisualDescendants().OfType<DrawingPreview>().Single().Bounds.Width > 0));
                SaveRendering(popup, "editor-borders.png");

                // One click: the effects of the preset, the background of the image kept, as one undo step.
                int index = BorderPresets.All.ToList().FindIndex(p => p.Id == "WhiteFrame");
                var preset = BorderPresets.All[index];
                tiles[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(preset.Effects with { Background = before.Background }, document.Effects);
                Assert.Same(preset, BorderPresets.Find(document.Effects));
                Assert.True(popup.IsOpen);
                Assert.Equal(new[] { index }, tiles.Select((t, i) => (t, i)).Where(x => x.t.Classes.Contains("selected")).Select(x => x.i));
                SaveRendering(popup, "editor-borders-selected.png");

                // Clicked again: nothing changes, no new undo step.
                long version = document.Version;
                tiles[index].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(version, document.Version);

                window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(before, document.Effects);
                tiles = window.FindControl<WrapPanel>("BordersPanel")!.Children.OfType<Button>().ToList();
                Assert.DoesNotContain(tiles, t => t.Classes.Contains("selected"));

                // "None", then the advanced settings open the effects panel in place of the gallery.
                tiles[0].RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.True(document.Effects.IsEmpty);
                Assert.Contains("selected", tiles[0].Classes);
                window.FindControl<Button>("BordersAdvancedButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Dispatcher.UIThread.RunJobs();
                Assert.False(popup.IsOpen);
                Assert.False(button.IsChecked);
                Assert.True(window.FindControl<Popup>("EffectsPopup")!.IsOpen);
                window.FindControl<ToggleButton>("EffectsButton")!.IsChecked = false;

                // Kept with the test results to look at the gallery in dark mode and right to left; never committed.
                app.RequestedThemeVariant = ThemeVariant.Dark;
                window.FlowDirection = FlowDirection.RightToLeft;
                document.SetEffects(preset.Effects);
                button.IsChecked = true;
                Dispatcher.UIThread.RunJobs();
                SaveRendering(popup, "editor-borders-dark-rtl.png");
                SaveRendering(window, "editor-window-dark-rtl.png");
                button.IsChecked = false;
                Dispatcher.UIThread.RunJobs();
                ForceClose(window);
            }
        }
        finally
        {
            app.RequestedThemeVariant = ThemeVariant.Default;
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(colors);
        }
    }

    /// <summary>The window, or what a popup shows, saved with the test results to look at it.</summary>
    private static void SaveRendering(object shown, string name)
    {
        var topLevel = shown is Popup popup ? TopLevel.GetTopLevel(popup.Child) : shown as TopLevel;
        Assert.NotNull(topLevel);
        var frame = topLevel!.CaptureRenderedFrame();
        Assert.NotNull(frame);
        ImageIO.Save(AvaloniaImages.ToPixelBuffer(frame!), Path.Combine(AppContext.BaseDirectory, name));
    }

    /// <summary>Closes without the save question (the document is left unsaved on purpose).</summary>
    private static void ForceClose(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closeConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
    }
}
