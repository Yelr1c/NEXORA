using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml.Controls;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Settings;

namespace Nexora.App.Services;

/// <summary>
/// One-click Game Boost: switches on the session-relevant gaming tweaks and remembers exactly which ones it
/// changed, so turning Boost off restores the previous configuration (even after a restart of NEXORA).
/// </summary>
public sealed class GameBoostService(
    StandardOptimizationCatalog catalog,
    OptimizationCoordinator coordinator,
    ISettingsService settings,
    NotificationService notifications,
    ILogger<GameBoostService> logger)
{
    public static readonly IReadOnlyList<string> BoostOptimizationIds =
    [
        StandardOptimizationCatalog.GameModeId,
        StandardOptimizationCatalog.GameDvrId,
        WindowedGamesOptimization.OptimizationId,
        VariableRefreshRateOptimization.OptimizationId,
        PowerPlanOptimization.OptimizationId,
    ];

    private readonly SemaphoreSlim _gate = new(1, 1);

    public event EventHandler? Changed;

    public bool IsActive => settings.Current.BoostActive;

    public int ActiveCount => settings.Current.BoostAppliedIds.Count;

    public async Task ToggleAsync()
    {
        if (IsActive)
        {
            await StopAsync();
        }
        else
        {
            await StartAsync();
        }
    }

    public async Task<bool> StartAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (IsActive)
            {
                return true;
            }

            var items = BoostOptimizationIds.Select(catalog.Find).OfType<IOptimization>().Where(o => !o.RequiresAdministrator || coordinator.IsElevated).ToList();
            var pending = new List<IOptimization>();
            foreach (var item in items)
            {
                try
                {
                    if ((await item.GetStateAsync()).Status == OptimizationStatus.NotApplied)
                    {
                        pending.Add(item);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Boost could not read {Id}", item.Id);
                }
            }

            if (!settings.Current.BoostExplained && pending.Count > 0)
            {
                var content = new StackPanel { Spacing = 12 };
                content.Children.Add(new TextBlock
                {
                    Text = "Game Boost switches on these gaming settings for your session. Turn Boost off at any time to restore exactly what you had before.",
                    TextWrapping = Microsoft.UI.Xaml.TextWrapping.WrapWholeWords,
                });
                content.Children.Add(coordinator.BuildExplanation(pending));
                if (!await coordinator.Dialogs.ConfirmAsync("Activate Game Boost?", content, "Boost"))
                {
                    return false;
                }

                await settings.UpdateAsync(s => s.BoostExplained = true);
            }

            var applied = new List<string>();
            if (pending.Count > 0)
            {
                var result = await coordinator.Engine.ApplyAsync(pending, rollbackBatchOnFailure: true, progress: null);
                if (result.BatchRolledBack)
                {
                    await coordinator.Dialogs.InformAsync("Game Boost could not start", string.Join("\n", result.Results.Where(r => !r.IsSuccess).Select(r => $"• {r.Name}: {r.Message}")));
                    coordinator.RaiseChanged();
                    return false;
                }

                applied.AddRange(result.Results.Where(r => r.Outcome == OptimizationOutcome.Applied).Select(r => r.OptimizationId));
            }

            await settings.UpdateAsync(s =>
            {
                s.BoostActive = true;
                s.BoostAppliedIds = applied;
            });
            coordinator.RaiseChanged();
            Changed?.Invoke(this, EventArgs.Empty);
            notifications.Show("Game Boost is on", applied.Count == 0 ? "Your PC was already set up for gaming." : $"{applied.Count} gaming setting(s) switched on. Turn Boost off to restore them.");
            logger.LogInformation("Game Boost on ({Count} changes)", applied.Count);
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (!IsActive)
            {
                return;
            }

            var history = await coordinator.History.GetAllAsync();
            foreach (var id in Enumerable.Reverse(settings.Current.BoostAppliedIds))
            {
                var entry = history.FirstOrDefault(e => e.OptimizationId == id && OptimizationEngine.CanUndo(e, history));
                if (entry is not null)
                {
                    var result = await coordinator.Engine.UndoAsync(entry.Id);
                    if (!result.IsSuccess)
                    {
                        logger.LogWarning("Boost could not restore {Id}: {Message}", id, result.Message);
                    }
                }
            }

            await settings.UpdateAsync(s =>
            {
                s.BoostActive = false;
                s.BoostAppliedIds = [];
            });
            coordinator.RaiseChanged();
            Changed?.Invoke(this, EventArgs.Empty);
            notifications.Show("Game Boost is off", "Your previous settings were restored.");
            logger.LogInformation("Game Boost off");
        }
        finally
        {
            _gate.Release();
        }
    }
}
