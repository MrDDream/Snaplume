using Snaplume.Core;

namespace Snaplume.Tests;

/// <summary>Folders compared, and the folders too broad to hold the captures (Windows, Linux).</summary>
public class PathRulesTests
{
    [Fact]
    public void Folders_are_compared_without_their_trailing_separator()
    {
        var folder = Path.Combine(Path.GetTempPath(), "Snaplume-rules", "Projet");
        Assert.True(PathRules.SamePath(folder, folder + Path.DirectorySeparatorChar));
        Assert.Equal(OperatingSystem.IsWindows(), PathRules.SamePath(folder, folder.ToUpperInvariant()));
        Assert.True(PathRules.IsInside(Path.Combine(folder, "a", "b"), folder + Path.DirectorySeparatorChar));
        Assert.True(PathRules.IsInside(folder, Path.GetPathRoot(folder)!));
        Assert.False(PathRules.IsInside(folder, folder));
        // A sibling whose name starts the same is not inside.
        Assert.False(PathRules.IsInside(folder + "2", folder));
        Assert.False(PathRules.IsInside(Path.GetDirectoryName(folder)!, folder));
    }

    [Fact]
    public void Roots_and_the_folders_of_snaplume_are_too_broad()
    {
        var application = AppContext.BaseDirectory;
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(application))!;
        Assert.True(PathRules.IsTooBroad(Path.GetPathRoot(application)!));
        // Snaplume's own folder and what it holds, its settings, and the folder where its portable updates go.
        Assert.True(PathRules.IsTooBroad(application));
        Assert.True(PathRules.IsTooBroad(Path.Combine(application, "Captures")));
        Assert.True(PathRules.IsTooBroad(AppSettings.SettingsFolder));
        Assert.True(PathRules.IsTooBroad(parent));
        Assert.True(PathRules.IsTooBroad(Path.GetDirectoryName(parent)!));
        Assert.False(PathRules.IsTooBroad(Path.Combine(parent, "Captures")));

        // Application data, and everything inside it.
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.True(PathRules.IsTooBroad(Path.Combine(appData, "Other", "Captures")));
        Assert.True(PathRules.IsTooBroad(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Captures")));
        if (OperatingSystem.IsWindows())
            Assert.True(PathRules.IsTooBroad(Path.Combine(Path.GetDirectoryName(appData)!, "LocalLow", "Captures")));

        // A personal folder and its parents, not what is inside.
        var downloads = Path.Combine(parent, "Downloads");
        Assert.True(PathRules.IsTooBroad(downloads, downloads));
        Assert.False(PathRules.IsTooBroad(Path.Combine(downloads, "Captures"), downloads));
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (pictures.Length > 0)
        {
            Assert.True(PathRules.IsTooBroad(pictures));
            Assert.False(PathRules.IsTooBroad(Path.Combine(pictures, "Snaplume")));
        }
    }

    [Fact]
    public void A_onedrive_root_is_a_personal_folder()
    {
        var saved = Environment.GetEnvironmentVariable("OneDriveCommercial");
        var oneDrive = Path.Combine(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(AppContext.BaseDirectory))!, "OneDrive - Contoso");
        try
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", oneDrive);
            Assert.True(PathRules.IsTooBroad(oneDrive));
            Assert.False(PathRules.IsTooBroad(Path.Combine(oneDrive, "Captures")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("OneDriveCommercial", saved);
        }
    }

    [Theory]
    [InlineData("/", true)]
    [InlineData("relative/folder", true)]
    [InlineData("/usr/share/x", true)]
    [InlineData("/etc/", true)]
    [InlineData("/srv/data", true)]
    [InlineData("/tmp", true)]
    [InlineData("/tmp/captures", true)]
    [InlineData("/var/tmp/captures", true)]
    [InlineData("/run/user/1000/captures", true)]
    [InlineData("/home/me/../../etc", true)]
    [InlineData("/home", true)]
    [InlineData("/var/home", true)]
    [InlineData("/mnt", true)]
    [InlineData("/media", true)]
    [InlineData("/media/me", true)]
    [InlineData("/run/media", true)]
    [InlineData("/run/media/me", true)]
    [InlineData("/home/me/Pictures/Snaplume", false)]
    [InlineData("/var/home/me/Pictures", false)]
    [InlineData("/media/me/USB/Captures", false)]
    [InlineData("/run/media/me/USB/Captures", false)]
    [InlineData("/mnt/nas/captures", false)]
    [InlineData("/usrdata/captures", false)]
    public void Linux_system_folders_are_refused(string folder, bool refused) =>
        Assert.Equal(refused, PathRules.IsUnixSystemFolder(folder));
}
