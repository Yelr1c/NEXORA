using Microsoft.Extensions.Logging;
using Nexora.Core.Storage;

namespace Nexora.Core.Optimization;

public interface IOptimizationBackupStore
{
    Task SaveAsync(OptimizationBackup backup, CancellationToken cancellationToken = default);

    Task<OptimizationBackup?> LoadAsync(string backupId, CancellationToken cancellationToken = default);

    Task DeleteAsync(string backupId, CancellationToken cancellationToken = default);
}

public interface IOptimizationHistoryStore
{
    Task<IReadOnlyList<OptimizationHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    Task AddAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default);

    Task UpdateAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default);

    event EventHandler? Changed;
}

/// <summary>One JSON file per backup in the Backups directory.</summary>
public sealed class FileBackupStore : IOptimizationBackupStore
{
    private readonly string _directory;

    public FileBackupStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        _directory = directory;
    }

    public Task SaveAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        return JsonFile.WriteAsync(PathFor(backup.Id), backup, cancellationToken);
    }

    public Task<OptimizationBackup?> LoadAsync(string backupId, CancellationToken cancellationToken = default)
        => JsonFile.ReadAsync<OptimizationBackup>(PathFor(backupId), cancellationToken);

    public Task DeleteAsync(string backupId, CancellationToken cancellationToken = default)
    {
        var path = PathFor(backupId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }

        return Task.CompletedTask;
    }

    private string PathFor(string backupId)
    {
        // Backup ids are GUIDs in "N" format; reject anything else to prevent path traversal.
        if (string.IsNullOrEmpty(backupId) || backupId.Length != 32 || !backupId.All(Uri.IsHexDigit))
        {
            throw new ArgumentException("Invalid backup id.", nameof(backupId));
        }

        return Path.Combine(_directory, backupId + ".json");
    }
}

public sealed class FileHistoryStore : IOptimizationHistoryStore
{
    private const int MaxEntries = 1000;
    private readonly string _path;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private List<OptimizationHistoryEntry>? _cache;

    public FileHistoryStore(string path, ILogger<FileHistoryStore> logger)
    {
        _path = path;
        _logger = logger;
    }

    public event EventHandler? Changed;

    public async Task<IReadOnlyList<OptimizationHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            return list.OrderByDescending(e => e.Timestamp).ToList();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task AddAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            list.Add(entry);
            if (list.Count > MaxEntries)
            {
                // Never drop entries that can still be undone.
                var removable = list.Where(e => e.Status != HistoryStatus.Applied).OrderBy(e => e.Timestamp).Take(list.Count - MaxEntries).ToList();
                foreach (var r in removable)
                {
                    list.Remove(r);
                }
            }

            await JsonFile.WriteAsync(_path, list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var list = await LoadUnsafeAsync(cancellationToken).ConfigureAwait(false);
            var index = list.FindIndex(e => e.Id == entry.Id);
            if (index < 0)
            {
                list.Add(entry);
            }
            else
            {
                list[index] = entry;
            }

            await JsonFile.WriteAsync(_path, list, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task<List<OptimizationHistoryEntry>> LoadUnsafeAsync(CancellationToken cancellationToken)
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            _cache = await JsonFile.ReadAsync<List<OptimizationHistoryEntry>>(_path, cancellationToken).ConfigureAwait(false) ?? [];
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "Optimization history could not be read; preserving the damaged file and starting a new history");
            TryPreserveCorruptFile();
            _cache = [];
        }

        return _cache;
    }

    private void TryPreserveCorruptFile()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
            }
        }
        catch (IOException)
        {
            // Best effort only.
        }
    }
}
