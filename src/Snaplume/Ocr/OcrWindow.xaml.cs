using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using Snaplume.Core;

namespace Snaplume.Ocr;

/// <summary>Shows the text recognized in an image; the text is copied to the clipboard as soon as it is read.</summary>
public partial class OcrWindow : Window
{
    private readonly BitmapSource _image;
    private bool _ready;
    private bool _closed;
    private int _run;

    public OcrWindow(BitmapSource image)
    {
        InitializeComponent();
        _image = image;

        var languages = OcrService.Languages;
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = OcrService.Find(App.Instance.Settings.OcrLanguage)
                                   ?? OcrService.Find(OcrService.DefaultLanguage?.LanguageTag);

        Loaded += (_, _) =>
        {
            _ready = true;
            _ = RecognizeAsync();
        };
        Closed += (_, _) => _closed = true;
    }

    private async Task RecognizeAsync()
    {
        int run = ++_run;
        if (OcrService.Languages.Count == 0)
        {
            ShowMessage(Loc.T("Ocr.NoLanguage"));
            LanguageSettingsButton.Visibility = Visibility.Visible;
            return;
        }

        StatusText.Text = Loc.T("Ocr.Reading");
        ResultBox.IsEnabled = false;
        try
        {
            var tag = (LanguageBox.SelectedItem as Windows.Globalization.Language)?.LanguageTag;
            var text = await OcrService.RecognizeAsync(_image, tag);
            // A newer recognition (other language) was started, or the window was closed meanwhile:
            // the clipboard must not change behind the user's back.
            if (run != _run || _closed) return;
            ResultBox.Text = text;
            ResultBox.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowMessage(Loc.T("Ocr.NothingFound"));
                StatusText.Text = "";
                return;
            }
            ShowMessage("");
            StatusText.Text = Loc.T(ClipboardHelper.TrySetText(text) ? "Ocr.Copied" : "Ocr.CopyFailed");
            ResultBox.Focus();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("OCR", ex);
            if (run != _run || _closed) return;
            ShowMessage(ex.Message);
            StatusText.Text = "";
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnLanguageChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not Windows.Globalization.Language language) return;
        var settings = App.Instance.Settings;
        settings.OcrLanguage = language.LanguageTag;
        try
        {
            settings.Save();
        }
        catch (Exception ex)
        {
            // Only the remembered language is lost.
            ErrorLog.Write("Enregistrement des paramètres", ex);
        }
        _ = RecognizeAsync();
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var text = ResultBox.SelectionLength > 0 ? ResultBox.SelectedText : ResultBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        StatusText.Text = Loc.T(!ClipboardHelper.TrySetText(text) ? "Ocr.CopyFailed"
            : ResultBox.SelectionLength > 0 ? "Ocr.SelectionCopied" : "Ocr.Copied");
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    private void OnLanguageSettings(object sender, RoutedEventArgs e) =>
        FileOps.OpenWithDefaultApp("ms-settings:regionlanguage");
}
