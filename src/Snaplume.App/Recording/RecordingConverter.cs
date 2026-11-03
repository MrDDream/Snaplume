using Avalonia.Threading;
using Snaplume.Core;
using Snaplume.Player;
using PixelRect = Snaplume.Capturing.PixelRect;

namespace Snaplume.Recording;

/// <summary>A finished recording waiting for its conversion (Settings › Video › Video format).</summary>
/// <param name="Recorded">The MP4 H.264 file, still in the folder of the recordings (<see cref="VideoService.RecordingsFolder"/>).</param>
/// <param name="Folder">The workspace it goes to.</param>
/// <param name="BaseName">Its name there, without extension ("Video_2026-11-03_14-22-05").</param>
/// <param name="Area">Where it was recorded (absolute physical pixels): the progress bar is shown next to it.</param>
public sealed record RecordingConversionJob(string Recorded, string Folder, string BaseName, RecordingConversionTarget Target, bool KeepOriginal,
    PixelRect Area);

/// <summary>
/// Converts the recordings that the recorder could not make in the chosen format or compression, one after the other,
/// in the background (ffmpeg or the system's encoder, see <see cref="WebVideoExport"/>): a small bar shows the
/// progress with a Cancel button, the tray tooltip too. The gallery and the notification get the final file only; a
/// failed, refused or cancelled conversion leaves the original (see <see cref="RecordingConversion"/>) and says why.
/// </summary>
public sealed class RecordingConverter
{
    private readonly App _app;
    private readonly Queue<RecordingConversionJob> _waiting = new();
    private CancellationTokenSource? _current;
    /// <summary>The job being converted (see <see cref="PendingFolders"/>).</summary>
    private RecordingConversionJob? _running;
    private TaskCompletionSource? _idle;
    private ConversionBar? _bar;
    /// <summary>Quitting: the conversions still waiting stop at once too.</summary>
    private bool _cancelAll;

    public RecordingConverter(App app) => _app = app;

    /// <summary>A conversion runs or waits.</summary>
    public bool IsBusy => _current != null || _waiting.Count > 0;

    /// <summary>
    /// The workspace folders that a conversion running or waiting will write to: they must not be renamed, deleted or
    /// moved meanwhile. UI thread.
    /// </summary>
    public IReadOnlyList<string> PendingFolders =>
        (_running is { } running ? _waiting.Prepend(running) : _waiting).Select(job => job.Folder).ToList();

    /// <summary>Adds a recording to convert; starts at once when none is running. UI thread.</summary>
    public void Enqueue(RecordingConversionJob job)
    {
        _waiting.Enqueue(job);
        if (_current == null) _ = RunAsync();
        else _bar?.SetWaiting(_waiting.Count);
    }

    /// <summary>Completes once nothing is converted any more (immediately when idle).</summary>
    public Task WhenIdleAsync()
    {
        if (!IsBusy) return Task.CompletedTask;
        _idle ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return _idle.Task;
    }

    /// <summary>Cancels the running conversion and those waiting: their originals go to the workspace.</summary>
    public void CancelAll()
    {
        _cancelAll = true;
        _current?.Cancel();
    }

    /// <summary>Cancels everything, then runs <paramref name="action"/> once the originals are in place (quitting).</summary>
    public void CancelThen(Action action)
    {
        var idle = WhenIdleAsync();
        CancelAll();
        idle.ContinueWith(_ => Dispatcher.UIThread.Post(action), TaskScheduler.Default);
    }

    private async Task RunAsync()
    {
        while (_waiting.Count > 0)
        {
            var job = _waiting.Dequeue();
            using var cancel = new CancellationTokenSource();
            _current = cancel;
            _running = job;
            if (_cancelAll) cancel.Cancel();
            try
            {
                await ConvertAsync(job, cancel);
            }
            catch (Exception ex)
            {
                // Before the conversion (ConvertAsync handles its own failures): the recording waits where it is.
                ErrorLog.Write("Conversion d'un enregistrement", ex);
                if (File.Exists(job.Recorded))
                    _app.Tray?.ShowError(Loc.T("Video.SaveFailed"), Loc.T("Video.KeptInTemp", ex.Message, job.Recorded));
            }
            finally
            {
                _current = null;
                _running = null;
            }
        }
        _cancelAll = false;
        CloseBar();
        _app.SetTrayStatus(null);
        var idle = _idle;
        _idle = null;
        idle?.TrySetResult();
    }

    private async Task ConvertAsync(RecordingConversionJob job, CancellationTokenSource cancel)
    {
        var formatName = Loc.T("Player.Web.Format." + WebFormats.Id(job.Target.Format));
        var title = job.Target.TargetBytes is { } bytes
            ? Loc.T("Video.ConvertingSize", formatName, WebVideoExport.Megabytes(bytes))
            : Loc.T("Video.Converting", formatName);
        ShowBar(job, title, cancel);
        var progress = new Progress<double>(value =>
        {
            if (_current != cancel) return;
            value = Math.Clamp(value, 0, 1);
            _bar?.SetProgress(value);
            _app.SetTrayStatus(Loc.T("Video.ConvertingTray", value * 100));
        });
        _app.SetTrayStatus(Loc.T("Video.ConvertingTray", 0.0));

        var video = _app.Platform.Video;
        RecordingConversionResult result;
        try
        {
            result = await RecordingConversion.RunAsync(job.Recorded, job.Folder, job.BaseName, job.Target, job.KeepOriginal,
                (path, token) => video.ProbeAsync(path, token),
                async (source, destination, settings, info, report, token) =>
                {
                    var ffmpeg = await FfmpegTools.DetectAsync();
                    await WebVideoExport.ExportAsync(video, ffmpeg, source, destination, settings, info, report, token);
                },
                progress, cancel.Token);
        }
        catch (Exception ex)
        {
            // The original could not even be moved into the workspace: it stays in the folder of the recordings.
            ErrorLog.Write("Enregistrement de la vidéo convertie", ex);
            _app.Tray?.ShowError(Loc.T("Video.SaveFailed"), Loc.T("Video.KeptInTemp", ex.Message, job.Recorded));
            return;
        }

        // The files are in place: a failure from here on only concerns the notifications.
        try
        {
            Announce(result);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Annonce de la vidéo convertie", ex);
        }
    }

    /// <summary>Shows the result of a conversion in the gallery and the notifications.</summary>
    private void Announce(RecordingConversionResult result)
    {
        var name = Path.GetFileName(result.Path);
        if (result.Converted)
        {
            if (result.Original != null) _app.Captures.NotifySaved(result.Original);
            _app.Videos.Announce(result.Path, Loc.T("Video.ConvertedDetails", name, WebVideoExport.Megabytes(SafeLength(result.Path))));
        }
        else if (result.Cancelled)
        {
            _app.Videos.Announce(result.Path, Loc.T("Video.ConvertCancelled", name), showPanel: false);
        }
        else
        {
            var path = result.Path;
            _app.Captures.NotifySaved(path);
            _app.Tray?.ShowWarning(Loc.T("Video.ConvertFailedTitle"), result.Problem + "\n" + Loc.T("Video.OriginalKept", name), () => _app.OpenPlayer(path));
            _app.Captures.WarnIfWorkspaceUnavailable();
        }
        // Converted, but the original to keep could not join it in the workspace.
        if (result.OriginalLeftAt is { } left)
            _app.Tray?.ShowWarning(Loc.T("Video.OriginalNotMovedTitle"), Loc.T("Video.OriginalNotMoved", name, left));
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    private void ShowBar(RecordingConversionJob job, string title, CancellationTokenSource cancel)
    {
        try
        {
            if (_bar == null)
            {
                _bar = new ConversionBar(job.Area);
                _bar.CancelClicked += () => _current?.Cancel();
                _bar.Show();
            }
            _bar.SetJob(title, job.BaseName, _waiting.Count);
        }
        catch (Exception ex)
        {
            // The conversion goes on without its bar (the tray tooltip still says it).
            ErrorLog.Write("Barre de conversion", ex);
            _bar = null;
        }
    }

    private void CloseBar()
    {
        var bar = _bar;
        _bar = null;
        try
        {
            bar?.Close();
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Barre de conversion", ex);
        }
    }
}
