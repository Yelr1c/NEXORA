using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Platform;
using Nexora.Core.Settings;

namespace Nexora.App.Services;

/// <summary>UI-side orchestration of optimizations: explanation + confirmation → engine → result reporting.</summary>
public sealed class OptimizationCoordinator(
    OptimizationEngine engine,
    IOptimizationHistoryStore history,
    QuarantineService quarantine,
    ISettingsService settings,
    IPrivilegeService privileges,
    DialogService dialogs,
    LiveScoreService score)
{
    public event EventHandler? Changed;

    public IOptimizationHistoryStore History => history;

    public bool IsElevated => privileges.IsElevated;

    public static string RiskText(RiskLevel risk) => risk switch
    {
        RiskLevel.Low => "Low risk",
        RiskLevel.Medium => "Medium risk",
        _ => "High risk",
    };

    public static string ImpactText(ImpactLevel impact) => impact switch
    {
        ImpactLevel.High => "High impact",
        ImpactLevel.Medium => "Medium impact",
        _ => "Low impact",
    };

    public static string CategoryText(OptimizationCategory category) => category switch
    {
        OptimizationCategory.BackgroundProcesses => "Background processes",
        _ => category.ToString(),
    };

    /// <summary>Explains every change and asks for confirmation (unless disabled), then applies transactionally.</summary>
    public async Task<OptimizationBatchResult?> ApplyAsync(IReadOnlyList<IOptimization> items, bool forceConfirm = false)
    {
        if (items.Count == 0)
        {
            return null;
        }

        var needsAdmin = items.Where(i => i.RequiresAdministrator && !privileges.IsElevated).ToList();
        if (needsAdmin.Count == items.Count)
        {
            await dialogs.InformAsync("Administrator rights required",
                $"\"{needsAdmin[0].Name}\" changes a machine-wide Windows setting, which Windows only allows for administrators.\n\nUse \"Restart as administrator\" in the title bar, then apply it again. NEXORA does not run elevated by default.");
            return null;
        }

        if (settings.Current.ConfirmBeforeApplying || forceConfirm || items.Any(i => i.Risk != RiskLevel.Low))
        {
            if (!await dialogs.ConfirmAsync(items.Count == 1 ? items[0].Name : $"Apply {items.Count} optimizations?", BuildExplanation(items), "Apply"))
            {
                return null;
            }
        }

        var result = await engine.ApplyAsync(items, settings.Current.RollbackBatchOnFailure, progress: null);
        score.InvalidateConfiguration();
        Changed?.Invoke(this, EventArgs.Empty);

        var failures = result.Results.Where(r => !r.IsSuccess).ToList();
        if (failures.Count > 0 || result.BatchRolledBack)
        {
            var text = string.Join("\n", failures.Select(f => $"• {f.Name}: {f.Message}"));
            if (result.BatchRolledBack)
            {
                text += "\n\nTo keep your system consistent, every change in this batch was rolled back.";
            }

            await dialogs.InformAsync(result.BatchRolledBack ? "Nothing was changed" : "Some optimizations were not applied", text);
        }
        else if (result.Results.Any(r => r.Message.Contains("Restart", StringComparison.OrdinalIgnoreCase)))
        {
            await dialogs.InformAsync("Restart required", "Some changes take effect after you restart Windows.");
        }

        return result;
    }

    public async Task<OptimizationResult> UndoAsync(string historyEntryId)
    {
        var result = await engine.UndoAsync(historyEntryId);
        score.InvalidateConfiguration();
        Changed?.Invoke(this, EventArgs.Empty);
        if (!result.IsSuccess)
        {
            await dialogs.InformAsync("Undo was not completed", result.Message);
        }

        return result;
    }

    /// <summary>Undoes the newest active change of a specific optimization.</summary>
    public async Task<OptimizationResult?> UndoLatestAsync(string optimizationId)
    {
        var entries = await history.GetAllAsync();
        var entry = entries.FirstOrDefault(e => e.OptimizationId == optimizationId && OptimizationEngine.CanUndo(e, entries));
        return entry is null ? null : await UndoAsync(entry.Id);
    }

    public async Task RestoreAllAsync()
    {
        if (!await dialogs.ConfirmAsync("Restore previous configuration?", "NEXORA will undo every active change it made, newest first, using the backups taken before each change.", "Restore all"))
        {
            return;
        }

        var results = await engine.RestoreAllAsync();
        score.InvalidateConfiguration();
        Changed?.Invoke(this, EventArgs.Empty);
        var failed = results.Where(r => !r.IsSuccess).ToList();
        await dialogs.InformAsync(failed.Count == 0 ? "Configuration restored" : "Restore partially completed",
            failed.Count == 0 ? $"{results.Count} change(s) were reverted." : string.Join("\n", failed.Select(f => $"• {f.Name}: {f.Message}")));
    }

    public async Task<bool> PurgeAsync(OptimizationHistoryEntry entry)
    {
        if (!await dialogs.ConfirmAsync("Permanently delete quarantined files?", "The temporary files from this cleanup will be deleted for good and the disk space released. This cleanup can no longer be undone.", "Delete permanently", destructive: true))
        {
            return false;
        }

        var ok = await quarantine.PurgeAsync(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return ok;
    }

    public async Task<bool> HasUndoableAsync(string optimizationId)
    {
        var entries = await history.GetAllAsync();
        return entries.Any(e => e.OptimizationId == optimizationId && OptimizationEngine.CanUndo(e, entries));
    }

    private UIElement BuildExplanation(IReadOnlyList<IOptimization> items)
    {
        var panel = new StackPanel { Spacing = 14, MaxWidth = 520 };
        foreach (var item in items.Take(6))
        {
            var block = new StackPanel { Spacing = 4 };
            block.Children.Add(new TextBlock { Text = item.Name, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.WrapWholeWords });
            block.Children.Add(Line("What changes", item.WhatItChanges));
            block.Children.Add(Line("Why it may help", item.WhyItHelps));
            block.Children.Add(Line("Expected benefit", item.ExpectedBenefit));
            var meta = $"{RiskText(item.Risk)} · Reversible from History";
            if (item.RequiresRestart)
            {
                meta += " · Restart required";
            }

            if (item.RequiresAdministrator && !privileges.IsElevated)
            {
                meta += " · Skipped: requires administrator";
            }

            block.Children.Add(new TextBlock { Text = meta, Opacity = 0.7, FontSize = 12 });
            panel.Children.Add(block);
        }

        if (items.Count > 6)
        {
            panel.Children.Add(new TextBlock { Text = $"…and {items.Count - 6} more.", Opacity = 0.7 });
        }

        panel.Children.Add(new TextBlock
        {
            Text = "A backup of every affected setting is saved before anything changes. If a change fails, it is rolled back automatically.",
            TextWrapping = TextWrapping.WrapWholeWords,
            FontSize = 12,
            Opacity = 0.7,
        });
        return new ScrollViewer { Content = panel, MaxHeight = 460 };
    }

    private static TextBlock Line(string label, string text)
    {
        var block = new TextBlock { TextWrapping = TextWrapping.WrapWholeWords, FontSize = 13 };
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
        block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = text });
        return block;
    }

    public static bool IsTempCleanup(OptimizationHistoryEntry entry) => entry.OptimizationId == TempFilesCleanupOptimization.OptimizationId;
}
