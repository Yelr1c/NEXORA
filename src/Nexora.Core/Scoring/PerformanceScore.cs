namespace Nexora.Core.Scoring;

/// <summary>Measured inputs. Null = not measured / unavailable; its weight is redistributed.</summary>
public sealed record ScoreInputs
{
    public double? CpuUsagePercent { get; init; }

    public double? GpuUsagePercent { get; init; }

    public double? RamUsagePercent { get; init; }

    public double? SystemDriveFreePercent { get; init; }

    public double? PingMs { get; init; }

    public double? JitterMs { get; init; }

    public double? PacketLossPercent { get; init; }

    public int? RecommendedSettingsTotal { get; init; }

    public int? RecommendedSettingsApplied { get; init; }

    public int? EnabledStartupApps { get; init; }

    public double? MaxTemperatureCelsius { get; init; }
}

public sealed record ScoreComponent(string Key, string Name, double Score, double Weight, string Detail);

public sealed record ScoreResult(int Score, string Rating, IReadOnlyList<ScoreComponent> Components)
{
    public static string RatingFor(int score) => score switch
    {
        >= 90 => "Excellent",
        >= 75 => "Good",
        >= 60 => "Fair",
        _ => "Needs attention",
    };
}

/// <summary>
/// The NEXORA Performance Score: a 0–100 health indicator derived only from measured values.
/// It describes how much headroom and how well-configured the system is — it is not an FPS prediction.
/// </summary>
public static class PerformanceScoreCalculator
{
    public static ScoreResult Calculate(ScoreInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        var components = new List<ScoreComponent>();

        if (inputs.CpuUsagePercent is { } cpu)
        {
            components.Add(new("cpu", "CPU headroom", Linear(cpu, 15, 95), 0.18, $"{cpu:0}% CPU load"));
        }

        if (inputs.GpuUsagePercent is { } gpu)
        {
            components.Add(new("gpu", "GPU headroom", Linear(gpu, 10, 90), 0.10, $"{gpu:0}% GPU load"));
        }

        if (inputs.RamUsagePercent is { } ram)
        {
            components.Add(new("ram", "Memory", Linear(ram, 50, 95), 0.15, $"{ram:0}% memory in use"));
        }

        if (inputs.SystemDriveFreePercent is { } free)
        {
            components.Add(new("storage", "Storage", Linear(-free, -25, -5), 0.12, $"{free:0}% free on system drive"));
        }

        if (inputs.PingMs is { } ping)
        {
            var pingScore = Linear(ping, 20, 150);
            var jitterScore = Linear(inputs.JitterMs ?? 0, 2, 30);
            var lossScore = Linear(inputs.PacketLossPercent ?? 0, 0, 5);
            components.Add(new("network", "Network", (pingScore * 0.5) + (jitterScore * 0.25) + (lossScore * 0.25), 0.15,
                $"{ping:0} ms ping, {inputs.JitterMs ?? 0:0} ms jitter, {inputs.PacketLossPercent ?? 0:0.#}% loss"));
        }
        else if (inputs.PacketLossPercent is >= 100)
        {
            components.Add(new("network", "Network", 0, 0.15, "No response from the internet"));
        }

        if (inputs.RecommendedSettingsTotal is { } total && total > 0 && inputs.RecommendedSettingsApplied is { } applied)
        {
            components.Add(new("config", "Gaming configuration", 100.0 * Math.Clamp(applied, 0, total) / total, 0.15,
                $"{applied} of {total} recommended settings active"));
        }

        if (inputs.EnabledStartupApps is { } startup)
        {
            components.Add(new("startup", "Startup load", Linear(startup, 5, 30), 0.08, $"{startup} apps start with Windows"));
        }

        if (inputs.MaxTemperatureCelsius is { } temp)
        {
            components.Add(new("thermal", "Thermals", Linear(temp, 70, 95), 0.07, $"{temp:0} °C"));
        }

        if (components.Count == 0)
        {
            return new ScoreResult(0, "Unavailable", components);
        }

        var weightSum = components.Sum(c => c.Weight);
        var score = components.Sum(c => c.Score * c.Weight) / weightSum;
        var rounded = (int)Math.Round(Math.Clamp(score, 0, 100), MidpointRounding.AwayFromZero);
        return new ScoreResult(rounded, ScoreResult.RatingFor(rounded), components);
    }

    /// <summary>100 at or below <paramref name="best"/>, 0 at or above <paramref name="worst"/>, linear in between.</summary>
    public static double Linear(double value, double best, double worst)
    {
        if (value <= best)
        {
            return 100;
        }

        if (value >= worst)
        {
            return 0;
        }

        return 100 * (worst - value) / (worst - best);
    }
}
