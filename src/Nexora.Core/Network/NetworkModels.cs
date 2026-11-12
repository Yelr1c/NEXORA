using System.Net;

namespace Nexora.Core.Network;

public readonly record struct LatencySample(DateTimeOffset Timestamp, double? RoundTripMs)
{
    public bool IsLost => RoundTripMs is null;
}

public enum ConnectionQuality
{
    Unknown,
    Excellent,
    Good,
    Fair,
    Poor,
}

public sealed record LatencyStatisticsResult(
    int Sent,
    int Received,
    double PacketLossPercent,
    double? AverageMs,
    double? MinimumMs,
    double? MaximumMs,
    double? MedianMs,
    double? JitterMs,
    double? StandardDeviationMs,
    double StabilityScore)
{
    public static LatencyStatisticsResult Empty { get; } = new(0, 0, 0, null, null, null, null, null, null, 0);
}

public enum AdapterKind
{
    Ethernet,
    WiFi,
    Cellular,
    Other,
}

public sealed record NetworkAdapterInfo(
    string Name,
    string Description,
    AdapterKind Kind,
    long? SpeedBitsPerSecond,
    bool IsUp,
    IPAddress? Gateway,
    IReadOnlyList<IPAddress> DnsServers,
    bool HasIPv4,
    bool HasIPv6,
    int? WifiSignalQualityPercent,
    double? WifiLinkSpeedMbps,
    string? WifiStandard)
{
    public string KindDescription => Kind switch
    {
        AdapterKind.Ethernet => "Ethernet (wired)",
        AdapterKind.WiFi => "Wi-Fi (wireless)",
        AdapterKind.Cellular => "Mobile broadband",
        _ => "Other",
    };
}

public interface INetworkInfoProvider
{
    /// <summary>The adapter Windows uses for internet traffic, or null when offline.</summary>
    NetworkAdapterInfo? GetActiveAdapter();
}

public sealed class NetworkTestOptions
{
    public IPAddress InternetTarget { get; init; } = IPAddress.Parse("1.1.1.1");

    public int SampleCount { get; init; } = 40;

    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(250);

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    public string DnsProbeName { get; init; } = "www.microsoft.com";

    public int DnsProbeCount { get; init; } = 5;
}

public sealed class NetworkTestResult
{
    public DateTimeOffset StartedAt { get; init; }

    public string InternetTarget { get; init; } = string.Empty;

    public LatencyStatisticsResult Internet { get; init; } = LatencyStatisticsResult.Empty;

    public LatencyStatisticsResult? Gateway { get; init; }

    public LatencyStatisticsResult? Dns { get; init; }

    public string? DnsServer { get; init; }

    public IReadOnlyList<LatencySample> InternetSamples { get; init; } = [];

    public IReadOnlyList<LatencySample> GatewaySamples { get; init; } = [];

    public NetworkAdapterInfo? Adapter { get; init; }

    public ConnectionQuality Quality { get; init; }

    public IReadOnlyList<NetworkInsight> Insights { get; init; } = [];
}

public enum InsightSeverity
{
    Good,
    Info,
    Warning,
    Problem,
}

public sealed record NetworkInsight(InsightSeverity Severity, string Title, string Detail);
