using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Snaplume.Core;

namespace Snaplume.Platform.Windows.Shell;

/// <summary>
/// Updates on Windows. The kind of installation is read from the folder of the executable: the "portable" marker
/// of the zip, the uninstaller Inno Setup leaves in {app} (unins000.exe / unins000.dat), or the MSI product
/// registered with the UpgradeCode of installer/Snaplume.wxs and installed in this folder.
/// </summary>
internal sealed class UpdateInstaller : IUpdateInstaller
{
    /// <summary>UpgradeCode of installer/Snaplume.wxs (never changes).</summary>
    private const string MsiUpgradeCode = "{EF28C5BC-363E-4A3D-A742-48FCC76F3FCF}";

    private readonly Lazy<InstallKind> _kind = new(Detect);

    public InstallKind InstallKind => _kind.Value;

    /// <summary>installer/Snaplume.iss starts Snaplume again after a silent update started with /RELAUNCH=1.</summary>
    public bool InstallerRestartsSnaplume => InstallKind == InstallKind.InnoSetup;

    private static InstallKind Detect()
    {
        try
        {
            if (AppSettings.IsPortable) return InstallKind.Portable;
            var folder = AppContext.BaseDirectory;
            if (Directory.EnumerateFiles(folder, "unins*.exe").Any() && Directory.EnumerateFiles(folder, "unins*.dat").Any())
                return InstallKind.InnoSetup;
            if (IsMsiInstallation(folder)) return InstallKind.Msi;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Type d'installation de Snaplume", ex);
        }
        return InstallKind.Unknown;
    }

    /// <summary>
    /// An MSI product of Snaplume whose installation folder is this one. Packages built before ARPINSTALLLOCATION
    /// was set do not record their folder: the default one (%ProgramFiles%\Snaplume) is assumed for them.
    /// </summary>
    private static bool IsMsiInstallation(string folder)
    {
        var product = new StringBuilder(39);
        for (uint i = 0; i < 16 && MsiEnumRelatedProducts(MsiUpgradeCode, 0, i, product) == 0; i++)
        {
            var location = ProductInfo(product.ToString(), "InstallLocation");
            if (string.IsNullOrEmpty(location))
                location = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Snaplume");
            if (SameFolder(location, folder)) return true;
        }
        return false;
    }

    private static string? ProductInfo(string product, string property)
    {
        uint length = 260;
        var value = new StringBuilder((int)length);
        return MsiGetProductInfo(product, property, value, ref length) == 0 ? value.ToString() : null;
    }

    private static bool SameFolder(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(a)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Inno Setup: silent, with its progress window and no Cancel button (/SP- /SILENT /NOCANCEL); it keeps the
    /// folder, tasks and privileges of the previous installation, closes what still runs from the folder, and
    /// starts Snaplume again (/RELAUNCH=1, see installer/Snaplume.iss). Started through the shell, so that an
    /// installation for all users can ask for elevation.
    /// MSI: msiexec with its basic interface (progress only, /passive); the per-machine package asks for elevation.
    /// </summary>
    public void StartInstaller(string package)
    {
        package = Path.GetFullPath(package);
        switch (InstallKind)
        {
            case InstallKind.InnoSetup:
                Process.Start(new ProcessStartInfo(package)
                {
                    UseShellExecute = true,
                    Arguments = "/SP- /SILENT /NOCANCEL /NORESTART /RELAUNCH=1",
                    WorkingDirectory = Path.GetDirectoryName(package)!,
                })?.Dispose();
                break;
            case InstallKind.Msi:
                var info = new ProcessStartInfo(MsiExec) { UseShellExecute = false };
                foreach (var argument in new[] { "/i", package, "/passive", "/norestart" }) info.ArgumentList.Add(argument);
                Process.Start(info)?.Dispose();
                break;
            default:
                throw new NotSupportedException($"Pas d'installateur pour {InstallKind}");
        }
    }

    public Task<bool> InstallPackageAsync(string package) => throw new NotSupportedException("Paquets Linux uniquement");

    public string ManualInstallCommand(string package) =>
        InstallKind == InstallKind.Msi ? $"\"{MsiExec}\" /i \"{package}\"" : $"\"{package}\"";

    /// <summary>By its full path, never an msiexec.exe found in the current folder.</summary>
    private static string MsiExec => Path.Combine(Environment.SystemDirectory, "msiexec.exe");

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiEnumRelatedProductsW")]
    private static extern uint MsiEnumRelatedProducts(string upgradeCode, uint reserved, uint index, StringBuilder productCode);

    [DllImport("msi.dll", CharSet = CharSet.Unicode, ExactSpelling = true, EntryPoint = "MsiGetProductInfoW")]
    private static extern uint MsiGetProductInfo(string product, string property, StringBuilder value, ref uint length);
}
