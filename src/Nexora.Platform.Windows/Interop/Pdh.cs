using System.Runtime.InteropServices;

namespace Nexora.Platform.Windows.Interop;

/// <summary>Thin, safe wrapper over the Performance Data Helper API.</summary>
internal sealed class PdhQuery : IDisposable
{
    private const uint PDH_FMT_DOUBLE = 0x00000200;
    private const uint PDH_FMT_NOCAP100 = 0x00008000;
    private const uint PDH_MORE_DATA = 0x800007D2;
    private const uint ERROR_SUCCESS = 0;

    private IntPtr _query;

    public PdhQuery()
    {
        var status = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (status != ERROR_SUCCESS)
        {
            _query = IntPtr.Zero;
        }
    }

    public bool IsValid => _query != IntPtr.Zero;

    /// <summary>Adds a counter using English (locale-independent) names. Returns IntPtr.Zero if unavailable.</summary>
    public IntPtr AddCounter(string path)
    {
        if (!IsValid)
        {
            return IntPtr.Zero;
        }

        return PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out var counter) == ERROR_SUCCESS ? counter : IntPtr.Zero;
    }

    public bool Collect() => IsValid && PdhCollectQueryData(_query) == ERROR_SUCCESS;

    public double? GetValue(IntPtr counter, bool noCap = false)
    {
        if (counter == IntPtr.Zero)
        {
            return null;
        }

        var status = PdhGetFormattedCounterValue(counter, PDH_FMT_DOUBLE | (noCap ? PDH_FMT_NOCAP100 : 0), out _, out var value);
        if (status != ERROR_SUCCESS || value.CStatus > 1)
        {
            return null;
        }

        return double.IsFinite(value.DoubleValue) ? value.DoubleValue : null;
    }

    /// <summary>Reads all instances of a wildcard counter.</summary>
    public IReadOnlyList<(string Instance, double Value)> GetArray(IntPtr counter)
    {
        if (counter == IntPtr.Zero)
        {
            return [];
        }

        uint size = 0;
        var status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out var count, IntPtr.Zero);
        if (status != PDH_MORE_DATA || size == 0)
        {
            return [];
        }

        var buffer = Marshal.AllocHGlobal((int)size);
        try
        {
            status = PdhGetFormattedCounterArrayW(counter, PDH_FMT_DOUBLE | PDH_FMT_NOCAP100, ref size, out count, buffer);
            if (status != ERROR_SUCCESS)
            {
                return [];
            }

            // PDH_FMT_COUNTERVALUE_ITEM_W on x64/ARM64: LPWSTR szName (8) + { DWORD CStatus; pad; double } (16) = 24 bytes.
            var itemSize = IntPtr.Size + 16;
            var result = new List<(string, double)>((int)count);
            for (var i = 0; i < count; i++)
            {
                var item = buffer + (i * itemSize);
                var namePtr = Marshal.ReadIntPtr(item);
                var cstatus = (uint)Marshal.ReadInt32(item + IntPtr.Size);
                if (cstatus > 1)
                {
                    continue;
                }

                var value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item + IntPtr.Size + 8));
                var name = Marshal.PtrToStringUni(namePtr) ?? string.Empty;
                if (double.IsFinite(value))
                {
                    result.Add((name, value));
                }
            }

            return result;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            _ = PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PDH_FMT_COUNTERVALUE
    {
        [FieldOffset(0)]
        public uint CStatus;

        [FieldOffset(8)]
        public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PDH_FMT_COUNTERVALUE value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint bufferSize, out uint itemCount, IntPtr itemBuffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
