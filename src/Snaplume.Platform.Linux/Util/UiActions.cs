using Avalonia.Threading;
using Snaplume.Core;

namespace Snaplume.Platform.Linux.Util;

/// <summary>Actions of the application started from a background thread (shortcuts, notification clicks).</summary>
internal static class UiActions
{
    /// <summary>Runs the action on the Avalonia UI thread; an exception is logged under <paramref name="context"/>, never thrown.</summary>
    public static void Post(Action action, string context) => Dispatcher.UIThread.Post(() =>
    {
        try
        {
            action();
        }
        catch (Exception ex)
        {
            ErrorLog.Write(context, ex);
        }
    });
}
