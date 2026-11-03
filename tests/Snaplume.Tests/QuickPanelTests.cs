using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Primitives;
using Avalonia.Styling;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Workspace;

namespace Snaplume.Tests;

/// <summary>The quick panel opened without a screen, with the application's themes; the mouse is the headless one.</summary>
[Collection(ApplicationState.Name)]
public class QuickPanelTests
{
    [AvaloniaFact]
    public void Hovering_a_capture_button_does_not_paint_behind_its_label()
    {
        var app = Application.Current!;
        var colors = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        var icons = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        var settings = AppServices.Settings;
        using var folder = new TempFolder();
        app.Resources.MergedDictionaries.Add(colors);
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        AppServices.Settings = new AppSettings { WorkspaceFolder = folder.Path };
        QuickPanel? panel = null;
        try
        {
            panel = new QuickPanel();
            panel.Show();
            Dispatcher.UIThread.RunJobs();

            // The split buttons: capture and video, with their menu arrows.
            var buttons = panel.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("split")).ToList();
            Assert.Equal(4, buttons.Count);
            foreach (var button in buttons)
            {
                var center = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), panel)!.Value;
                panel.MouseMove(center);
                Dispatcher.UIThread.RunJobs();
                Assert.True(button.IsPointerOver);
                // Fluent styles the presenter named PART_ContentPresenter: a light background behind the white label.
                var presenter = button.GetVisualDescendants().OfType<ContentPresenter>().Single();
                Assert.NotEqual("PART_ContentPresenter", presenter.Name);
                Assert.Null(presenter.Background);
            }
        }
        finally
        {
            panel?.Close();
            AppServices.Settings = settings;
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(colors);
        }
    }

    [AvaloniaFact]
    public async Task Workspace_button_shows_the_side_panel_where_a_click_switches_workspace()
    {
        var app = Application.Current!;
        var colors = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        var icons = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        var settings = AppServices.Settings;
        var theme = app.RequestedThemeVariant;
        using var folder = new TempFolder();
        var root = Path.Combine(folder.Path, "Captures");
        Directory.CreateDirectory(Path.Combine(root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(root, "Beta"));
        var external = Path.Combine(folder.Path, "Clients");
        Directory.CreateDirectory(external);
        app.Resources.MergedDictionaries.Add(colors);
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        AppServices.Settings = new AppSettings
        {
            WorkspaceFolder = root,
            WorkspaceFolders = new(StringComparer.OrdinalIgnoreCase) { ["Clients"] = external, ["Disque USB"] = Path.Combine(folder.Path, "absent") },
        };
        Workspaces.ForgetAvailability();
        QuickPanel? panel = null;
        try
        {
            panel = new QuickPanel();
            panel.Show();
            Dispatcher.UIThread.RunJobs();
            var pane = panel.FindControl<Border>("WorkspacePane")!;
            var gallery = panel.FindControl<CaptureGallery>("Gallery")!;
            var button = panel.FindControl<ToggleButton>("WorkspaceButton")!;
            // The layout once the panel has slid (the transition runs on the render clock).
            pane.Transitions = null;
            double galleryWidth = gallery.Bounds.Width, windowWidth = panel.Bounds.Width;
            Assert.False(pane.IsVisible);
            Save(panel, "quick-panel-workspaces-closed.png");

            // From the keyboard: the button is in the Tab order, Enter opens the panel.
            Assert.True(button.Focusable);
            button.Focus();
            panel.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(panel.IsWorkspacePaneOpen);
            Assert.True(pane.IsVisible);
            Assert.True(AppServices.Settings.PanelWorkspacesOpen);
            Assert.Equal(QuickPanel.WorkspacePaneWidth, pane.Bounds.Width, 1);
            Assert.Equal(galleryWidth - QuickPanel.WorkspacePaneWidth, gallery.Bounds.Width, 1);
            Assert.Equal(windowWidth, panel.Bounds.Width);

            // The workspaces (read in the background), Base selected; the missing folder greyed with its path in the tooltip.
            var list = panel.FindControl<WorkspaceList>("WorkspaceList")!;
            var expected = new[] { "", "Alpha", "Beta", "Clients", "Disque USB" };
            await WaitForAsync(() => list.Entries.Select(e => e.Name).SequenceEqual(expected));
            Assert.Equal(expected, list.Entries.Select(e => e.Name));
            Assert.True(list.Entries.Single(e => e.Name == "Disque USB").IsMissing);
            Assert.Contains(external, list.Entries.Single(e => e.Name == "Clients").Tip);
            var listBox = list.GetVisualDescendants().OfType<ListBox>().Single();
            Assert.Equal("", ((WorkspaceEntry)listBox.SelectedItem!).Name);
            Save(panel, "quick-panel-workspaces-open.png");

            // A click on "Beta" makes it the active workspace.
            var beta = listBox.ContainerFromIndex(2)!;
            var center = beta.TranslatePoint(new Point(beta.Bounds.Width / 2, beta.Bounds.Height / 2), panel)!.Value;
            panel.MouseDown(center, MouseButton.Left);
            panel.MouseUp(center, MouseButton.Left);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Beta", Workspaces.ActiveName);
            Assert.Equal("Beta", AppServices.Settings.ActiveWorkspace);

            // Dark theme, right to left: the panel is on the right.
            app.RequestedThemeVariant = ThemeVariant.Dark;
            panel.FlowDirection = Avalonia.Media.FlowDirection.RightToLeft;
            Dispatcher.UIThread.RunJobs();
            var paneLeft = pane.TranslatePoint(new Point(0, 0), panel)!.Value.X;
            Assert.True(paneLeft > panel.Bounds.Width / 2);
            Save(panel, "quick-panel-workspaces-open-dark-rtl.png");

            // A click on the button again closes it, and the next panel remembers.
            var buttonCenter = button.TranslatePoint(new Point(button.Bounds.Width / 2, button.Bounds.Height / 2), panel)!.Value;
            panel.MouseDown(buttonCenter, MouseButton.Left);
            panel.MouseUp(buttonCenter, MouseButton.Left);
            for (int i = 0; i < 50 && pane.IsVisible; i++)
            {
                await Task.Delay(20);
                Dispatcher.UIThread.RunJobs();
            }
            Assert.False(panel.IsWorkspacePaneOpen);
            Assert.False(pane.IsVisible);
            Assert.False(AppServices.Settings.PanelWorkspacesOpen);
        }
        finally
        {
            panel?.Close();
            AppServices.Settings = settings;
            app.RequestedThemeVariant = theme;
            Workspaces.ForgetAvailability();
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(colors);
        }
    }

    [AvaloniaFact]
    public void New_workspace_folder_choice_defaults_to_the_snaplume_folder()
    {
        var app = Application.Current!;
        var colors = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        var styles = new StyleInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        app.Resources.MergedDictionaries.Add(colors);
        app.Styles.Add(styles);
        var window = new Window { Width = 420, SizeToContent = SizeToContent.Height };
        try
        {
            var choice = new WorkspaceFolderChoice { Margin = new Thickness(16) };
            window.Content = choice;
            window.Show();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(choice.Folder);
            var radios = choice.Children.OfType<RadioButton>().ToList();
            Assert.True(radios[0].IsChecked);

            // What the folder picker gives back.
            choice.Folder = @"D:\Captures\Clients";
            Dispatcher.UIThread.RunJobs();
            Assert.True(radios[1].IsChecked);
            Assert.Contains(choice.Children.OfType<TextBlock>(), t => t.IsVisible && t.Text == @"D:\Captures\Clients");
            Save(window, "new-workspace-folder-choice.png");

            radios[0].IsChecked = true;
            Assert.Null(choice.Folder);
        }
        finally
        {
            window.Close();
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(colors);
        }
    }

    [AvaloniaFact]
    public async Task Workspace_window_sidebar_lists_the_custom_folders_too()
    {
        var app = Application.Current!;
        var colors = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        var icons = new ResourceInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        var styles = new StyleInclude(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        var settings = AppServices.Settings;
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "Captures", "Alpha"));
        app.Resources.MergedDictionaries.Add(colors);
        app.Resources.MergedDictionaries.Add(icons);
        app.Styles.Add(styles);
        AppServices.Settings = new AppSettings
        {
            WorkspaceFolder = Path.Combine(folder.Path, "Captures"),
            WorkspaceFolders = new(StringComparer.OrdinalIgnoreCase) { ["Partage réseau"] = Path.Combine(folder.Path, "offline") },
        };
        Workspaces.ForgetAvailability();
        WorkspaceWindow? window = null;
        try
        {
            window = new WorkspaceWindow();
            window.Show();
            Dispatcher.UIThread.RunJobs();
            var list = window.FindControl<WorkspaceList>("WorkspaceList")!;
            var expected = new[] { "", "Alpha", "Partage réseau" };
            await WaitForAsync(() => list.Entries.Select(e => e.Name).SequenceEqual(expected));
            Assert.Equal(expected, list.Entries.Select(e => e.Name));
            Assert.True(list.Entries[2].IsMissing);
            Save(window, "workspace-window-sidebar.png");
        }
        finally
        {
            window?.Close();
            AppServices.Settings = settings;
            Workspaces.ForgetAvailability();
            app.Styles.Remove(styles);
            app.Resources.MergedDictionaries.Remove(icons);
            app.Resources.MergedDictionaries.Remove(colors);
        }
    }

    /// <summary>Runs the UI jobs until <paramref name="done"/> (work done in the background, timers), five seconds at most.</summary>
    internal static async Task WaitForAsync(Func<bool> done, int timeoutMs = 5000)
    {
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        Dispatcher.UIThread.RunJobs();
        while (!done() && stopwatch.ElapsedMilliseconds < timeoutMs)
        {
            await Task.Delay(20);
            Dispatcher.UIThread.RunJobs();
        }
    }

    /// <summary>Kept with the test results to look at the panel; never committed.</summary>
    private static void Save(Window window, string name) =>
        Snaplume.Imaging.ImageIO.Save(Snaplume.Imaging.AvaloniaImages.ToPixelBuffer(window.CaptureRenderedFrame()!),
            Path.Combine(AppContext.BaseDirectory, name));
}
