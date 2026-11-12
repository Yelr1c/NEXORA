using Microsoft.Extensions.Logging;
using Nexora.Core.Platform;

namespace Nexora.Core.Optimization;

/// <summary>
/// Applies optimizations transactionally:
/// state check → backup persisted to disk → apply → verify → history. Any failure restores the backup.
/// </summary>
public sealed class OptimizationEngine
{
    private readonly IOptimizationBackupStore _backups;
    private readonly IOptimizationHistoryStore _history;
    private readonly IPrivilegeService _privileges;
    private readonly IOptimizationCatalog _catalog;
    private readonly ILogger<OptimizationEngine> _logger;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public OptimizationEngine(
        IOptimizationBackupStore backups,
        IOptimizationHistoryStore history,
        IPrivilegeService privileges,
        IOptimizationCatalog catalog,
        ILogger<OptimizationEngine> logger,
        TimeProvider? timeProvider = null)
    {
        _backups = backups;
        _history = history;
        _privileges = privileges;
        _catalog = catalog;
        _logger = logger;
        _time = timeProvider ?? TimeProvider.System;
    }

    public IOptimizationHistoryStore History => _history;

    public async Task<OptimizationResult> ApplyAsync(IOptimization optimization, CancellationToken cancellationToken = default)
    {
        var batch = await ApplyAsync([optimization], rollbackBatchOnFailure: false, progress: null, cancellationToken).ConfigureAwait(false);
        return batch.Results[0];
    }

    /// <summary>Applies several optimizations. If <paramref name="rollbackBatchOnFailure"/> is set, any failure undoes the whole batch.</summary>
    public async Task<OptimizationBatchResult> ApplyAsync(
        IReadOnlyList<IOptimization> optimizations,
        bool rollbackBatchOnFailure,
        IProgress<(int Completed, int Total, string Current)>? progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(optimizations);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var results = new List<OptimizationResult>(optimizations.Count);
            var appliedInBatch = new List<(IOptimization Optimization, OptimizationHistoryEntry Entry, OptimizationBackup Backup)>();
            var rolledBack = false;

            for (var i = 0; i < optimizations.Count; i++)
            {
                var optimization = optimizations[i];
                progress?.Report((i, optimizations.Count, optimization.Name));
                var (result, entry, backup) = await ApplyCoreAsync(optimization, cancellationToken).ConfigureAwait(false);
                results.Add(result);

                if (entry is not null && backup is not null)
                {
                    appliedInBatch.Add((optimization, entry, backup));
                }

                var hardFailure = result.Outcome is OptimizationOutcome.FailedAndRolledBack or OptimizationOutcome.FailedRollbackFailed;
                if (hardFailure && rollbackBatchOnFailure && appliedInBatch.Count > 0)
                {
                    _logger.LogWarning("Optimization {OptimizationId} failed; rolling back {Count} optimizations applied in this batch", optimization.Id, appliedInBatch.Count);
                    for (var j = appliedInBatch.Count - 1; j >= 0; j--)
                    {
                        var (opt, appliedEntry, appliedBackup) = appliedInBatch[j];
                        var undo = await RevertCoreAsync(opt, appliedEntry, appliedBackup, HistoryStatus.RolledBack, "Rolled back because another optimization in the batch failed", cancellationToken).ConfigureAwait(false);
                        var index = results.FindIndex(r => r.OptimizationId == opt.Id);
                        if (index >= 0)
                        {
                            results[index] = undo with { Message = undo.IsSuccess ? "Rolled back (batch failed)" : undo.Message };
                        }
                    }

                    rolledBack = true;
                    break;
                }
            }

            progress?.Report((optimizations.Count, optimizations.Count, string.Empty));
            return new OptimizationBatchResult(results, rolledBack);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Undoes a previously applied history entry using its stored backup.</summary>
    public async Task<OptimizationResult> UndoAsync(string historyEntryId, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var all = await _history.GetAllAsync(cancellationToken).ConfigureAwait(false);
            var entry = all.FirstOrDefault(e => e.Id == historyEntryId);
            if (entry is null)
            {
                return new OptimizationResult(string.Empty, string.Empty, OptimizationOutcome.NotFound, "History entry not found.");
            }

            if (!CanUndo(entry, all))
            {
                return new OptimizationResult(entry.OptimizationId, entry.OptimizationName, OptimizationOutcome.NotAllowed,
                    "Only the most recent active change of an optimization can be undone.");
            }

            var optimization = _catalog.Find(entry.OptimizationId);
            if (optimization is null)
            {
                return new OptimizationResult(entry.OptimizationId, entry.OptimizationName, OptimizationOutcome.NotFound, "This optimization is no longer available on this system.");
            }

            if (optimization.RequiresAdministrator && !_privileges.IsElevated)
            {
                return new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.RequiresElevation,
                    "Undoing this change requires administrator rights. Restart NEXORA as administrator.");
            }

            var backup = entry.BackupId is null ? null : await _backups.LoadAsync(entry.BackupId, cancellationToken).ConfigureAwait(false);
            if (backup is null)
            {
                return new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.NotFound, "The backup for this change could not be found.");
            }

            return await RevertCoreAsync(optimization, entry, backup, HistoryStatus.Reverted, null, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Reverts every active change, newest first (restore the configuration that existed before NEXORA).</summary>
    public async Task<IReadOnlyList<OptimizationResult>> RestoreAllAsync(CancellationToken cancellationToken = default)
    {
        var all = await _history.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var active = all.Where(e => e.Status == HistoryStatus.Applied).OrderByDescending(e => e.Timestamp).ToList();
        var results = new List<OptimizationResult>();
        foreach (var entry in active)
        {
            results.Add(await UndoAsync(entry.Id, cancellationToken).ConfigureAwait(false));
        }

        return results;
    }

    /// <summary>An entry can be undone if it is applied and is the newest applied entry for that optimization.</summary>
    public static bool CanUndo(OptimizationHistoryEntry entry, IEnumerable<OptimizationHistoryEntry> all)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.Status != HistoryStatus.Applied || entry.BackupId is null)
        {
            return false;
        }

        var newest = all.Where(e => e.OptimizationId == entry.OptimizationId && e.Status == HistoryStatus.Applied)
            .OrderByDescending(e => e.Timestamp)
            .FirstOrDefault();
        return newest?.Id == entry.Id;
    }

    private async Task<(OptimizationResult Result, OptimizationHistoryEntry? Entry, OptimizationBackup? Backup)> ApplyCoreAsync(IOptimization optimization, CancellationToken cancellationToken)
    {
        if (optimization.RequiresAdministrator && !_privileges.IsElevated)
        {
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.RequiresElevation,
                "This change requires administrator rights. Restart NEXORA as administrator to apply it."), null, null);
        }

        OptimizationState before;
        try
        {
            before = await optimization.GetStateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Could not read state of {OptimizationId}", optimization.Id);
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.Unavailable, "The current state could not be read: " + ex.Message), null, null);
        }

        if (before.Status == OptimizationStatus.Unavailable)
        {
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.Unavailable, before.UnavailableReason ?? "Unavailable on this system"), null, null);
        }

        if (before.Status == OptimizationStatus.Applied && optimization.Kind == OptimizationKind.Setting)
        {
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.AlreadyApplied, "Already configured."), null, null);
        }

        OptimizationBackup backup;
        try
        {
            backup = await optimization.CreateBackupAsync(cancellationToken).ConfigureAwait(false);
            backup.OptimizationId = optimization.Id;
            backup.CreatedAt = _time.GetUtcNow();
            backup.PreviousValue = before.CurrentValue;

            // The backup must be on disk before anything changes.
            await _backups.SaveAsync(backup, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Backup failed for {OptimizationId}; nothing was changed", optimization.Id);
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.Unavailable, "A backup could not be created, so nothing was changed: " + ex.Message), null, null);
        }

        try
        {
            await optimization.ApplyAsync(backup, cancellationToken).ConfigureAwait(false);
            await _backups.SaveAsync(backup, CancellationToken.None).ConfigureAwait(false);

            var after = await optimization.GetStateAsync(CancellationToken.None).ConfigureAwait(false);
            if (optimization.Kind == OptimizationKind.Setting && after.Status != OptimizationStatus.Applied)
            {
                throw new OptimizationVerificationException("Windows did not accept the new setting (verification failed).");
            }

            var entry = new OptimizationHistoryEntry
            {
                Timestamp = _time.GetUtcNow(),
                OptimizationId = optimization.Id,
                OptimizationName = optimization.Name,
                PreviousValue = before.CurrentValue,
                NewValue = after.CurrentValue,
                Status = HistoryStatus.Applied,
                BackupId = backup.Id,
                Message = optimization.RequiresRestart ? "Takes effect after restarting Windows." : null,
            };
            await _history.AddAsync(entry, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Applied optimization {OptimizationId}", optimization.Id);
            return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.Applied,
                optimization.RequiresRestart ? "Applied. Restart Windows for it to take effect." : "Applied successfully."), entry, backup);
        }
        catch (Exception applyError)
        {
            _logger.LogError(applyError, "Applying {OptimizationId} failed; restoring backup {BackupId}", optimization.Id, backup.Id);
            try
            {
                await optimization.RestoreAsync(backup, CancellationToken.None).ConfigureAwait(false);
                await _history.AddAsync(new OptimizationHistoryEntry
                {
                    Timestamp = _time.GetUtcNow(),
                    OptimizationId = optimization.Id,
                    OptimizationName = optimization.Name,
                    PreviousValue = before.CurrentValue,
                    NewValue = before.CurrentValue,
                    Status = HistoryStatus.Failed,
                    BackupId = backup.Id,
                    Message = "Failed and was rolled back: " + applyError.Message,
                }, CancellationToken.None).ConfigureAwait(false);
                var cancelled = applyError is OperationCanceledException;
                return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.FailedAndRolledBack,
                    cancelled ? "Cancelled. No changes were kept." : "Failed: " + applyError.Message + " The previous configuration was restored."), null, null);
            }
            catch (Exception rollbackError)
            {
                _logger.LogCritical(rollbackError, "Rollback of {OptimizationId} failed. Backup {BackupId} retained for manual restore", optimization.Id, backup.Id);
                // Record as Applied-equivalent failure so the user can retry the undo from history.
                await _history.AddAsync(new OptimizationHistoryEntry
                {
                    Timestamp = _time.GetUtcNow(),
                    OptimizationId = optimization.Id,
                    OptimizationName = optimization.Name,
                    PreviousValue = before.CurrentValue,
                    NewValue = "Unknown (rollback failed)",
                    Status = HistoryStatus.Applied,
                    BackupId = backup.Id,
                    Message = "Apply failed and automatic rollback failed. Use Undo to retry the restore.",
                }, CancellationToken.None).ConfigureAwait(false);
                return (new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.FailedRollbackFailed,
                    "Failed, and the automatic restore also failed. The backup was kept; use Undo in History to retry."), null, null);
            }
        }
    }

    private async Task<OptimizationResult> RevertCoreAsync(
        IOptimization optimization,
        OptimizationHistoryEntry entry,
        OptimizationBackup backup,
        HistoryStatus newStatus,
        string? message,
        CancellationToken cancellationToken)
    {
        try
        {
            await optimization.RestoreAsync(backup, cancellationToken).ConfigureAwait(false);
            entry.Status = newStatus;
            entry.RevertedAt = _time.GetUtcNow();
            entry.Message = message ?? "Reverted to the previous configuration.";
            await _history.UpdateAsync(entry, CancellationToken.None).ConfigureAwait(false);
            _logger.LogInformation("Reverted optimization {OptimizationId}", optimization.Id);
            return new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.Reverted, "Restored the previous configuration.");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Reverting {OptimizationId} failed", optimization.Id);
            return new OptimizationResult(optimization.Id, optimization.Name, OptimizationOutcome.FailedRollbackFailed, "Restore failed: " + ex.Message);
        }
    }
}
