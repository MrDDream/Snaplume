using System.Diagnostics;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Snaplume.Core;
using Snaplume.Platform;

namespace Snaplume.Ui;

/// <summary>
/// Installs a newer version from inside Snaplume: the offer (from the notification), the download with its progress
/// and Cancel, the SHA-256 check (<see cref="UpdateDownloader"/>), then the step that fits how this copy was
/// installed (<see cref="IUpdateInstaller"/>): a Windows installer once Snaplume has quit, a portable copy
/// unzipped next to this one (<see cref="PortableUpdate"/>), or a Linux package handed to the package manager.
/// Built in code like the message boxes of <see cref="Dialogs"/>, whose look it shares.
/// </summary>
public sealed class UpdateWindow : Window
{
    /// <summary>One at a time: a second request brings it to the front.</summary>
    private static UpdateWindow? _open;

    private readonly UpdateChecker.Release _release;
    private readonly UpdatePackage _plan;
    private readonly SelectableTextBlock _message;
    private readonly ProgressBar _progress;
    private readonly TextBlock _detail;
    private readonly StackPanel _buttons;
    private CancellationTokenSource? _download;
    /// <summary>The package manager is at work: the window stays until it answers.</summary>
    private bool _installing;
    private bool _closed;

    /// <summary>
    /// Downloaded packages, in a folder only this user can write to (never the shared temporary folder); emptied before
    /// each download.
    /// </summary>
    /// <exception cref="IOException">The folder is a link, or others may write to it.</exception>
    private static string DownloadFolder() => UpdateDownloader.PrivateFolder("update");

    private IUpdateInstaller Installer => AppServices.Platform.Updates;

    /// <summary>What to download for this copy, or null when it cannot be updated from here (the release page only).</summary>
    public static UpdatePackage? Plan(UpdateChecker.Release release)
    {
        if (!release.IsNewer || !AppServices.HasPlatform) return null;
        var kind = AppServices.Platform.Updates.InstallKind;
        return kind == InstallKind.Unknown ? null : UpdatePackages.Choose(release, kind, RuntimeInformation.OSArchitecture);
    }

    /// <param name="owner">Window asking (Settings), or null from the notification.</param>
    /// <param name="offer">True asks first (notification); false starts downloading at once (the Settings button).</param>
    public static void Show(Window? owner, UpdateChecker.Release release, bool offer)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        var plan = Plan(release);
        if (plan == null)
        {
            FileOps.OpenWithDefaultApp(release.Page);
            return;
        }
        bool owned = owner is { IsVisible: true };
        var window = new UpdateWindow(release, plan, owned);
        _open = window;
        if (offer) window.ShowOffer();
        else _ = window.DownloadAsync();
        if (owned) _ = window.ShowDialog(owner!);
        else window.Show();
    }

    /// <summary>
    /// "Install now" could not quit Snaplume (an editor kept its unsaved changes): the window comes back, ready to try again.
    /// </summary>
    private static void ShowPostponed(UpdateChecker.Release release, UpdatePackage plan, Action<UpdateWindow> state)
    {
        if (_open != null)
        {
            _open.Activate();
            return;
        }
        var window = new UpdateWindow(release, plan, owned: false);
        _open = window;
        state(window);
        window.Show();
        window.Activate();
    }

    private UpdateWindow(UpdateChecker.Release release, UpdatePackage plan, bool owned)
    {
        _release = release;
        _plan = plan;
        Title = Loc.T("Update.WindowTitle");
        Width = 480;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = !owned;
        WindowStartupLocation = owned ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen;
        Topmost = !owned;

        _message = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap };
        _progress = new ProgressBar { Minimum = 0, Maximum = 1, Margin = new Thickness(0, 16, 0, 0), IsVisible = false };
        _progress.Classes.Add("thin");
        _detail = new TextBlock { Margin = new Thickness(0, 6, 0, 0), IsVisible = false };
        _detail.Classes.Add("row-description");
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8, Margin = new Thickness(0, 18, 0, 0) };

        var body = new DockPanel { Margin = new Thickness(22, 20, 22, 18) };
        DockPanel.SetDock(_buttons, Dock.Bottom);
        body.Children.Add(_buttons);
        body.Children.Add(new StackPanel { Children = { _message, _progress, _detail } });
        Content = body;

        Opened += (_, _) => (_buttons.Children.FirstOrDefault() as Button)?.Focus();
        // Escape closes like the last button (Later, Cancel, Close); not while the package manager works.
        KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Escape || e.Handled) return;
            e.Handled = true;
            Close();
        };
        Closing += (_, e) =>
        {
            // Killing pkexec would not stop the package manager: wait for its answer.
            if (_installing) e.Cancel = true;
        };
        Closed += (_, _) =>
        {
            _closed = true;
            _download?.Cancel();
            if (_open == this) _open = null;
        };
    }

    // ---- States ----

    private void ShowOffer()
    {
        SetState(Loc.T("Update.Available", _release.DisplayVersion, UpdateChecker.CurrentVersion.ToString(3)) + "\n\n" + KindHint(), progress: null);
        SetButtons(("Update.Install", () => _ = DownloadAsync()), ("Update.Open", OpenPage), ("Update.Later", Close));
    }

    /// <summary>What will happen, for this kind of installation.</summary>
    private string KindHint() => _plan.Kind switch
    {
        InstallKind.InnoSetup when Installer.InstallerRestartsSnaplume => Loc.T("Update.HintRestart"),
        InstallKind.InnoSetup or InstallKind.Msi => Loc.T("Update.HintReopen"),
        InstallKind.Portable => Loc.T("Update.HintPortable"),
        _ => Loc.T("Update.HintPackage"),
    };

    private async Task DownloadAsync()
    {
        using var cancel = new CancellationTokenSource();
        _download = cancel;
        var package = _plan.Package;
        SetState(Loc.T("Update.Downloading", _release.DisplayVersion), progress: 0);
        ShowProgress(0, package.Size);
        SetButtons(("Dialog.Cancel", Close));
        VerifiedDownload download;
        try
        {
            var folder = await Task.Run(() =>
            {
                var created = DownloadFolder();
                EmptyFolder(created);
                return created;
            });
            var progress = new Progress<long>(received =>
            {
                if (_download == cancel) ShowProgress(received, package.Size);
            });
            download = await new UpdateDownloader().DownloadVerifiedAsync(_plan, folder, progress, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            if (!_closed) Close();
            return;
        }
        catch (UpdateVerificationException ex)
        {
            ErrorLog.Write("Vérification de la mise à jour téléchargée", ex.Message);
            ShowFailure(Loc.T("Update.ChecksumMismatch"));
            return;
        }
        catch (Exception ex)
        {
            ErrorLog.Write($"Téléchargement de la mise à jour ({package.Name})", ex);
            ShowFailure(Loc.T("Update.DownloadFailed", ex.Message));
            return;
        }
        finally
        {
            if (_download == cancel) _download = null;
        }
        if (_closed) return;

        switch (_plan.Kind)
        {
            case InstallKind.InnoSetup or InstallKind.Msi:
                ShowReadyToInstall(download, postponed: false);
                break;
            case InstallKind.Portable:
                await InstallPortableAsync(download);
                break;
            default:
                await InstallPackageAsync(download);
                break;
        }
    }

    /// <summary>
    /// Windows installer: Snaplume quits (editors may ask to save first), then the installer starts. An editor that
    /// keeps its unsaved changes keeps Snaplume running: the window comes back (<paramref name="postponed"/>) to try again.
    /// </summary>
    private void ShowReadyToInstall(VerifiedDownload download, bool postponed)
    {
        var text = Loc.T("Update.Ready", _release.DisplayVersion) + "\n\n" + KindHint();
        SetState(postponed ? Loc.T("Update.Postponed") + "\n\n" + text : text, progress: null);
        SetButtons(("Update.InstallNow", () =>
        {
            Close();
            App.Instance.QuitForUpdate(() => StartInstaller(download),
                () => ShowPostponed(_release, _plan, window => window.ShowReadyToInstall(download, postponed: true)));
        }), ("Dialog.Cancel", Close));
    }

    private bool StartInstaller(VerifiedDownload download)
    {
        try
        {
            Installer.StartInstaller(download.Path, download.Sha256);
            return true;
        }
        catch (UpdateVerificationException ex)
        {
            // Changed since it was downloaded: never run it.
            ErrorLog.Write("Vérification de la mise à jour avant l'installation", ex.Message);
            TryDelete(download.Path);
            _ = Dialogs.ShowAsync(null, Loc.T("Update.ChecksumMismatch"), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return false;
        }
        catch (Exception ex)
        {
            // E.g. the elevation prompt was refused: Snaplume keeps running.
            ErrorLog.Write("Lancement de l'installation de la mise à jour", ex);
            _ = Dialogs.ShowAsync(null, Loc.T("Update.InstallerFailed", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return false;
        }
    }

    private async Task InstallPortableAsync(VerifiedDownload download)
    {
        SetState(Loc.T("Update.Extracting", _release.DisplayVersion), progress: -1);
        SetButtons();
        var executable = Path.GetFileName(Environment.ProcessPath) is { Length: > 0 } name ? name : "Snaplume.exe";
        var zip = download.Path;
        string folder;
        try
        {
            var current = AppContext.BaseDirectory;
            folder = await Task.Run(() =>
            {
                // Checked again right before it is unzipped.
                if (!UpdateDownloader.HasSha256(zip, download.Sha256))
                    throw new UpdateVerificationException($"Somme SHA-256 de {Path.GetFileName(zip)} différente avant l'extraction");
                return PortableUpdate.Install(zip, current, _release.DisplayVersion, executable);
            });
        }
        catch (UpdateVerificationException ex)
        {
            ErrorLog.Write("Vérification de la mise à jour avant l'extraction", ex.Message);
            TryDelete(zip);
            ShowFailure(Loc.T("Update.ChecksumMismatch"));
            return;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Extraction de la mise à jour portable", ex);
            ShowFailure(Loc.T("Update.ExtractFailed", ex.Message));
            return;
        }
        TryDelete(zip);
        if (_closed) return;
        ShowPortableReady(folder, executable, postponed: false);
    }

    /// <summary>The new portable copy is unzipped: Snaplume quits and starts it (editors may ask to save first).</summary>
    private void ShowPortableReady(string folder, string executable, bool postponed)
    {
        var text = Loc.T("Update.PortableReady", _release.DisplayVersion, folder);
        SetState(postponed ? Loc.T("Update.Postponed") + "\n\n" + text : text, progress: null);
        SetButtons(("Update.StartNew", () =>
        {
            Close();
            App.Instance.QuitForUpdate(() => StartPortable(Path.Combine(folder, executable)),
                () => ShowPostponed(_release, _plan, window => window.ShowPortableReady(folder, executable, postponed: true)));
        }), ("Update.OpenFolder", () =>
        {
            Close();
            if (!FileOps.OpenWithDefaultApp(folder)) ErrorLog.Write("Ouverture du dossier de la mise à jour", folder);
        }), ("Update.Close", Close));
    }

    /// <summary>The new copy waits for this one to release the single instance ("--restarted").</summary>
    private static bool StartPortable(string executable)
    {
        try
        {
            Process.Start(executable, "--restarted")?.Dispose();
            return true;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Démarrage de la nouvelle version portable", ex);
            _ = Dialogs.ShowAsync(null, Loc.T("Update.StartNewFailed", ex.Message), "Snaplume", DialogButtons.Ok, DialogIcon.Error);
            return false;
        }
    }

    /// <summary>Linux package: the package manager, through the system's password prompt; the command otherwise.</summary>
    private async Task InstallPackageAsync(VerifiedDownload download)
    {
        SetState(Loc.T("Update.InstallingPackage", _release.DisplayVersion), progress: -1);
        SetButtons();
        var path = download.Path;
        _installing = true;
        bool installed;
        try
        {
            installed = await Installer.InstallPackageAsync(path, download.Sha256);
        }
        catch (UpdateVerificationException ex)
        {
            // Changed since it was downloaded: never handed to the package manager.
            ErrorLog.Write("Vérification de la mise à jour avant l'installation", ex.Message);
            TryDelete(path);
            ShowFailure(Loc.T("Update.ChecksumMismatch"));
            return;
        }
        finally
        {
            _installing = false;
        }
        if (installed)
        {
            TryDelete(path);
            SetState(Loc.T("Update.PackageInstalled", _release.DisplayVersion), progress: null);
            SetButtons(("Update.RestartNow", () =>
            {
                Close();
                App.Instance.Restart();
            }), ("Update.Later", Close));
            return;
        }
        var command = Installer.ManualInstallCommand(path);
        try
        {
            FileOps.Reveal(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Affichage du paquet de la mise à jour", ex);
        }
        SetState(Loc.T("Update.PackageManual", command), progress: null);
        SetButtons(("Update.CopyCommand", () => _ = CopyAsync(command)), ("Update.Close", Close));
    }

    private static async Task CopyAsync(string text)
    {
        try
        {
            if (!await AppServices.Platform.Clipboard.SetTextAsync(text)) ErrorLog.Write("Copie de la commande d'installation", "Presse-papiers indisponible");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Copie de la commande d'installation", ex);
        }
    }

    private void ShowFailure(string message)
    {
        if (_closed) return;
        SetState(message, progress: null);
        SetButtons(("Update.Open", OpenPage), ("Update.Close", Close));
    }

    private void OpenPage()
    {
        Close();
        FileOps.OpenWithDefaultApp(_release.Page);
    }

    // ---- Display ----

    /// <param name="progress">Null hides the bar, a negative value shows it indeterminate.</param>
    private void SetState(string message, double? progress)
    {
        _message.Text = message;
        _progress.IsVisible = progress != null;
        _progress.IsIndeterminate = progress < 0;
        if (progress >= 0) _progress.Value = progress.Value;
        _detail.IsVisible = false;
    }

    private void ShowProgress(long received, long total)
    {
        double fraction = total > 0 ? Math.Clamp((double)received / total, 0, 1) : 0;
        _progress.Value = fraction;
        _detail.Text = Loc.T("Update.Progress", received / 1048576.0, total / 1048576.0, fraction * 100);
        _detail.IsVisible = true;
    }

    /// <summary>Buttons from left to right, the first one in the accent color and focused.</summary>
    private void SetButtons(params (string Key, Action Click)[] buttons)
    {
        _buttons.Children.Clear();
        Button? first = null;
        foreach (var (key, click) in buttons)
        {
            var button = new Button { Content = Loc.T(key), MinWidth = 88, HorizontalContentAlignment = HorizontalAlignment.Center };
            button.Click += (_, _) => click();
            if (first == null)
            {
                button.Classes.Add("accent");
                first = button;
            }
            _buttons.Children.Add(button);
        }
        first?.Focus();
    }

    private static void EmptyFolder(string folder)
    {
        if (!Directory.Exists(folder)) return;
        foreach (var file in Directory.EnumerateFiles(folder)) TryDelete(file);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Suppression d'un fichier de mise à jour", ex);
        }
    }
}
