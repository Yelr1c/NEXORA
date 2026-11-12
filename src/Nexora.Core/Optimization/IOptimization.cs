namespace Nexora.Core.Optimization;

/// <summary>
/// A single, self-describing, reversible system change.
/// Implementations must never touch security software, Windows Update or critical services.
/// </summary>
public interface IOptimization
{
    string Id { get; }

    string Name { get; }

    /// <summary>Short one-line description.</summary>
    string Description { get; }

    /// <summary>Exactly what is modified on the system.</summary>
    string WhatItChanges { get; }

    /// <summary>Why the change may help, written honestly.</summary>
    string WhyItHelps { get; }

    string ExpectedBenefit { get; }

    OptimizationCategory Category { get; }

    OptimizationKind Kind { get; }

    RiskLevel Risk { get; }

    ImpactLevel Impact { get; }

    bool RequiresAdministrator { get; }

    bool RequiresRestart { get; }

    /// <summary>Whether Quick Optimize may select this item (only low-risk, no-admin items).</summary>
    bool IsQuickOptimizeCandidate { get; }

    Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Captures every value the optimization will change.</summary>
    Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies the change. Implementations may add information to <paramref name="backup"/>.</summary>
    Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default);

    Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default);
}

public interface IOptimizationCatalog
{
    /// <summary>Returns the current set of optimizations (dynamic items such as startup entries are re-enumerated).</summary>
    IReadOnlyList<IOptimization> GetAll();

    IOptimization? Find(string id);
}

public abstract class OptimizationBase : IOptimization
{
    public abstract string Id { get; }

    public abstract string Name { get; }

    public abstract string Description { get; }

    public abstract string WhatItChanges { get; }

    public abstract string WhyItHelps { get; }

    public abstract string ExpectedBenefit { get; }

    public abstract OptimizationCategory Category { get; }

    public virtual OptimizationKind Kind => OptimizationKind.Setting;

    public virtual RiskLevel Risk => RiskLevel.Low;

    public virtual ImpactLevel Impact => ImpactLevel.Low;

    public virtual bool RequiresAdministrator => false;

    public virtual bool RequiresRestart => false;

    public virtual bool IsQuickOptimizeCandidate => Risk == RiskLevel.Low && !RequiresAdministrator && Kind == OptimizationKind.Setting;

    public abstract Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default);

    public abstract Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default);

    public abstract Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default);

    public abstract Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default);

    protected OptimizationBackup NewBackup() => new()
    {
        OptimizationId = Id,
        CreatedAt = DateTimeOffset.UtcNow,
    };
}
