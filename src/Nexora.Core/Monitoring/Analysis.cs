namespace Nexora.Core.Monitoring;

public enum Bottleneck
{
    Unknown,
    Balanced,
    Gpu,
    Cpu,
    Memory,
    VideoMemory,
    Thermal,
    FrameLimit,
}

public sealed record BottleneckResult(Bottleneck Kind, string Title, string Explanation);

/// <summary>Explains which component limits performance, based only on measured utilisation.</summary>
public static class BottleneckAnalyzer
{
    public static BottleneckResult Analyze(IReadOnlyList<MetricSnapshot> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        var gpu = Avg(samples.Select(s => s.GpuUsagePercent));
        var cpu = Avg(samples.Select(s => s.CpuUsagePercent));
        var cpuPeak = samples.Select(s => s.CpuUsagePercent).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var ram = Avg(samples.Where(s => s.RamTotalBytes > 0).Select(s => (double?)s.RamUsagePercent));
        var vram = Avg(samples.Select(s => s.VramUsagePercent));
        var cpuTemp = samples.Select(s => s.CpuTemperatureCelsius).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var gpuTemp = samples.Select(s => s.GpuTemperatureCelsius).Where(v => v.HasValue).Select(v => v!.Value).DefaultIfEmpty(0).Max();
        var hasFps = samples.Any(s => s.Fps.HasValue);

        if (samples.Count < 3 || (gpu is null && cpu is null))
        {
            return new BottleneckResult(Bottleneck.Unknown, "Not enough data", "Record a longer session while a game is running.");
        }

        if (cpuTemp >= 95 || gpuTemp >= 90)
        {
            return new BottleneckResult(Bottleneck.Thermal, "Heat (thermal throttling)",
                $"Temperatures reached {Math.Max(cpuTemp, gpuTemp):0} °C. Hardware slows itself down at these temperatures — improve cooling or airflow before tuning anything else.");
        }

        if (ram is > 90)
        {
            return new BottleneckResult(Bottleneck.Memory, "System memory (RAM)",
                $"RAM was {ram:0}% full on average. Windows starts moving data to disk, which causes stutter. Close memory-heavy apps (browsers, launchers) while gaming.");
        }

        if (vram is > 95)
        {
            return new BottleneckResult(Bottleneck.VideoMemory, "Video memory (VRAM)",
                $"Video memory was {vram:0}% full. Lower texture quality or resolution to stop textures being swapped out mid-game.");
        }

        if (gpu is > 90)
        {
            return new BottleneckResult(Bottleneck.Gpu, "Graphics card (GPU)",
                $"The GPU was {gpu:0}% busy — it is the limiting part, which is normal and healthy for gaming. For more FPS lower resolution, graphics presets or enable DLSS/FSR.");
        }

        if (cpuPeak > 90 || (cpu is > 70 && gpu is < 75))
        {
            return new BottleneckResult(Bottleneck.Cpu, "Processor (CPU)",
                $"The GPU only worked {gpu ?? 0:0}% while CPU load peaked at {cpuPeak:0}%. The game is waiting on the processor: close background apps, use a higher-performance power mode or lower CPU-heavy settings (view distance, crowds).");
        }

        if (hasFps && gpu is < 70 && cpu is < 60)
        {
            return new BottleneckResult(Bottleneck.FrameLimit, "Frame rate limit",
                "Neither CPU nor GPU is fully used, so the game is most likely capped (V-Sync, an FPS limiter or the engine's maximum). Raise or remove the cap if you want more FPS.");
        }

        return new BottleneckResult(Bottleneck.Balanced, "Balanced",
            $"No single part is maxed out (CPU {cpu ?? 0:0}%, GPU {gpu ?? 0:0}%). Your system has headroom.");
    }

    private static double? Avg(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }
}

public enum ComparisonVerdict
{
    Improved,
    Unchanged,
    Worse,
    Unavailable,
}

public sealed record ComparisonRow(string Metric, string Before, string After, ComparisonVerdict Verdict, string Note)
{
    public string VerdictText => Verdict switch
    {
        ComparisonVerdict.Improved => "Improved",
        ComparisonVerdict.Worse => "Worse",
        ComparisonVerdict.Unchanged => Metric == "Network latency" ? "Stable" : "No real change",
        _ => "Not measured",
    };
}

public sealed record SessionComparison(
    PerformanceSessionResult Before,
    PerformanceSessionResult After,
    IReadOnlyList<ComparisonRow> Rows,
    string Summary,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Compares two measured performance tests. Differences inside normal run-to-run variation are reported as
/// "no real change" — NEXORA never claims an improvement the data does not support.
/// </summary>
public static class SessionComparer
{
    /// <summary>Changes smaller than this are within normal run-to-run variation.</summary>
    public const double FpsNoise = 0.03;
    public const double LowsNoise = 0.05;

    public static SessionComparison Compare(PerformanceSessionResult before, PerformanceSessionResult after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var rows = new List<ComparisonRow>();
        var warnings = new List<string>();

        if (!string.Equals(before.ProcessName, after.ProcessName, StringComparison.OrdinalIgnoreCase))
        {
            warnings.Add($"The tests measured different applications ({before.ProcessName ?? "none"} vs {after.ProcessName ?? "none"}); the comparison is only meaningful for the same game.");
        }

        if (Math.Abs(before.DurationSeconds - after.DurationSeconds) > Math.Max(15, 0.5 * before.DurationSeconds))
        {
            warnings.Add("The tests had very different lengths. For a fair comparison play the same scene for the same time.");
        }

        rows.Add(HigherIsBetter("Average FPS", before.Frames?.AverageFps, after.Frames?.AverageFps, FpsNoise, "0"));
        rows.Add(HigherIsBetter("1% low FPS", before.Frames?.OnePercentLowFps, after.Frames?.OnePercentLowFps, LowsNoise, "0"));
        rows.Add(Stability(before.Frames, after.Frames));
        rows.Add(Latency(before, after));
        rows.Add(Resources(before, after));

        var improved = rows.Count(r => r.Verdict == ComparisonVerdict.Improved);
        var worse = rows.Count(r => r.Verdict == ComparisonVerdict.Worse);
        var summary = (improved, worse) switch
        {
            (0, 0) => "No measurable difference between the two tests — everything is within normal variation.",
            ( > 0, 0) => $"{improved} area(s) measurably improved, nothing got worse.",
            (0, > 0) => $"{worse} area(s) got worse. If you changed settings in between, consider undoing them in History.",
            _ => $"Mixed result: {improved} improved, {worse} got worse.",
        };
        return new SessionComparison(before, after, rows, summary, warnings);
    }

    private static ComparisonRow HigherIsBetter(string metric, double? before, double? after, double noise, string format)
    {
        if (before is not { } b || after is not { } a || b <= 0)
        {
            return new ComparisonRow(metric, Fmt(before, format), Fmt(after, format), ComparisonVerdict.Unavailable, "FPS was not captured in one of the tests.");
        }

        var change = (a - b) / b;
        var verdict = Math.Abs(change) < noise ? ComparisonVerdict.Unchanged : change > 0 ? ComparisonVerdict.Improved : ComparisonVerdict.Worse;
        return new ComparisonRow(metric, Fmt(b, format), Fmt(a, format), verdict, $"{change * 100:+0.0;-0.0;0}%");
    }

    /// <summary>Frame-time stability = 1% low ÷ average FPS (1.0 = perfectly even frame pacing).</summary>
    private static ComparisonRow Stability(FrameStatisticsResult? before, FrameStatisticsResult? after)
    {
        if (before is null || after is null || before.AverageFps <= 0 || after.AverageFps <= 0)
        {
            return new ComparisonRow("Frametime stability", "—", "—", ComparisonVerdict.Unavailable, "FPS was not captured in one of the tests.");
        }

        var b = before.OnePercentLowFps / before.AverageFps;
        var a = after.OnePercentLowFps / after.AverageFps;
        var verdict = Math.Abs(a - b) < 0.04 ? ComparisonVerdict.Unchanged : a > b ? ComparisonVerdict.Improved : ComparisonVerdict.Worse;
        return new ComparisonRow("Frametime stability", $"{b * 100:0}%", $"{a * 100:0}%", verdict, "1% low as a share of average FPS — higher means smoother.");
    }

    private static ComparisonRow Latency(PerformanceSessionResult before, PerformanceSessionResult after)
    {
        if (before.PingAverageMs is not { } b || after.PingAverageMs is not { } a)
        {
            return new ComparisonRow("Network latency", Fmt(before.PingAverageMs, "0", " ms"), Fmt(after.PingAverageMs, "0", " ms"), ComparisonVerdict.Unavailable, "Live network monitoring was off during a test.");
        }

        var jitterB = before.PingJitterMs ?? 0;
        var jitterA = after.PingJitterMs ?? 0;
        var verdict = Math.Abs(a - b) <= Math.Max(3, 0.1 * b) && Math.Abs(jitterA - jitterB) <= 3
            ? ComparisonVerdict.Unchanged
            : a + jitterA < b + jitterB ? ComparisonVerdict.Improved : ComparisonVerdict.Worse;
        return new ComparisonRow("Network latency", $"{b:0} ms", $"{a:0} ms", verdict, $"Jitter {jitterB:0} → {jitterA:0} ms. Ping depends on your connection, not on PC settings.");
    }

    private static ComparisonRow Resources(PerformanceSessionResult before, PerformanceSessionResult after)
    {
        double? Load(PerformanceSessionResult r) => r.Cpu.Average is { } c && r.Ram.Average is { } m ? (c + m) / 2 : null;
        if (Load(before) is not { } b || Load(after) is not { } a)
        {
            return new ComparisonRow("System resource usage", "—", "—", ComparisonVerdict.Unavailable, "CPU or memory was not measured.");
        }

        var verdict = Math.Abs(a - b) < 5 ? ComparisonVerdict.Unchanged : a < b ? ComparisonVerdict.Improved : ComparisonVerdict.Worse;
        return new ComparisonRow("System resource usage", $"CPU {before.Cpu.Average:0}% · RAM {before.Ram.Average:0}%", $"CPU {after.Cpu.Average:0}% · RAM {after.Ram.Average:0}%", verdict,
            "Lower background load leaves more headroom for the game.");
    }

    private static string Fmt(double? value, string format, string suffix = "") => value is { } v ? v.ToString(format, System.Globalization.CultureInfo.CurrentCulture) + suffix : "—";
}
