using Nexora.Core.Platform;

namespace Nexora.Core.Optimization;

/// <summary>A registry value that an optimization sets to a target value.</summary>
public sealed record RegistryChange(RegistryRoot Root, string KeyPath, string ValueName, RegistryValue TargetValue)
{
    public string FullName => $"{RootName(Root)}\\{KeyPath}\\{ValueName}";

    public static string RootName(RegistryRoot root) => root switch
    {
        RegistryRoot.CurrentUser => "HKCU",
        RegistryRoot.LocalMachine => "HKLM",
        RegistryRoot.Users => "HKU",
        _ => root.ToString(),
    };
}

/// <summary>
/// Generic, data-driven optimization that sets one or more documented registry values.
/// The original values (or their absence) are captured and restored exactly.
/// </summary>
public sealed class RegistryOptimization : OptimizationBase
{
    private readonly IRegistryAccessor _registry;
    private readonly IReadOnlyList<RegistryChange> _changes;
    private readonly Func<IReadOnlyList<RegistryValue?>, string> _describe;
    private readonly Func<string?>? _availability;
    private readonly Func<IReadOnlyList<RegistryValue?>, bool>? _isApplied;

    /// <param name="isApplied">
    /// Optional predicate deciding whether the current values already match the desired configuration
    /// (e.g. when an absent value means the Windows default is already the desired state).
    /// Defaults to "every value equals its target".
    /// </param>
    public RegistryOptimization(
        IRegistryAccessor registry,
        OptimizationDescriptor descriptor,
        IReadOnlyList<RegistryChange> changes,
        Func<IReadOnlyList<RegistryValue?>, string> describeCurrent,
        Func<string?>? unavailableReason = null,
        Func<IReadOnlyList<RegistryValue?>, bool>? isApplied = null)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(descriptor);
        if (changes is null || changes.Count == 0)
        {
            throw new ArgumentException("At least one registry change is required.", nameof(changes));
        }

        _registry = registry;
        Descriptor = descriptor;
        _changes = changes;
        _describe = describeCurrent ?? throw new ArgumentNullException(nameof(describeCurrent));
        _availability = unavailableReason;
        _isApplied = isApplied;
    }

    public OptimizationDescriptor Descriptor { get; }

    public IReadOnlyList<RegistryChange> Changes => _changes;

    public override string Id => Descriptor.Id;

    public override string Name => Descriptor.Name;

    public override string Description => Descriptor.Description;

    public override string WhatItChanges => Descriptor.WhatItChanges;

    public override string WhyItHelps => Descriptor.WhyItHelps;

    public override string ExpectedBenefit => Descriptor.ExpectedBenefit;

    public override OptimizationCategory Category => Descriptor.Category;

    public override RiskLevel Risk => Descriptor.Risk;

    public override ImpactLevel Impact => Descriptor.Impact;

    public override bool RequiresAdministrator => Descriptor.RequiresAdministrator;

    public override bool RequiresRestart => Descriptor.RequiresRestart;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var reason = _availability?.Invoke();
        if (reason is not null)
        {
            return Task.FromResult(OptimizationState.Unavailable(reason));
        }

        var current = _changes.Select(c => _registry.GetValue(c.Root, c.KeyPath, c.ValueName)).ToList();
        var applied = _isApplied?.Invoke(current)
            ?? _changes.Zip(current).All(p => p.Second is not null && p.Second.Equals(p.First.TargetValue));
        return Task.FromResult(new OptimizationState(applied ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, _describe(current)));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        foreach (var change in _changes)
        {
            backup.Registry.Add(RegistrySnapshot.Capture(_registry, change.Root, change.KeyPath, change.ValueName));
        }

        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        foreach (var change in _changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _registry.SetValue(change.Root, change.KeyPath, change.ValueName, change.TargetValue);
        }

        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        List<Exception>? errors = null;

        // Restore in reverse order and keep going on failure so as much as possible is restored.
        foreach (var snapshot in Enumerable.Reverse(backup.Registry))
        {
            try
            {
                snapshot.Restore(_registry);
            }
            catch (Exception ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        if (errors is not null)
        {
            throw new AggregateException("One or more registry values could not be restored.", errors);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Static, human-facing information about an optimization.</summary>
public sealed record OptimizationDescriptor(
    string Id,
    string Name,
    string Description,
    string WhatItChanges,
    string WhyItHelps,
    string ExpectedBenefit,
    OptimizationCategory Category,
    RiskLevel Risk = RiskLevel.Low,
    ImpactLevel Impact = ImpactLevel.Low,
    bool RequiresAdministrator = false,
    bool RequiresRestart = false);
