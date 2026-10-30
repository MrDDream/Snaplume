using System.Runtime.InteropServices;
using Snaplume.Core;
using static Snaplume.Platform.Windows.Native.NativeMethods;

namespace Snaplume.Platform.Windows.Capturing;

/// <summary>Mouse wheel, cursor and Esc key for the scrolling capture; no permission is needed on Windows.</summary>
public sealed class PointerControl : IPointerControl
{
    public Task<bool> PrepareAsync(CancellationToken cancel = default)
    {
        // Forgets an Esc pressed before the capture started ("pressed since the last call" bit).
        GetAsyncKeyState(VK_ESCAPE);
        return Task.FromResult(true);
    }

    public bool TryGetPosition(out int x, out int y)
    {
        bool ok = GetCursorPos(out var p);
        x = p.X;
        y = p.Y;
        return ok;
    }

    public bool SetPosition(int x, int y) => SetCursorPos(x, y);

    /// <summary>Turns the mouse wheel by whole notches at the cursor position (negative: towards the user, scrolls down).</summary>
    /// <returns>
    /// False if Windows refused the input (e.g. input blocked by another thread). A window of a higher
    /// integrity level (run as administrator) silently drops it (UIPI): SendInput still reports success,
    /// so the caller can only notice that nothing scrolled.
    /// </returns>
    public bool Scroll(int notches)
    {
        if (notches == 0) return true;
        int count = Math.Abs(notches);
        var inputs = new INPUT[count];
        for (int i = 0; i < count; i++)
        {
            inputs[i].type = INPUT_MOUSE;
            inputs[i].mi.dwFlags = MOUSEEVENTF_WHEEL;
            inputs[i].mi.mouseData = unchecked((uint)(Math.Sign(notches) * WHEEL_DELTA));
        }
        return SendInput((uint)count, inputs, Marshal.SizeOf<INPUT>()) == count;
    }

    /// <summary>Down now (high bit) or pressed since the previous call (low bit).</summary>
    public bool EscapePressed() => (GetAsyncKeyState(VK_ESCAPE) & 0x8001) != 0;

    /// <summary>
    /// Brings the window under the point to the front: applications that only scroll their active
    /// window (or a system set not to scroll inactive windows) then receive the wheel.
    /// </summary>
    public void ActivateWindowAt(int x, int y)
    {
        try
        {
            var root = GetAncestor(WindowFromPoint(new POINT { X = x, Y = y }), GA_ROOT);
            if (root != IntPtr.Zero && root != GetForegroundWindow()) SetForegroundWindow(root);
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Capture défilante", ex);
        }
    }

    public void Release()
    {
    }
}
