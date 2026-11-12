using Nexora.Core.Monitoring;
using Nexora.Core.Optimization;
using Nexora.Core.Platform;

namespace Nexora.Core.Tests;

internal sealed class InMemoryRegistry : IRegistryAccessor
{
    private readonly Dictionary<(RegistryRoot, string), Dictionary<string, RegistryValue>> _keys = new();

    public int WriteCount { get; private set; }

    public Func<string, bool>? FailWritesFor { get; set; }

    private static (RegistryRoot, string) K(RegistryRoot r, string p) => (r, p.ToLowerInvariant());

    public RegistryValue? GetValue(RegistryRoot root, string keyPath, string valueName)
        => _keys.TryGetValue(K(root, keyPath), out var values) && values.TryGetValue(valueName, out var v) ? v : null;

    public void SetValue(RegistryRoot root, string keyPath, string valueName, RegistryValue value)
    {
        if (FailWritesFor?.Invoke(valueName) == true)
        {
            throw new UnauthorizedAccessException("simulated");
        }

        WriteCount++;
        if (!_keys.TryGetValue(K(root, keyPath), out var values))
        {
            values = new Dictionary<string, RegistryValue>(StringComparer.OrdinalIgnoreCase);
            _keys[K(root, keyPath)] = values;
        }

        values[valueName] = value;
    }

    public bool DeleteValue(RegistryRoot root, string keyPath, string valueName)
        => _keys.TryGetValue(K(root, keyPath), out var values) && values.Remove(valueName);

    public bool KeyExists(RegistryRoot root, string keyPath) => _keys.ContainsKey(K(root, keyPath));

    public IReadOnlyList<string> GetValueNames(RegistryRoot root, string keyPath)
        => _keys.TryGetValue(K(root, keyPath), out var values) ? values.Keys.ToList() : [];

    public IReadOnlyList<string> GetSubKeyNames(RegistryRoot root, string keyPath) => [];
}

internal sealed class FakePrivileges(bool elevated) : IPrivilegeService
{
    public bool IsElevated { get; set; } = elevated;
}

internal sealed class FakeOs(int build) : IOsInfo
{
    public int Build { get; } = build;

    public string ProductName => "Windows 11 Pro";

    public string DisplayVersion => "24H2";
}

internal sealed class FakePower : IPowerManager
{
    public List<PowerScheme> Schemes { get; } = [];

    public Guid Active { get; set; } = PowerSchemes.Balanced;

    public Guid? Overlay { get; set; }

    public Guid? GetActiveScheme() => Active;

    public IReadOnlyList<PowerScheme> GetSchemes() => Schemes.Select(s => s with { IsActive = s.Id == Active }).ToList();

    public void SetActiveScheme(Guid scheme) => Active = scheme;

    public Guid? GetActiveOverlay() => Overlay;

    public bool IsOverlaySupported => Overlay is not null;

    public void SetActiveOverlay(Guid overlay) => Overlay = overlay;
}

internal sealed class FakeSystemParameters : ISystemParameters
{
    public bool Animation { get; set; } = true;

    public MouseSettings Mouse { get; set; } = new(6, 10, 1);

    public bool GetClientAreaAnimation() => Animation;

    public void SetClientAreaAnimation(bool enabled) => Animation = enabled;

    public MouseSettings GetMouseSettings() => Mouse;

    public void SetMouseSettings(MouseSettings settings) => Mouse = settings;
}

internal sealed class ListCatalog(params IOptimization[] items) : IOptimizationCatalog
{
    public List<IOptimization> Items { get; } = items.ToList();

    public IReadOnlyList<IOptimization> GetAll() => Items;

    public IOptimization? Find(string id) => Items.FirstOrDefault(i => i.Id == id);
}

internal sealed class InMemoryBackupStore : IOptimizationBackupStore
{
    public Dictionary<string, string> Saved { get; } = [];

    public Task SaveAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        Saved[backup.Id] = System.Text.Json.JsonSerializer.Serialize(backup);
        return Task.CompletedTask;
    }

    public Task<OptimizationBackup?> LoadAsync(string backupId, CancellationToken cancellationToken = default)
        => Task.FromResult(Saved.TryGetValue(backupId, out var json) ? System.Text.Json.JsonSerializer.Deserialize<OptimizationBackup>(json) : null);

    public Task DeleteAsync(string backupId, CancellationToken cancellationToken = default)
    {
        Saved.Remove(backupId);
        return Task.CompletedTask;
    }
}

internal sealed class InMemoryHistory : IOptimizationHistoryStore
{
    public List<OptimizationHistoryEntry> Entries { get; } = [];

    public event EventHandler? Changed;

    public Task<IReadOnlyList<OptimizationHistoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<OptimizationHistoryEntry>>(Entries.OrderByDescending(e => e.Timestamp).ToList());

    public Task AddAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        Entries.Add(entry);
        Changed?.Invoke(this, EventArgs.Empty);
        return Task.CompletedTask;
    }

    public Task UpdateAsync(OptimizationHistoryEntry entry, CancellationToken cancellationToken = default)
    {
        var i = Entries.FindIndex(e => e.Id == entry.Id);
        Entries[i] = entry;
        return Task.CompletedTask;
    }
}

/// <summary>Advancing clock so history entries are strictly ordered.</summary>
internal sealed class SteppingTime : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow()
    {
        _now = _now.AddSeconds(1);
        return _now;
    }
}

internal sealed class ThrowingMetricsProvider : IMetricsProvider
{
    public MetricSnapshot Sample() => throw new InvalidOperationException("sensor missing");

    public void Dispose()
    {
    }
}

internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "nexora-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
