using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Snaplume.Core;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.Shell;
using Snaplume.Preferences;
using Snaplume.Ui;
using Snaplume.Workspace;

namespace Snaplume.Tests;

/// <summary>The gallery, the workspace list, the message boxes and the settings, opened without a screen.</summary>
[Collection(ApplicationState.Name)]
public class AppUiTests
{
    /// <summary>The application's colors, icons and styles, removed when disposed.</summary>
    private sealed class Themes : IDisposable
    {
        private readonly ResourceInclude _colors = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Colors.axaml") };
        private readonly ResourceInclude _icons = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Icons.axaml") };
        private readonly StyleInclude _styles = new(new Uri("avares://Snaplume/")) { Source = new Uri("avares://Snaplume/Themes/Styles.axaml") };
        private readonly AppSettings _settings = AppServices.Settings;

        public Themes()
        {
            var app = Application.Current!;
            app.Resources.MergedDictionaries.Add(_colors);
            app.Resources.MergedDictionaries.Add(_icons);
            app.Styles.Add(_styles);
        }

        public void Dispose()
        {
            var app = Application.Current!;
            AppServices.Settings = _settings;
            Workspaces.ForgetAvailability();
            app.Styles.Remove(_styles);
            app.Resources.MergedDictionaries.Remove(_icons);
            app.Resources.MergedDictionaries.Remove(_colors);
        }
    }

    [AvaloniaFact]
    public async Task Gallery_keeps_only_the_thumbnails_near_what_is_visible()
    {
        using var themes = new Themes();
        using var folder = new TempFolder();
        var png = ImageIO.EncodePng(TestImages.Solid(64, 48, Avalonia.Media.Colors.SteelBlue));
        for (int i = 0; i < 2000; i++) File.WriteAllBytes(folder.File($"Capture_{i:D4}.png"), png);
        AppServices.Settings = new AppSettings { WorkspaceFolder = folder.Path };

        var gallery = new CaptureGallery();
        var window = new Window { Width = 800, Height = 600, Content = gallery };
        window.Resources["GalleryItemWidth"] = 150.0;
        window.Resources["GalleryThumbnailHeight"] = 96.0;
        window.Resources["GalleryTextAlignment"] = Avalonia.Media.TextAlignment.Center;
        try
        {
            window.Show();
            gallery.Reload();
            var list = gallery.FindControl<ListBox>("CaptureList")!;
            IReadOnlyList<CaptureItem> Items() => ((IEnumerable<CaptureItem>)list.ItemsSource!).ToList();
            int Loaded() => Items().Count(i => i.Thumbnail != null);

            await QuickPanelTests.WaitForAsync(() => Items().Count == 2000 && Loaded() > 0, timeoutMs: 15000);
            // Let the loading settle: the captures near the top are loaded, the others are not.
            await QuickPanelTests.WaitForAsync(() => false, timeoutMs: 500);
            var items = Items();
            Assert.Equal(2000, items.Count);
            int atTop = Loaded();
            Assert.InRange(atTop, 1, 200);
            Assert.NotNull(items[0].Thumbnail);
            Assert.Null(items[^1].Thumbnail);

            // At the end of the list: the last ones are loaded, those at the top let go.
            var scroller = list.GetVisualDescendants().OfType<ScrollViewer>().First();
            scroller.Offset = new Vector(0, scroller.Extent.Height);
            await QuickPanelTests.WaitForAsync(() => Items()[^1].Thumbnail != null, timeoutMs: 15000);
            await QuickPanelTests.WaitForAsync(() => false, timeoutMs: 500);
            Assert.NotNull(items[^1].Thumbnail);
            Assert.Null(items[0].Thumbnail);
            Assert.InRange(Loaded(), 1, 200);
        }
        finally
        {
            window.Close();
            gallery.Detach();
        }
    }

    [AvaloniaFact]
    public async Task Workspace_list_switches_once_the_arrow_keys_rest_or_with_enter()
    {
        using var themes = new Themes();
        using var folder = new TempFolder();
        Directory.CreateDirectory(Path.Combine(folder.Path, "Alpha"));
        Directory.CreateDirectory(Path.Combine(folder.Path, "Beta"));
        AppServices.Settings = new AppSettings { WorkspaceFolder = folder.Path };
        Workspaces.ForgetAvailability();
        var window = new Window { Width = 300, Height = 400, Content = new WorkspaceList() };
        try
        {
            window.Show();
            var list = (WorkspaceList)window.Content!;
            await QuickPanelTests.WaitForAsync(() => list.Entries.Select(e => e.Name).SequenceEqual(new[] { "", "Alpha", "Beta" }));
            var listBox = list.GetVisualDescendants().OfType<ListBox>().Single();
            listBox.ContainerFromIndex(0)!.Focus();

            // An arrow key only moves the highlight...
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Alpha", ((WorkspaceEntry)listBox.SelectedItem!).Name);
            Assert.Equal("", Workspaces.ActiveName);
            // ...the workspace becomes active once the keys rest.
            await QuickPanelTests.WaitForAsync(() => Workspaces.ActiveName == "Alpha");
            Assert.Equal("Alpha", Workspaces.ActiveName);

            // Enter switches at once.
            window.KeyPress(Key.Down, RawInputModifiers.None, PhysicalKey.ArrowDown, null);
            window.KeyPress(Key.Enter, RawInputModifiers.None, PhysicalKey.Enter, null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Beta", Workspaces.ActiveName);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaTheory]
    [InlineData(DialogButtons.Ok, DialogResult.Ok)]
    [InlineData(DialogButtons.OkCancel, DialogResult.Cancel)]
    [InlineData(DialogButtons.YesNo, DialogResult.No)]
    [InlineData(DialogButtons.YesNoCancel, DialogResult.Cancel)]
    public void Escape_closes_a_message_box_with_its_cancel_answer(DialogButtons buttons, DialogResult expected)
    {
        var owner = new Window { Width = 400, Height = 300 };
        try
        {
            owner.Show();
            var answer = Dialogs.ShowAsync(owner, "Question ?", "Snaplume", buttons, DialogIcon.Question, DialogResult.Yes);
            Dispatcher.UIThread.RunJobs();
            var dialog = Assert.Single(owner.OwnedWindows);
            dialog.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(answer.IsCompleted);
            Assert.Equal(expected, answer.Result);
        }
        finally
        {
            owner.Close();
        }
    }

    private static readonly UpdateChecker.Release NewerRelease =
        new(new Version(99, 0, 0), "99.0.0", UpdateChecker.ReleasesPage, Array.Empty<ReleaseAsset>());

    /// <summary>The update offer, owned by a visible window so that the test can find it.</summary>
    private static (Window Owner, Window Offer, List<Button> Buttons) ShowUpdateOffer()
    {
        var owner = new Window { Width = 900, Height = 700 };
        owner.Show();
        UpdateWindow.Show(owner, NewerRelease, offer: true);
        Dispatcher.UIThread.RunJobs();
        var offer = Assert.Single(owner.OwnedWindows);
        return (owner, offer, offer.GetVisualDescendants().OfType<Button>().ToList());
    }

    [AvaloniaTheory]
    [InlineData("fr")]
    [InlineData("en")]
    [InlineData("de")]
    [InlineData("es")]
    [InlineData("it")]
    [InlineData("ar")]
    public void Update_offer_shows_its_three_choices_in_full(string language)
    {
        using var themes = new Themes();
        Loc.Initialize(language);
        var (owner, offer, buttons) = ShowUpdateOffer();
        Assert.IsType<UpdateWindow>(offer);
        try
        {
            Assert.Equal(new[] { Loc.T("Update.UpdateNow"), Loc.T("Update.RemindLater"), Loc.T("Update.Skip") },
                buttons.Select(b => b.Content as string));
            foreach (var button in buttons)
            {
                var origin = button.TranslatePoint(default, offer)!.Value;
                Assert.True(origin.X >= 0 && origin.X + button.Bounds.Width <= offer.Bounds.Width, $"{button.Content} dépasse");
                Assert.True(button.DesiredSize.Width <= button.Bounds.Width + 0.5, $"{button.Content} est coupé");
            }
        }
        finally
        {
            offer.Close();
            owner.Close();
            Loc.Initialize("fr");
        }
    }

    [AvaloniaFact]
    public void Update_offer_remembers_dont_remind_me_but_not_later()
    {
        using var themes = new Themes();
        AppServices.Settings = new AppSettings();

        var (owner, offer, buttons) = ShowUpdateOffer();
        Assert.IsType<UpdateWindow>(offer);
        offer.KeyPress(Key.Escape, RawInputModifiers.None, PhysicalKey.Escape, null);
        Dispatcher.UIThread.RunJobs();
        owner.Close();
        Assert.False(offer.IsVisible);
        Assert.Equal("", AppServices.Settings.UpdateSkippedVersion);

        (owner, offer, buttons) = ShowUpdateOffer();
        buttons[2].RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        owner.Close();
        Assert.False(offer.IsVisible);
        Assert.Equal("99.0.0", AppServices.Settings.UpdateSkippedVersion);
    }

    [AvaloniaFact]
    public void Settings_keep_what_changed_elsewhere_while_the_window_was_open()
    {
        using var themes = new Themes();
        var current = new AppSettings { Theme = "light", PanelWorkspacesOpen = false, VideoMicrophone = false, DelaySeconds = 3 };
        var window = new SettingsWindow(current.Clone());
        try
        {
            // Changed in the window...
            window.Result.Theme = "dark";
            window.Result.DelaySeconds = 10;
            // ...and elsewhere meanwhile: the quick panel, the recording bar, the active workspace.
            current.PanelWorkspacesOpen = true;
            current.VideoMicrophone = true;
            current.ActiveWorkspace = "Clients";

            var saved = current.Clone();
            window.MergeInto(saved);
            Assert.Equal("dark", saved.Theme);
            Assert.Equal(10, saved.DelaySeconds);
            Assert.True(saved.PanelWorkspacesOpen);
            Assert.True(saved.VideoMicrophone);
            Assert.Equal("Clients", saved.ActiveWorkspace);
        }
        finally
        {
            window.Close();
        }
    }

    [AvaloniaFact]
    public void Shortcut_fields_tell_when_they_have_the_focus()
    {
        var hotkey = new HotkeyBox();
        var other = new TextBox();
        var window = new Window { Content = new StackPanel { Children = { hotkey, other } } };
        var changes = new List<bool>();
        void Record(bool focused) => changes.Add(focused);
        HotkeyBox.FocusChanged += Record;
        try
        {
            window.Show();
            hotkey.Focus();
            Assert.True(HotkeyBox.IsAnyFocused);
            other.Focus();
            Assert.False(HotkeyBox.IsAnyFocused);
            hotkey.Focus();
            window.Close();
            // Closed with the focus in the field: the shortcuts come back too.
            Assert.False(HotkeyBox.IsAnyFocused);
            Assert.Equal(new[] { true, false, true, false }, changes);
        }
        finally
        {
            HotkeyBox.FocusChanged -= Record;
            window.Close();
        }
    }

    [Fact]
    public async Task Linux_package_changed_after_its_download_is_never_installed()
    {
        using var folder = new TempFolder();
        var package = folder.File("snaplume_9.9.9_amd64.deb");
        File.WriteAllText(package, "not the package that was verified");
        await Assert.ThrowsAsync<UpdateVerificationException>(() => new LinuxUpdateInstaller().InstallPackageAsync(package, new string('a', 64)));
    }
}
