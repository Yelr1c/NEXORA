namespace Nexora.Core.Monitoring;

/// <summary>One sample of live system metrics. Null means "not available on this system".</summary>
public sealed record MetricSnapshot
{
    public DateTimeOffset Timestamp { get; init; }

    public double? CpuUsagePercent { get; init; }

    public double? CpuFrequencyMhz { get; init; }

    public double? CpuTemperatureCelsius { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? GpuFrequencyMhz { get; init; }

    public double? GpuTemperatureCelsius { get; init; }

    public int? GpuFanRpm { get; init; }

    public long? VramUsedBytes { get; init; }

    public long? VramTotalBytes { get; init; }

    public long RamUsedBytes { get; init; }

    public long RamTotalBytes { get; init; }

    public double RamUsagePercent => RamTotalBytes > 0 ? 100.0 * RamUsedBytes / RamTotalBytes : 0;

    public double? VramUsagePercent => VramUsedBytes is { } used && VramTotalBytes is { } total && total > 0 ? 100.0 * used / total : null;

    public int ProcessCount { get; init; }

    /// <summary>Frames per second of the monitored process (null when no frames are being captured).</summary>
    public double? Fps { get; init; }

    public double? FrameTimeMs { get; init; }

    public string? FpsProcessName { get; init; }
}

/// <summary>Samples live metrics. Implementations must be cheap and must never throw for missing sensors.</summary>
public interface IMetricsProvider : IDisposable
{
    MetricSnapshot Sample();
}

public enum FrameCaptureStatus
{
    Stopped,
    Running,
    RequiresElevation,
    Unavailable,
}

public sealed record FrameRateReading(int ProcessId, string ProcessName, double Fps, double FrameTimeMs);

public sealed record RenderingProcess(int ProcessId, string ProcessName, double Fps);

/// <summary>Captures per-process present events (FPS / frame times) without injecting into games.</summary>
public interface IFrameRateSource : IDisposable
{
    FrameCaptureStatus Status { get; }

    string StatusMessage { get; }

    bool TryStart();

    void Stop();

    /// <summary>Process to report. Null = the foreground window's process.</summary>
    int? TargetProcessId { get; set; }

    FrameRateReading? GetCurrent();

    IReadOnlyList<RenderingProcess> GetRenderingProcesses();

    /// <summary>Begins buffering every frame time of the current target for a performance session.</summary>
    void BeginRecording();

    /// <summary>Returns and clears frame times (ms) buffered since the last call.</summary>
    IReadOnlyList<double> DrainRecordedFrameTimes();

    void EndRecording();

    event EventHandler? StatusChanged;
}

/// <summary>Fixed-size circular buffer. Not thread-safe; callers synchronise.</summary>
public sealed class RingBuffer<T>
{
    private readonly T[] _items;
    private int _start;

    public RingBuffer(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _items = new T[capacity];
    }

    public int Capacity => _items.Length;

    public int Count { get; private set; }

    public void Add(T item)
    {
        if (Count < _items.Length)
        {
            _items[(_start + Count) % _items.Length] = item;
            Count++;
        }
        else
        {
            _items[_start] = item;
            _start = (_start + 1) % _items.Length;
        }
    }

    public T this[int index]
    {
        get
        {
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return _items[(_start + index) % _items.Length];
        }
    }

    public void Clear()
    {
        Array.Clear(_items);
        _start = 0;
        Count = 0;
    }

    public List<T> ToList()
    {
        var list = new List<T>(Count);
        for (var i = 0; i < Count; i++)
        {
            list.Add(this[i]);
        }

        return list;
    }
}
