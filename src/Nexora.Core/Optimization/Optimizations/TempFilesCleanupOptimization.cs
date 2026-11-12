using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Nexora.Core.Optimization.Optimizations;

/// <summary>
/// Moves old files from the user's temp folder into a NEXORA quarantine. Undo moves them back.
/// Quarantined files are permanently deleted when the undo window expires (or when the user purges them).
/// </summary>
public sealed class TempFilesCleanupOptimization : OptimizationBase
{
    public const string OptimizationId = "storage.temp-files";
    public const long CleanThresholdBytes = 50L * 1024 * 1024;
    internal const string QuarantineKey = "quarantinePath";
    internal const string ManifestFileName = "manifest.jsonl";
    private const int MaxFilesPerRun = 200_000;

    private readonly IReadOnlyList<string> _sourceDirectories;
    private readonly string _quarantineRoot;
    private readonly TimeSpan _minimumAge;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;

    public TempFilesCleanupOptimization(IReadOnlyList<string> sourceDirectories, string quarantineRoot, TimeSpan minimumAge, ILogger logger, TimeProvider? time = null)
    {
        _sourceDirectories = sourceDirectories.Where(d => !string.IsNullOrWhiteSpace(d)).Select(Path.GetFullPath).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        _quarantineRoot = Path.GetFullPath(quarantineRoot);
        _minimumAge = minimumAge;
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    public override string Id => OptimizationId;

    public override string Name => "Clean temporary files";

    public override string Description => "Remove temporary files older than 24 hours from your user temp folder.";

    public override string WhatItChanges => "Moves files older than 24 hours out of your temp folder (%TEMP%) into a NEXORA quarantine folder. Files in use are skipped. System folders are never touched.";

    public override string WhyItHelps => "Applications often leave installers, caches and logs behind in the temp folder. Removing them frees disk space; low free space on the system drive can slow Windows and game updates.";

    public override string ExpectedBenefit => "Frees disk space. Files stay restorable for 7 days via Undo; space is released when they are purged.";

    public override OptimizationCategory Category => OptimizationCategory.Storage;

    public override OptimizationKind Kind => OptimizationKind.Action;

    public override bool IsQuickOptimizeCandidate => true;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
        => Task.Run(() =>
        {
            var (count, bytes) = Measure(cancellationToken);
            var description = $"{FormatBytes(bytes)} in {count.ToString("N0", CultureInfo.CurrentCulture)} files";
            return new OptimizationState(bytes < CleanThresholdBytes ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, description);
        }, cancellationToken);

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        backup.Properties[QuarantineKey] = Path.Combine(_quarantineRoot, backup.Id);
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
        => Task.Run(() => ApplyCore(backup, cancellationToken), cancellationToken);

    private void ApplyCore(OptimizationBackup backup, CancellationToken cancellationToken)
    {
        var quarantine = GetQuarantinePath(backup);
        Directory.CreateDirectory(quarantine);
        var manifestPath = Path.Combine(quarantine, ManifestFileName);
        using var manifest = new StreamWriter(new FileStream(manifestPath, FileMode.Append, FileAccess.Write, FileShare.Read));
        long moved = 0;
        long bytes = 0;
        var index = 0;

        foreach (var file in EnumerateEligible(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var target = Path.Combine(quarantine, index.ToString("D7", CultureInfo.InvariantCulture) + ".bin");
            try
            {
                var length = file.Length;
                // Record before moving so a crash never loses track of a moved file.
                manifest.WriteLine(JsonSerializer.Serialize(new QuarantineRecord(file.FullName, target)));
                manifest.Flush();
                File.Move(file.FullName, target);
                moved++;
                bytes += length;
                index++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // File is in use or protected: skip it.
            }
        }

        backup.Properties["movedFiles"] = moved.ToString(CultureInfo.InvariantCulture);
        backup.Properties["movedBytes"] = bytes.ToString(CultureInfo.InvariantCulture);
        _logger.LogInformation("Temp cleanup quarantined {Count} files ({Bytes} bytes)", moved, bytes);
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
        => Task.Run(() => RestoreCore(backup), cancellationToken);

    private void RestoreCore(OptimizationBackup backup)
    {
        var quarantine = GetQuarantinePath(backup);
        var manifestPath = Path.Combine(quarantine, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return;
        }

        var failures = 0;
        foreach (var line in File.ReadLines(manifestPath))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var record = JsonSerializer.Deserialize<QuarantineRecord>(line);
            if (record is null || !File.Exists(record.QuarantinePath))
            {
                continue;
            }

            // Only ever restore into the directories we cleaned.
            if (!IsUnderSource(record.OriginalPath) || !IsUnder(record.QuarantinePath, quarantine))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(record.OriginalPath)!);
                if (File.Exists(record.OriginalPath))
                {
                    // A newer file now exists at the original path; keep it and drop the stale copy.
                    File.Delete(record.QuarantinePath);
                }
                else
                {
                    File.Move(record.QuarantinePath, record.OriginalPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                failures++;
                _logger.LogWarning(ex, "A quarantined file could not be restored");
            }
        }

        if (failures > 0)
        {
            throw new IOException($"{failures} file(s) could not be restored. They remain in the quarantine folder.");
        }

        Directory.Delete(quarantine, recursive: true);
    }

    private string GetQuarantinePath(OptimizationBackup backup)
    {
        if (!backup.Properties.TryGetValue(QuarantineKey, out var path) || !IsUnder(path, _quarantineRoot))
        {
            throw new InvalidOperationException("The backup does not reference a valid quarantine folder.");
        }

        return Path.GetFullPath(path);
    }

    private (long Count, long Bytes) Measure(CancellationToken cancellationToken)
    {
        long count = 0;
        long bytes = 0;
        foreach (var file in EnumerateEligible(cancellationToken))
        {
            count++;
            try
            {
                bytes += file.Length;
            }
            catch (IOException)
            {
                // File vanished.
            }
        }

        return (count, bytes);
    }

    private IEnumerable<FileInfo> EnumerateEligible(CancellationToken cancellationToken)
    {
        var cutoff = _time.GetUtcNow().UtcDateTime - _minimumAge;
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.System | FileAttributes.ReparsePoint | FileAttributes.Offline,
            ReturnSpecialDirectories = false,
        };
        var yielded = 0;
        foreach (var directory in _sourceDirectories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<FileInfo> files;
            try
            {
                files = new DirectoryInfo(directory).EnumerateFiles("*", options);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (yielded >= MaxFilesPerRun)
                {
                    yield break;
                }

                DateTime lastWrite;
                DateTime lastAccess;
                try
                {
                    lastWrite = file.LastWriteTimeUtc;
                    lastAccess = file.LastAccessTimeUtc;
                }
                catch (IOException)
                {
                    continue;
                }

                if (lastWrite > cutoff || lastAccess > cutoff || IsUnder(file.FullName, _quarantineRoot))
                {
                    continue;
                }

                yielded++;
                yield return file;
            }
        }
    }

    private bool IsUnderSource(string path) => _sourceDirectories.Any(d => IsUnder(path, d));

    internal static bool IsUnder(string path, string directory)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return full.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    public static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0 ? $"{bytes} B" : $"{value.ToString("0.#", CultureInfo.CurrentCulture)} {units[unit]}";
    }

    internal sealed record QuarantineRecord(string OriginalPath, string QuarantinePath);
}

/// <summary>Permanently deletes expired temp-file quarantines and marks their history entries as committed.</summary>
public sealed class QuarantineService(string quarantineRoot, IOptimizationHistoryStore history, IOptimizationBackupStore backups, ILogger<QuarantineService> logger, TimeProvider? time = null)
{
    public static readonly TimeSpan RetentionPeriod = TimeSpan.FromDays(7);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public async Task<int> PurgeExpiredAsync(CancellationToken cancellationToken = default)
    {
        var entries = await history.GetAllAsync(cancellationToken).ConfigureAwait(false);
        var now = _time.GetUtcNow();
        var purged = 0;
        foreach (var entry in entries.Where(e => e.OptimizationId == TempFilesCleanupOptimization.OptimizationId && e.Status == HistoryStatus.Applied && now - e.Timestamp > RetentionPeriod))
        {
            if (await PurgeAsync(entry, cancellationToken).ConfigureAwait(false))
            {
                purged++;
            }
        }

        return purged;
    }

    /// <summary>Permanently deletes the quarantined files of one cleanup run.</summary>
    public async Task<bool> PurgeAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (entry.OptimizationId != TempFilesCleanupOptimization.OptimizationId || entry.Status != HistoryStatus.Applied || entry.BackupId is null)
        {
            return false;
        }

        var backup = await backups.LoadAsync(entry.BackupId, cancellationToken).ConfigureAwait(false);
        if (backup is not null && backup.Properties.TryGetValue(TempFilesCleanupOptimization.QuarantineKey, out var path)
            && TempFilesCleanupOptimization.IsUnder(path, quarantineRoot) && Directory.Exists(path))
        {
            try
            {
                await Task.Run(() => Directory.Delete(path, recursive: true), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Quarantine folder could not be fully deleted");
                return false;
            }
        }

        entry.Status = HistoryStatus.Committed;
        entry.Message = "Quarantined files were permanently deleted.";
        await history.UpdateAsync(entry, cancellationToken).ConfigureAwait(false);
        return true;
    }
}
