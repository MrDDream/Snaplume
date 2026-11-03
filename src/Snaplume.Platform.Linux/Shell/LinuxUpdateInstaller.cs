using Snaplume.Core;
using Snaplume.Platform.Linux.Util;

namespace Snaplume.Platform.Linux.Shell;

/// <summary>
/// Updates of the .deb / .rpm packages (packaging/linux/nfpm.yaml installs Snaplume in /opt/snaplume). The
/// package is handed to the distribution's package manager through pkexec: the system shows its own password
/// prompt (polkit), dependencies are resolved, and the exit code tells whether it worked. Opening the file with
/// xdg-open (software center) was not chosen: several centers cannot install a local package (Ubuntu's App
/// Center), and Snaplume would not know the result. Without pkexec, or when it fails, the user gets the command.
/// </summary>
internal sealed class LinuxUpdateInstaller : IUpdateInstaller
{
    /// <summary>Where the packages install the application.</summary>
    internal const string PackageFolder = "/opt/snaplume/";
    /// <summary>Downloading dependencies may take a while; a password prompt left open too.</summary>
    private static readonly TimeSpan InstallTimeout = TimeSpan.FromMinutes(30);

    private readonly Lazy<InstallKind> _kind = new(Detect);

    public InstallKind InstallKind => _kind.Value;

    public bool InstallerRestartsSnaplume => false;

    /// <summary>
    /// Installed by a package when running from /opt/snaplume: the package database that knows "snaplume" tells
    /// which (dpkg, then rpm), else the family of the distribution (/etc/os-release). Anything else (a build run
    /// from its folder) is unknown.
    /// </summary>
    private static InstallKind Detect()
    {
        try
        {
            if (!AppContext.BaseDirectory.StartsWith(PackageFolder, StringComparison.Ordinal)) return InstallKind.Unknown;
            if (File.Exists("/var/lib/dpkg/info/snaplume.list")) return InstallKind.Deb;
            if (ExternalTools.Find("rpm") is { } rpm && ExternalTools.Run(rpm, new[] { "-q", "--quiet", "snaplume" }, TimeSpan.FromSeconds(10)).Succeeded)
                return InstallKind.Rpm;
            foreach (var file in new[] { "/etc/os-release", "/usr/lib/os-release" })
            {
                if (File.Exists(file)) return UpdatePackages.LinuxPackageKind(File.ReadAllText(file));
            }
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Type d'installation de Snaplume", ex);
        }
        return InstallKind.Unknown;
    }

    public void StartInstaller(string package, string sha256) => throw new NotSupportedException("Installateurs Windows uniquement");

    public async Task<bool> InstallPackageAsync(string package, string sha256)
    {
        package = Path.GetFullPath(package);
        // Checked again right before the package manager gets it, as root.
        if (!UpdateDownloader.HasSha256(package, sha256))
            throw new UpdateVerificationException($"Somme SHA-256 de {Path.GetFileName(package)} différente avant l'installation");
        var pkexec = File.Exists(Pkexec) ? Pkexec : null;
        var command = PackageManagerCommand(package);
        if (pkexec == null || command == null)
        {
            ErrorLog.Write("Installation de la mise à jour", pkexec == null ? "pkexec introuvable" : "Aucun gestionnaire de paquets connu");
            return false;
        }
        try
        {
            var result = await ExternalTools.RunAsync(pkexec, command, InstallTimeout, environment: ExternalTools.CLocale).ConfigureAwait(false);
            if (result.Succeeded) return true;
            // 126: password prompt dismissed; 127: not authorized or no polkit agent.
            ErrorLog.Write("Installation de la mise à jour",
                $"{string.Join(' ', command)} : code {result.ExitCode}{Environment.NewLine}{result.Errors.Trim()}");
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Installation de la mise à jour", ex);
        }
        return false;
    }

    /// <summary>
    /// Debian / Ubuntu: apt-get, which installs a local .deb with its dependencies. Fedora: dnf. openSUSE: zypper
    /// (the release packages are not signed). Absolute paths: pkexec does not search PATH the same way everywhere.
    /// </summary>
    private List<string>? PackageManagerCommand(string package)
    {
        if (InstallKind == InstallKind.Deb && FindSystemTool("apt-get") is { } apt)
            return new List<string> { apt, "install", "-y", package };
        if (InstallKind == InstallKind.Rpm)
        {
            if (FindSystemTool("dnf") is { } dnf) return new List<string> { dnf, "install", "-y", package };
            if (FindSystemTool("zypper") is { } zypper)
                return new List<string> { zypper, "--non-interactive", "install", "--allow-unsigned-rpm", package };
        }
        return null;
    }

    /// <summary>pkexec by its absolute path: never one found earlier in the user's PATH.</summary>
    private const string Pkexec = "/usr/bin/pkexec";

    /// <summary>Folders of the system's own programs: a program run as root is never taken from the user's PATH (~/bin...).</summary>
    internal const string SystemPath = "/usr/bin:/usr/sbin:/bin:/sbin";

    internal static string? FindSystemTool(string name) => ExternalTools.Find(name, SystemPath);

    public string ManualInstallCommand(string package)
    {
        var tool = InstallKind switch
        {
            InstallKind.Rpm when FindSystemTool("dnf") == null && FindSystemTool("zypper") != null => "zypper install --allow-unsigned-rpm",
            InstallKind.Rpm => "dnf install",
            _ => "apt install",
        };
        return $"sudo {tool} {Quote(Path.GetFullPath(package))}";
    }

    /// <summary>Single quotes for a POSIX shell (a quote inside becomes '\'').</summary>
    internal static string Quote(string text) => "'" + text.Replace("'", "'\\''") + "'";
}
