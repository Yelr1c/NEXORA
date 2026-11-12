using System.Runtime.InteropServices;
using Nexora.Core.Hardware;

namespace Nexora.Platform.Windows.Interop;

internal readonly record struct AdapterLuid(uint Low, int High)
{
    /// <summary>Matches the "luid_0xHHHHHHHH_0xLLLLLLLL" token used by GPU performance counter instances.</summary>
    public string CounterToken => $"luid_0x{High:X8}_0x{Low:X8}";
}

internal sealed record DxgiAdapter(GpuInfo Info, AdapterLuid Luid);

/// <summary>DXGI adapter enumeration and D3DKMT telemetry (temperature, clocks, HAGS caps) via raw vtable calls.</summary>
internal static unsafe class GpuInterop
{
    private static readonly Guid IidFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");
    private const int KMTQAITYPE_NODEPERFDATA = 61;
    private const int KMTQAITYPE_ADAPTERPERFDATA = 62;
    private const int KMTQAITYPE_WDDM_2_7_CAPS = 70;

    public static IReadOnlyList<DxgiAdapter> EnumerateAdapters()
    {
        var result = new List<DxgiAdapter>();
        IntPtr factory = IntPtr.Zero;
        try
        {
            var iid = IidFactory1;
            if (CreateDXGIFactory1(ref iid, out factory) < 0 || factory == IntPtr.Zero)
            {
                return result;
            }

            var vtbl = *(IntPtr**)factory;
            // IUnknown(3) + IDXGIObject(4) + IDXGIFactory(5) => EnumAdapters1 is slot 12.
            var enumAdapters1 = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)vtbl[12];
            for (uint i = 0; i < 16; i++)
            {
                IntPtr adapter;
                if (enumAdapters1(factory, i, &adapter) < 0 || adapter == IntPtr.Zero)
                {
                    break;
                }

                try
                {
                    var avtbl = *(IntPtr**)adapter;
                    // IUnknown(3) + IDXGIObject(4) + IDXGIAdapter(3) => GetDesc1 is slot 10.
                    var getDesc1 = (delegate* unmanaged[Stdcall]<IntPtr, DXGI_ADAPTER_DESC1*, int>)avtbl[10];
                    DXGI_ADAPTER_DESC1 desc;
                    if (getDesc1(adapter, &desc) >= 0)
                    {
                        var name = new string(desc.Description).TrimEnd('\0').Trim();
                        var software = (desc.Flags & 0x2) != 0 || desc.VendorId == 0x1414;
                        result.Add(new DxgiAdapter(new GpuInfo(name, (long)desc.DedicatedVideoMemory, desc.VendorId, software), new AdapterLuid(desc.LuidLow, desc.LuidHigh)));
                    }
                }
                finally
                {
                    Release(adapter);
                }
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or SEHException)
        {
            // DXGI unavailable (e.g. some server/remote configurations).
        }
        finally
        {
            Release(factory);
        }

        return result;
    }

    public sealed record AdapterTelemetry(double? TemperatureCelsius, double? CoreFrequencyMhz);

    public static AdapterTelemetry QueryTelemetry(AdapterLuid luid)
    {
        if (!TryOpen(luid, out var handle))
        {
            return new AdapterTelemetry(null, null);
        }

        try
        {
            double? temperature = null;
            double? frequency = null;
            var perf = new D3DKMT_ADAPTER_PERFDATA();
            if (Query(handle, KMTQAITYPE_ADAPTERPERFDATA, &perf, sizeof(D3DKMT_ADAPTER_PERFDATA)) && perf.Temperature > 0)
            {
                // Reported in deci-degrees Celsius.
                var celsius = perf.Temperature / 10.0;
                if (celsius is > 5 and < 125)
                {
                    temperature = celsius;
                }
            }

            var node = new D3DKMT_NODE_PERFDATA { NodeOrdinal = 0, PhysicalAdapterIndex = 0 };
            if (Query(handle, KMTQAITYPE_NODEPERFDATA, &node, sizeof(D3DKMT_NODE_PERFDATA)) && node.Frequency > 0)
            {
                var mhz = node.Frequency / 1_000_000.0;
                if (mhz is > 10 and < 10_000)
                {
                    frequency = mhz;
                }
            }

            return new AdapterTelemetry(temperature, frequency);
        }
        finally
        {
            Close(handle);
        }
    }

    /// <summary>Hardware-accelerated GPU scheduling support for the adapter; null when the query fails.</summary>
    public static bool? IsHardwareSchedulingSupported(AdapterLuid luid)
    {
        if (!TryOpen(luid, out var handle))
        {
            return null;
        }

        try
        {
            uint caps = 0;
            return Query(handle, KMTQAITYPE_WDDM_2_7_CAPS, &caps, sizeof(uint)) ? (caps & 0x1) != 0 : null;
        }
        finally
        {
            Close(handle);
        }
    }

    private static bool TryOpen(AdapterLuid luid, out uint handle)
    {
        handle = 0;
        try
        {
            var open = new D3DKMT_OPENADAPTERFROMLUID { LuidLow = luid.Low, LuidHigh = luid.High };
            if (D3DKMTOpenAdapterFromLuid(ref open) != 0)
            {
                return false;
            }

            handle = open.hAdapter;
            return true;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool Query(uint handle, int type, void* data, int size)
    {
        var query = new D3DKMT_QUERYADAPTERINFO { hAdapter = handle, Type = type, pPrivateDriverData = (IntPtr)data, PrivateDriverDataSize = (uint)size };
        return D3DKMTQueryAdapterInfo(ref query) == 0;
    }

    private static void Close(uint handle)
    {
        var close = new D3DKMT_CLOSEADAPTER { hAdapter = handle };
        _ = D3DKMTCloseAdapter(ref close);
    }

    private static void Release(IntPtr unknown)
    {
        if (unknown != IntPtr.Zero)
        {
            var vtbl = *(IntPtr**)unknown;
            var release = (delegate* unmanaged[Stdcall]<IntPtr, uint>)vtbl[2];
            release(unknown);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DXGI_ADAPTER_DESC1
    {
        public fixed char Description[128];
        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public uint LuidLow;
        public int LuidHigh;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_OPENADAPTERFROMLUID
    {
        public uint LuidLow;
        public int LuidHigh;
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_CLOSEADAPTER
    {
        public uint hAdapter;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_QUERYADAPTERINFO
    {
        public uint hAdapter;
        public int Type;
        public IntPtr pPrivateDriverData;
        public uint PrivateDriverDataSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_ADAPTER_PERFDATA
    {
        public uint PhysicalAdapterIndex;
        public ulong MemoryFrequency;
        public ulong MaxMemoryFrequency;
        public ulong MaxMemoryFrequencyOC;
        public ulong MemoryBandwidth;
        public ulong PCIEBandwidth;
        public uint FanRPM;
        public uint Power;
        public uint Temperature;
        public byte PowerStateOverride;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3DKMT_NODE_PERFDATA
    {
        public uint NodeOrdinal;
        public uint PhysicalAdapterIndex;
        public ulong Frequency;
        public ulong MaxFrequency;
        public ulong MaxFrequencyOC;
        public uint Voltage;
        public uint VoltageMax;
        public uint VoltageMaxOC;
        public ulong MaxTransitionLatency;
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr factory);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTOpenAdapterFromLuid(ref D3DKMT_OPENADAPTERFROMLUID args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTQueryAdapterInfo(ref D3DKMT_QUERYADAPTERINFO args);

    [DllImport("gdi32.dll")]
    private static extern int D3DKMTCloseAdapter(ref D3DKMT_CLOSEADAPTER args);
}
