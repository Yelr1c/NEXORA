using Microsoft.Extensions.Logging;
using Nexora.Core.Storage;

namespace Nexora.Core.Monitoring;

public sealed record MetricAggregate(double? Average, double? Max)
{
    public static MetricAggregate From(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToList();
        return list.Count == 0 ? new MetricAggregate(null, null) : new MetricAggregate(list.Average(), list.Max());
    }
}

public sealed class PerformanceSessionResult
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset StartedAt { get; set; }

    public double DurationSeconds { get; set; }

    public string? ProcessName { get; set; }

    public FrameStatisticsResult? Frames { get; set; }

    public string? FrameUnavailableReason { get; set; }

    public MetricAggregate Cpu { get; set; } = new(null, null);

    public MetricAggregate Gpu { get; set; } = new(null, null);

    public MetricAggregate Ram { get; set; } = new(null, null);

    public MetricAggregate Vram { get; set; } = new(null, null);

    public MetricAggregate CpuTemperature { get; set; } = new(null, null);

    public MetricAggregate GpuTemperature { get; set; } = new(null, null);

    public MetricAggregate CpuFrequency { get; set; } = new(null, null);

    public int SampleCount { get; set; }

    public double? PingAverageMs { get; set; }

    public double? PingJitterMs { get; set; }

    public Bottleneck Bottleneck { get; set; }

    public string? BottleneckTitle { get; set; }

    public string? BottleneckExplanation { get; set; }

    /// <summary>Plain-language findings derived from the measured data.</summary>
    public List<string> Insights { get; set; } = [];
}

public static class PerformanceSessionAnalyzer
{
    public static PerformanceSessionResult Analyze(
        DateTimeOffset startedAt,
        double durationSeconds,
        IReadOnlyList<MetricSnapshot> samples,
        IReadOnlyList<double> frameTimesMs,
        string? processName,
        string? frameUnavailableReason)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        var result = new PerformanceSessionResult
        {
            StartedAt = startedAt,
            DurationSeconds = durationSeconds,
            ProcessName = processName,
            Frames = FrameStatistics.Compute(frameTimesMs),
            Cpu = MetricAggregate.From(samples.Select(s => s.CpuUsagePercent)),
            Gpu = MetricAggregate.From(samples.Select(s => s.GpuUsagePercent)),
            Ram = MetricAggregate.From(samples.Where(s => s.RamTotalBytes > 0).Select(s => (double?)s.RamUsagePercent)),
            Vram = MetricAggregate.From(samples.Select(s => s.VramUsagePercent)),
            CpuTemperature = MetricAggregate.From(samples.Select(s => s.CpuTemperatureCelsius)),
            GpuTemperature = MetricAggregate.From(samples.Select(s => s.GpuTemperatureCelsius)),
            CpuFrequency = MetricAggregate.From(samples.Select(s => s.CpuFrequencyMhz)),
            SampleCount = samples.Count,
        };

        var bottleneck = BottleneckAnalyzer.Analyze(samples);
        result.Bottleneck = bottleneck.Kind;
        result.BottleneckTitle = bottleneck.Title;
        result.BottleneckExplanation = bottleneck.Explanation;
        result.FrameUnavailableReason = result.Frames is null
            ? frameUnavailableReason ?? "No frames were presented by the monitored application during the session."
            : null;
        result.Insights.AddRange(BuildInsights(result));
        return result;
    }

    private static IEnumerable<string> BuildInsights(PerformanceSessionResult r)
    {
        if (r.Frames is { } f)
        {
            var ratio = f.AverageFps > 0 ? f.OnePercentLowFps / f.AverageFps : 1;
            if (ratio < 0.5)
            {
                yield return $"Frame pacing is uneven: 1% lows ({f.OnePercentLowFps:0}) are less than half of the average ({f.AverageFps:0}). This is felt as stutter.";
            }
            else if (ratio > 0.75)
            {
                yield return "Frame pacing is consistent (1% lows are close to the average).";
            }

            if (f.MaxFrameTimeMs > 100)
            {
                yield return $"At least one severe hitch of {f.MaxFrameTimeMs:0} ms was recorded.";
            }
        }

        if (r.Gpu.Average is > 90)
        {
            yield return "The GPU was fully utilised (GPU-bound). Lowering resolution or graphics settings will raise FPS the most.";
        }
        else if (r.Cpu.Max is > 90 && r.Gpu.Average is < 75)
        {
            yield return "The GPU was not fully used while CPU load peaked; the game is likely CPU-limited. Closing background apps can help.";
        }

        if (r.Ram.Max is > 90)
        {
            yield return "System memory was nearly full. Close memory-heavy background apps to avoid stutter from paging.";
        }

        if (r.Vram.Max is > 95)
        {
            yield return "Video memory was nearly full. Lowering texture quality can prevent stutter.";
        }

        if (r.CpuTemperature.Max is > 95 || r.GpuTemperature.Max is > 90)
        {
            yield return "High temperatures were measured; thermal throttling may reduce performance. Check cooling and airflow.";
        }
    }
}

/// <summary>Coordinates one timed performance test using the shared monitor and frame source.</summary>
public sealed class PerformanceSessionService
{
    private const int MaxStoredSessions = 50;
    private readonly MetricsMonitor _monitor;
    private readonly IFrameRateSource _frames;
    private readonly ILogger<PerformanceSessionService> _logger;
    private readonly TimeProvider _time;
    private readonly string _storePath;
    private readonly List<double> _frameTimes = [];
    private readonly List<MetricSnapshot> _samples = [];
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;
    private IDisposable? _activeRequest;
    private DateTimeOffset _startedAt;
    private string? _processName;

    private readonly Func<(double? Average, double? Jitter)>? _latency;

    public PerformanceSessionService(MetricsMonitor monitor, IFrameRateSource frames, string storePath, ILogger<PerformanceSessionService> logger, TimeProvider? time = null, Func<(double? Average, double? Jitter)>? latency = null)
    {
        _latency = latency;
        _monitor = monitor;
        _frames = frames;
        _storePath = storePath;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public bool IsRunning => _cts is not null;

    public event EventHandler<double>? ProgressChanged;

    public event EventHandler<PerformanceSessionResult>? Completed;

    public TimeSpan Duration { get; private set; }

    public async Task<PerformanceSessionResult> RunAsync(TimeSpan duration, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new InvalidOperationException("A performance session is already running.");
        }

        Duration = duration;
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _cts.Token;
        lock (_sync)
        {
            _frameTimes.Clear();
            _samples.Clear();
        }

        _startedAt = _time.GetUtcNow();
        _processName = null;
        _activeRequest = _monitor.RequestActive();
        var frameCapture = _frames.TryStart();
        if (frameCapture)
        {
            _frames.BeginRecording();
        }

        _monitor.SnapshotAvailable += OnSnapshot;
        try
        {
            var stopAt = _startedAt + duration;
            while (!token.IsCancellationRequested)
            {
                var now = _time.GetUtcNow();
                if (now >= stopAt)
                {
                    break;
                }

                DrainFrames();
                ProgressChanged?.Invoke(this, Math.Clamp((now - _startedAt).TotalSeconds / duration.TotalSeconds, 0, 1));
                try
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(250), _time, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            DrainFrames();
        }
        finally
        {
            _monitor.SnapshotAvailable -= OnSnapshot;
            if (frameCapture)
            {
                _frames.EndRecording();
            }

            _activeRequest?.Dispose();
            _activeRequest = null;
            _cts.Dispose();
            _cts = null;
        }

        var elapsed = (_time.GetUtcNow() - _startedAt).TotalSeconds;
        PerformanceSessionResult result;
        lock (_sync)
        {
            result = PerformanceSessionAnalyzer.Analyze(_startedAt, elapsed, _samples.ToList(), _frameTimes.ToList(), _processName,
                frameCapture ? null : _frames.StatusMessage);
        }

        if (_latency?.Invoke() is { } latency)
        {
            result.PingAverageMs = latency.Average;
            result.PingJitterMs = latency.Jitter;
        }

        ProgressChanged?.Invoke(this, 1);
        await SaveAsync(result).ConfigureAwait(false);
        Completed?.Invoke(this, result);
        return result;
    }

    /// <summary>Stops the running session early; the partial result is still produced.</summary>
    public void Stop() => _cts?.Cancel();

    public async Task<IReadOnlyList<PerformanceSessionResult>> GetHistoryAsync()
    {
        try
        {
            return await JsonFile.ReadAsync<List<PerformanceSessionResult>>(_storePath).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "Session history could not be read");
            return [];
        }
    }

    private async Task SaveAsync(PerformanceSessionResult result)
    {
        try
        {
            var history = (await GetHistoryAsync().ConfigureAwait(false)).ToList();
            history.Insert(0, result);
            if (history.Count > MaxStoredSessions)
            {
                history.RemoveRange(MaxStoredSessions, history.Count - MaxStoredSessions);
            }

            await JsonFile.WriteAsync(_storePath, history).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Session result could not be saved");
        }
    }

    private void DrainFrames()
    {
        var drained = _frames.DrainRecordedFrameTimes();
        if (drained.Count == 0)
        {
            return;
        }

        lock (_sync)
        {
            _frameTimes.AddRange(drained);
        }
    }

    private void OnSnapshot(object? sender, MetricSnapshot snapshot)
    {
        lock (_sync)
        {
            _samples.Add(snapshot);
            if (snapshot.FpsProcessName is { } name)
            {
                _processName = name;
            }
        }
    }
}
