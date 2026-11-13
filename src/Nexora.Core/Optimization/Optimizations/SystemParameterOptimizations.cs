using System.Globalization;
using Nexora.Core.Platform;

namespace Nexora.Core.Optimization.Optimizations;

/// <summary>Disables "Enhance pointer precision" (mouse acceleration) for consistent 1:1 aiming.</summary>
public sealed class MouseAccelerationOptimization(ISystemParameters parameters) : OptimizationBase
{
    public const string OptimizationId = "gaming.mouse-acceleration";

    public override string Id => OptimizationId;

    public override string Name => "Disable Enhance pointer precision";

    public override string Description => "Turn off Windows mouse acceleration so cursor movement is proportional to physical movement.";

    public override string WhatItChanges => "Sets the Windows mouse acceleration setting (Settings › Bluetooth & devices › Mouse › Enhance pointer precision) to off for your user account.";

    public override string WhyItHelps => "With acceleration on, the same hand movement produces different cursor distances depending on speed, which makes aim less consistent. Games that use raw input are unaffected, but menus and games using the Windows cursor are.";

    public override string ExpectedBenefit => "More consistent aim and muscle memory. No effect on FPS.";

    public override OptimizationCategory Category => OptimizationCategory.Gaming;

    /// <summary>Mouse feel is a personal preference: never part of Quick Optimize or scan pre-selection.</summary>
    public override bool IsQuickOptimizeCandidate => false;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var current = parameters.GetMouseSettings();
        var enabled = current.Acceleration != 0;
        return Task.FromResult(new OptimizationState(enabled ? OptimizationStatus.NotApplied : OptimizationStatus.Applied,
            enabled ? "Enhance pointer precision: On" : "Enhance pointer precision: Off"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        var current = parameters.GetMouseSettings();
        backup.Properties["threshold1"] = current.Threshold1.ToString(CultureInfo.InvariantCulture);
        backup.Properties["threshold2"] = current.Threshold2.ToString(CultureInfo.InvariantCulture);
        backup.Properties["acceleration"] = current.Acceleration.ToString(CultureInfo.InvariantCulture);
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        parameters.SetMouseSettings(new MouseSettings(0, 0, 0));
        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        var settings = new MouseSettings(
            int.Parse(backup.Properties["threshold1"], CultureInfo.InvariantCulture),
            int.Parse(backup.Properties["threshold2"], CultureInfo.InvariantCulture),
            int.Parse(backup.Properties["acceleration"], CultureInfo.InvariantCulture));
        parameters.SetMouseSettings(settings);
        return Task.CompletedTask;
    }
}

/// <summary>Turns off Windows UI animations (window/menu/control animations).</summary>
public sealed class UiAnimationOptimization(ISystemParameters parameters) : OptimizationBase
{
    public const string OptimizationId = "windows.ui-animations";

    public override string Id => OptimizationId;

    public override string Name => "Reduce Windows animation effects";

    public override string Description => "Turn off Windows animation effects for a snappier desktop.";

    public override string WhatItChanges => "Sets Settings › Accessibility › Visual effects › Animation effects to off for your user account.";

    public override string WhyItHelps => "Removes the delay of window and menu animations, so the desktop responds instantly when alt-tabbing out of games.";

    public override string ExpectedBenefit => "Snappier desktop and alt-tab. No effect on in-game FPS.";

    public override OptimizationCategory Category => OptimizationCategory.Windows;

    public override bool IsQuickOptimizeCandidate => false;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var enabled = parameters.GetClientAreaAnimation();
        return Task.FromResult(new OptimizationState(enabled ? OptimizationStatus.NotApplied : OptimizationStatus.Applied,
            enabled ? "Animation effects: On" : "Animation effects: Off"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        backup.Properties["enabled"] = parameters.GetClientAreaAnimation() ? "1" : "0";
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        parameters.SetClientAreaAnimation(false);
        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        parameters.SetClientAreaAnimation(backup.Properties.TryGetValue("enabled", out var v) && v == "1");
        return Task.CompletedTask;
    }
}
