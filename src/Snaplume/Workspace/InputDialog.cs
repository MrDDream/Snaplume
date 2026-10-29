using System.Windows;
using System.Windows.Controls;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>Minimal single-line text prompt.</summary>
public sealed class InputDialog : Window
{
    private readonly TextBox _input;

    public string Value => _input.Text;

    /// <param name="isFileName">Select the name without its extension, like Explorer (otherwise the whole text).</param>
    public InputDialog(Window owner, string title, string label, string value, bool isFileName = true)
    {
        Owner = owner;
        Title = title;
        Width = 420;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        SetResourceReference(BackgroundProperty, "WindowBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");

        _input = new TextBox { Text = value, Margin = new Thickness(0, 6, 0, 12), Padding = new Thickness(4, 3, 4, 3) };
        var ok = new Button { Content = Loc.T("Workspace.Dialog.Ok"), IsDefault = true, Width = 90 };
        ok.Click += (_, _) => DialogResult = true;
        var cancel = new Button { Content = Loc.T("Workspace.Dialog.Cancel"), IsCancel = true, Width = 90, Margin = new Thickness(8, 0, 0, 0) };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var root = new StackPanel { Margin = new Thickness(16) };
        root.Children.Add(new TextBlock { Text = label });
        root.Children.Add(_input);
        root.Children.Add(buttons);
        Content = root;

        Loaded += (_, _) =>
        {
            _input.Focus();
            // Select the name without the extension, like Explorer.
            int dot = isFileName ? value.LastIndexOf('.') : -1;
            _input.Select(0, dot > 0 ? dot : value.Length);
        };
    }
}
