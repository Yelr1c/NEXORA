using Microsoft.Extensions.Logging;
using Nexora.Core.Monitoring;
using Nexora.Platform.Windows.Interop;

namespace Nexora.Platform.Windows.Monitoring;

/// <summary>
/// Collects live metrics using only native, unprivileged Windows APIs:
/// GetSystemTimes / PDH (CPU), GlobalMemoryStatusEx (RAM), PDH GPU Engine counters (GPU usage),
/// PDH GPU Adapter Memory (VRAM), D3DKMT (GPU temperature/clock) and ACPI thermal zones (CPU temperature).
/// </summary>
public sealed class WindowsMetricsProvider : IMetricsProvider
{
    private readonly ILogger<WindowsMetricsProvider> _logger;
    private readonly IFrameRateSource _frames;
    private readonly object _sync = new();
    private readonly PdhQuery _pdh = new();
    private readonly IntPtr _cpuUtility;
    private readonly IntPtr _cpuPerformance;
    private readonly IntPtr _cpuFrequency;
    private readonly IntPtr _gpuEngine;
    private readonly IntPtr _gpuMemory;
    private readonly IntPtr _thermal;
    private readonly IntPtr _processes;
    private readonly DxgiAdapter? _primaryAdapter;
    private ulong _lastIdle;
    private ulong _lastTotal;
    private int _telemetryCounter;
    private GpuInterop.AdapterTelemetry _telemetry = new(null, null);
    private Dictionary<int, double> _perProcessGpu = [];

    public WindowsMetricsProvider(IFrameRateSource frames, ILogger<WindowsMetricsProvider> logger)
    {
        _frames = frames;
        _logger = logger;
        _cpuUtility = _pdh.AddCounter(@"\Processor Information(_Total)\% Processor Utility");
        _cpuPerformance = _pdh.AddCounter(@"\Processor Information(_Total)\% Processor Performance");
        _cpuFrequency = _pdh.AddCounter(@"\Processor Information(_Total)\Processor Frequency");
        _gpuEngine = _pdh.AddCounter(@"\GPU Engine(*)\Utilization Percentage");
        _gpuMemory = _pdh.AddCounter(@"\GPU Adapter Memory(*)\Dedicated Usage");
        _thermal = _pdh.AddCounter(@"\Thermal Zone Information(*)\Temperature");
        _processes = _pdh.AddCounter(@"\System\Processes");
        _pdh.Collect();
        ReadSystemTimes(out _lastIdle, out _lastTotal);

        try
        {
            _primaryAdapter = GpuInterop.EnumerateAdapters().Where(a => !a.Info.IsSoftware).OrderByDescending(a => a.Info.DedicatedMemoryBytes).FirstOrDefault();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "GPU enumeration failed");
        }

        _logger.LogInformation("Metrics provider ready (PDH: {Pdh}, GPU counters: {Gpu}, thermal zones: {Thermal})",
            _pdh.IsValid, _gpuEngine != IntPtr.Zero, _thermal != IntPtr.Zero);
    }

    /// <summary>Latest per-process GPU utilisation (from the most recent sample).</summary>
    public IReadOnlyDictionary<int, double> PerProcessGpu
    {
        get
        {
            lock (_sync)
            {
                return _perProcessGpu;
            }
        }
    }

    public MetricSnapshot Sample()
    {
        lock (_sync)
        {
            var collected = _pdh.Collect();

            double? cpu = collected ? _pdh.GetValue(_cpuUtility) : null;
            var systemTimesCpu = SampleSystemTimes();
            cpu = cpu is null ? systemTimesCpu : Math.Clamp(cpu.Value, 0, 100);

            double? cpuMhz = null;
            if (collected && _pdh.GetValue(_cpuFrequency) is { } baseMhz && baseMhz > 0)
            {
                var perf = _pdh.GetValue(_cpuPerformance, noCap: true);
                cpuMhz = perf is { } p && p > 0 ? baseMhz * p / 100.0 : baseMhz;
            }

            double? gpu = null;
            if (collected)
            {
                var engines = _pdh.GetArray(_gpuEngine);
                gpu = GpuCounterReader.AdapterUtilization(engines, _primaryAdapter?.Luid);
                _perProcessGpu = GpuCounterReader.PerProcess(engines);
            }

            long? vramUsed = null;
            if (collected && _primaryAdapter is not null)
            {
                var token = _primaryAdapter.Luid.CounterToken;
                var match = _pdh.GetArray(_gpuMemory).Where(i => i.Instance.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
                if (match.Count > 0)
                {
                    vramUsed = (long)match.Sum(m => m.Value);
                }
            }

            double? cpuTemp = null;
            if (collected)
            {
                var zones = _pdh.GetArray(_thermal).Select(z => z.Value - 273.15).Where(c => c is > 10 and < 120).ToList();
                if (zones.Count > 0)
                {
                    cpuTemp = zones.Max();
                }
            }

            // D3DKMT telemetry is a kernel call; refresh every other sample.
            if (_primaryAdapter is not null && _telemetryCounter++ % 2 == 0)
            {
                try
                {
                    _telemetry = GpuInterop.QueryTelemetry(_primaryAdapter.Luid);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "GPU telemetry query failed");
                    _telemetry = new GpuInterop.AdapterTelemetry(null, null);
                }
            }

            var memory = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
            long ramTotal = 0, ramUsed = 0;
            if (NativeMethods.GlobalMemoryStatusEx(ref memory))
            {
                ramTotal = (long)memory.ullTotalPhys;
                ramUsed = (long)(memory.ullTotalPhys - memory.ullAvailPhys);
            }

            FrameRateReading? frames = null;
            try
            {
                frames = _frames.GetCurrent();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Frame rate read failed");
            }

            return new MetricSnapshot
            {
                Timestamp = DateTimeOffset.UtcNow,
                CpuUsagePercent = cpu,
                CpuFrequencyMhz = cpuMhz,
                CpuTemperatureCelsius = cpuTemp,
                GpuUsagePercent = gpu,
                GpuFrequencyMhz = _telemetry.CoreFrequencyMhz,
                GpuTemperatureCelsius = _telemetry.TemperatureCelsius,
                GpuFanRpm = _telemetry.FanRpm,
                VramUsedBytes = vramUsed,
                VramTotalBytes = _primaryAdapter?.Info.DedicatedMemoryBytes is > 0 and var total ? total : null,
                RamTotalBytes = ramTotal,
                RamUsedBytes = ramUsed,
                ProcessCount = collected && _pdh.GetValue(_processes) is { } count ? (int)count : 0,
                Fps = frames?.Fps,
                FrameTimeMs = frames?.FrameTimeMs,
                FpsProcessName = frames?.ProcessName,
            };
        }
    }

    private double? SampleSystemTimes()
    {
        if (!ReadSystemTimes(out var idle, out var total))
        {
            return null;
        }

        var idleDelta = idle - _lastIdle;
        var totalDelta = total - _lastTotal;
        _lastIdle = idle;
        _lastTotal = total;
        return totalDelta == 0 ? null : Math.Clamp(100.0 * (totalDelta - idleDelta) / totalDelta, 0, 100);
    }

    private static bool ReadSystemTimes(out ulong idle, out ulong total)
    {
        if (NativeMethods.GetSystemTimes(out var i, out var k, out var u))
        {
            idle = i.Value;
            total = k.Value + u.Value; // kernel time includes idle time
            return true;
        }

        idle = total = 0;
        return false;
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _pdh.Dispose();
        }
    }
}
