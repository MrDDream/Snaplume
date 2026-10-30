using Snaplume.Core;
using Snaplume.Platform.Linux.Interop;

namespace Snaplume.Platform.Linux.Media;

/// <summary>GStreamer, initialised once per process on first use.</summary>
internal static class GStreamerRuntime
{
    private static readonly Lazy<string?> InitError = new(Initialize);

    /// <summary>True when the GStreamer libraries are installed and initialised.</summary>
    public static bool TryInit(out string? error)
    {
        error = InitError.Value;
        return error == null;
    }

    /// <exception cref="InvalidOperationException">GStreamer is missing (message ready to show).</exception>
    public static void EnsureInit()
    {
        if (!TryInit(out var error)) throw new InvalidOperationException(Loc.T("Linux.GStreamerMissing") + Environment.NewLine + error);
    }

    private static string? Initialize()
    {
        try
        {
            if (Gst.gst_init_check(IntPtr.Zero, IntPtr.Zero, out var error) == 0) return Gst.TakeError(error);
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            ErrorLog.Write("Initialisation de GStreamer", ex);
            return ex.Message;
        }
    }

    /// <summary>The plugin providing this element is installed.</summary>
    public static bool Has(string element)
    {
        if (!TryInit(out _)) return false;
        var factory = Gst.gst_element_factory_find(element);
        if (factory == IntPtr.Zero) return false;
        Gst.gst_object_unref(factory);
        return true;
    }

    /// <summary>Text of an error or warning message of the bus, with the element that sent it.</summary>
    public static unsafe string DescribeMessage(IntPtr message, bool warning = false)
    {
        IntPtr error, debug;
        if (warning) Gst.gst_message_parse_warning(message, out error, out debug);
        else Gst.gst_message_parse_error(message, out error, out debug);
        var source = *(IntPtr*)((byte*)message + Gst.MessageSourceOffset);
        var name = source == IntPtr.Zero ? null : Gst.TakeString(Gst.gst_object_get_name(source));
        var text = Gst.TakeError(error);
        var details = Gst.TakeString(debug);
        return $"{name ?? "?"}: {text}" + (details != null ? $" ({details})" : "");
    }

    public static unsafe int MessageType(IntPtr message) => *(int*)((byte*)message + Gst.MessageTypeOffset);
}
