using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Nexora.Core.Scoring;

namespace Nexora.Core.Tests;

public class FrameStatisticsTests
{
    [Fact]
    public void ConstantFrameTimesProduceMatchingFpsAndLows()
    {
        var frames = Enumerable.Repeat(10.0, 1000).ToList();
        var result = FrameStatistics.Compute(frames)!;
        Assert.Equal(100, result.AverageFps, 6);
        Assert.Equal(100, result.OnePercentLowFps, 6);
        Assert.Equal(100, result.PointOnePercentLowFps, 6);
        Assert.Equal(10, result.AverageFrameTimeMs, 6);
        Assert.Equal(10, result.DurationSeconds, 6);
    }

    [Fact]
    public void LowsReflectSlowestFrames()
    {
        // 990 frames at 10 ms, 10 frames at 50 ms.
        var frames = Enumerable.Repeat(10.0, 990).Concat(Enumerable.Repeat(50.0, 10)).ToList();
        var result = FrameStatistics.Compute(frames)!;
        Assert.Equal(1000 * 1000.0 / ((990 * 10) + (10 * 50)), result.AverageFps, 6);
        Assert.Equal(20, result.OnePercentLowFps, 6);       // worst 10 frames are all 50 ms
        Assert.Equal(20, result.PointOnePercentLowFps, 6);  // worst 1 frame
        Assert.Equal(50, result.MaxFrameTimeMs);
    }

    [Fact]
    public void AverageFpsIsFramesOverTimeNotMeanOfInstantFps()
    {
        var result = FrameStatistics.Compute([10, 30])!;
        Assert.Equal(50, result.AverageFps, 6); // 2 frames / 40 ms
    }

    [Fact]
    public void InvalidValuesAreIgnoredAndEmptyReturnsNull()
    {
        Assert.Null(FrameStatistics.Compute([]));
        Assert.Null(FrameStatistics.Compute([0, -5, double.NaN, double.PositiveInfinity]));
        var result = FrameStatistics.Compute([16.6, double.NaN, 0])!;
        Assert.Equal(1, result.FrameCount);
    }

    [Fact]
    public void PercentileInterpolates()
    {
        Assert.Equal(5.5, FrameStatistics.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 0.5), 6);
        Assert.Equal(10, FrameStatistics.Percentile([1, 2, 3, 4, 5, 6, 7, 8, 9, 10], 1));
    }

    [Fact]
    public void SessionAnalyzerReportsWhyFramesAreMissing()
    {
        var samples = new[]
        {
            new MetricSnapshot { CpuUsagePercent = 50, GpuUsagePercent = 99, RamTotalBytes = 100, RamUsedBytes = 50 },
            new MetricSnapshot { CpuUsagePercent = 70, GpuUsagePercent = 97, RamTotalBytes = 100, RamUsedBytes = 60 },
        };
        var result = PerformanceSessionAnalyzer.Analyze(DateTimeOffset.UtcNow, 10, samples, [], null, "requires admin");
        Assert.Null(result.Frames);
        Assert.Equal("requires admin", result.FrameUnavailableReason);
        Assert.Equal(60, result.Cpu.Average);
        Assert.Equal(70, result.Cpu.Max);
        Assert.Equal(55, result.Ram.Average!.Value, 6);
        Assert.Contains(result.Insights, i => i.Contains("GPU-bound", StringComparison.Ordinal));
    }

    [Fact]
    public void RingBufferKeepsNewestItems()
    {
        var ring = new RingBuffer<int>(3);
        for (var i = 1; i <= 5; i++)
        {
            ring.Add(i);
        }

        Assert.Equal([3, 4, 5], ring.ToList());
    }
}

public class LatencyStatisticsTests
{
    private static List<LatencySample> Samples(params double?[] values)
        => values.Select(v => new LatencySample(DateTimeOffset.UtcNow, v)).ToList();

    [Fact]
    public void ComputesAverageJitterAndLoss()
    {
        var stats = LatencyStatistics.Compute(Samples(20, 22, null, 18, 20));
        Assert.Equal(5, stats.Sent);
        Assert.Equal(4, stats.Received);
        Assert.Equal(20, stats.PacketLossPercent, 6);
        Assert.Equal(20, stats.AverageMs!.Value, 6);
        Assert.Equal(18, stats.MinimumMs);
        Assert.Equal(22, stats.MaximumMs);
        // |22-20| + |18-22| + |20-18| = 8 over 3 intervals
        Assert.Equal(8.0 / 3, stats.JitterMs!.Value, 6);
        Assert.Equal(20, stats.MedianMs);
    }

    [Fact]
    public void AllLostIsPoorWithoutAverage()
    {
        var stats = LatencyStatistics.Compute(Samples(null, null));
        Assert.Equal(100, stats.PacketLossPercent);
        Assert.Null(stats.AverageMs);
        Assert.Equal(ConnectionQuality.Poor, LatencyStatistics.Classify(stats));
    }

    [Theory]
    [InlineData(new double[] { 10, 11, 10, 12 }, ConnectionQuality.Excellent)]
    [InlineData(new double[] { 45, 50, 45, 50 }, ConnectionQuality.Good)]
    [InlineData(new double[] { 90, 80, 95, 85 }, ConnectionQuality.Fair)]
    [InlineData(new double[] { 150, 200, 150, 220 }, ConnectionQuality.Poor)]
    public void ClassifiesQuality(double[] rtts, ConnectionQuality expected)
    {
        var stats = LatencyStatistics.Compute(rtts.Select(r => new LatencySample(DateTimeOffset.UtcNow, r)).ToList());
        Assert.Equal(expected, LatencyStatistics.Classify(stats));
    }

    [Fact]
    public void EmptyIsUnknown()
    {
        Assert.Equal(ConnectionQuality.Unknown, LatencyStatistics.Classify(LatencyStatistics.Compute([])));
    }

    [Fact]
    public void StabilityDropsWithLossAndJitter()
    {
        var steady = LatencyStatistics.StabilityScore(0, 1, 1, 20);
        var shaky = LatencyStatistics.StabilityScore(5, 15, 10, 20);
        Assert.True(steady > 90);
        Assert.True(shaky < steady);
        Assert.InRange(LatencyStatistics.StabilityScore(100, 100, 100, 1), 0, 100);
    }

    [Fact]
    public void DnsQueryIsWellFormed()
    {
        var query = DnsMessage.BuildQuery(0xBEEF, "www.example.com");
        Assert.Equal(0xBE, query[0]);
        Assert.Equal(0xEF, query[1]);
        Assert.Equal(1, query[5]); // QDCOUNT
        Assert.Equal(3, query[12]); // "www"
        Assert.Equal(12 + 17 + 4, query.Length);
        var response = (byte[])query.Clone();
        response[2] |= 0x80;
        Assert.True(DnsMessage.IsResponseTo(response, 0xBEEF));
        Assert.False(DnsMessage.IsResponseTo(query, 0xBEEF));
        Assert.False(DnsMessage.IsResponseTo(response, 0x1234));
        Assert.Throws<ArgumentException>(() => DnsMessage.BuildQuery(1, "bad..name"));
    }

    [Fact]
    public void AnalyzerBlamesWifiForLocalLoss()
    {
        var adapter = new NetworkAdapterInfo("Wi-Fi", "Test", AdapterKind.WiFi, 866_000_000, true, System.Net.IPAddress.Parse("192.168.1.1"), [], true, false, 40, 400, null);
        var internet = LatencyStatistics.Compute(Samples(30, 40, null, 35, 50, 32));
        var gateway = LatencyStatistics.Compute(Samples(2, null, 20, 3, null, 15));
        var insights = NetworkAnalyzer.Diagnose(internet, gateway, null, adapter);
        Assert.Contains(insights, i => i.Title.Contains("local network", StringComparison.Ordinal));
        Assert.Contains(insights, i => i.Title.Contains("Weak Wi-Fi", StringComparison.Ordinal));
    }

    [Fact]
    public void AnalyzerNeverClaimsDnsChangesPing()
    {
        var adapter = new NetworkAdapterInfo("Ethernet", "Test", AdapterKind.Ethernet, 1_000_000_000, true, System.Net.IPAddress.Parse("192.168.1.1"), [], true, false, null, null, null);
        var stats = LatencyStatistics.Compute(Samples(20, 20, 21));
        var dns = LatencyStatistics.Compute(Samples(150, 140));
        var insight = Assert.Single(NetworkAnalyzer.Diagnose(stats, LatencyStatistics.Compute(Samples(1, 1, 1)), dns, adapter));
        Assert.Contains("does not change your in-game ping", insight.Detail, StringComparison.Ordinal);
    }
}

public class PerformanceScoreTests
{
    [Fact]
    public void IdealSystemScoresHundred()
    {
        var result = PerformanceScoreCalculator.Calculate(new ScoreInputs
        {
            CpuUsagePercent = 5, GpuUsagePercent = 2, RamUsagePercent = 30, SystemDriveFreePercent = 60,
            PingMs = 10, JitterMs = 1, PacketLossPercent = 0, RecommendedSettingsTotal = 5, RecommendedSettingsApplied = 5,
            EnabledStartupApps = 3, MaxTemperatureCelsius = 50,
        });
        Assert.Equal(100, result.Score);
        Assert.Equal("Excellent", result.Rating);
    }

    [Fact]
    public void MissingComponentsAreRenormalized()
    {
        var result = PerformanceScoreCalculator.Calculate(new ScoreInputs { CpuUsagePercent = 5, RamUsagePercent = 30 });
        Assert.Equal(100, result.Score);
        Assert.Equal(2, result.Components.Count);
    }

    [Fact]
    public void NoInputsIsUnavailable()
    {
        var result = PerformanceScoreCalculator.Calculate(new ScoreInputs());
        Assert.Equal(0, result.Score);
        Assert.Equal("Unavailable", result.Rating);
    }

    [Fact]
    public void HeavyLoadLowersScore()
    {
        var result = PerformanceScoreCalculator.Calculate(new ScoreInputs { CpuUsagePercent = 95, RamUsagePercent = 95, SystemDriveFreePercent = 3 });
        Assert.Equal(0, result.Score);
        Assert.Equal("Needs attention", result.Rating);
    }

    [Theory]
    [InlineData(10, 100)]
    [InlineData(55, 50)]
    [InlineData(100, 0)]
    public void LinearMapsRange(double value, double expected)
        => Assert.Equal(expected, PerformanceScoreCalculator.Linear(value, 10, 100), 6);

    [Theory]
    [InlineData(95, "Excellent")]
    [InlineData(80, "Good")]
    [InlineData(65, "Fair")]
    [InlineData(10, "Needs attention")]
    public void Ratings(int score, string rating) => Assert.Equal(rating, Scoring.ScoreResult.RatingFor(score));
}

public class DnsBenchmarkTests
{
    private sealed class FakeProbe(Dictionary<string, double?> latencyByIp) : IDnsProbe
    {
        public Task<double?> MeasureAsync(System.Net.IPAddress resolver, string hostName, TimeSpan timeout, CancellationToken cancellationToken = default)
            => Task.FromResult(latencyByIp.TryGetValue(resolver.ToString(), out var v) ? v : null);
    }

    [Fact]
    public async Task RanksFastestFirstAndUnreachableLast()
    {
        var probe = new FakeProbe(new() { ["1.1.1.1"] = 12, ["8.8.8.8"] = 20, ["9.9.9.9"] = null, ["208.67.222.222"] = 15, ["192.168.1.1"] = 30 });
        var providers = DnsBenchmark.BuildProviderList([System.Net.IPAddress.Parse("192.168.1.1")]);
        Assert.True(providers[0].IsCurrent);
        Assert.Equal(5, providers.Count);
        var results = await new DnsBenchmark(probe).RunAsync(providers, null);
        Assert.Equal("Cloudflare", results[0].Provider.Name);
        Assert.Equal(12, results[0].MedianMs);
        Assert.False(results[^1].Reachable);
        Assert.Equal("Quad9", results[^1].Provider.Name);
    }

    [Fact]
    public void CurrentDnsThatIsAPublicProviderIsNotDuplicated()
    {
        var providers = DnsBenchmark.BuildProviderList([System.Net.IPAddress.Parse("8.8.8.8")]);
        Assert.Equal(4, providers.Count);
        Assert.Equal("Google (your DNS)", providers[0].Name);
    }

    [Fact]
    public void SummarizeUsesMedian()
    {
        var r = DnsBenchmark.Summarize(new DnsProvider("x", System.Net.IPAddress.Loopback), [10, 50, 12], 6);
        Assert.Equal(12, r.MedianMs);
        Assert.Equal(3, r.Answered);
    }
}
