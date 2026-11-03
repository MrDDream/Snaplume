using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Snaplume.Core;

namespace Snaplume.Ui;

public enum DialogButtons { Ok, OkCancel, YesNo, YesNoCancel }

public enum DialogResult { None, Ok, Cancel, Yes, No }

public enum DialogIcon { None, Information, Question, Warning, Error }

/// <summary>
/// Message boxes (Avalonia has none). Replace WPF's MessageBox.Show: the call is awaited, the window is modal to
/// its owner (or centered on the screen without one). Closing the window, or Escape, answers Cancel, or No / Ok without
/// Cancel.
/// </summary>
public static class Dialogs
{
    /// <param name="defaultResult">Button focused, so that Enter answers it (e.g. No before a deletion).</param>
    public static async Task<DialogResult> ShowAsync(Window? owner, string text, string? title = null,
        DialogButtons buttons = DialogButtons.Ok, DialogIcon icon = DialogIcon.None, DialogResult defaultResult = DialogResult.None)
    {
        var window = new Window
        {
            Title = title ?? "Snaplume",
            SizeToContent = SizeToContent.WidthAndHeight,
            MinWidth = 360,
            MaxWidth = 560,
            CanResize = false,
            ShowInTaskbar = owner == null,
            WindowStartupLocation = owner != null ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Topmost = owner == null,
        };
        var result = buttons switch
        {
            DialogButtons.YesNoCancel or DialogButtons.OkCancel => DialogResult.Cancel,
            DialogButtons.YesNo => DialogResult.No,
            _ => DialogResult.Ok,
        };

        var panel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };
        Button? focused = null;
        void Add(string key, DialogResult answer, bool accent)
        {
            // Escape answers like closing the window: Cancel, else No, else Ok.
            var button = new Button
            {
                Content = Loc.T(key),
                MinWidth = 88,
                HorizontalContentAlignment = HorizontalAlignment.Center,
                IsCancel = answer == result,
            };
            if (accent) button.Classes.Add("accent");
            button.Click += (_, _) =>
            {
                result = answer;
                window.Close();
            };
            panel.Children.Add(button);
            if (answer == defaultResult || (defaultResult == DialogResult.None && focused == null)) focused = button;
        }
        switch (buttons)
        {
            case DialogButtons.Ok: Add("Dialog.Ok", DialogResult.Ok, true); break;
            case DialogButtons.OkCancel: Add("Dialog.Ok", DialogResult.Ok, true); Add("Dialog.Cancel", DialogResult.Cancel, false); break;
            case DialogButtons.YesNo: Add("Dialog.Yes", DialogResult.Yes, true); Add("Dialog.No", DialogResult.No, false); break;
            case DialogButtons.YesNoCancel:
                Add("Dialog.Yes", DialogResult.Yes, true); Add("Dialog.No", DialogResult.No, false); Add("Dialog.Cancel", DialogResult.Cancel, false);
                break;
        }

        var glyph = icon switch
        {
            DialogIcon.Information => "ℹ",
            DialogIcon.Question => "?",
            DialogIcon.Warning => "⚠",
            DialogIcon.Error => "✕",
            _ => null,
        };
        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
        DockPanel.SetDock(panel, Dock.Bottom);
        body.Children.Add(panel);
        if (glyph != null)
        {
            var mark = new TextBlock { Text = glyph, FontSize = 26, Margin = new Thickness(0, -4, 14, 0), VerticalAlignment = VerticalAlignment.Top };
            if (icon is DialogIcon.Error or DialogIcon.Warning) mark.Foreground = icon == DialogIcon.Error ? Brushes.IndianRed : Brushes.DarkOrange;
            DockPanel.SetDock(mark, Dock.Left);
            body.Children.Add(mark);
        }
        body.Children.Add(new SelectableTextBlock { Text = text, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 });
        window.Content = body;
        window.Opened += (_, _) => focused?.Focus();
        // IsCancel needs the focus on a button: Escape also closes from the text (selected with the mouse).
        window.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Escape || e.Handled) return;
            e.Handled = true;
            window.Close();
        };

        if (owner is { IsVisible: true }) await window.ShowDialog(owner);
        else
        {
            var closed = new TaskCompletionSource();
            window.Closed += (_, _) => closed.TrySetResult();
            window.Show();
            await closed.Task;
        }
        return result;
    }

    /// <summary>The window to own a dialog when none is given: the active one of the application.</summary>
    public static Window? ActiveWindow =>
        (Avalonia.Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Windows.FirstOrDefault(w => w.IsActive);
}
