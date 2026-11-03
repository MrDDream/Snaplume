using System.Runtime.InteropServices;
using Snaplume.Core;

namespace Snaplume.Platform.Windows.Video;

/// <summary>
/// Whether Media Foundation has an HEVC encoder: one of the graphics card, or the software one of the "HEVC Video
/// Extensions" (Microsoft Store). Without it, MediaEncodingProfile.CreateHevc renders fail.
/// </summary>
internal static class HevcEncoders
{
    private const uint MFT_ENUM_FLAG_SYNCMFT = 0x1;
    private const uint MFT_ENUM_FLAG_ASYNCMFT = 0x2;
    private const uint MFT_ENUM_FLAG_HARDWARE = 0x4;
    private const uint MFT_ENUM_FLAG_SORTANDFILTER = 0x40;

    private static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    private static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    /// <summary>FOURCC "HEVC".</summary>
    private static readonly Guid MFVideoFormat_HEVC = new("43564548-0000-0010-8000-00aa00389b71");

    [StructLayout(LayoutKind.Sequential)]
    private struct MFT_REGISTER_TYPE_INFO
    {
        public Guid guidMajorType;
        public Guid guidSubtype;
    }

    [DllImport("mfplat.dll", ExactSpelling = true)]
    private static extern int MFTEnumEx(Guid guidCategory, uint flags, IntPtr inputType, ref MFT_REGISTER_TYPE_INFO outputType,
        out IntPtr activates, out uint count);

    private static readonly Lazy<bool> Present = new(Find);

    /// <summary>True when an HEVC encoder is registered (asked once per session).</summary>
    public static bool Available => Present.Value;

    private static bool Find()
    {
        try
        {
            var output = new MFT_REGISTER_TYPE_INFO { guidMajorType = MFMediaType_Video, guidSubtype = MFVideoFormat_HEVC };
            int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER,
                MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_ASYNCMFT | MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER,
                IntPtr.Zero, ref output, out var activates, out var count);
            if (hr < 0) return false;
            // Only counted: each IMFActivate is released, then the array.
            for (int i = 0; i < count; i++)
            {
                var activate = Marshal.ReadIntPtr(activates, i * IntPtr.Size);
                if (activate != IntPtr.Zero) Marshal.Release(activate);
            }
            if (activates != IntPtr.Zero) Marshal.FreeCoTaskMem(activates);
            return count > 0;
        }
        catch (Exception ex)
        {
            ErrorLog.Write("Recherche d'un encodeur HEVC", ex);
            return false;
        }
    }
}
