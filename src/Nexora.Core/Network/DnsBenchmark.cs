using System.Net;

namespace Nexora.Core.Network;

public sealed record DnsProvider(string Name, IPAddress Address, bool IsCurrent = false);

public sealed record DnsBenchmarkResult(DnsProvider Provider, double? MedianMs, double? AverageMs, int Answered, int Sent)
{
    public bool Reachable => Answered > 0;
}

/// <summary>
/// Measures how fast different DNS resolvers answer from this connection (direct UDP queries, no cache).
/// This only affects how quickly names are looked up — never in-game ping.
/// </summary>
public sealed class DnsBenchmark(IDnsProbe probe)
{
    public static readonly IReadOnlyList<DnsProvider> PublicProviders =
    [
        new("Cloudflare", IPAddress.Parse("1.1.1.1")),
        new("Google", IPAddress.Parse("8.8.8.8")),
        new("Quad9", IPAddress.Parse("9.9.9.9")),
        new("OpenDNS", IPAddress.Parse("208.67.222.222")),
    ];

    /// <summary>Common, globally cached names so results reflect resolver latency rather than authoritative lookups.</summary>
    public static readonly IReadOnlyList<string> TestNames =
        ["www.microsoft.com", "www.google.com", "store.steampowered.com", "www.epicgames.com", "www.cloudflare.com", "www.youtube.com"];

    public static IReadOnlyList<DnsProvider> BuildProviderList(IEnumerable<IPAddress> currentServers)
    {
        var list = new List<DnsProvider>();
        foreach (var current in currentServers.Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).Take(1))
        {
            var known = PublicProviders.FirstOrDefault(p => p.Address.Equals(current));
            list.Add(new DnsProvider(known is null ? $"Your DNS ({current})" : $"{known.Name} (your DNS)", current, IsCurrent: true));
        }

        list.AddRange(PublicProviders.Where(p => !list.Any(l => l.Address.Equals(p.Address))));
        return list;
    }

    public async Task<IReadOnlyList<DnsBenchmarkResult>> RunAsync(IReadOnlyList<DnsProvider> providers, IProgress<double>? progress, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(providers);
        var results = new List<DnsBenchmarkResult>();
        var total = providers.Count * TestNames.Count;
        var done = 0;
        foreach (var provider in providers)
        {
            var times = new List<double>();
            foreach (var name in TestNames)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (await probe.MeasureAsync(provider.Address, name, TimeSpan.FromSeconds(1.5), cancellationToken).ConfigureAwait(false) is { } ms)
                {
                    times.Add(ms);
                }

                progress?.Report(++done / (double)total);
            }

            results.Add(Summarize(provider, times, TestNames.Count));
        }

        return Rank(results);
    }

    public static DnsBenchmarkResult Summarize(DnsProvider provider, IReadOnlyList<double> times, int sent)
    {
        if (times.Count == 0)
        {
            return new DnsBenchmarkResult(provider, null, null, 0, sent);
        }

        var sorted = times.OrderBy(t => t).ToArray();
        var median = sorted.Length % 2 == 1 ? sorted[sorted.Length / 2] : (sorted[(sorted.Length / 2) - 1] + sorted[sorted.Length / 2]) / 2;
        return new DnsBenchmarkResult(provider, median, times.Average(), times.Count, sent);
    }

    /// <summary>Fastest reachable first (by median), unreachable last.</summary>
    public static IReadOnlyList<DnsBenchmarkResult> Rank(IEnumerable<DnsBenchmarkResult> results)
        => results.OrderBy(r => r.Reachable ? 0 : 1).ThenBy(r => r.MedianMs ?? double.MaxValue).ToList();
}
