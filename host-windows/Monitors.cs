using System.Runtime.InteropServices;

namespace IPadScreenHost;

/// <summary>A desktop monitor plus the DXGI indices FFmpeg's ddagrab needs to capture it.</summary>
internal sealed record MonitorInfo(
    int Index,
    string DeviceName,      // \\.\DISPLAY3
    string FriendlyName,    // monitor name from the driver, e.g. "VDD by MTT"
    int Left, int Top, int Width, int Height,   // physical pixels, virtual-desktop coordinates
    int AdapterIndex, int OutputIndex,
    uint VendorId, string AdapterName)
{
    public bool IsPrimary => Left == 0 && Top == 0;
    public int Right => Left + Width;
    public int Bottom => Top + Height;

    public string Vendor => VendorId switch
    {
        0x8086 => "Intel",
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        _ => $"0x{VendorId:X4}",
    };

    public override string ToString() =>
        $"[{Index}] {DeviceName}  {Width}x{Height} at ({Left},{Top})  \"{FriendlyName}\"{(IsPrimary ? "  (primary)" : "")}  on {AdapterName}";
}

internal static unsafe class Monitors
{
    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(in Guid riid, out IntPtr factory);

    private static readonly Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC
    {
        public fixed char Description[128];
        public uint VendorId, DeviceId, SubSysId, Revision;
        public nuint DedicatedVideoMemory, DedicatedSystemMemory, SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_OUTPUT_DESC
    {
        public fixed char DeviceName[32];
        public Native.RECT DesktopCoordinates;
        public int AttachedToDesktop;
        public int Rotation;
        public IntPtr Monitor;
    }

    // COM vtable slots (IUnknown 0-2, IDXGIObject 3-6, then the interface's own methods).
    private const int Slot_Release = 2;
    private const int Slot_Factory_EnumAdapters = 7;  // same enumeration FFmpeg's d3d11va device uses
    private const int Slot_Adapter_EnumOutputs = 7;
    private const int Slot_Adapter_GetDesc = 8;
    private const int Slot_Output_GetDesc = 7;

    private static IntPtr Vt(IntPtr obj, int slot) => (*(IntPtr**)obj)[slot];
    private static void Release(IntPtr obj) => ((delegate* unmanaged[Stdcall]<IntPtr, uint>)Vt(obj, Slot_Release))(obj);

    public static List<MonitorInfo> Enumerate()
    {
        var result = new List<MonitorInfo>();
        if (CreateDXGIFactory1(IID_IDXGIFactory1, out var factory) < 0) return result;
        try
        {
            for (uint a = 0; ; a++)
            {
                IntPtr adapter;
                if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(factory, Slot_Factory_EnumAdapters))(factory, a, &adapter) < 0)
                    break; // DXGI_ERROR_NOT_FOUND
                try
                {
                    DXGI_ADAPTER_DESC ad;
                    ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC*, int>)Vt(adapter, Slot_Adapter_GetDesc))(adapter, &ad);
                    string adapterName = new string(ad.Description).TrimEnd('\0');

                    for (uint o = 0; ; o++)
                    {
                        IntPtr output;
                        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Vt(adapter, Slot_Adapter_EnumOutputs))(adapter, o, &output) < 0)
                            break;
                        try
                        {
                            DXGI_OUTPUT_DESC od;
                            ((delegate* unmanaged[Stdcall]<IntPtr, DXGI_OUTPUT_DESC*, int>)Vt(output, Slot_Output_GetDesc))(output, &od);
                            if (od.AttachedToDesktop == 0) continue;
                            string dev = new string(od.DeviceName).TrimEnd('\0');
                            var r = od.DesktopCoordinates;
                            result.Add(new MonitorInfo(result.Count, dev, FriendlyName(dev),
                                r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top,
                                (int)a, (int)o, ad.VendorId, adapterName));
                        }
                        finally { Release(output); }
                    }
                }
                finally { Release(adapter); }
            }
        }
        finally { Release(factory); }
        return result;
    }

    private static string FriendlyName(string deviceName)
    {
        var dd = new Native.DISPLAY_DEVICEW { cb = Marshal.SizeOf<Native.DISPLAY_DEVICEW>() };
        return Native.EnumDisplayDevicesW(deviceName, 0, ref dd, 0) ? dd.DeviceString : "?";
    }

    /// <summary>
    /// Picks the monitor to stream. selector: null = first non-primary monitor (the virtual display),
    /// a number = index from --list, or a device name like \\.\DISPLAY3.
    /// </summary>
    public static MonitorInfo? Select(string? selector)
    {
        var all = Enumerate();
        if (selector is null)
            return all.FirstOrDefault(m => !m.IsPrimary);
        if (int.TryParse(selector, out int idx))
            return all.FirstOrDefault(m => m.Index == idx);
        return all.FirstOrDefault(m => m.DeviceName.Equals(selector, StringComparison.OrdinalIgnoreCase)
                                    || m.DeviceName.EndsWith(selector, StringComparison.OrdinalIgnoreCase));
    }
}
