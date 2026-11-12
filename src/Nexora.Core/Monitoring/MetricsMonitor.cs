using Microsoft.Extensions.Logging;

namespace Nexora.Core.Monitoring;

/// <summary>
/// Single shared sampling loop. Runs at the configured interval while the UI is visible, slows down when
/// NEXORA is in the background and stops completely when nothing needs live data.
/// </summary>
public sealed class MetricsMonitor : IDisposable
{
    public const int HistoryCapacity = 300;

    private readonly IMetricsProvider _provider;
    private readonly ILogger<MetricsMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly RingBuffer<MetricSnapshot> _history = new(HistoryCapacity);
    private readonly object _sync = new();
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    private int _activeRequests;
    private bool _foreground = true;
    private bool _backgroundMonitoring;
    private TimeSpan _interval = TimeSpan.FromSeconds(1);
    private int _consecutiveFailures;

    public MetricsMonitor(IMetricsProvider provider, ILogger<MetricsMonitor> logger, TimeProvider? time = null)
    {
        _provider = provider;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<MetricSnapshot>? SnapshotAvailable;

    public MetricSnapshot? Latest { get; private set; }

    public bool IsRunning => _loop is { IsCompleted: false };

    public TimeSpan Interval
    {
        get => _interval;
        set => _interval = value < TimeSpan.FromMilliseconds(250) ? TimeSpan.FromMilliseconds(250) : value;
    }

    public static TimeSpan BackgroundInterval => TimeSpan.FromSeconds(5);

    public IReadOnlyList<MetricSnapshot> GetHistory()
    {
        lock (_sync)
        {
            return _history.ToList();
        }
    }

    /// <summary>Keeps sampling alive (e.g. for a performance session) even while NEXORA is hidden.</summary>
    public IDisposable RequestActive()
    {
        Interlocked.Increment(ref _activeRequests);
        UpdateRunState();
        return new Releaser(this);
    }

    public void SetForeground(bool foreground)
    {
        _foreground = foreground;
        UpdateRunState();
    }

    public void SetBackgroundMonitoring(bool enabled)
    {
        _backgroundMonitoring = enabled;
        UpdateRunState();
    }

    /// <summary>Takes one sample immediately (used by scanners).</summary>
    public MetricSnapshot SampleNow()
    {
        var snapshot = SafeSample();
        Publish(snapshot);
        return snapshot;
    }

    private void UpdateRunState()
    {
        var shouldRun = _foreground || _backgroundMonitoring || Volatile.Read(ref _activeRequests) > 0;
        lock (_sync)
        {
            if (shouldRun && !IsRunning)
            {
                _loopCts = new CancellationTokenSource();
                var token = _loopCts.Token;
                _loop = Task.Run(() => RunAsync(token), CancellationToken.None);
            }
            else if (!shouldRun && IsRunning)
            {
                _loopCts?.Cancel();
                _loopCts?.Dispose();
                _loopCts = null;
                _loop = null;
            }
        }
    }

    private TimeSpan CurrentInterval => _foreground || Volatile.Read(ref _activeRequests) > 0 ? _interval : BackgroundInterval;

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Publish(SafeSample());
                await Task.Delay(CurrentInterval, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }
    }

    private MetricSnapshot SafeSample()
    {
        try
        {
            var snapshot = _provider.Sample();
            _consecutiveFailures = 0;
            return snapshot;
        }
        catch (Exception ex)
        {
            if (_consecutiveFailures++ < 3)
            {
                _logger.LogWarning(ex, "Metric sampling failed");
            }

            return new MetricSnapshot { Timestamp = _time.GetUtcNow() };
        }
    }

    private void Publish(MetricSnapshot snapshot)
    {
        lock (_sync)
        {
            _history.Add(snapshot);
            Latest = snapshot;
        }

        try
        {
            SnapshotAvailable?.Invoke(this, snapshot);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A metrics subscriber threw an exception");
        }
    }

    public void Dispose()
    {
        lock (_sync)
        {
            _loopCts?.Cancel();
            _loopCts?.Dispose();
            _loopCts = null;
        }
    }

    private sealed class Releaser(MetricsMonitor owner) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
            {
                Interlocked.Decrement(ref owner._activeRequests);
                owner.UpdateRunState();
            }
        }
    }
}
