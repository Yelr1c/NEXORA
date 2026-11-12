using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Network;

public sealed record NetworkTestProgress(double Fraction, LatencySample? InternetSample, LatencySample? GatewaySample, string Stage);

/// <summary>Runs a complete network test: internet ping, gateway ping, DNS resolver latency.</summary>
public sealed class NetworkTestRunner(IPinger pinger, IDnsProbe dnsProbe, INetworkInfoProvider networkInfo, ILogger<NetworkTestRunner> logger, TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<NetworkTestResult> RunAsync(NetworkTestOptions options, IProgress<NetworkTestProgress>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        var started = _time.GetUtcNow();
        NetworkAdapterInfo? adapter = null;
        try
        {
            adapter = networkInfo.GetActiveAdapter();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Active network adapter could not be determined");
        }

        var internetSamples = new List<LatencySample>(options.SampleCount);
        var gatewaySamples = new List<LatencySample>(options.SampleCount);
        var gateway = adapter?.Gateway is { AddressFamily: AddressFamily.InterNetwork } g ? g : adapter?.Gateway;

        for (var i = 0; i < options.SampleCount; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var internetTask = pinger.PingAsync(options.InternetTarget, options.Timeout, cancellationToken);
            var gatewayTask = gateway is null ? Task.FromResult<double?>(null) : pinger.PingAsync(gateway, options.Timeout, cancellationToken);
            await Task.WhenAll(internetTask, gatewayTask).ConfigureAwait(false);

            var now = _time.GetUtcNow();
            var internet = new LatencySample(now, internetTask.Result);
            internetSamples.Add(internet);
            LatencySample? gatewaySample = null;
            if (gateway is not null)
            {
                gatewaySample = new LatencySample(now, gatewayTask.Result);
                gatewaySamples.Add(gatewaySample.Value);
            }

            progress?.Report(new NetworkTestProgress((i + 1.0) / (options.SampleCount + options.DnsProbeCount), internet, gatewaySample, "Measuring latency"));
            await Task.Delay(options.Interval, _time, cancellationToken).ConfigureAwait(false);
        }

        LatencyStatisticsResult? dnsStats = null;
        string? dnsServer = null;
        var resolver = adapter?.DnsServers.FirstOrDefault(d => d.AddressFamily == AddressFamily.InterNetwork) ?? adapter?.DnsServers.FirstOrDefault();
        if (resolver is not null && options.DnsProbeCount > 0)
        {
            dnsServer = resolver.ToString();
            var dnsSamples = new List<LatencySample>();
            for (var i = 0; i < options.DnsProbeCount; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var rtt = await dnsProbe.MeasureAsync(resolver, options.DnsProbeName, TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
                dnsSamples.Add(new LatencySample(_time.GetUtcNow(), rtt));
                progress?.Report(new NetworkTestProgress((options.SampleCount + i + 1.0) / (options.SampleCount + options.DnsProbeCount), null, null, "Measuring DNS"));
            }

            dnsStats = LatencyStatistics.Compute(dnsSamples);
        }

        var internetStats = LatencyStatistics.Compute(internetSamples);
        var gatewayStats = gateway is null ? null : LatencyStatistics.Compute(gatewaySamples);
        var quality = LatencyStatistics.Classify(internetStats);
        return new NetworkTestResult
        {
            StartedAt = started,
            InternetTarget = options.InternetTarget.ToString(),
            Internet = internetStats,
            Gateway = gatewayStats,
            Dns = dnsStats,
            DnsServer = dnsServer,
            InternetSamples = internetSamples,
            GatewaySamples = gatewaySamples,
            Adapter = adapter,
            Quality = quality,
            Insights = NetworkAnalyzer.Diagnose(internetStats, gatewayStats, dnsStats, adapter),
        };
    }
}

/// <summary>Lightweight periodic ping used for the live latency indicator.</summary>
public sealed class NetworkMonitor : IDisposable
{
    public const int HistoryCapacity = 120;
    private readonly IPinger _pinger;
    private readonly ILogger<NetworkMonitor> _logger;
    private readonly TimeProvider _time;
    private readonly Monitoring.RingBuffer<LatencySample> _samples = new(HistoryCapacity);
    private readonly object _sync = new();
    private CancellationTokenSource? _cts;

    public NetworkMonitor(IPinger pinger, ILogger<NetworkMonitor> logger, TimeProvider? time = null)
    {
        _pinger = pinger;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public event EventHandler<LatencySample>? SampleAvailable;

    public IPAddress Target { get; set; } = IPAddress.Parse("1.1.1.1");

    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(5);

    public bool IsRunning => _cts is not null;

    public IReadOnlyList<LatencySample> GetSamples()
    {
        lock (_sync)
        {
            return _samples.ToList();
        }
    }

    /// <summary>Statistics over the most recent samples (default: last 30).</summary>
    public LatencyStatisticsResult GetRecentStatistics(int count = 30)
    {
        var samples = GetSamples();
        return LatencyStatistics.Compute(samples.Skip(Math.Max(0, samples.Count - count)).ToList());
    }

    public void Start()
    {
        lock (_sync)
        {
            if (_cts is not null)
            {
                return;
            }

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _ = Task.Run(() => RunAsync(token), CancellationToken.None);
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        }
    }

    private async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var rtt = await _pinger.PingAsync(Target, TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
                var sample = new LatencySample(_time.GetUtcNow(), rtt);
                lock (_sync)
                {
                    _samples.Add(sample);
                }

                SampleAvailable?.Invoke(this, sample);
                await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Live latency probe failed");
                try
                {
                    await Task.Delay(Interval, _time, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }
    }

    public void Dispose() => Stop();
}
