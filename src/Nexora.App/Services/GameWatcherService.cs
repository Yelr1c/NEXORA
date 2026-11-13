using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexora.Core.Games;
using Nexora.Core.Monitoring;
using Nexora.Core.Processes;
using Nexora.Core.Settings;

namespace Nexora.App.Services;

/// <summary>
/// Smart game detection: notices when a game from your profiles starts, points FPS capture at it, applies the
/// profile's priority and (optionally) Game Boost — and undoes all of that when the game closes.
/// Polls the process list every 5 seconds (a single cheap snapshot, no hooks or injection).
/// </summary>
public sealed class GameWatcherService(
    GameProfileStore store,
    IFrameRateSource frames,
    IProcessService processes,
    GameBoostService boost,
    NotificationService notifications,
    ISettingsService settings,
    UiDispatcher dispatcher,
    ILogger<GameWatcherService> logger) : IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ProfileRefresh = TimeSpan.FromSeconds(30);
    private CancellationTokenSource? _cts;
    private Dictionary<string, GameProfile> _index = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _indexLoaded = DateTimeOffset.MinValue;
    private bool _startedBoost;

    public event EventHandler? Changed;

    public GameProfile? CurrentGame { get; private set; }

    public int? CurrentProcessId { get; private set; }

    public void Start()
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        Task.Run(() => LoopAsync(token), CancellationToken.None);
    }

    public void Stop()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Forces the profile list to be re-read (after editing game profiles).</summary>
    public void InvalidateProfiles() => _indexLoaded = DateTimeOffset.MinValue;

    private async Task LoopAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                if (settings.Current.SmartGameDetection)
                {
                    await PollAsync().ConfigureAwait(false);
                }
                else if (CurrentGame is not null)
                {
                    await EndSessionAsync().ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Game detection poll failed");
            }

            try
            {
                await Task.Delay(PollInterval, token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task PollAsync()
    {
        if (DateTimeOffset.UtcNow - _indexLoaded > ProfileRefresh)
        {
            _index = GameProcessMatcher.BuildIndex(await store.LoadAsync().ConfigureAwait(false));
            _indexLoaded = DateTimeOffset.UtcNow;
        }

        if (CurrentProcessId is { } pid)
        {
            if (IsRunning(pid))
            {
                return;
            }

            await EndSessionAsync().ConfigureAwait(false);
        }

        if (_index.Count == 0)
        {
            return;
        }

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (_index.TryGetValue(process.ProcessName, out var profile))
                {
                    await BeginSessionAsync(profile, process.Id).ConfigureAwait(false);
                    return;
                }
            }
        }
    }

    private async Task BeginSessionAsync(GameProfile profile, int processId)
    {
        CurrentGame = profile;
        CurrentProcessId = processId;
        logger.LogInformation("Game detected: {Game}", profile.Name);

        frames.TargetProcessId = processId;
        if (settings.Current.FpsCaptureEnabled)
        {
            frames.TryStart();
        }

        if (profile.Priority is not ProcessPriorityLevel.Normal and not ProcessPriorityLevel.Unknown)
        {
            var result = processes.SetPriority(processId, profile.Priority);
            logger.LogInformation("Game priority: {Message}", result.Message);
        }

        _startedBoost = false;
        if (settings.Current.AutoBoostOnGameStart && settings.Current.BoostExplained && !boost.IsActive)
        {
            var tcs = new TaskCompletionSource<bool>();
            dispatcher.Post(() => boost.StartAsync().ContinueWith(t => tcs.TrySetResult(t.IsCompletedSuccessfully && t.Result), TaskScheduler.Default));
            _startedBoost = await tcs.Task.ConfigureAwait(false);
        }

        dispatcher.Post(() =>
        {
            notifications.Show($"{profile.Name} detected", _startedBoost
                ? "Game Boost is on and FPS monitoring is following your game."
                : "FPS monitoring is following your game.");
            Changed?.Invoke(this, EventArgs.Empty);
        });
    }

    private async Task EndSessionAsync()
    {
        var game = CurrentGame;
        CurrentGame = null;
        CurrentProcessId = null;
        frames.TargetProcessId = null;
        if (_startedBoost && boost.IsActive)
        {
            var tcs = new TaskCompletionSource();
            dispatcher.Post(() => boost.StopAsync().ContinueWith(_ => tcs.TrySetResult(), TaskScheduler.Default));
            await tcs.Task.ConfigureAwait(false);
        }

        _startedBoost = false;
        logger.LogInformation("Game session ended: {Game}", game?.Name);
        dispatcher.Post(() => Changed?.Invoke(this, EventArgs.Empty));
    }

    private static bool IsRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose() => Stop();
}
