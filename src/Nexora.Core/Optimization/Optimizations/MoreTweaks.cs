using System.Globalization;
using Nexora.Core.Platform;

namespace Nexora.Core.Optimization.Optimizations;

/// <summary>Windows "Variable refresh rate" optimization (VRROptimizeEnable in the user's DirectX settings).</summary>
public sealed class VariableRefreshRateOptimization(IRegistryAccessor registry, IOsInfo os) : OptimizationBase
{
    public const string OptimizationId = "gaming.variable-refresh-rate";
    private const string SettingName = "VRROptimizeEnable";

    public override string Id => OptimizationId;

    public override string Name => "Variable refresh rate for games";

    public override string Description => "Let DirectX 11 games that don't support VRR natively use your G-SYNC / FreeSync display's variable refresh rate.";

    public override string WhatItChanges => "Turns on Settings › System › Display › Graphics › Variable refresh rate (VRROptimizeEnable in your user's DirectX settings). Other DirectX preferences are left untouched.";

    public override string WhyItHelps => "With VRR the monitor waits for each frame instead of showing it at a fixed rate, which removes tearing and reduces stutter when FPS fluctuates below the refresh rate.";

    public override string ExpectedBenefit => "Smoother motion without tearing in supported windowed and fullscreen games. Requires a VRR-capable (G-SYNC Compatible / FreeSync) monitor; no effect otherwise.";

    public override OptimizationCategory Category => OptimizationCategory.Gaming;

    public override ImpactLevel Impact => ImpactLevel.Medium;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (os.Build < 19041)
        {
            return Task.FromResult(OptimizationState.Unavailable("Requires Windows 10 version 2004 or later."));
        }

        var settings = WindowedGamesOptimization.Parse(ReadRaw());
        var enabled = settings.TryGetValue(SettingName, out var v) && v == "1";
        return Task.FromResult(new OptimizationState(enabled ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, enabled ? "On" : "Off"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        backup.Registry.Add(RegistrySnapshot.Capture(registry, RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName));
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        var settings = WindowedGamesOptimization.Parse(ReadRaw());
        settings[SettingName] = "1";
        registry.SetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName, RegistryValue.FromString(WindowedGamesOptimization.Format(settings)));
        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        foreach (var snapshot in backup.Registry)
        {
            snapshot.Restore(registry);
        }

        return Task.CompletedTask;
    }

    private string ReadRaw()
    {
        var value = registry.GetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName);
        return value?.Type is RegistryValueType.String or RegistryValueType.ExpandString ? value.AsString() : string.Empty;
    }
}

/// <summary>
/// Turns off the keyboard shortcuts that open the Sticky Keys / Filter Keys / Toggle Keys dialogs
/// (pressing Shift five times, holding right Shift, holding Num Lock) — the classic "game minimized by a pop-up" problem.
/// The accessibility features themselves stay available in Settings.
/// </summary>
public sealed class AccessibilityShortcutsOptimization(IRegistryAccessor registry) : OptimizationBase
{
    public const string OptimizationId = "gaming.accessibility-shortcuts";
    private const int HotkeyActive = 0x4;
    private const int FeatureOn = 0x1;

    internal static readonly (string Key, string Default)[] Keys =
    [
        (@"Control Panel\Accessibility\StickyKeys", "510"),
        (@"Control Panel\Accessibility\Keyboard Response", "126"),
        (@"Control Panel\Accessibility\ToggleKeys", "62"),
    ];

    public override string Id => OptimizationId;

    public override string Name => "Disable Sticky Keys pop-ups";

    public override string Description => "Stop Shift ×5, holding Shift or holding Num Lock from opening accessibility dialogs mid-game.";

    public override string WhatItChanges => "Clears the \"keyboard shortcut\" flag of Sticky Keys, Filter Keys and Toggle Keys for your user account (Settings › Accessibility › Keyboard). The features themselves are not removed and are left as they are.";

    public override string WhyItHelps => "Pressing Shift repeatedly is common in games; the Sticky Keys prompt then steals focus and can minimize a fullscreen game.";

    public override string ExpectedBenefit => "No more accidental accessibility pop-ups while gaming. Takes effect after you sign out and back in.";

    public override OptimizationCategory Category => OptimizationCategory.Gaming;

    public override bool RequiresRestart => true;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        var active = Keys.Count(k => (ReadFlags(k.Key, k.Default) & HotkeyActive) != 0);
        return Task.FromResult(new OptimizationState(active == 0 ? OptimizationStatus.Applied : OptimizationStatus.NotApplied,
            active == 0 ? "Shortcuts off" : $"{active} of 3 shortcuts active"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        foreach (var (key, _) in Keys)
        {
            backup.Registry.Add(RegistrySnapshot.Capture(registry, RegistryRoot.CurrentUser, key, "Flags"));
        }

        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        foreach (var (key, defaults) in Keys)
        {
            var flags = ReadFlags(key, defaults);
            if ((flags & FeatureOn) != 0)
            {
                continue; // The user actively uses this feature: leave it completely untouched.
            }

            registry.SetValue(RegistryRoot.CurrentUser, key, "Flags", RegistryValue.FromString((flags & ~HotkeyActive).ToString(CultureInfo.InvariantCulture)));
        }

        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        foreach (var snapshot in Enumerable.Reverse(backup.Registry))
        {
            snapshot.Restore(registry);
        }

        return Task.CompletedTask;
    }

    internal int ReadFlags(string key, string defaults)
    {
        var raw = registry.GetValue(RegistryRoot.CurrentUser, key, "Flags");
        var text = raw?.Type is RegistryValueType.String or RegistryValueType.ExpandString ? raw.AsString() : defaults;
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var flags) ? flags : int.Parse(defaults, CultureInfo.InvariantCulture);
    }
}
