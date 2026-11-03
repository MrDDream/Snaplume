using System.Runtime.InteropServices;

namespace Snaplume.Platform.Linux.Interop;

/// <summary>
/// GStreamer 1.x, GObject and GLib, called in process. Only non-variadic functions are used: properties are set
/// through GValue (g_object_set_property) because variadic calls are not portable through P/Invoke.
/// </summary>
internal static unsafe class Gst
{
    private const string Core = "libgstreamer-1.0.so.0";
    private const string App = "libgstapp-1.0.so.0";
    private const string GObject = "libgobject-2.0.so.0";
    private const string GLib = "libglib-2.0.so.0";

    public const int StateNull = 1, StatePaused = 3, StatePlaying = 4;
    public const int StateChangeFailure = 0;
    public const int FormatTime = 3;
    public const int SeekFlagFlush = 1, SeekFlagAccurate = 2, SeekFlagKeyUnit = 4;
    public const int MapRead = 1;

    public const int MessageEos = 1 << 0, MessageError = 1 << 1, MessageWarning = 1 << 2, MessageDurationChanged = 1 << 18;

    /// <summary>GstMessage.type, after the 64-byte GstMiniObject header (LP64).</summary>
    public const int MessageTypeOffset = 64;
    /// <summary>GstMessage.src.</summary>
    public const int MessageSourceOffset = 80;

    // Fundamental GTypes are fixed numbers (type id << 2).
    public const nuint TypeInt = 6 << 2, TypeDouble = 15 << 2, TypeString = 16 << 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct GValue
    {
        public nuint Type;
        public ulong Data0;
        public ulong Data1;
    }

    /// <summary>GstMapInfo (104 bytes on LP64); only data and size are read.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 104)]
    public struct MapInfo
    {
        [FieldOffset(16)] public byte* Data;
        [FieldOffset(24)] public nuint Size;
    }

    [DllImport(Core)] public static extern int gst_init_check(IntPtr argc, IntPtr argv, out IntPtr error);
    [DllImport(Core)] public static extern IntPtr gst_parse_launch([MarshalAs(UnmanagedType.LPUTF8Str)] string description, out IntPtr error);
    [DllImport(Core)] public static extern IntPtr gst_parse_bin_from_description([MarshalAs(UnmanagedType.LPUTF8Str)] string description, int ghostUnlinkedPads, out IntPtr error);
    [DllImport(Core)] public static extern IntPtr gst_element_factory_make([MarshalAs(UnmanagedType.LPUTF8Str)] string factory, IntPtr name);
    [DllImport(Core)] public static extern IntPtr gst_element_factory_find([MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] public static extern int gst_element_set_state(IntPtr element, int state);
    [DllImport(Core)] public static extern int gst_element_get_state(IntPtr element, out int state, out int pending, ulong timeout);
    [DllImport(Core)] public static extern IntPtr gst_element_get_bus(IntPtr element);
    [DllImport(Core)] public static extern IntPtr gst_bus_timed_pop_filtered(IntPtr bus, ulong timeout, int types);
    [DllImport(Core)] public static extern void gst_mini_object_unref(IntPtr obj);
    [DllImport(Core)] public static extern IntPtr gst_object_ref(IntPtr obj);
    [DllImport(Core)] public static extern void gst_object_unref(IntPtr obj);
    [DllImport(Core)] public static extern IntPtr gst_object_get_name(IntPtr obj);
    [DllImport(Core)] public static extern void gst_message_parse_error(IntPtr message, out IntPtr error, out IntPtr debug);
    [DllImport(Core)] public static extern void gst_message_parse_warning(IntPtr message, out IntPtr error, out IntPtr debug);
    [DllImport(Core)] public static extern IntPtr gst_event_new_eos();
    [DllImport(Core)] public static extern int gst_element_send_event(IntPtr element, IntPtr gstEvent);
    [DllImport(Core)] public static extern IntPtr gst_bin_get_by_name(IntPtr bin, [MarshalAs(UnmanagedType.LPUTF8Str)] string name);
    [DllImport(Core)] public static extern int gst_element_query_position(IntPtr element, int format, out long position);
    [DllImport(Core)] public static extern int gst_element_query_duration(IntPtr element, int format, out long duration);
    /// <summary>Seek with a playback rate (GstSeekType: 0 none, 1 set).</summary>
    [DllImport(Core)] public static extern int gst_element_seek(IntPtr element, double rate, int format, int flags, int startType, long start, int stopType, long stop);
    public const int SeekTypeNone = 0, SeekTypeSet = 1;
    [DllImport(Core)] public static extern IntPtr gst_sample_get_buffer(IntPtr sample);
    [DllImport(Core)] public static extern IntPtr gst_sample_get_caps(IntPtr sample);
    [DllImport(Core)] public static extern IntPtr gst_caps_get_structure(IntPtr caps, uint index);
    [DllImport(Core)] public static extern int gst_structure_get_int(IntPtr structure, [MarshalAs(UnmanagedType.LPUTF8Str)] string field, out int value);
    [DllImport(Core)] public static extern int gst_buffer_map(IntPtr buffer, MapInfo* info, int flags);
    [DllImport(Core)] public static extern void gst_buffer_unmap(IntPtr buffer, MapInfo* info);

    [DllImport(App)] public static extern IntPtr gst_app_sink_try_pull_sample(IntPtr sink, ulong timeout);
    [DllImport(App)] public static extern IntPtr gst_app_sink_try_pull_preroll(IntPtr sink, ulong timeout);
    [DllImport(App)] public static extern int gst_app_sink_is_eos(IntPtr sink);

    [DllImport(GObject)] public static extern void g_object_set_property(IntPtr obj, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, GValue* value);
    [DllImport(GObject)] public static extern IntPtr g_value_init(GValue* value, nuint type);
    [DllImport(GObject)] public static extern void g_value_set_string(GValue* value, [MarshalAs(UnmanagedType.LPUTF8Str)] string text);
    [DllImport(GObject)] public static extern void g_value_set_double(GValue* value, double number);
    [DllImport(GObject)] public static extern void g_value_set_int(GValue* value, int number);
    [DllImport(GObject)] public static extern void g_value_set_object(GValue* value, IntPtr obj);
    [DllImport(GObject)] public static extern void g_value_unset(GValue* value);

    [DllImport(GLib)] public static extern void g_error_free(IntPtr error);
    [DllImport(GLib)] public static extern void g_free(IntPtr memory);

    /// <summary>GError.message (after the domain and the code).</summary>
    public static string? ErrorMessage(IntPtr error) => error == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(*(IntPtr*)((byte*)error + 8));

    /// <summary>Message of a GError, freed afterwards.</summary>
    public static string TakeError(IntPtr error)
    {
        if (error == IntPtr.Zero) return "unknown error";
        try
        {
            return ErrorMessage(error) ?? "unknown error";
        }
        finally
        {
            g_error_free(error);
        }
    }

    public static string? TakeString(IntPtr text)
    {
        if (text == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUTF8(text);
        }
        finally
        {
            g_free(text);
        }
    }

    public static void SetString(IntPtr obj, string property, string value)
    {
        var v = new GValue();
        g_value_init(&v, TypeString);
        g_value_set_string(&v, value);
        g_object_set_property(obj, property, &v);
        g_value_unset(&v);
    }

    public static void SetInt(IntPtr obj, string property, int value)
    {
        var v = new GValue();
        g_value_init(&v, TypeInt);
        g_value_set_int(&v, value);
        g_object_set_property(obj, property, &v);
        g_value_unset(&v);
    }

    public static void SetDouble(IntPtr obj, string property, double value)
    {
        var v = new GValue();
        g_value_init(&v, TypeDouble);
        g_value_set_double(&v, value);
        g_object_set_property(obj, property, &v);
        g_value_unset(&v);
    }

    /// <summary>Sets an element-typed property (playbin's video-sink).</summary>
    public static void SetElement(IntPtr obj, string property, IntPtr element)
    {
        var v = new GValue();
        g_value_init(&v, ElementType());
        g_value_set_object(&v, element);
        g_object_set_property(obj, property, &v);
        g_value_unset(&v);
    }

    [DllImport(Core, EntryPoint = "gst_element_get_type")] private static extern nuint ElementType();
}
