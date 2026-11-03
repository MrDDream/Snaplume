using Avalonia.Threading;
using Snaplume.Core;

namespace Snaplume.Workspace;

/// <summary>A workspace as last read.</summary>
/// <param name="Name">"" for Base.</param>
/// <param name="Folder">Full path of its folder ("" when the name is not valid).</param>
/// <param name="IsCustom">Its folder was chosen by the user (anywhere).</param>
/// <param name="IsMissing">Its folder of the user's choice cannot be reached (unplugged drive, offline share).</param>
public sealed record WorkspaceInfo(string Name, string Folder, bool IsCustom, bool IsMissing);

/// <summary>The workspaces, Base first, and the active one, as last read.</summary>
public sealed record WorkspaceSnapshot(IReadOnlyList<WorkspaceInfo> Workspaces, string ActiveName)
{
    /// <summary>Before the first reading: Base only.</summary>
    public static readonly WorkspaceSnapshot Empty = new(new[] { new WorkspaceInfo("", "", false, false) }, "");
}

/// <summary>
/// The workspaces and the active one, read off the UI thread (a folder on an offline network share or an unplugged
/// drive may take seconds to answer) and kept for what shows them: the workspace lists, the tray menu and the "Move to"
/// menu of the gallery. Read again when the workspaces change, and when a list or a menu is shown (<see cref="Refresh"/>).
/// </summary>
public static class WorkspaceListing
{
    private static bool _reading;
    private static bool _readAgain;
    private static bool _following;

    /// <summary>The last reading (any thread: replaced as a whole).</summary>
    public static WorkspaceSnapshot Current { get; private set; } = WorkspaceSnapshot.Empty;

    /// <summary>A new reading is in <see cref="Current"/> (UI thread).</summary>
    public static event Action? Updated;

    /// <summary>Reads the workspaces again in the background; <see cref="Updated"/> follows. Can be called from any thread.</summary>
    public static void Refresh()
    {
        if (!Dispatcher.UIThread.CheckAccess())
        {
            Dispatcher.UIThread.Post(Refresh);
            return;
        }
        if (!_following)
        {
            _following = true;
            Workspaces.Changed += Refresh;
        }
        // One reading at a time: a request made meanwhile reads once more afterwards.
        if (_reading)
        {
            _readAgain = true;
            return;
        }
        _ = ReadAsync();
    }

    private static async Task ReadAsync()
    {
        _reading = true;
        try
        {
            do
            {
                _readAgain = false;
                WorkspaceSnapshot? snapshot = null;
                try
                {
                    snapshot = await Task.Run(Read);
                }
                catch (Exception ex)
                {
                    ErrorLog.Write("Liste des espaces de travail", ex);
                }
                // Asked again meanwhile (a workspace created, renamed...): that newer reading replaces this one.
                if (snapshot == null || _readAgain) continue;
                Current = snapshot;
                Updated?.Invoke();
            }
            while (_readAgain);
        }
        finally
        {
            _reading = false;
        }
    }

    /// <summary>
    /// The workspaces as the settings say now. Runs off the UI thread: a change made by the UI thread meanwhile raises
    /// <see cref="Workspaces.Changed"/>, whose newer reading replaces this one (an error is only logged).
    /// </summary>
    internal static WorkspaceSnapshot Read()
    {
        var workspaces = Workspaces.List().Select(name =>
        {
            bool custom = Workspaces.IsCustom(name);
            string folder;
            try
            {
                folder = Workspaces.FolderOf(name);
            }
            catch (ArgumentException)
            {
                folder = "";
            }
            return new WorkspaceInfo(name, folder, custom, custom && !Workspaces.IsAvailable(name));
        }).ToList();
        return new WorkspaceSnapshot(workspaces, Workspaces.ActiveName);
    }
}
