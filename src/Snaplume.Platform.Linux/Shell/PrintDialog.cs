using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Snaplume.Core;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>The choice made in <see cref="PrintDialog"/>.</summary>
internal sealed record PrintChoice(string Printer, int Copies);

/// <summary>
/// A small print dialog for CUPS: the printer (the system default selected) and the number of copies. The page
/// layout is already decided by the PDF Snaplume generated, one image per page.
/// </summary>
internal sealed class PrintDialog : Window
{
    private readonly ComboBox _printers;
    private readonly NumericUpDown _copies;
    private PrintChoice? _choice;

    private PrintDialog(IReadOnlyList<string> printers, string? defaultPrinter, string title)
    {
        Title = Loc.T("Linux.Print.Title");
        SizeToContent = SizeToContent.WidthAndHeight;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        FlowDirection = Loc.IsRightToLeft ? Avalonia.Media.FlowDirection.RightToLeft : Avalonia.Media.FlowDirection.LeftToRight;

        _printers = new ComboBox { ItemsSource = printers, MinWidth = 280, HorizontalAlignment = HorizontalAlignment.Stretch };
        _printers.SelectedItem = printers.Contains(defaultPrinter) ? defaultPrinter : printers.FirstOrDefault();
        _copies = new NumericUpDown { Minimum = 1, Maximum = 99, Value = 1, Increment = 1, FormatString = "0", MinWidth = 120, HorizontalAlignment = HorizontalAlignment.Left };

        var print = new Button { Content = Loc.T("Linux.Print.Print"), IsDefault = true, MinWidth = 96 };
        print.Click += (_, _) =>
        {
            if (_printers.SelectedItem is string printer)
            {
                _choice = new PrintChoice(printer, (int)Math.Clamp(_copies.Value ?? 1, 1, 99));
                Close();
            }
        };
        var cancel = new Button { Content = Loc.T("Dialog.Cancel"), IsCancel = true, MinWidth = 96 };
        cancel.Click += (_, _) => Close();

        Content = new StackPanel
        {
            Margin = new Thickness(20),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = title, FontWeight = Avalonia.Media.FontWeight.SemiBold, MaxWidth = 420, TextWrapping = Avalonia.Media.TextWrapping.Wrap },
                new TextBlock { Text = Loc.T("Linux.Print.Printer"), Margin = new Thickness(0, 8, 0, 0) },
                _printers,
                new TextBlock { Text = Loc.T("Linux.Print.Copies"), Margin = new Thickness(0, 8, 0, 0) },
                _copies,
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 16, 0, 0),
                    Children = { print, cancel },
                },
            },
        };
    }

    /// <summary>Shows the dialog on the UI thread, modal to the window whose native handle is <paramref name="owner"/> when it is found.</summary>
    /// <returns>Null if cancelled.</returns>
    public static async Task<PrintChoice?> AskAsync(IReadOnlyList<string> printers, string? defaultPrinter, string title, IntPtr owner)
    {
        var dialog = new PrintDialog(printers, defaultPrinter, title);
        var parent = FindWindow(owner);
        if (parent != null)
        {
            await dialog.ShowDialog(parent);
        }
        else
        {
            var closed = new TaskCompletionSource();
            dialog.Closed += (_, _) => closed.TrySetResult();
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
            dialog.Show();
            await closed.Task;
        }
        return dialog._choice;
    }

    private static Window? FindWindow(IntPtr handle)
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return null;
        return desktop.Windows.FirstOrDefault(w => handle != IntPtr.Zero && w.TryGetPlatformHandle()?.Handle == handle)
               ?? desktop.Windows.FirstOrDefault(w => w.IsActive);
    }
}
