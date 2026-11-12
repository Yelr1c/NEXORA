using Nexora.Platform.Windows.Interop;

namespace Nexora.Platform.Windows.Monitoring;

/// <summary>Parses "GPU Engine" counter instances the same way Task Manager aggregates them.</summary>
internal static class GpuCounterReader
{
    public readonly record struct EngineInstance(int ProcessId, string Luid, int Physical, int Engine, string EngineType);

    /// <summary>Parses e.g. "pid_1234_luid_0x00000000_0x0000C3F2_phys_0_eng_0_engtype_3D".</summary>
    public static bool TryParse(string instance, out EngineInstance result)
    {
        result = default;
        var parts = instance.Split('_');
        int pid = -1, phys = 0, eng = -1;
        string? luid = null, type = null;
        for (var i = 0; i < parts.Length; i++)
        {
            switch (parts[i])
            {
                case "pid" when i + 1 < parts.Length:
                    int.TryParse(parts[i + 1], out pid);
                    break;
                case "luid" when i + 2 < parts.Length:
                    luid = $"luid_{parts[i + 1]}_{parts[i + 2]}";
                    break;
                case "phys" when i + 1 < parts.Length:
                    int.TryParse(parts[i + 1], out phys);
                    break;
                case "eng" when i + 1 < parts.Length:
                    int.TryParse(parts[i + 1], out eng);
                    break;
                case "engtype" when i + 1 < parts.Length:
                    type = string.Join('_', parts[(i + 1)..]);
                    break;
            }
        }

        if (pid < 0 || luid is null || eng < 0)
        {
            return false;
        }

        result = new EngineInstance(pid, luid, phys, eng, type ?? string.Empty);
        return true;
    }

    /// <summary>Adapter utilisation = busiest engine (sum over processes), clamped to 100.</summary>
    public static double? AdapterUtilization(IReadOnlyList<(string Instance, double Value)> engines, AdapterLuid? luid)
    {
        if (engines.Count == 0)
        {
            return null;
        }

        var token = luid?.CounterToken;
        var perEngine = new Dictionary<(string, int, int), double>();
        foreach (var (instance, value) in engines)
        {
            if (!TryParse(instance, out var e) || (token is not null && !string.Equals(e.Luid, token, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var key = (e.Luid, e.Physical, e.Engine);
            perEngine[key] = perEngine.GetValueOrDefault(key) + value;
        }

        return perEngine.Count == 0 ? 0 : Math.Clamp(perEngine.Values.Max(), 0, 100);
    }

    /// <summary>Per-process GPU usage (max engine per process, like Task Manager's GPU column).</summary>
    public static Dictionary<int, double> PerProcess(IReadOnlyList<(string Instance, double Value)> engines)
    {
        var perProcessEngine = new Dictionary<(int, string, int, int), double>();
        foreach (var (instance, value) in engines)
        {
            if (TryParse(instance, out var e))
            {
                var key = (e.ProcessId, e.Luid, e.Physical, e.Engine);
                perProcessEngine[key] = perProcessEngine.GetValueOrDefault(key) + value;
            }
        }

        var result = new Dictionary<int, double>();
        foreach (var ((pid, _, _, _), value) in perProcessEngine)
        {
            result[pid] = Math.Clamp(Math.Max(result.GetValueOrDefault(pid), value), 0, 100);
        }

        return result;
    }
}
