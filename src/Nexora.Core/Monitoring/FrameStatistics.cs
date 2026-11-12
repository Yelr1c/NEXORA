namespace Nexora.Core.Monitoring;

public sealed record FrameStatisticsResult(
    int FrameCount,
    double DurationSeconds,
    double AverageFps,
    double OnePercentLowFps,
    double PointOnePercentLowFps,
    double AverageFrameTimeMs,
    double P99FrameTimeMs,
    double MaxFrameTimeMs,
    double MinFps,
    double MaxFps);

/// <summary>
/// Frame time statistics using the definitions common in benchmarking tools:
/// <list type="bullet">
/// <item>Average FPS = frames / total time (not the mean of per-frame FPS).</item>
/// <item>1% / 0.1% low = average FPS of the slowest 1% / 0.1% of frames (by frame time).</item>
/// </list>
/// </summary>
public static class FrameStatistics
{
    public static FrameStatisticsResult? Compute(IReadOnlyList<double> frameTimesMs)
    {
        ArgumentNullException.ThrowIfNull(frameTimesMs);
        var valid = frameTimesMs.Where(t => t > 0 && double.IsFinite(t) && t < 10_000).ToArray();
        if (valid.Length == 0)
        {
            return null;
        }

        var totalMs = valid.Sum();
        var averageFps = valid.Length * 1000.0 / totalMs;
        var sorted = (double[])valid.Clone();
        Array.Sort(sorted);

        return new FrameStatisticsResult(
            valid.Length,
            totalMs / 1000.0,
            averageFps,
            LowAverageFps(sorted, 0.01),
            LowAverageFps(sorted, 0.001),
            totalMs / valid.Length,
            Percentile(sorted, 0.99),
            sorted[^1],
            1000.0 / sorted[^1],
            1000.0 / sorted[0]);
    }

    /// <summary>Average FPS of the slowest <paramref name="fraction"/> of frames. <paramref name="sortedAscending"/> must be sorted.</summary>
    public static double LowAverageFps(double[] sortedAscending, double fraction)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Length == 0)
        {
            return 0;
        }

        var count = Math.Max(1, (int)Math.Ceiling(sortedAscending.Length * fraction));
        double sum = 0;
        for (var i = sortedAscending.Length - count; i < sortedAscending.Length; i++)
        {
            sum += sortedAscending[i];
        }

        return count * 1000.0 / sum;
    }

    /// <summary>Linear-interpolated percentile of a sorted array (p in 0..1).</summary>
    public static double Percentile(double[] sortedAscending, double p)
    {
        ArgumentNullException.ThrowIfNull(sortedAscending);
        if (sortedAscending.Length == 0)
        {
            return 0;
        }

        if (sortedAscending.Length == 1)
        {
            return sortedAscending[0];
        }

        var position = Math.Clamp(p, 0, 1) * (sortedAscending.Length - 1);
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        var weight = position - lower;
        return sortedAscending[lower] + ((sortedAscending[upper] - sortedAscending[lower]) * weight);
    }
}
