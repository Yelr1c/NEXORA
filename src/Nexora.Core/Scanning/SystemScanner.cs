using Microsoft.Extensions.Logging;
using Nexora.Core.Hardware;
using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Nexora.Core.Optimization;
using Nexora.Core.Processes;
using Nexora.Core.Scoring;

namespace Nexora.Core.Scanning;

public enum FindingSeverity
{
    Good,
    Info,
    Warning,
    Problem,
}

public sealed record ScanFinding(string Area, FindingSeverity Severity, string Title, string Detail);

public sealed record ScanRecommendation(IOptimization Optimization, OptimizationState State);

public sealed class ScanReport
{
    public DateTimeOffset CompletedAt { get; init; }

    public required ScoreResult Score { get; init; }

    public required IReadOnlyList<ScanFinding> Findings { get; init; }

    public required IReadOnlyList<ScanRecommendation> Recommendations { get; init; }

    public SystemInfo? System { get; init; }

    public int HighImpactCount => Recommendations.Count(r => r.Optimization.Impact == ImpactLevel.High);

    public int MediumImpactCount => Recommendations.Count(r => r.Optimization.Impact == ImpactLevel.Medium);

    public int LowImpactCount => Recommendations.Count(r => r.Optimization.Impact == ImpactLevel.Low);
}

public sealed record ScanProgress(int StepIndex, int StepCount, string StepName, double Fraction);

/// <summary>Analyses the PC using real measurements and produces findings plus applicable optimizations.</summary>
public sealed class SystemScanner(
    ISystemInfoProvider systemInfo,
    MetricsMonitor monitor,
    StandardOptimizationCatalog catalog,
    IProcessService processes,
    IPinger pinger,
    INetworkInfoProvider networkInfo,
    ILogger<SystemScanner> logger,
    TimeProvider? time = null)
{
    public static readonly IReadOnlyList<string> Steps =
    [
        "CPU", "GPU", "Memory", "Storage", "Startup apps", "Background processes", "Power plan", "Gaming settings", "Network", "Windows configuration",
    ];

    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public ScanReport? LastReport { get; private set; }

    public event EventHandler<ScanReport>? ScanCompleted;

    public async Task<ScanReport> ScanAsync(IProgress<ScanProgress>? progress, CancellationToken cancellationToken = default)
    {
        var findings = new List<ScanFinding>();
        var inputs = new ScoreInputs();
        using var keepAlive = monitor.RequestActive();
        void Report(int index) => progress?.Report(new ScanProgress(index, Steps.Count, Steps[index], (double)index / Steps.Count));

        // CPU, GPU and memory: average several live samples so a momentary spike does not skew results.
        Report(0);
        SystemInfo? info = null;
        try
        {
            info = await Task.Run(systemInfo.GetSystemInfo, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "System information unavailable");
        }

        var samples = new List<MetricSnapshot>();
        for (var i = 0; i < 4; i++)
        {
            samples.Add(await Task.Run(monitor.SampleNow, cancellationToken).ConfigureAwait(false));
            await Task.Delay(TimeSpan.FromMilliseconds(500), _time, cancellationToken).ConfigureAwait(false);
        }

        var cpu = Average(samples.Select(s => s.CpuUsagePercent));
        var cpuTemp = samples.Select(s => s.CpuTemperatureCelsius).LastOrDefault(t => t.HasValue);
        inputs = inputs with { CpuUsagePercent = cpu };
        findings.Add(cpu switch
        {
            null => new ScanFinding("CPU", FindingSeverity.Info, "CPU load unavailable", "CPU utilisation could not be measured on this system."),
            > 60 => new ScanFinding("CPU", FindingSeverity.Warning, $"High idle CPU load ({cpu:0}%)", "Something is using a lot of CPU while you are not gaming. Check the Processes page."),
            _ => new ScanFinding("CPU", FindingSeverity.Good, $"CPU load is healthy ({cpu:0}%)", info is null ? "" : $"{info.CpuName}, {info.LogicalProcessors} threads"),
        });

        Report(1);
        var gpu = Average(samples.Select(s => s.GpuUsagePercent));
        var gpuTemp = samples.Select(s => s.GpuTemperatureCelsius).LastOrDefault(t => t.HasValue);
        inputs = inputs with { GpuUsagePercent = gpu };
        var gpuName = info?.PrimaryGpu?.Name ?? "Graphics adapter";
        findings.Add(gpu switch
        {
            null => new ScanFinding("GPU", FindingSeverity.Info, "GPU load unavailable", "GPU utilisation counters are not available on this system."),
            > 50 => new ScanFinding("GPU", FindingSeverity.Warning, $"GPU busy while idle ({gpu:0}%)", "An application is rendering in the background. Close overlays, wallpaper engines or browser tabs with video."),
            _ => new ScanFinding("GPU", FindingSeverity.Good, $"GPU is idle and ready ({gpu:0}%)", gpuName),
        });

        Report(2);
        var last = samples.LastOrDefault(s => s.RamTotalBytes > 0);
        if (last is not null)
        {
            inputs = inputs with { RamUsagePercent = last.RamUsagePercent };
            var totalGb = last.RamTotalBytes / 1024d / 1024 / 1024;
            findings.Add(last.RamUsagePercent switch
            {
                > 85 => new ScanFinding("Memory", FindingSeverity.Problem, $"Memory nearly full ({last.RamUsagePercent:0}%)", "Games may stutter when Windows has to page memory to disk. Close memory-heavy applications."),
                > 70 => new ScanFinding("Memory", FindingSeverity.Warning, $"Memory usage is high ({last.RamUsagePercent:0}%)", $"{totalGb:0.#} GB installed."),
                _ => new ScanFinding("Memory", FindingSeverity.Good, $"Plenty of free memory ({100 - last.RamUsagePercent:0}% free)", $"{totalGb:0.#} GB installed."),
            });
            if (totalGb < 11.5)
            {
                findings.Add(new ScanFinding("Memory", FindingSeverity.Info, "Less than 16 GB of RAM", "Many current games recommend 16 GB. Upgrading memory is one of the few hardware changes with a large effect on stutter."));
            }
        }

        Report(3);
        var drives = StorageInfo.GetFixedDrives();
        var systemDrive = drives.FirstOrDefault(d => d.IsSystemDrive);
        if (systemDrive is not null)
        {
            inputs = inputs with { SystemDriveFreePercent = systemDrive.FreePercent };
            var freeGb = systemDrive.FreeBytes / 1024d / 1024 / 1024;
            findings.Add(systemDrive.FreePercent switch
            {
                < 10 => new ScanFinding("Storage", FindingSeverity.Problem, $"System drive almost full ({freeGb:0} GB free)", "Windows needs free space for updates, the page file and shader caches. Free up space."),
                < 20 => new ScanFinding("Storage", FindingSeverity.Warning, $"System drive getting full ({freeGb:0} GB free)", "Consider cleaning temporary files or moving games to another drive."),
                _ => new ScanFinding("Storage", FindingSeverity.Good, $"Healthy free space ({freeGb:0} GB free)", $"{systemDrive.Name} {systemDrive.FreePercent:0}% free"),
            });
        }

        Report(4);
        var startup = catalog.GetStartupOptimizations();
        var enabledStartup = startup.Count(s => s.Entry.IsEnabled);
        inputs = inputs with { EnabledStartupApps = enabledStartup };
        findings.Add(enabledStartup switch
        {
            > 15 => new ScanFinding("Startup", FindingSeverity.Warning, $"{enabledStartup} apps start with Windows", "Review startup apps in the Optimizer and disable the ones you do not need immediately after sign-in."),
            > 8 => new ScanFinding("Startup", FindingSeverity.Info, $"{enabledStartup} apps start with Windows", "A moderate number of startup apps. Review them in the Optimizer."),
            _ => new ScanFinding("Startup", FindingSeverity.Good, $"{enabledStartup} apps start with Windows", "Startup load is light."),
        });

        Report(5);
        try
        {
            var procs = await Task.Run(processes.GetProcesses, cancellationToken).ConfigureAwait(false);
            var heavy = procs.Where(p => !p.IsProtected && p.MemoryBytes > 500L * 1024 * 1024).OrderByDescending(p => p.MemoryBytes).Take(3).ToList();
            findings.Add(heavy.Count > 0
                ? new ScanFinding("Processes", FindingSeverity.Info, $"{procs.Count} processes running", "Largest user apps: " + string.Join(", ", heavy.Select(h => $"{h.Name} ({h.MemoryBytes / 1024 / 1024} MB)")) + ". Close them before gaming if you do not need them.")
                : new ScanFinding("Processes", FindingSeverity.Good, $"{procs.Count} processes running", "No unusually large background applications."));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Process enumeration failed during scan");
        }

        // Optimization states (power, gaming, Windows configuration).
        Report(6);
        var recommendations = new List<ScanRecommendation>();
        var states = new Dictionary<string, OptimizationState>();
        foreach (var optimization in catalog.StaticOptimizations)
        {
            try
            {
                states[optimization.Id] = await optimization.GetStateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "State check failed for {OptimizationId}", optimization.Id);
            }
        }

        Report(7);
        var settingItems = catalog.StaticOptimizations.Where(o => o.Kind == OptimizationKind.Setting && states.TryGetValue(o.Id, out var s) && s.Status != OptimizationStatus.Unavailable).ToList();
        var applied = settingItems.Count(o => states[o.Id].Status == OptimizationStatus.Applied);
        inputs = inputs with { RecommendedSettingsTotal = settingItems.Count, RecommendedSettingsApplied = applied };
        foreach (var optimization in catalog.StaticOptimizations)
        {
            if (states.TryGetValue(optimization.Id, out var state) && state.Status == OptimizationStatus.NotApplied)
            {
                recommendations.Add(new ScanRecommendation(optimization, state));
            }
        }

        if (states.TryGetValue(Optimization.Optimizations.PowerPlanOptimization.OptimizationId, out var power))
        {
            findings.Add(new ScanFinding("Power", power.Status == OptimizationStatus.Applied ? FindingSeverity.Good : FindingSeverity.Info,
                power.Status == OptimizationStatus.Applied ? "Performance power mode active" : "Power plan is not set for performance", power.CurrentValue));
        }

        Report(8);
        try
        {
            var adapter = networkInfo.GetActiveAdapter();
            if (adapter is null)
            {
                findings.Add(new ScanFinding("Network", FindingSeverity.Warning, "No active internet connection", "Network checks were skipped."));
            }
            else
            {
                var pings = new List<LatencySample>();
                for (var i = 0; i < 8; i++)
                {
                    pings.Add(new LatencySample(_time.GetUtcNow(), await pinger.PingAsync(System.Net.IPAddress.Parse("1.1.1.1"), TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false)));
                    await Task.Delay(TimeSpan.FromMilliseconds(150), _time, cancellationToken).ConfigureAwait(false);
                }

                var stats = LatencyStatistics.Compute(pings);
                inputs = inputs with { PingMs = stats.AverageMs, JitterMs = stats.JitterMs, PacketLossPercent = stats.PacketLossPercent };
                var quality = LatencyStatistics.Classify(stats);
                findings.Add(new ScanFinding("Network", quality is ConnectionQuality.Excellent or ConnectionQuality.Good ? FindingSeverity.Good : FindingSeverity.Warning,
                    $"{adapter.KindDescription}: {quality} connection",
                    stats.AverageMs is { } avg ? $"{avg:0} ms ping, {stats.JitterMs ?? 0:0} ms jitter, {stats.PacketLossPercent:0.#}% loss" : "The internet test server did not respond."));
                if (adapter.Kind == AdapterKind.WiFi)
                {
                    findings.Add(new ScanFinding("Network", FindingSeverity.Info, "Connected over Wi-Fi", "For competitive online games, a wired Ethernet connection gives the most stable latency."));
                }
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Network check failed during scan");
        }

        Report(9);
        var temps = new[] { cpuTemp, gpuTemp }.Where(t => t.HasValue).Select(t => t!.Value).ToList();
        if (temps.Count > 0)
        {
            inputs = inputs with { MaxTemperatureCelsius = temps.Max() };
        }

        if (info is not null && info.OsBuild < 22000)
        {
            findings.Add(new ScanFinding("Windows", FindingSeverity.Info, "Windows 10 detected", "Some optimizations (windowed game optimizations) need Windows 11."));
        }

        var score = PerformanceScoreCalculator.Calculate(inputs);
        var report = new ScanReport
        {
            CompletedAt = _time.GetUtcNow(),
            Score = score,
            Findings = findings,
            Recommendations = recommendations.OrderByDescending(r => r.Optimization.Impact).ThenBy(r => r.Optimization.Risk).ToList(),
            System = info,
        };
        progress?.Report(new ScanProgress(Steps.Count, Steps.Count, "Complete", 1));
        LastReport = report;
        ScanCompleted?.Invoke(this, report);
        return report;
    }

    private static double? Average(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }
}
