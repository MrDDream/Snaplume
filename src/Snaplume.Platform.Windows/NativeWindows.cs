using System.ComponentModel;
using System.Runtime.InteropServices;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows;

/// <summary>Window styles Avalonia does not expose. Call them once the window has its handle.</summary>
public sealed class NativeWindows : INativeWindows
{
    /// <summary>
    /// Hides a window from screenshots and recordings (same call as ScreenRecorderLib's SetExcludeFromCapture);
    /// a failure is logged (the window then shows in them).
    /// </summary>
    public void ExcludeFromCapture(IntPtr handle)
    {
        // Before Windows 10 2004 the flag does not exist (the window could be shown black): leave it visible.
        if (handle == IntPtr.Zero || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)) return;
        if (!SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE))
            ErrorLog.Write("Exclusion de la capture", new Win32Exception(Marshal.GetLastPInvokeError()));
    }

    /// <summary>
    /// Clicks go through the window: for a top-level window, WS_EX_TRANSPARENT only works together with WS_EX_LAYERED.
    /// A window that was not layered yet is given full opacity, or it would not be drawn at all.
    /// </summary>
    public void MakeClickThrough(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (AddExtendedStyle(handle, WS_EX_TRANSPARENT | WS_EX_LAYERED) is int previous && (previous & WS_EX_LAYERED) == 0)
        {
            if (!SetLayeredWindowAttributes(handle, 0, 255, LWA_ALPHA))
                ErrorLog.Write("Style de fenêtre", new Win32Exception(Marshal.GetLastPInvokeError()));
        }
    }

    /// <summary>
    /// Not in the task bar or Alt+Tab, never activated by a click. Windows reads the task bar style when a window is
    /// shown: call it before showing, or hide and show the window again.
    /// </summary>
    public void MakeToolWindow(IntPtr handle)
    {
        if (handle == IntPtr.Zero) return;
        if (AddExtendedStyle(handle, WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) is not null)
        {
            // A window explicitly marked as an application window would stay in the task bar.
            int styles = GetWindowLong(handle, GWL_EXSTYLE);
            if ((styles & WS_EX_APPWINDOW) != 0) SetWindowLong(handle, GWL_EXSTYLE, styles & ~WS_EX_APPWINDOW);
            SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }
}
