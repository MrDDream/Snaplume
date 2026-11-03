using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Export;
using Snaplume.Imaging;
using Snaplume.Platform.Linux.DBus;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// Desktop integration through the freedesktop tools and services: xdg-open, the FileManager1 D-Bus interface,
/// gio trash, xdg-email, CUPS (lpstat/lp), the autostart folder.
/// </summary>
internal sealed class LinuxDesktopShell : IDesktopShell
{
    private static readonly TimeSpan ShortTimeout = TimeSpan.FromSeconds(15);
    private readonly Lazy<string> _downloads = new(ReadDownloadsFolder);

    public void Open(string target)
    {
        var opener = ExternalTools.Find("xdg-open") ?? throw new InvalidOperationException(Loc.T("Linux.ToolMissing", "xdg-open"));
        _ = Task.Run(async () =>
        {
            try
            {
                var code = await ExternalTools.LaunchAsync(opener, new[] { target }, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (code is { } c and not 0) ErrorLog.Write($"Ouverture de {target}", $"xdg-open a renvoyé {c}");
            }
            catch (Exception ex)
            {
                ErrorLog.Write($"Ouverture de {target}", ex);
            }
        });
    }

    /// <summary>Asks the file manager (Nautilus, Dolphin, Nemo, Thunar...) to show the file selected; opens the folder otherwise.</summary>
    public void Reveal(string path)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var connection = await DBusCalls.SessionAsync().ConfigureAwait(false);
                await DBusCalls.CallAsync(connection, "org.freedesktop.FileManager1", "/org/freedesktop/FileManager1",
                    "org.freedesktop.FileManager1", "ShowItems", new DStrings(new[] { FileUris.FromPath(path) }), new DString("")).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Afficher dans le dossier (FileManager1)", ex);
                var folder = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
                if (folder != null && Directory.Exists(folder)) Open(folder);
            }
        });
    }

    public bool MoveToTrash(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return true;
        if (ExternalTools.Find("gio") is { } gio)
        {
            try
            {
                var arguments = new List<string> { "trash", "--" };
                arguments.AddRange(paths);
                var result = ExternalTools.Run(gio, arguments, TimeSpan.FromMinutes(2));
                if (result.Succeeded) return true;
                ErrorLog.Write("Corbeille (gio trash)", result.Errors.Trim());
                return false;
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Corbeille (gio trash)", ex);
                return false;
            }
        }
        return paths.All(HomeTrash.TryMove);
    }

    public bool CanEmail => ExternalTools.Find("xdg-email") != null;

    public void Email(IReadOnlyList<string> paths, string subject)
    {
        var email = ExternalTools.Find("xdg-email") ?? throw new InvalidOperationException(Loc.T("Linux.EmailNoClient"));
        var arguments = new List<string> { "--subject", subject };
        foreach (var path in paths)
        {
            arguments.Add("--attach");
            arguments.Add(path);
        }
        // xdg-email may stay until the mail program it started is closed (Thunderbird): only an immediate failure
        // is waited for, briefly since the caller is the UI thread; a later one can only be logged.
        var launch = ExternalTools.LaunchAsync(email, arguments, TimeSpan.FromMinutes(1));
        Task.WhenAny(launch, Task.Delay(EmailWait)).GetAwaiter().GetResult();
        if (!launch.IsCompleted)
        {
            _ = launch.ContinueWith(t =>
            {
                if (t.IsFaulted) ErrorLog.Write("Envoi par e-mail (xdg-email)", t.Exception!.GetBaseException());
                else if (EmailFailure(t.Result) is { } failure) ErrorLog.Write("Envoi par e-mail (xdg-email)", failure);
            }, TaskScheduler.Default);
            return;
        }
        if (EmailFailure(launch.GetAwaiter().GetResult()) is { } message) throw new InvalidOperationException(message);
    }

    private static readonly TimeSpan EmailWait = TimeSpan.FromMilliseconds(500);

    /// <summary>xdg-email: 3 = a required tool is missing, 4 = the action failed (no mail client configured).</summary>
    private static string? EmailFailure(int? code) => code switch
    {
        3 or 4 => Loc.T("Linux.EmailNoClient"),
        { } c and not 0 => Loc.T("Linux.EmailFailed") + $" (xdg-email {c})",
        _ => null,
    };

    public async Task<bool> PrintImagesAsync(IReadOnlyList<string> paths, string title, IntPtr owner)
    {
        var images = paths.Where(ImageIO.IsImage).ToList();
        if (images.Count == 0) throw new InvalidOperationException(Loc.T("Share.Print.NoImage"));
        var folder = Directory.CreateTempSubdirectory(PrintFolderPrefix);
        bool keep = false;
        try
        {
            var pdf = Path.Combine(folder.FullName, FileOps.SanitizeFileName(string.IsNullOrWhiteSpace(title) ? "Snaplume" : title) + ".pdf");
            var options = new DocumentOptions
            {
                Kind = DocumentKind.Pdf,
                Title = title,
                OnePerPage = true,
                IncludeFileName = false,
                Culture = Loc.Culture,
                Language = Loc.Code,
            };
            await Task.Run(() => DocumentGenerator.Generate(images, options, pdf)).ConfigureAwait(false);

            var lp = ExternalTools.Find("lp");
            var printers = lp == null ? (Printers: Array.Empty<string>(), Default: null) : await ReadPrintersAsync().ConfigureAwait(false);
            if (lp == null || printers.Printers.Count == 0)
            {
                // No CUPS: the PDF viewer prints it with the desktop's own dialog. The file stays in the temporary
                // folder (CleanStalePrintFolders removes it at a later start).
                Open(pdf);
                keep = true;
                return true;
            }
            var choice = await Dispatcher.UIThread.InvokeAsync(() => PrintDialog.AskAsync(printers.Printers, printers.Default, title, owner));
            if (choice == null) return false;
            var result = await ExternalTools.RunAsync(lp, new[]
            {
                "-d", choice.Printer, "-n", choice.Copies.ToString(System.Globalization.CultureInfo.InvariantCulture), "-t", title, "--", pdf,
            }, TimeSpan.FromMinutes(2), environment: ExternalTools.CLocale).ConfigureAwait(false);
            if (!result.Succeeded) throw new InvalidOperationException($"lp: {result.Errors.Trim()}");
            return true;
        }
        finally
        {
            // lp copies the file into the CUPS spool before returning.
            if (!keep)
            {
                try
                {
                    folder.Delete(recursive: true);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Dossier temporaire d'impression", ex);
                }
            }
        }
    }

    private const string PrintFolderPrefix = "snaplume-print-";

    /// <summary>
    /// Removes the print folders of earlier runs, older than a day: those handed to a PDF viewer, or left by a
    /// crash. Links are skipped (in a shared /tmp, another user could plant one).
    /// </summary>
    internal static void CleanStalePrintFolders(string tempFolder, DateTime nowUtc)
    {
        try
        {
            foreach (var folder in new DirectoryInfo(tempFolder).EnumerateDirectories(PrintFolderPrefix + "*"))
            {
                try
                {
                    if (folder.LinkTarget != null || folder.LastWriteTimeUtc > nowUtc.AddDays(-1)) continue;
                    folder.Delete(recursive: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Another user's, or in use: left alone.
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ErrorLog.Write("Dossiers temporaires d'impression", ex);
        }
    }

    private static async Task<(IReadOnlyList<string> Printers, string? Default)> ReadPrintersAsync()
    {
        if (ExternalTools.Find("lpstat") is not { } lpstat) return (Array.Empty<string>(), null);
        try
        {
            var all = await ExternalTools.RunAsync(lpstat, new[] { "-a" }, ShortTimeout, environment: ExternalTools.CLocale).ConfigureAwait(false);
            var def = await ExternalTools.RunAsync(lpstat, new[] { "-d" }, ShortTimeout, environment: ExternalTools.CLocale).ConfigureAwait(false);
            return (all.Succeeded ? Shell.DesktopFiles.ParsePrinters(all.Text) : Array.Empty<string>(), DesktopFiles.ParseDefaultPrinter(def.Text));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Liste des imprimantes (lpstat)", ex);
            return (Array.Empty<string>(), null);
        }
    }

    public string DownloadsFolder => _downloads.Value;

    /// <summary>xdg-user-dir answers the home folder when no download folder is configured: ~/Downloads then.</summary>
    private static string ReadDownloadsFolder()
    {
        var fallback = Path.Combine(Xdg.Home, "Downloads");
        try
        {
            if (ExternalTools.Find("xdg-user-dir") is { } tool)
            {
                var result = ExternalTools.Run(tool, new[] { "DOWNLOAD" }, TimeSpan.FromSeconds(5));
                var folder = result.Text.Trim();
                if (result.Succeeded && folder.StartsWith('/') && Path.TrimEndingDirectorySeparator(folder) != Path.TrimEndingDirectorySeparator(Xdg.Home))
                    return folder;
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Dossier des téléchargements", ex);
        }
        return fallback;
    }

    private static string AutostartFile => Path.Combine(Xdg.ConfigHome, "autostart", Xdg.AppId + ".desktop");

    public bool StartsWithSession
    {
        get
        {
            try
            {
                return File.Exists(AutostartFile) && DesktopFiles.IsAutostartActive(File.ReadAllText(AutostartFile));
            }
            catch (Exception ex)
            {
                ErrorLog.Write("Lecture du démarrage automatique", ex);
                return false;
            }
        }
    }

    public void SetStartWithSession(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(AutostartFile);
                return;
            }
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Unknown executable path");
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile, DesktopFiles.AutostartEntry(executable, "Snaplume", Loc.T("Linux.AutostartComment")));
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Démarrage automatique", ex);
        }
    }

    public bool PrintScreenTakenBySystem => false;

    public string? CheckWorkspaceFolder(string folder)
    {
        try
        {
            var full = Path.GetFullPath(folder);
            return DesktopFiles.IsSystemFolder(full) ? Loc.T("Linux.SystemFolder", full) : null;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Loc.T("Settings.InvalidFolder", folder);
        }
    }
}
