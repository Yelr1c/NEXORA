namespace Nexora.Core.Network;

public static class LatencyStatistics
{
    /// <summary>
    /// Computes latency statistics. Jitter is the mean absolute difference between consecutive successful
    /// round-trip times (the same idea as RFC 3550 inter-arrival jitter, without exponential smoothing).
    /// </summary>
    public static LatencyStatisticsResult Compute(IReadOnlyList<LatencySample> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.Count == 0)
        {
            return LatencyStatisticsResult.Empty;
        }

        var rtts = samples.Where(s => s.RoundTripMs.HasValue).Select(s => s.RoundTripMs!.Value).ToArray();
        var sent = samples.Count;
        var received = rtts.Length;
        var loss = 100.0 * (sent - received) / sent;
        if (received == 0)
        {
            return new LatencyStatisticsResult(sent, 0, loss, null, null, null, null, null, null, 0);
        }

        var average = rtts.Average();
        var sorted = (double[])rtts.Clone();
        Array.Sort(sorted);
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        double? jitter = null;
        if (rtts.Length >= 2)
        {
            double sum = 0;
            for (var i = 1; i < rtts.Length; i++)
            {
                sum += Math.Abs(rtts[i] - rtts[i - 1]);
            }

            jitter = sum / (rtts.Length - 1);
        }

        var variance = rtts.Sum(r => (r - average) * (r - average)) / rtts.Length;
        var stdDev = Math.Sqrt(variance);
        return new LatencyStatisticsResult(sent, received, loss, average, sorted[0], sorted[^1], median, jitter, stdDev,
            StabilityScore(loss, jitter ?? 0, stdDev, average));
    }

    /// <summary>0–100 score of how steady the connection is (independent of absolute ping).</summary>
    public static double StabilityScore(double lossPercent, double jitterMs, double stdDevMs, double averageMs)
    {
        var lossPenalty = Math.Min(60, lossPercent * 12);
        var jitterPenalty = Math.Min(30, jitterMs * 1.5);
        var cv = averageMs > 0 ? stdDevMs / averageMs : 0;
        var variationPenalty = Math.Min(20, cv * 40);
        return Math.Clamp(100 - lossPenalty - jitterPenalty - variationPenalty, 0, 100);
    }

    public static ConnectionQuality Classify(LatencyStatisticsResult stats)
    {
        ArgumentNullException.ThrowIfNull(stats);
        if (stats.Sent == 0)
        {
            return ConnectionQuality.Unknown;
        }

        if (stats.AverageMs is not { } ping)
        {
            return ConnectionQuality.Poor;
        }

        var jitter = stats.JitterMs ?? 0;
        var loss = stats.PacketLossPercent;
        if (ping <= 30 && jitter <= 5 && loss <= 0.0)
        {
            return ConnectionQuality.Excellent;
        }

        if (ping <= 60 && jitter <= 10 && loss <= 1.0)
        {
            return ConnectionQuality.Good;
        }

        if (ping <= 100 && jitter <= 20 && loss <= 3.0)
        {
            return ConnectionQuality.Fair;
        }

        return ConnectionQuality.Poor;
    }
}
