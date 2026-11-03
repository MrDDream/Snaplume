using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Workspace;

namespace Snaplume.Tests;

/// <summary>The quick panel opened without a screen, with the application's themes; the mouse is the headless one.</summary>
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
}
