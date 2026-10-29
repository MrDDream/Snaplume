using System.Windows;
using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Qr;

/// <summary>
/// Lists the QR codes and barcodes read in an image. A single code is copied to the clipboard as soon as
/// it is read; a web link is only opened after a confirmation showing the full address.
/// </summary>
public partial class QrWindow : Window
{
    /// <summary>Pixels to decode; released once read (they may weigh tens of megabytes).</summary>
    private byte[]? _pixels;
    private readonly int _width;
    private readonly int _height;
    /// <summary>Cancelled when the window closes: the result is then dropped.</summary>
    private readonly CancellationTokenSource _closing = new();

    public QrWindow(BitmapSource image)
    {
        InitializeComponent();
        // Copied now: the image may belong to this thread (not frozen), the reading runs in the background.
        (_pixels, _width, _height) = QrService.ToBgra(image);
        Loaded += (_, _) => _ = ReadAsync();
        Closed += (_, _) =>
        {
            _pixels = null;
            _closing.Cancel();
        };
    }

    private async Task ReadAsync()
    {
        var pixels = _pixels;
        _pixels = null;
        if (pixels == null) return;
        var token = _closing.Token;
        StatusText.Text = Loc.T("Qr.Reading");
        try
        {
            var codes = await Task.Run(() => QrService.Decode(pixels, _width, _height, token), token);
            pixels = null;
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
            if (codes.Count == 1 && ClipboardHelper.TrySetText(codes[0].Text))
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

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QrItem item) return;
        StatusText.Text = Loc.T(ClipboardHelper.TrySetText(item.Text) ? "Qr.Copied" : "Qr.CopyFailed");
    }

    /// <summary>
    /// Only http and https links, and only once the user has seen the full address and the site it
    /// leads to (on its own line: a long address could hide it) and agreed.
    /// </summary>
    private void OnOpenLink(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is not QrItem { Link: { } link }) return;
        var address = link.AbsoluteUri;
        var details = address + "\n\n" + Loc.T("Qr.OpenLinkHost", QrService.DisplayHost(link));
        var answer = MessageBox.Show(this, Loc.T("Qr.OpenLinkConfirm", details), Loc.T("Qr.OpenLinkTitle"),
            MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes) return;
        if (!FileOps.OpenWithDefaultApp(address)) StatusText.Text = Loc.T("Qr.OpenLinkFailed");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    /// <summary>One code of the list (public: WPF bindings read its properties).</summary>
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
        public Visibility LinkVisibility => Link != null ? Visibility.Visible : Visibility.Collapsed;
    }
}
