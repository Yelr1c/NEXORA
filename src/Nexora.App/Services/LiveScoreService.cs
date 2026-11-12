using Microsoft.Extensions.Logging;
using Nexora.Core.Hardware;
using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Nexora.Core.Optimization;
using Nexora.Core.Scoring;

namespace Nexora.App.Services;

/// <summary>
/// Computes the NEXORA Performance Score continuously from measured data: averaged live metrics,
/// live latency, storage, startup load and the state of recommended settings (refreshed periodically).
/// </summary>
public sealed class LiveScoreService(MetricsMonitor monitor, NetworkMonitor network, StandardOptimizationCatalog catalog, ILogger<LiveScoreService> logger)
{
    private static readonly TimeSpan ConfigRefresh = TimeSpan.FromMinutes(2);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DateTimeOffset _configCheckedAt = DateTimeOffset.MinValue;
    private (int Total, int Applied, int Startup)? _config;

    public ScoreResult? Latest { get; private set; }

    public void InvalidateConfiguration() => _configCheckedAt = DateTimeOffset.MinValue;

    public async Task<ScoreResult> ComputeAsync()
    {
        await RefreshConfigurationAsync().ConfigureAwait(false);
        var history = monitor.GetHistory();
        var recent = history.Skip(Math.Max(0, history.Count - 30)).ToList();
        var latency = network.GetRecentStatistics(20);
        var system = StorageInfo.GetFixedDrives().FirstOrDefault(d => d.IsSystemDrive);
        var last = recent.LastOrDefault(s => s.RamTotalBytes > 0);
        var temps = new[] { recent.LastOrDefault(s => s.CpuTemperatureCelsius.HasValue)?.CpuTemperatureCelsius, recent.LastOrDefault(s => s.GpuTemperatureCelsius.HasValue)?.GpuTemperatureCelsius }
            .Where(t => t.HasValue).Select(t => t!.Value).ToList();

        var inputs = new ScoreInputs
        {
            CpuUsagePercent = Average(recent.Select(s => s.CpuUsagePercent)),
            GpuUsagePercent = Average(recent.Select(s => s.GpuUsagePercent)),
            RamUsagePercent = last?.RamUsagePercent,
            SystemDriveFreePercent = system?.FreePercent,
            PingMs = latency.AverageMs,
            JitterMs = latency.JitterMs,
            PacketLossPercent = latency.Sent > 0 ? latency.PacketLossPercent : null,
            RecommendedSettingsTotal = _config?.Total,
            RecommendedSettingsApplied = _config?.Applied,
            EnabledStartupApps = _config?.Startup,
            MaxTemperatureCelsius = temps.Count > 0 ? temps.Max() : null,
        };
        Latest = PerformanceScoreCalculator.Calculate(inputs);
        return Latest;
    }

    private async Task RefreshConfigurationAsync()
    {
        if (DateTimeOffset.UtcNow - _configCheckedAt < ConfigRefresh)
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (DateTimeOffset.UtcNow - _configCheckedAt < ConfigRefresh)
            {
                return;
            }

            var total = 0;
            var applied = 0;
            foreach (var optimization in catalog.StaticOptimizations.Where(o => o.Kind == OptimizationKind.Setting))
            {
                try
                {
                    var state = await optimization.GetStateAsync().ConfigureAwait(false);
                    if (state.Status == OptimizationStatus.Unavailable)
                    {
                        continue;
                    }

                    total++;
                    if (state.Status == OptimizationStatus.Applied)
                    {
                        applied++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogDebug(ex, "State check failed for {Id}", optimization.Id);
                }
            }

            var startup = await Task.Run(() => catalog.GetStartupOptimizations().Count(s => s.Entry.IsEnabled)).ConfigureAwait(false);
            _config = (total, applied, startup);
            _configCheckedAt = DateTimeOffset.UtcNow;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Configuration score refresh failed");
        }
        finally
        {
            _gate.Release();
        }
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }
}
