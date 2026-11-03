using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>Minimal single-line text prompt (workspace and file names).</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _input;
    private bool _accepted;

    /// <param name="isFileName">Select the name without its extension, like file managers (otherwise the whole text).</param>
    /// <param name="extra">More fields, under the text box (e.g. the folder of a new workspace).</param>
    private InputDialog(string title, string label, string value, bool isFileName, bool hasOwner, Control? extra)
    {
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = !hasOwner;
        Topmost = !hasOwner;
        WindowStartupLocation = hasOwner ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;

        _input = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 12) };
        var ok = new Button { Content = Loc.T("Workspace.Dialog.Ok"), IsDefault = true, MinWidth = 90, HorizontalContentAlignment = HorizontalAlignment.Center };
        ok.Classes.Add("accent");
        ok.Click += (_, _) =>
        {
            _accepted = true;
            Close();
        };
        var cancel = new Button
        {
            Content = Loc.T("Workspace.Dialog.Cancel"),
            IsCancel = true,
            MinWidth = 90,
            Margin = new Thickness(8, 0, 0, 0),
            HorizontalContentAlignment = HorizontalAlignment.Center,
        };
        cancel.Click += (_, _) => Close();

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label, TextWrapping = Avalonia.Media.TextWrapping.Wrap });
        root.Children.Add(_input);
        if (extra != null)
        {
            extra.Margin = new Thickness(0, 0, 0, 14);
            root.Children.Add(extra);
        }
        root.Children.Add(buttons);
        Content = root;

        Opened += (_, _) =>
        {
            _input.Focus();
            // Select the name without the extension, like file managers.
            int dot = isFileName ? value.LastIndexOf('.') : -1;
            _input.SelectionStart = 0;
            _input.SelectionEnd = dot > 0 ? dot : value.Length;
        };
    }

    /// <summary>Asks for a text; modal to <paramref name="owner"/>, or centered on the screen above everything without one.</summary>
    /// <returns>The text typed, or null if cancelled.</returns>
    /// <param name="extra">More fields shown under the text box; the caller reads them afterwards.</param>
    public static async Task<string?> AskAsync(Window? owner, string title, string label, string value, bool isFileName = true,
        Control? extra = null)
    {
        var dialog = new InputDialog(title, label, value, isFileName, owner is { IsVisible: true }, extra);
        if (owner is { IsVisible: true })
        {
            await dialog.ShowDialog(owner);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.Show();
            dialog.Activate();
            await closed.Task;
        }
        return dialog._accepted ? dialog._input.Text ?? "" : null;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        // IsCancel only works when a button has the focus path: Escape closes from the text box too.
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}
