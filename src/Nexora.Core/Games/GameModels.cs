using Microsoft.Extensions.Logging;
using Nexora.Core.Processes;
using Nexora.Core.Storage;

namespace Nexora.Core.Games;

public enum GameSource
{
    Custom,
    Steam,
    Epic,
    Gog,
    Riot,
    Other,
}

public enum GameOptimizationProfile
{
    /// <summary>Launch without changing anything.</summary>
    None,

    /// <summary>Game Mode, windowed optimizations and background recording (no admin).</summary>
    Balanced,

    /// <summary>Balanced plus high performance power mode while the game runs.</summary>
    Competitive,
}

public sealed class GameProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public string ExecutablePath { get; set; } = string.Empty;

    public string LaunchArguments { get; set; } = string.Empty;

    /// <summary>Optional store launch URI (validated; only steam://rungameid/&lt;id&gt; and Epic launch URIs are accepted).</summary>
    public string? LaunchUri { get; set; }

    /// <summary>Executable name of the actual game process (when launched through a launcher).</summary>
    public string? ProcessName { get; set; }

    public GameSource Source { get; set; } = GameSource.Custom;

    public string? StoreId { get; set; }

    public GameOptimizationProfile OptimizationProfile { get; set; } = GameOptimizationProfile.Balanced;

    public ProcessPriorityLevel Priority { get; set; } = ProcessPriorityLevel.Normal;

    public bool MonitorPerformance { get; set; } = true;

    public bool RevertOptimizationsOnExit { get; set; } = true;

    public bool IsDetected { get; set; }

    public DateTimeOffset? LastPlayed { get; set; }

    public string EffectiveProcessName => !string.IsNullOrWhiteSpace(ProcessName)
        ? Path.GetFileNameWithoutExtension(ProcessName)
        : Path.GetFileNameWithoutExtension(ExecutablePath);
}

public sealed record DetectedGame(string Name, GameSource Source, string? ExecutablePath, string InstallDirectory, string? StoreId, string? LaunchUri, string? LaunchArguments);

public interface IGameDetector
{
    IReadOnlyList<DetectedGame> Detect();
}

public sealed class GameProfileStore(string path, ILogger<GameProfileStore> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public async Task<List<GameProfile>> LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            return await JsonFile.ReadAsync<List<GameProfile>>(path, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            logger.LogError(ex, "Game profiles could not be read");
            return [];
        }
    }

    public async Task SaveAsync(IReadOnlyList<GameProfile> profiles, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await JsonFile.WriteAsync(path, profiles, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Adds newly detected games without overwriting user customisations.</summary>
    public static List<GameProfile> Merge(IReadOnlyList<GameProfile> existing, IReadOnlyList<DetectedGame> detected)
    {
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(detected);
        var result = existing.ToList();
        foreach (var game in detected)
        {
            var match = result.FirstOrDefault(p =>
                (p.Source == game.Source && game.StoreId is not null && p.StoreId == game.StoreId)
                || (!string.IsNullOrEmpty(game.ExecutablePath) && string.Equals(p.ExecutablePath, game.ExecutablePath, StringComparison.OrdinalIgnoreCase)));
            if (match is not null)
            {
                match.IsDetected = true;
                if (string.IsNullOrEmpty(match.ExecutablePath) && game.ExecutablePath is not null)
                {
                    match.ExecutablePath = game.ExecutablePath;
                }

                continue;
            }

            result.Add(new GameProfile
            {
                Name = game.Name,
                Source = game.Source,
                StoreId = game.StoreId,
                ExecutablePath = game.ExecutablePath ?? string.Empty,
                LaunchUri = game.LaunchUri,
                LaunchArguments = game.LaunchArguments ?? string.Empty,
                IsDetected = true,
            });
        }

        return result.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }
}
