using System.Runtime.InteropServices;
using Snaplume.Core;

namespace Snaplume.Platform.Windows.Native;

/// <summary>The graphics adapters Direct3D (and so ANGLE, Avalonia's GPU rendering) can use.</summary>
internal static class GraphicsAdapter
{
    private const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;
    /// <summary>
    /// Microsoft's own adapters are no GPU: Basic Render Driver (WARP, Direct3D emulated on the processor), Basic
    /// Display, Remote Display (RDP), Hyper-V video.
    /// </summary>
    private const uint MicrosoftVendorId = 0x1414;
    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    /// <summary>
    /// False when Direct3D can only render on the processor (virtual machines and remote sessions without a GPU):
    /// ANGLE then runs on WARP, several times slower than Skia's own software rendering. True when a GPU is there,
    /// or when it cannot be told.
    /// </summary>
    public static unsafe bool HasHardwareGpu()
    {
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = IID_IDXGIFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero) return true;
            // IDXGIFactory1::EnumAdapters1 is the 13th method (IUnknown 3, IDXGIObject 4, IDXGIFactory 5).
            var enumAdapters = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)(*(IntPtr**)factory)[12];
            for (uint index = 0; ; index++)
            {
                IntPtr adapter;
                // DXGI_ERROR_NOT_FOUND after the last one.
                if (enumAdapters(factory, index, &adapter) < 0 || adapter == IntPtr.Zero) return false;
                try
                {
                    // IDXGIAdapter1::GetDesc1 is the 11th method (IUnknown 3, IDXGIObject 4, IDXGIAdapter 3).
                    var getDesc = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)(*(IntPtr**)adapter)[10];
                    DXGI_ADAPTER_DESC1 desc;
                    if (getDesc(adapter, &desc) < 0) return true;
                    if ((desc.Flags & DXGI_ADAPTER_FLAG_SOFTWARE) == 0 && desc.VendorId != MicrosoftVendorId) return true;
                }
                finally
                {
                    Marshal.Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            ErrorLog.Write("Carte graphique", ex);
            return true;
        }
        finally
        {
            if (factory != IntPtr.Zero) Marshal.Release(factory);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);
}
