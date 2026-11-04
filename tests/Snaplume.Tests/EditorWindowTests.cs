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
[Collection(ApplicationState.Name)]
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

    [AvaloniaFact]
    public async Task Save_runs_in_the_background_and_a_close_meanwhile_waits_for_it()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            var document = canvas.Document!;
            var before = document.BeginChange();
            document.Annotations.Add(new RectangleAnnotation { Start = new(20, 20), End = new(120, 80), Color = Colors.Red, Filled = true });
            document.CommitChange(before);
            bool closed = false;
            window.Closed += (_, _) => closed = true;
            var saveMethod = typeof(EditorWindow).GetMethod("SaveAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;

            var save = (Task<bool>)saveMethod.Invoke(window, null)!;
            // While the files are written: the commands are disabled, a second save does nothing, a close waits.
            var saveButton = window.FindControl<Button>("SaveButton")!;
            Assert.False(save.IsCompleted);
            Assert.False(saveButton.IsEnabled);
            var second = (Task<bool>)saveMethod.Invoke(window, null)!;
            Assert.True(second.IsCompleted);
            Assert.False(second.Result);
            window.Close();
            Assert.False(closed);

            Assert.True(await save);
            Assert.False(document.IsDirty);
            Assert.True(saveButton.IsEnabled);
            Dispatcher.UIThread.RunJobs();
            Assert.True(closed);
            Assert.True(Near(At(ImageIO.Load(window.FilePath), 70, 50), Colors.Red));
        }
    }

    [AvaloniaFact]
    public void Changes_are_told_once_and_once_done()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            var document = canvas.Document!;
            var counts = new List<int>();
            document.Changed += () => counts.Add(document.Annotations.Count);

            // A bullet is added in one click: the window is told with the bullet there.
            window.KeyPress(Key.N, RawInputModifiers.None, PhysicalKey.N, "n");
            window.MouseDown(OnWindow(window, canvas, 100, 100), MouseButton.Left);
            window.MouseUp(OnWindow(window, canvas, 100, 100), MouseButton.Left);
            var step = Assert.IsType<StepAnnotation>(Assert.Single(document.Annotations));
            Assert.NotEmpty(counts);
            Assert.All(counts, count => Assert.Equal(1, count));

            // A style applied to the selection: told once, with the new style.
            var colors = new List<Color>();
            document.Changed += () => colors.Add(step.Color);
            canvas.Select(step);
            canvas.ApplyToSelection(a => a.Color = Colors.Blue);
            Assert.Equal(new[] { Colors.Blue }, colors);

            // Deleted: told once, without it.
            counts.Clear();
            window.KeyPress(Key.Delete, RawInputModifiers.None, PhysicalKey.Delete, null);
            Assert.Equal(new[] { 0 }, counts);
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Single(document.Annotations);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Escape_closes_the_border_gallery_and_frees_its_samples()
    {
        var (window, canvas, folder) = Open();
        using (folder)
        {
            canvas.Document!.SetEffects(ImageEffects.None with { Shadow = true });
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Dispatcher.UIThread.RunJobs();
            var popup = window.FindControl<Popup>("BordersPopup")!;
            window.FindControl<ToggleButton>("BordersButton")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(popup.IsOpen);
            var layouts = (List<EffectsLayout>)typeof(EditorWindow).GetField("_borderLayouts", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(window)!;
            Assert.NotEmpty(layouts);

            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(popup.IsOpen);
            Assert.Empty(layouts);
            // That Escape only closed the gallery: the tool is kept.
            Assert.Equal(EditorTool.Arrow, canvas.Tool);
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Canvas_handles_grow_the_canvas_in_one_undo_step_and_escape_cancels()
    {
        using var look = new AppLook(new AppSettings());
        var (window, canvas, folder) = Open(200, 120);
        using (folder)
        {
            var document = canvas.Document!;
            var arrow = new ArrowAnnotation { Start = new(20, 20), End = new(150, 90), Color = Colors.Red, Thickness = 4 };
            document.Annotations.Add(arrow);
            document.NotifyChanged();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(1, canvas.ViewScale, 3);

            // The corner handle, just outside the image, dragged by 100 × 60 pixels: the size follows the mouse.
            window.MouseMove(OnWindow(window, canvas, 205, 125));
            Assert.Same(CanvasCursor(2), canvas.Cursor);
            window.MouseDown(OnWindow(window, canvas, 205, 125), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 260, 160));
            window.MouseMove(OnWindow(window, canvas, 305, 185));
            Assert.Equal(new PixelSize(300, 180), canvas.CanvasDragSize);
            Assert.Equal((200, 120), (document.BaseImage.Width, document.BaseImage.Height));
            SaveRendering(window, "editor-canvas-drag.png", Around(window, canvas, 340, 230));
            window.MouseUp(OnWindow(window, canvas, 305, 185), MouseButton.Left);
            Assert.Null(canvas.CanvasDragSize);
            Assert.Equal((300, 180), (document.BaseImage.Width, document.BaseImage.Height));
            Assert.Equal(Colors.White, At(document.BaseImage.Pixels, 250, 150));
            Assert.Equal(Color.FromRgb(0xDD, 0xE6, 0xF0), At(document.BaseImage.Pixels, 199, 119));
            Assert.Equal(new Point(20, 20), arrow.Start);
            Assert.Single(document.Annotations);
            Dispatcher.UIThread.RunJobs();
            SaveRendering(window, "editor-canvas-handles.png", Around(window, canvas, 340, 230));

            // One undo step.
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal((200, 120), (document.BaseImage.Width, document.BaseImage.Height));
            Assert.False(document.CanUndo);

            // Escape during a drag: nothing changes, no undo step.
            long version = document.Version;
            window.MouseDown(OnWindow(window, canvas, 205, 60), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 260, 60));
            Assert.Equal(new PixelSize(255, 120), canvas.CanvasDragSize);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Assert.Null(canvas.CanvasDragSize);
            window.MouseUp(OnWindow(window, canvas, 260, 60), MouseButton.Left);
            Assert.Equal((200, 120), (document.BaseImage.Width, document.BaseImage.Height));
            Assert.Equal(version, document.Version);
            Assert.Equal(EditorTool.Select, canvas.Tool);

            // With another tool and zoomed in: the bottom handle (2.5 image pixels below at 200 %) adds whole pixels, nothing is drawn.
            typeof(EditorWindow).GetMethod("SetZoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { 2.0 });
            window.KeyPress(Key.A, RawInputModifiers.None, PhysicalKey.A, "a");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, canvas.ViewScale, 3);
            window.MouseDown(OnWindow(window, canvas, 100, 122.5), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 130, 152.75));
            Assert.Equal(new PixelSize(200, 150), canvas.CanvasDragSize);
            window.MouseUp(OnWindow(window, canvas, 130, 152.75), MouseButton.Left);
            Assert.Equal((200, 150), (document.BaseImage.Width, document.BaseImage.Height));
            Assert.Single(document.Annotations);

            // Cropping: no handles, the drag crops as before.
            window.KeyPress(Key.C, RawInputModifiers.None, PhysicalKey.C, "c");
            window.MouseDown(OnWindow(window, canvas, 205, 77.5), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 240, 77.5));
            Assert.Null(canvas.CanvasDragSize);
            window.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            window.MouseUp(OnWindow(window, canvas, 240, 77.5), MouseButton.Left);
            Assert.Equal((200, 150), (document.BaseImage.Width, document.BaseImage.Height));

            // Dark and right to left: the image and its handles are never mirrored.
            window.KeyPress(Key.V, RawInputModifiers.None, PhysicalKey.V, "v");
            typeof(EditorWindow).GetMethod("SetZoom", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, new object[] { 1.0 });
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            window.FlowDirection = FlowDirection.RightToLeft;
            Dispatcher.UIThread.RunJobs();
            window.MouseDown(OnWindow(window, canvas, 205, 155), MouseButton.Left);
            window.MouseMove(OnWindow(window, canvas, 285, 215));
            Assert.Equal(new PixelSize(280, 210), canvas.CanvasDragSize);
            SaveRendering(window, "editor-canvas-drag-dark-rtl.png", Around(window, canvas, 340, 260));
            window.MouseUp(OnWindow(window, canvas, 285, 215), MouseButton.Left);
            Assert.Equal((280, 210), (document.BaseImage.Width, document.BaseImage.Height));
            Dispatcher.UIThread.RunJobs();
            SaveRendering(window, "editor-canvas-handles-dark-rtl.png", Around(window, canvas, 340, 260));
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public async Task Resize_dialog_in_canvas_mode_adds_space_evenly_around_the_image()
    {
        using var look = new AppLook(new AppSettings());
        var (window, canvas, folder) = Open(400, 260);
        using (folder)
        {
            var document = canvas.Document!;
            document.Annotations.Add(new RectangleAnnotation { Start = new(10, 10), End = new(60, 40), Color = Colors.Red });
            var resize = (Task)typeof(EditorWindow).GetMethod("ResizeAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.IsType<ResizeDialog>(Assert.Single(window.OwnedWindows));
            dialog.FindControl<RadioButton>("CanvasModeButton")!.IsChecked = true;
            Dispatcher.UIThread.RunJobs();
            Assert.True(dialog.FindControl<StackPanel>("CanvasPanel")!.IsVisible);
            Assert.False(dialog.FindControl<StackPanel>("StretchPanel")!.IsVisible);
            // Centred, on white, by default.
            Assert.Equal(CanvasAnchor.Center, dialog.Anchor);
            Assert.Equal(Colors.White, dialog.CanvasBackground);
            dialog.FindControl<TextBox>("CanvasWidthBox")!.Text = "600";
            dialog.FindControl<TextBox>("CanvasHeightBox")!.Text = "400";
            Dispatcher.UIThread.RunJobs();
            SaveRendering(dialog, "editor-canvas-dialog.png");

            dialog.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Dispatcher.UIThread.RunJobs();
            await resize;
            Assert.Equal((600, 400), (document.BaseImage.Width, document.BaseImage.Height));
            var pixels = document.BaseImage.Pixels;
            // 100 pixels on the left and on the right, 70 above and below.
            Assert.Equal(Color.FromRgb(0xDD, 0xE6, 0xF0), At(pixels, 100, 70));
            Assert.Equal(Color.FromRgb(0xDD, 0xE6, 0xF0), At(pixels, 499, 329));
            Assert.Equal(Colors.White, At(pixels, 99, 69));
            Assert.Equal(Colors.White, At(pixels, 500, 330));
            Assert.Equal(new Point(110, 80), ((RectangleAnnotation)document.Annotations[0]).Start);
            Assert.Equal("#FFFFFFFF", AppServices.Settings.EditorCanvasBackground);
            window.KeyPress(Key.Z, RawInputModifiers.Control, PhysicalKey.Z, "z");
            Assert.Equal((400, 260), (document.BaseImage.Width, document.BaseImage.Height));
            ForceClose(window);
        }
    }

    [AvaloniaFact]
    public void Resize_dialog_remembers_the_background_and_keeps_the_anchor_grid_unmirrored()
    {
        using var look = new AppLook(new AppSettings { EditorCanvasBackground = "#00FFFFFF" });
        using var folder = new TempFolder();
        var path = folder.File("capture.png");
        ImageIO.Save(Solid(320, 200, Colors.SteelBlue), path);
        var dialog = new ResizeDialog(EditorDocument.Load(path));
        dialog.Show();
        Dispatcher.UIThread.RunJobs();
        try
        {
            // The last background comes back.
            Assert.Equal(Colors.Transparent, dialog.CanvasBackground);
            dialog.FindControl<RadioButton>("CanvasModeButton")!.IsChecked = true;
            var anchors = dialog.FindControl<Grid>("AnchorGrid")!.Children.OfType<ToggleButton>().ToList();
            Assert.Equal(9, anchors.Count);
            anchors.Single(b => (CanvasAnchor)b.Tag! == CanvasAnchor.TopLeft).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(CanvasAnchor.TopLeft, dialog.Anchor);
            Assert.Equal(new[] { true }, anchors.Where(b => b.IsChecked == true).Select(b => (CanvasAnchor)b.Tag! == CanvasAnchor.TopLeft));
            Dispatcher.UIThread.RunJobs();
            SaveRendering(dialog, "editor-canvas-dialog-transparent.png");

            // Right to left: the grid is laid out as the image, its top left button still on the left.
            Application.Current!.RequestedThemeVariant = ThemeVariant.Dark;
            dialog.FlowDirection = FlowDirection.RightToLeft;
            Dispatcher.UIThread.RunJobs();
            var topLeft = anchors.Single(b => (CanvasAnchor)b.Tag! == CanvasAnchor.TopLeft).TranslatePoint(default, dialog)!.Value;
            var topRight = anchors.Single(b => (CanvasAnchor)b.Tag! == CanvasAnchor.TopRight).TranslatePoint(default, dialog)!.Value;
            Assert.True(topLeft.X < topRight.X);
            SaveRendering(dialog, "editor-canvas-dialog-dark-rtl.png");

            // Black, remembered on OK.
            var black = dialog.FindControl<StackPanel>("BackgroundSwatches")!.Children.OfType<Button>().ElementAt(2);
            black.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.Equal(Colors.Black, dialog.CanvasBackground);
            Assert.Contains("selected", black.Classes);
            dialog.FindControl<TextBox>("CanvasWidthBox")!.Text = "400";
            dialog.FindControl<Button>("OkButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Assert.True(dialog.ExtendsCanvas);
            Assert.Equal((400, 200), (dialog.NewWidth, dialog.NewHeight));
            Assert.Equal("#FF000000", AppServices.Settings.EditorCanvasBackground);
        }
        finally
        {
            dialog.Close();
        }
    }

    /// <summary>The cursor of the canvas over its handle <paramref name="index"/> (right, bottom, corner).</summary>
    private static Cursor CanvasCursor(int index) =>
        ((Cursor[])typeof(AnnotationCanvas).GetField("CanvasCursors", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!)[index];

    /// <summary>The part of the window showing the image from its top left corner (in image pixels), for a closer look.</summary>
    private static PixelRect Around(EditorWindow window, AnnotationCanvas canvas, double width, double height)
    {
        var topLeft = OnWindow(window, canvas, -20, -20) * window.RenderScaling;
        var bottomRight = OnWindow(window, canvas, width, height) * window.RenderScaling;
        return new PixelRect((int)Math.Min(topLeft.X, bottomRight.X), (int)topLeft.Y, (int)Math.Abs(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y));
    }

    /// <summary>The application's themes and other settings while a test runs, so that it is drawn as in the application.</summary>
    private sealed class AppLook : IDisposable
    {
        private readonly Application _app = Application.Current!;
        private readonly ResourceInclude _colors = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        private readonly ResourceInclude _icons = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        private readonly StyleInclude _styles = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        private readonly AppSettings _settings = AppServices.Settings;

        public AppLook(AppSettings settings)
        {
            _app.Resources.MergedDictionaries.Add(_colors);
            _app.Resources.MergedDictionaries.Add(_icons);
            _app.Styles.Add(_styles);
            AppServices.Settings = settings;
        }

        public void Dispose()
        {
            AppServices.Settings = _settings;
            _app.RequestedThemeVariant = ThemeVariant.Default;
            _app.Styles.Remove(_styles);
            _app.Resources.MergedDictionaries.Remove(_icons);
            _app.Resources.MergedDictionaries.Remove(_colors);
        }
    }

    /// <summary>The window, or what a popup shows, saved with the test results to look at it.</summary>
    /// <param name="closeUp">Also saved alone, as "name-zoom.png", for a closer look.</param>
    private static void SaveRendering(object shown, string name, PixelRect? closeUp = null)
    {
        var topLevel = shown is Popup popup ? TopLevel.GetTopLevel(popup.Child) : shown as TopLevel;
        Assert.NotNull(topLevel);
        var frame = topLevel!.CaptureRenderedFrame();
        Assert.NotNull(frame);
        var pixels = AvaloniaImages.ToPixelBuffer(frame!);
        ImageIO.Save(pixels, Path.Combine(AppContext.BaseDirectory, name));
        if (closeUp is { } area)
            ImageIO.Save(pixels.Crop(area.X, area.Y, area.Width, area.Height), Path.Combine(AppContext.BaseDirectory, Path.GetFileNameWithoutExtension(name) + "-zoom.png"));
    }

    /// <summary>Closes without the save question (the document is left unsaved on purpose).</summary>
    private static void ForceClose(EditorWindow window)
    {
        typeof(EditorWindow).GetField("_closeConfirmed", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, true);
        window.Close();
    }
}
