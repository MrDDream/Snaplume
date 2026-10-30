using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform;

namespace Snaplume.Ocr;

/// <summary>Shows the text recognized in an image (platform OCR); the text is copied to the clipboard as soon as it is read.</summary>
public partial class OcrWindow : Window
{
    private readonly PixelBuffer _image;
    private readonly CancellationTokenSource _closing = new();
    private bool _ready;
    private int _run;

    /// <summary>For the XAML designer only.</summary>
    public OcrWindow()
    {
        InitializeComponent();
        _image = new PixelBuffer(1, 1);
    }

    public OcrWindow(PixelBuffer image)
    {
        InitializeComponent();
        _image = image;

        var ocr = AppServices.Platform.Ocr;
        var languages = ocr.Languages;
        LanguageBox.ItemsSource = languages;
        LanguageBox.SelectedItem = Find(languages, AppServices.Settings.OcrLanguage) ?? Find(languages, ocr.DefaultLanguage?.Tag)
                                   ?? languages.FirstOrDefault();
        LanguageBox.SelectionChanged += OnLanguageChanged;

        Opened += (_, _) =>
        {
            _ready = true;
            _ = RecognizeAsync();
        };
        Closed += (_, _) => _closing.Cancel();
    }

    private static OcrLanguage? Find(IReadOnlyList<OcrLanguage> languages, string? tag) =>
        string.IsNullOrEmpty(tag) ? null : languages.FirstOrDefault(l => string.Equals(l.Tag, tag, StringComparison.OrdinalIgnoreCase));

    private async Task RecognizeAsync()
    {
        int run = ++_run;
        var token = _closing.Token;
        var ocr = AppServices.Platform.Ocr;
        if (ocr.Languages.Count == 0)
        {
            // Windows: languages are added in the system settings; Linux: Tesseract and its language packages.
            bool windows = AppServices.Platform.Id == "windows";
            ShowMessage(Loc.T(windows ? "Ocr.NoLanguage" : "Ocr.NoLanguageTesseract"));
            LanguageSettingsButton.IsVisible = windows;
            LanguageBox.IsEnabled = false;
            return;
        }

        StatusText.Text = Loc.T("Ocr.Reading");
        ResultBox.IsEnabled = false;
        try
        {
            var tag = (LanguageBox.SelectedItem as OcrLanguage)?.Tag;
            var text = await ocr.RecognizeAsync(_image, tag, token);
            // A newer recognition (other language) was started, or the window was closed meanwhile:
            // the clipboard must not change behind the user's back.
            if (run != _run || token.IsCancellationRequested) return;
            ResultBox.Text = text;
            ResultBox.IsEnabled = true;
            if (string.IsNullOrWhiteSpace(text))
            {
                ShowMessage(Loc.T("Ocr.NothingFound"));
                StatusText.Text = "";
                return;
            }
            ShowMessage("");
            StatusText.Text = Loc.T(await TrySetTextAsync(text) ? "Ocr.Copied" : "Ocr.CopyFailed");
            ResultBox.Focus();
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Window closed during the reading.
        }
        catch (Exception ex)
        {
            // InvalidOperationException: OCR not available, with a message ready to show.
            ErrorLog.Write("OCR", ex);
            if (run != _run || token.IsCancellationRequested) return;
            ShowMessage(ex is InvalidOperationException ? ex.Message : Loc.T("Ocr.Failed") + "\n" + ex.Message);
            StatusText.Text = "";
            ResultBox.IsEnabled = true;
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
            ErrorLog.Write("Copie du texte", ex);
            return false;
        }
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.IsVisible = message.Length > 0;
    }

    private void OnLanguageChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not OcrLanguage language) return;
        var settings = AppServices.Settings;
        settings.OcrLanguage = language.Tag;
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

    private async void OnCopy(object? sender, RoutedEventArgs e)
    {
        var selected = ResultBox.SelectedText;
        var text = !string.IsNullOrEmpty(selected) ? selected : ResultBox.Text;
        if (string.IsNullOrEmpty(text)) return;
        StatusText.Text = Loc.T(!await TrySetTextAsync(text) ? "Ocr.CopyFailed"
            : !string.IsNullOrEmpty(selected) ? "Ocr.SelectionCopied" : "Ocr.Copied");
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();

    private void OnLanguageSettings(object? sender, RoutedEventArgs e) =>
        FileOps.OpenWithDefaultApp("ms-settings:regionlanguage");

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape && !e.Handled && !LanguageBox.IsDropDownOpen)
        {
            e.Handled = true;
            Close();
        }
    }
}
