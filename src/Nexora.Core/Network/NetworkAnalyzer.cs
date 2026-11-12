namespace Nexora.Core.Network;

/// <summary>Turns measurements into honest, plain-language explanations.</summary>
public static class NetworkAnalyzer
{
    public static IReadOnlyList<NetworkInsight> Diagnose(
        LatencyStatisticsResult internet,
        LatencyStatisticsResult? gateway,
        LatencyStatisticsResult? dns,
        NetworkAdapterInfo? adapter)
    {
        ArgumentNullException.ThrowIfNull(internet);
        var insights = new List<NetworkInsight>();

        if (adapter is null)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Problem, "No active network connection", "Windows reports no adapter with a route to the internet. Check that you are connected."));
            return insights;
        }

        if (internet.Received == 0)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Problem, "The test server did not respond",
                gateway is { Received: > 0 }
                    ? "Your router responds, but nothing beyond it does. The problem is likely with your internet provider, or ICMP (ping) is blocked by a firewall."
                    : "Neither your router nor the internet responded. Check your cable/Wi-Fi connection and router."));
            return insights;
        }

        var gatewayLoss = gateway?.PacketLossPercent ?? 0;
        var gatewayJitter = gateway?.JitterMs ?? 0;
        var gatewayAvg = gateway?.AverageMs;

        if (gateway is { Sent: > 0 } && gatewayLoss > 1)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Problem, "Packet loss on your local network",
                $"{gatewayLoss:0.#}% of pings to your own router were lost. Loss this close to your PC is almost always caused by Wi-Fi interference, signal strength or a faulty cable — not your provider."));
        }
        else if (internet.PacketLossPercent > 1)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Warning, "Packet loss beyond your router",
                $"{internet.PacketLossPercent:0.#}% loss to the internet while your local network is clean. This points to congestion at your provider or along the route. Contact your ISP if it persists."));
        }

        if (gatewayAvg is > 10 && adapter.Kind == AdapterKind.WiFi)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Warning, "Slow Wi-Fi link to the router",
                $"Reaching your router takes {gatewayAvg:0} ms on average. On a healthy network this is 1–3 ms. Move closer to the router, use the 5 GHz/6 GHz band, or connect by Ethernet."));
        }

        if (gatewayJitter > 5 || (internet.JitterMs is > 10 && adapter.Kind == AdapterKind.WiFi))
        {
            insights.Add(new NetworkInsight(InsightSeverity.Warning, "Unstable latency (jitter)",
                adapter.Kind == AdapterKind.WiFi
                    ? "Latency varies a lot between packets. Wi-Fi is the most common cause; a wired connection usually removes it."
                    : "Latency varies a lot between packets. Large downloads, uploads or streaming on your network can cause this (bufferbloat)."));
        }
        else if (internet.JitterMs is > 10)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Warning, "Unstable latency beyond your router",
                "Your local network is steady but latency to the internet varies. Other traffic on your connection (downloads, streaming, cloud backup) or provider congestion are likely causes."));
        }

        if (internet.AverageMs is > 80 && (gatewayAvg ?? 0) < 5)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Info, "High latency to the internet",
                "Your local network is fast, so the extra latency comes from your provider or the distance to the test server. In games, choosing a closer server region is the most effective fix. No PC setting can shorten this path."));
        }

        if (adapter.Kind == AdapterKind.WiFi && adapter.WifiSignalQualityPercent is < 50)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Warning, "Weak Wi-Fi signal",
                $"Signal quality is {adapter.WifiSignalQualityPercent}%. Weak signal causes retransmissions, which show up as jitter and loss."));
        }

        if (dns is { AverageMs: > 80 })
        {
            insights.Add(new NetworkInsight(InsightSeverity.Info, "Slow DNS resolver",
                $"Your DNS server answers in about {dns.AverageMs:0} ms. This only affects how fast names are looked up (e.g. opening websites or connecting to a match) — it does not change your in-game ping."));
        }
        else if (dns is { Sent: > 0, Received: 0 })
        {
            insights.Add(new NetworkInsight(InsightSeverity.Info, "DNS resolver did not answer direct queries",
                "Your DNS server did not respond to NEXORA's direct query. Some routers and VPNs block this; normal browsing may still work."));
        }

        if (adapter.Kind == AdapterKind.Ethernet && adapter.SpeedBitsPerSecond is { } speed && speed < 1_000_000_000 && speed > 0)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Info, "Ethernet link below gigabit",
                $"Your adapter negotiated {speed / 1_000_000} Mbps. If your internet plan is faster than this, check the cable (Cat5e or better) and router port."));
        }

        if (insights.Count == 0)
        {
            insights.Add(new NetworkInsight(InsightSeverity.Good, "Your connection looks healthy",
                "Latency is low and stable with no packet loss. Network conditions are not limiting your online gaming."));
        }

        return insights;
    }
}
