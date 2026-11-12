using Nexora.Core.Platform;

namespace Nexora.Core.Optimization;

public enum OptimizationCategory
{
    Gaming,
    Windows,
    Network,
    BackgroundProcesses,
    Power,
    Storage,
}

public enum RiskLevel
{
    Low,
    Medium,
    High,
}

public enum ImpactLevel
{
    Low,
    Medium,
    High,
}

/// <summary>Setting = persistent configuration change. Action = one-shot operation (e.g. cleaning temp files).</summary>
public enum OptimizationKind
{
    Setting,
    Action,
}

public enum OptimizationStatus
{
    NotApplied,
    Applied,
    Unavailable,
}

public sealed record OptimizationState(OptimizationStatus Status, string CurrentValue, string? UnavailableReason = null)
{
    public static OptimizationState Unavailable(string reason) => new(OptimizationStatus.Unavailable, "Unavailable on this system", reason);
}

/// <summary>Snapshot of a single registry value taken before a change.</summary>
public sealed class RegistrySnapshot
{
    public RegistryRoot Root { get; set; }

    public string KeyPath { get; set; } = string.Empty;

    public string ValueName { get; set; } = string.Empty;

    public bool Existed { get; set; }

    public RegistryValueType? Type { get; set; }

    public string? SerializedData { get; set; }

    public static RegistrySnapshot Capture(IRegistryAccessor registry, RegistryRoot root, string keyPath, string valueName)
    {
        var value = registry.GetValue(root, keyPath, valueName);
        return new RegistrySnapshot
        {
            Root = root,
            KeyPath = keyPath,
            ValueName = valueName,
            Existed = value is not null,
            Type = value?.Type,
            SerializedData = value?.Serialize(),
        };
    }

    public RegistryValue? ToValue() => Existed && Type is { } t && SerializedData is not null
        ? RegistryValue.Deserialize(t, SerializedData)
        : null;

    public void Restore(IRegistryAccessor registry)
    {
        var value = ToValue();
        if (value is null)
        {
            registry.DeleteValue(Root, KeyPath, ValueName);
        }
        else
        {
            registry.SetValue(Root, KeyPath, ValueName, value);
        }
    }
}

/// <summary>Everything required to undo an optimization. Persisted to disk before any change is made.</summary>
public sealed class OptimizationBackup
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string OptimizationId { get; set; } = string.Empty;

    public DateTimeOffset CreatedAt { get; set; }

    public string PreviousValue { get; set; } = string.Empty;

    public List<RegistrySnapshot> Registry { get; set; } = [];

    public Dictionary<string, string> Properties { get; set; } = new(StringComparer.Ordinal);
}

public enum HistoryStatus
{
    Applied,
    Reverted,
    Failed,
    RolledBack,

    /// <summary>Permanently finalised (e.g. quarantined temp files purged). Can no longer be undone.</summary>
    Committed,
}

public sealed class OptimizationHistoryEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTimeOffset Timestamp { get; set; }

    public string OptimizationId { get; set; } = string.Empty;

    public string OptimizationName { get; set; } = string.Empty;

    public string PreviousValue { get; set; } = string.Empty;

    public string NewValue { get; set; } = string.Empty;

    public HistoryStatus Status { get; set; }

    public string? BackupId { get; set; }

    public string? Message { get; set; }

    public DateTimeOffset? RevertedAt { get; set; }
}

public enum OptimizationOutcome
{
    Applied,
    AlreadyApplied,
    Reverted,
    RequiresElevation,
    Unavailable,
    FailedAndRolledBack,
    FailedRollbackFailed,
    NotFound,
    NotAllowed,
}

public sealed record OptimizationResult(string OptimizationId, string Name, OptimizationOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome is OptimizationOutcome.Applied or OptimizationOutcome.AlreadyApplied or OptimizationOutcome.Reverted;
}

public sealed record OptimizationBatchResult(IReadOnlyList<OptimizationResult> Results, bool BatchRolledBack)
{
    public int SucceededCount => Results.Count(r => r.IsSuccess);

    public int FailedCount => Results.Count(r => !r.IsSuccess);
}

public sealed class OptimizationVerificationException(string message) : Exception(message);
