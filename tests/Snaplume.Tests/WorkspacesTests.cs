using Avalonia.Headless.XUnit;
using Avalonia.Media;
using Snaplume.Core;
using Snaplume.Editor;
using Snaplume.Imaging;
using static Snaplume.Tests.TestImages;

namespace Snaplume.Tests;

/// <summary>Workspaces in sub-folders of the captures folder and in folders chosen anywhere.</summary>
[Collection(ApplicationState.Name)]
public class WorkspacesTests
{
    /// <summary>A captures folder ("root") and a folder elsewhere ("other"), with the settings pointing at them.</summary>
    private sealed class Scope : IDisposable
    {
        private readonly AppSettings _saved = AppServices.Settings;
        private readonly TempFolder _folder = new();

        public Scope()
        {
            Directory.CreateDirectory(Root);
            Directory.CreateDirectory(Other);
            AppServices.Settings = new AppSettings { WorkspaceFolder = Root };
            Workspaces.ForgetAvailability();
        }

        public string Root => Path.Combine(_folder.Path, "root");
        public string Other => Path.Combine(_folder.Path, "other");
        public AppSettings Settings => AppServices.Settings;

        public void Dispose()
        {
            AppServices.Settings = _saved;
            Workspaces.ForgetAvailability();
            _folder.Dispose();
        }
    }

    [AvaloniaFact]
    public void Custom_folders_are_listed_with_the_sub_folders_once()
    {
        using var scope = new Scope();
        Directory.CreateDirectory(Path.Combine(scope.Root, "Alpha"));
        Directory.CreateDirectory(Path.Combine(scope.Root, "Beta"));
        Directory.CreateDirectory(Path.Combine(scope.Root, ".snaplume"));
        var external = Path.Combine(scope.Other, "Clients");
        Directory.CreateDirectory(external);
        scope.Settings.WorkspaceFolders["Clients"] = external;
        // A custom workspace whose folder is the sub-folder "Beta": listed once, under its own name.
        scope.Settings.WorkspaceFolders["Projet"] = Path.Combine(scope.Root, "Beta");
        // A custom workspace named like a sub-folder: the custom one wins.
        scope.Settings.WorkspaceFolders["Alpha"] = Path.Combine(scope.Other, "Alpha ailleurs");

        Assert.Equal(new[] { "", "Alpha", "Clients", "Projet" }, Workspaces.List());
        Assert.Equal(external, Workspaces.FolderOf("Clients"));
        Assert.Equal(Path.Combine(scope.Other, "Alpha ailleurs"), Workspaces.FolderOf("Alpha"));
        Assert.Equal(Path.Combine(scope.Root, "Gamma"), Workspaces.FolderOf("Gamma"));
        Assert.True(Workspaces.IsCustom("clients"));
        Assert.False(Workspaces.IsCustom(""));

        // A folder already taken by a workspace, or the root, cannot be given to another one.
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", external));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", scope.Root + Path.DirectorySeparatorChar));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", "relative"));
        Assert.Equal(external, Workspaces.CheckFolder("Clients", external));
    }

    [AvaloniaFact]
    public void Old_settings_without_custom_folders_keep_the_old_behaviour()
    {
        var settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>("""{ "WorkspaceFolder": "C:\\Captures", "ActiveWorkspace": "A" }""")!;
        settings.Sanitize();
        Assert.Empty(settings.WorkspaceFolders);

        var odd = new AppSettings { WorkspaceFolders = new() { ["ok"] = Path.GetTempPath(), ["bad/name"] = Path.GetTempPath(), ["rel"] = "relative" } };
        odd.Sanitize();
        Assert.Equal(new[] { "ok" }, odd.WorkspaceFolders.Keys);
        Assert.True(odd.WorkspaceFolders.ContainsKey("OK"));
    }

    [AvaloniaFact]
    public void A_missing_folder_stays_listed_and_captures_go_to_base()
    {
        using var scope = new Scope();
        var unplugged = Path.Combine(scope.Other, "unplugged-drive");
        scope.Settings.WorkspaceFolders["USB"] = unplugged;
        scope.Settings.ActiveWorkspace = "USB";

        Assert.Contains("USB", Workspaces.List());
        Assert.False(Workspaces.IsAvailable("USB"));
        Assert.Equal("", Workspaces.ActiveName);
        Assert.Equal(scope.Root, Workspaces.ActiveFolder);
        Assert.Equal("USB", Workspaces.UnavailableActive);
        Assert.Throws<IOException>(() => Workspaces.MoveFiles(Array.Empty<string>(), "USB"));
        Assert.False(Directory.Exists(unplugged));

        // The drive is back.
        Directory.CreateDirectory(unplugged);
        Workspaces.ForgetAvailability();
        Assert.Equal("USB", Workspaces.ActiveName);
        Assert.Null(Workspaces.UnavailableActive);
    }

    [AvaloniaFact]
    public void Deleting_a_custom_workspace_never_touches_its_folder()
    {
        using var scope = new Scope();
        var folder = Path.Combine(scope.Other, "Mine");
        var name = Workspaces.Create("Mine", folder);
        Assert.Equal("Mine", name);
        Assert.Equal("Mine", Workspaces.ActiveName);
        var capture = Path.Combine(folder, "capture.png");
        ImageIO.Save(Solid(10, 10, Colors.Red), capture);

        Assert.True(Workspaces.Delete("Mine"));
        Assert.True(File.Exists(capture));
        Assert.DoesNotContain("Mine", Workspaces.List());
        Assert.Equal("", scope.Settings.ActiveWorkspace);
    }

    [AvaloniaFact]
    public void Renaming_a_custom_workspace_keeps_its_folder()
    {
        using var scope = new Scope();
        Workspaces.Create("Ancien", scope.Other);
        Assert.Equal("Nouveau", Workspaces.Rename("Ancien", "Nouveau"));
        Assert.Equal(scope.Other, Workspaces.FolderOf("Nouveau"));
        Assert.True(Directory.Exists(scope.Other));
        Assert.Equal("Nouveau", scope.Settings.ActiveWorkspace);
        // The names of the custom workspaces are taken too.
        Directory.CreateDirectory(Path.Combine(scope.Root, "Sub"));
        Assert.Equal("Nouveau 2", Workspaces.Rename("Sub", "Nouveau"));
    }

    [AvaloniaFact]
    public void Changing_the_folder_moves_the_captures_with_their_annotations()
    {
        using var scope = new Scope();
        Workspaces.Create("Projet");
        var oldFolder = Path.Combine(scope.Root, "Projet");
        var capture = Path.Combine(oldFolder, "capture.png");
        ImageIO.Save(Solid(120, 80, Colors.SteelBlue), capture);
        var document = EditorDocument.Load(capture);
        document.PushUndo();
        document.Annotations.Add(new RectangleAnnotation { Start = new(10, 10), End = new(50, 40), Color = Colors.Red, Filled = true });
        document.Save();
        File.WriteAllText(Path.Combine(oldFolder, "notes.txt"), "not a capture");
        var target = Path.Combine(scope.Other, "Projet ailleurs");

        var (moved, failed, _) = Workspaces.MoveCaptures("Projet", target);
        Workspaces.SetFolder("Projet", target);
        Assert.Equal((1, 0), (moved, failed));
        Assert.Equal(target, Workspaces.FolderOf("Projet"));
        var movedCapture = Path.Combine(target, "capture.png");
        Assert.True(File.Exists(movedCapture));
        Assert.Single(EditorDocument.Load(movedCapture).Annotations);
        // Other files stay: the old sub-folder is kept for them (it would come back as a workspace if it were empty).
        Assert.True(File.Exists(Path.Combine(oldFolder, "notes.txt")));

        // Back to the default folder, the captures with it; the folder chosen is left, never deleted.
        var (back, _, _) = Workspaces.MoveCaptures("Projet", Workspaces.DefaultFolderOf("Projet"));
        Workspaces.SetFolder("Projet", null);
        Assert.Equal(1, back);
        Assert.False(Workspaces.IsCustom("Projet"));
        Assert.Single(EditorDocument.Load(capture).Annotations);
        Assert.True(Directory.Exists(target));

        // A default sub-folder left empty is removed once its captures have moved.
        File.Delete(Path.Combine(oldFolder, "notes.txt"));
        Workspaces.MoveCaptures("Projet", target);
        Assert.False(Directory.Exists(oldFolder));
    }

    [Fact]
    public void A_move_refused_across_drives_becomes_a_copy_with_the_same_dates()
    {
        using var folder = new TempFolder();
        var source = folder.File("a.png");
        File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
        var written = new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(source, written);
        var destination = folder.File("b.png");

        // What a plain move does between two volumes on some systems.
        FileOps.MoveFile(source, destination, (_, _) => throw new IOException("Not the same device"));
        Assert.False(File.Exists(source));
        Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(destination));
        Assert.Equal(written, File.GetLastWriteTimeUtc(destination));

        if (!OperatingSystem.IsWindows()) return;
        // The original cannot be deleted (open elsewhere): no second copy is left, the error goes up.
        var third = folder.File("c.png");
        using (new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            Assert.ThrowsAny<IOException>(() => FileOps.MoveFile(destination, third, (_, _) => throw new IOException("Not the same device")));
        }
        Assert.True(File.Exists(destination));
        Assert.False(File.Exists(third));
    }

    [AvaloniaFact]
    public void Workspace_folders_cannot_be_nested()
    {
        using var scope = new Scope();
        var clients = Path.Combine(scope.Other, "Clients");
        Workspaces.Create("Clients", clients);
        Workspaces.Create("Alpha");

        // Inside or around the folder of another workspace, or of the root.
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", Path.Combine(clients, "Sub")));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", scope.Other));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", Path.Combine(scope.Root, "Alpha", "Deeper")));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", Path.Combine(scope.Root, ".snaplume")));
        Assert.Throws<ArgumentException>(() => Workspaces.CheckFolder("New", Path.GetDirectoryName(scope.Root)!));
        // A direct sub-folder of the root, or a folder beside the others.
        Assert.Equal(Path.Combine(scope.Root, "New"), Workspaces.CheckFolder("New", Path.Combine(scope.Root, "New")));
        Assert.Equal(Path.Combine(scope.Other, "Beside"), Workspaces.CheckFolder("New", Path.Combine(scope.Other, "Beside")));
        // Its own folder, and a folder inside it, are its to take.
        Assert.Equal(clients, Workspaces.CheckFolder("Clients", clients));
        Assert.Equal(Path.Combine(clients, "2026"), Workspaces.CheckFolder("Clients", Path.Combine(clients, "2026")));
    }

    [AvaloniaFact]
    public void A_sub_folder_holding_the_folder_of_another_workspace_is_not_deleted()
    {
        using var scope = new Scope();
        Workspaces.Create("Parent");
        var inner = Path.Combine(scope.Root, "Parent", "Inner");
        Directory.CreateDirectory(inner);
        // Made by an older version: CheckFolder refuses it now.
        scope.Settings.WorkspaceFolders["Inner"] = inner;

        Assert.Equal(new[] { "Inner" }, Workspaces.NestedCustomWorkspaces("Parent"));
        Assert.Empty(Workspaces.NestedCustomWorkspaces("Inner"));
        Assert.Throws<ArgumentException>(() => Workspaces.Delete("Parent"));
        Assert.True(Directory.Exists(inner));
        Assert.Contains("Parent", Workspaces.List());

        // Removing the custom one from the list touches nothing.
        Assert.True(Workspaces.Delete("Inner"));
        Assert.True(Directory.Exists(inner));
        Assert.Empty(Workspaces.NestedCustomWorkspaces("Parent"));
    }

    [AvaloniaFact]
    public async Task Captures_are_moved_from_another_thread_with_a_resolved_workspace()
    {
        using var scope = new Scope();
        Workspaces.Create("Projet");
        Workspaces.Create("Dossier", Path.Combine(scope.Other, "Dossier"));
        var capture = Path.Combine(scope.Root, "capture.png");
        ImageIO.Save(Solid(10, 10, Colors.Red), capture);

        var target = Workspaces.Resolve("Dossier");
        Assert.True(target.IsCustom);
        var summary = await Task.Run(() => Workspaces.MoveFiles(new[] { capture }, target), TestContext.Current.CancellationToken);
        Assert.Contains("Dossier", summary);
        Assert.True(File.Exists(Path.Combine(target.Folder, "capture.png")));

        // The default sub-folder, left with only an empty annotation folder, is removed; never what is in it.
        var projet = Workspaces.Resolve("Projet");
        ImageIO.Save(Solid(10, 10, Colors.Blue), Path.Combine(projet.Folder, "b.png"));
        Directory.CreateDirectory(Path.Combine(projet.Folder, SidecarFiles.Folder));
        var (moved, failed, _) = await Task.Run(() => Workspaces.MoveCaptures(projet, target.Folder), TestContext.Current.CancellationToken);
        Assert.Equal((1, 0), (moved, failed));
        Assert.False(Directory.Exists(projet.Folder));
    }

    [AvaloniaFact]
    public void A_rename_that_only_changes_the_case_keeps_another_folder_of_that_name()
    {
        using var scope = new Scope();
        Workspaces.Create("Projet");
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("projet", Workspaces.Rename("Projet", "projet"));
            Assert.Equal(new[] { "projet" }, new DirectoryInfo(scope.Root).GetDirectories().Select(d => d.Name));
            return;
        }
        // Linux tells the two apart: "projet" is another folder (another workspace) there.
        Directory.CreateDirectory(Path.Combine(scope.Root, "projet"));
        Assert.Equal("projet 2", Workspaces.Rename("Projet", "projet"));
        Assert.True(Directory.Exists(Path.Combine(scope.Root, "projet")));
    }

    [AvaloniaFact]
    public void Settings_copies_and_hidden_workspace_names()
    {
        var settings = new AppSettings { ActiveWorkspace = ".snaplume" };
        settings.WorkspaceFolders["A"] = Path.GetTempPath();
        var copy = settings.Clone();
        copy.WorkspaceFolders["B"] = Path.GetTempPath();
        Assert.Equal(new[] { "A" }, settings.WorkspaceFolders.Keys);
        Assert.True(copy.WorkspaceFolders.ContainsKey("a"));

        settings.Sanitize();
        Assert.Equal("", settings.ActiveWorkspace);
    }

    [AvaloniaFact]
    public void Unreadable_settings_are_reported_and_numbers_as_text_are_read()
    {
        var file = Path.Combine(AppSettings.SettingsFolder, "settings.json");
        var saved = File.Exists(file) ? File.ReadAllBytes(file) : null;
        try
        {
            File.WriteAllText(file, """{ "DelaySeconds": "7", "JpegQuality": 80 }""");
            var settings = AppSettings.Load();
            Assert.False(settings.LoadFailed);
            Assert.Equal(7, settings.DelaySeconds);

            File.WriteAllText(file, "{ not json");
            Assert.True(AppSettings.Load().LoadFailed);
            File.WriteAllText(file, "null");
            Assert.True(AppSettings.Load().LoadFailed);
        }
        finally
        {
            if (saved != null) File.WriteAllBytes(file, saved);
            else File.Delete(file);
            File.Delete(file + ".bak");
        }
    }
}
