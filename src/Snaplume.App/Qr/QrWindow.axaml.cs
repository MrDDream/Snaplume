using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Ui;

namespace Snaplume.Qr;

/// <summary>
/// Lists the QR codes and barcodes read in an image. A single code is copied to the clipboard as soon as
/// it is read; a web link is only opened after a confirmation showing the full address and its site.
/// </summary>
public partial class QrWindow : Window
{
    /// <summary>Image to decode; released once read (it may weigh tens of megabytes).</summary>
    private PixelBuffer? _image;
    /// <summary>Cancelled when the window closes: the result is then dropped.</summary>
    private readonly CancellationTokenSource _closing = new();

    /// <summary>For the XAML designer only.</summary>
    public QrWindow() => InitializeComponent();

    public QrWindow(PixelBuffer image)
    {
        InitializeComponent();
        _image = image;
        Opened += (_, _) => _ = ReadAsync();
        Closed += (_, _) =>
        {
            _image = null;
            _closing.Cancel();
        };
    }

    private async Task ReadAsync()
    {
        var image = _image;
        _image = null;
        if (image == null) return;
        var token = _closing.Token;
        StatusText.Text = Loc.T("Qr.Reading");
        try
        {
            var codes = await Task.Run(() => QrService.Decode(image, token), token);
            image = null;
            if (token.IsCancellationRequested) return;
            CodeList.ItemsSource = codes.Select(c => new QrItem(c)).ToList();
            if (codes.Count == 0)
            {
                ShowMessage(Loc.T("Qr.NothingFound"));
                StatusText.Text = "";
                return;
            }
            ShowMessage("");
            StatusText.Text = Loc.Plural(codes.Count, "Qr.FoundOne", "Qr.FoundMany");
            if (codes.Count == 1 && await TrySetTextAsync(codes[0].Text) && !token.IsCancellationRequested)
                StatusText.Text = Loc.T("Qr.FoundAndCopied");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Window closed during the reading.
        }
        catch (Exception ex)
        {
            ErrorLog.Write("QR code", ex);
            if (token.IsCancellationRequested) return;
            ShowMessage(Loc.T("Qr.Failed") + "\n" + ex.Message);
            StatusText.Text = "";
        }
    }

    private static async Task<bool> TrySetTextAsync(string text)
    {
        try
        {
            return await AppServices.Platform.Clipboard.SetTextAsync(text);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie d'un QR code", ex);
            return false;
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.IsVisible = message.Length > 0;
    }

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not QrItem item) return;
        StatusText.Text = Loc.T(await TrySetTextAsync(item.Text) ? "Qr.Copied" : "Qr.CopyFailed");
    }

    /// <summary>
    /// Only http and https links, and only once the user has seen the full address and the site it
    /// leads to (on its own line: a long address could hide it) and agreed. The default answer is No.
    /// </summary>
    private async void OnOpenLink(object? sender, RoutedEventArgs e)
    {
        if ((sender as Control)?.DataContext is not QrItem { Link: { } link }) return;
        var address = link.AbsoluteUri;
        var details = address + "\n\n" + Loc.T("Qr.OpenLinkHost", QrService.DisplayHost(link));
        var answer = await Dialogs.ShowAsync(this, Loc.T("Qr.OpenLinkConfirm", details), Loc.T("Qr.OpenLinkTitle"),
            DialogButtons.YesNo, DialogIcon.Question, DialogResult.No);
        if (answer != DialogResult.Yes) return;
        if (!FileOps.OpenWithDefaultApp(address)) StatusText.Text = Loc.T("Qr.OpenLinkFailed");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled)
        {
            e.Handled = true;
            Close();
        }
    }
}

/// <summary>One code of the list.</summary>
public sealed class QrItem
{
    private static readonly Dictionary<string, string> FormatNames = new()
    {
        ["QR_CODE"] = "QR Code", ["DATA_MATRIX"] = "Data Matrix", ["AZTEC"] = "Aztec", ["PDF_417"] = "PDF417",
        ["EAN_13"] = "EAN-13", ["EAN_8"] = "EAN-8", ["UPC_A"] = "UPC-A", ["UPC_E"] = "UPC-E",
        ["CODE_128"] = "Code 128", ["CODE_39"] = "Code 39", ["CODE_93"] = "Code 93", ["CODABAR"] = "Codabar", ["ITF"] = "ITF",
    };

    public QrItem(QrCode code)
    {
        Format = FormatNames.GetValueOrDefault(code.Format, code.Format);
        Text = code.Text;
        Link = QrService.AsWebLink(code.Text);
    }

    public string Format { get; }
    public string Text { get; }
    public Uri? Link { get; }
    public bool HasLink => Link != null;
}
