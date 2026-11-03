using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;
using Snaplume.Platform.Linux.Util;
using static Snaplume.Platform.Linux.Interop.Xlib;

namespace Snaplume.Platform.Linux.X11;

/// <summary>
/// Global shortcuts under X11: XGrabKey on the root window, on a connection of its own read by a background
/// thread. Grabs are made from the caller's thread (Xlib is thread-safe after XInitThreads, and the C# lock
/// keeps the event thread out meanwhile); actions run on the Avalonia UI thread.
/// </summary>
internal sealed unsafe class X11Hotkeys : IGlobalHotkeys
{
    private readonly object _gate = new();
    private readonly List<Binding> _bindings = new();
    private IntPtr _display;
    private nuint _root;
    private uint _lockMasks;
    private Thread? _thread;
    private volatile bool _stopping;

    private sealed record Binding(int Keycode, uint Modifiers, Action Action)
    {
        public long LastFired;
    }

    public X11Hotkeys()
    {
        try
        {
            _display = X11Errors.OpenDisplay();
            if (_display == IntPtr.Zero) return;
            _root = XDefaultRootWindow(_display);
            _lockMasks = LockMasks(_display);
            XSelectInput(_display, _root, KeyPressMask);
            _thread = new Thread(EventLoop) { IsBackground = true, Name = "Snaplume X11 hotkeys" };
            _thread.Start();
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Raccourcis X11", ex);
            _display = IntPtr.Zero;
        }
    }

    public bool IsSupported => _display != IntPtr.Zero;

    public bool Register(string id, string description, Hotkey hotkey, Action action)
    {
        if (!IsSupported || hotkey.IsEmpty || KeySyms.Of(hotkey.Key) is not { } key) return false;
        lock (_gate)
        {
            int keycode = XKeysymToKeycode(_display, key.Sym);
            if (keycode == 0) return false;
            uint modifiers = KeySyms.ModifierMask(hotkey.Modifiers);
            var combinations = KeySyms.LockCombinations(_lockMasks);
            XSync(_display, 0);
            X11Errors.TakeLastError(_display);
            foreach (var locks in combinations)
                XGrabKey(_display, keycode, modifiers | locks, _root, 0, GrabModeAsync, GrabModeAsync);
            XSync(_display, 0);
            if (X11Errors.TakeLastError(_display) != 0)
            {
                // BadAccess: another application holds the shortcut. Release the combinations that did succeed.
                foreach (var locks in combinations) XUngrabKey(_display, keycode, modifiers | locks, _root);
                XSync(_display, 0);
                X11Errors.TakeLastError(_display);
                return false;
            }
            _bindings.Add(new Binding(keycode, modifiers, action));
            return true;
        }
    }

    public void UnregisterAll()
    {
        if (!IsSupported) return;
        lock (_gate)
        {
            var combinations = KeySyms.LockCombinations(_lockMasks);
            foreach (var binding in _bindings)
                foreach (var locks in combinations)
                    XUngrabKey(_display, binding.Keycode, binding.Modifiers | locks, _root);
            XSync(_display, 0);
            X11Errors.TakeLastError(_display);
            _bindings.Clear();
        }
    }

    private void EventLoop()
    {
        var xevent = stackalloc byte[XEventSize];
        int fd;
        lock (_gate) fd = XConnectionNumber(_display);
        var pollFd = new LibC.PollFd { Fd = fd, Events = LibC.POLLIN };
        while (!_stopping)
        {
            var fired = new List<Action>();
            lock (_gate)
            {
                if (_display == IntPtr.Zero) return;
                while (XPending(_display) > 0)
                {
                    XNextEvent(_display, xevent);
                    if (*(int*)(xevent + XOffsets.EventType) != KeyPress) continue;
                    int keycode = *(int*)(xevent + XOffsets.KeyKeycode);
                    uint state = *(uint*)(xevent + XOffsets.KeyState) & ~_lockMasks & (ShiftMask | ControlMask | Mod1Mask | Mod4Mask);
                    foreach (var binding in _bindings)
                    {
                        if (binding.Keycode != keycode || binding.Modifiers != state) continue;
                        // Auto-repeat sends the press again while the key is held: one action per 400 ms at most.
                        long now = Environment.TickCount64;
                        if (now - binding.LastFired < 400) continue;
                        binding.LastFired = now;
                        fired.Add(binding.Action);
                    }
                }
            }
            foreach (var action in fired) UiActions.Post(action, "Raccourci clavier");
            // Outside the lock: grabs from the UI thread are not delayed. The timeout bounds the delay of events
            // that a grab's XSync read into the queue while this thread was waiting.
            LibC.poll(&pollFd, 1, 100);
        }
    }

    /// <summary>Modifier bits of Caps Lock, Num Lock and Scroll Lock (Num Lock is usually Mod2, but not always).</summary>
    private static uint LockMasks(IntPtr display)
    {
        uint masks = LockMask;
        int numLock = XKeysymToKeycode(display, KeySyms.NumLock);
        int scrollLock = XKeysymToKeycode(display, KeySyms.ScrollLock);
        var map = XGetModifierMapping(display);
        if (map == IntPtr.Zero) return masks | 16;
        try
        {
            int perModifier = *(int*)((byte*)map + XOffsets.ModMaxKeysPerMod);
            var codes = *(byte**)((byte*)map + XOffsets.ModModifierMap);
            for (int modifier = 0; modifier < 8; modifier++)
                for (int k = 0; k < perModifier; k++)
                {
                    int code = codes[modifier * perModifier + k];
                    if (code != 0 && (code == numLock || code == scrollLock)) masks |= 1u << modifier;
                }
        }
        finally
        {
            XFreeModifiermap(map);
        }
        return masks;
    }

    public void Dispose()
    {
        _stopping = true;
        UnregisterAll();
        _thread?.Join(500);
        lock (_gate)
        {
            if (_display == IntPtr.Zero) return;
            X11Errors.CloseDisplay(_display);
            _display = IntPtr.Zero;
        }
    }
}
