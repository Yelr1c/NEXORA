using Nexora.Core.Platform;

namespace Nexora.Core.Optimization.Optimizations;

/// <summary>Switches to the High performance plan, or the Windows 11 "Best performance" power mode on systems without one.</summary>
public sealed class PowerPlanOptimization(IPowerManager power) : OptimizationBase
{
    public const string OptimizationId = "power.high-performance";
    private const string SchemeKey = "activeScheme";
    private const string OverlayKey = "activeOverlay";

    public override string Id => OptimizationId;

    public override string Name => "High performance power mode";

    public override string Description => "Use the High performance power plan (or the \"Best performance\" power mode) while gaming.";

    public override string WhatItChanges => "Changes the active Windows power plan, or on systems that only offer the Balanced plan, the Windows 11 power mode overlay. No power plans are created, deleted or edited.";

    public override string WhyItHelps => "Performance plans let the CPU raise clocks faster and stay in deeper boost states, which can reduce stutter caused by slow frequency ramp-up in CPU-bound games.";

    public override string ExpectedBenefit => "Fewer frequency-related stutters in CPU-limited scenarios. Typically small or no change in average FPS. Increases power use and heat, especially on laptops.";

    public override OptimizationCategory Category => OptimizationCategory.Power;

    public override ImpactLevel Impact => ImpactLevel.Medium;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var active = power.GetActiveScheme();
        if (active is null)
        {
            return Task.FromResult(OptimizationState.Unavailable("The active power plan could not be read."));
        }

        var schemes = power.GetSchemes();
        var activeName = schemes.FirstOrDefault(s => s.Id == active)?.Name ?? active.Value.ToString();
        if (active == PowerSchemes.HighPerformance || active == PowerSchemes.UltimatePerformance)
        {
            return Task.FromResult(new OptimizationState(OptimizationStatus.Applied, activeName));
        }

        var target = ChooseTargetScheme(schemes);
        if (target is null && power.IsOverlaySupported)
        {
            var overlay = power.GetActiveOverlay() ?? Guid.Empty;
            var description = $"{activeName} · Power mode: {PowerSchemes.DescribeOverlay(overlay)}";
            return Task.FromResult(new OptimizationState(overlay == PowerSchemes.OverlayBestPerformance ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, description));
        }

        if (target is null)
        {
            return Task.FromResult(OptimizationState.Unavailable("No high performance plan or power mode is available on this system."));
        }

        return Task.FromResult(new OptimizationState(OptimizationStatus.NotApplied, activeName));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        var active = power.GetActiveScheme() ?? throw new InvalidOperationException("The active power plan could not be read.");
        backup.Properties[SchemeKey] = active.ToString();
        if (power.IsOverlaySupported && power.GetActiveOverlay() is { } overlay)
        {
            backup.Properties[OverlayKey] = overlay.ToString();
        }

        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        var target = ChooseTargetScheme(power.GetSchemes());
        if (target is { } scheme)
        {
            power.SetActiveScheme(scheme);
        }
        else if (power.IsOverlaySupported)
        {
            power.SetActiveOverlay(PowerSchemes.OverlayBestPerformance);
        }
        else
        {
            throw new InvalidOperationException("No high performance plan or power mode is available.");
        }

        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        if (backup.Properties.TryGetValue(SchemeKey, out var scheme) && Guid.TryParse(scheme, out var schemeId))
        {
            power.SetActiveScheme(schemeId);
        }

        if (backup.Properties.TryGetValue(OverlayKey, out var overlay) && Guid.TryParse(overlay, out var overlayId) && power.IsOverlaySupported)
        {
            power.SetActiveOverlay(overlayId);
        }

        return Task.CompletedTask;
    }

    private static Guid? ChooseTargetScheme(IReadOnlyList<PowerScheme> schemes)
    {
        if (schemes.Any(s => s.Id == PowerSchemes.HighPerformance))
        {
            return PowerSchemes.HighPerformance;
        }

        if (schemes.Any(s => s.Id == PowerSchemes.UltimatePerformance))
        {
            return PowerSchemes.UltimatePerformance;
        }

        return null;
    }
}
