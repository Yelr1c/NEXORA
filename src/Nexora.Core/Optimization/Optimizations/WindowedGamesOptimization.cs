using Nexora.Core.Platform;

namespace Nexora.Core.Optimization.Optimizations;

/// <summary>
/// Windows 11 "Optimizations for windowed games" (flip-model presentation for DX10/11 windowed games).
/// Stored as one key in the semicolon separated DirectXUserGlobalSettings string; other keys are preserved.
/// </summary>
public sealed class WindowedGamesOptimization(IRegistryAccessor registry, IOsInfo os) : OptimizationBase
{
    public const string OptimizationId = "gaming.windowed-optimizations";
    internal const string KeyPath = @"Software\Microsoft\DirectX\UserGpuPreferences";
    internal const string ValueName = "DirectXUserGlobalSettings";
    internal const string SettingName = "SwapEffectUpgradeEnable";

    public override string Id => OptimizationId;

    public override string Name => "Optimizations for windowed games";

    public override string Description => "Use the modern flip presentation model for DirectX 10/11 games running windowed or borderless.";

    public override string WhatItChanges => "Turns on Settings › System › Display › Graphics › Optimizations for windowed games (SwapEffectUpgradeEnable in your user's DirectX settings). Other DirectX preferences are left untouched.";

    public override string WhyItHelps => "Flip-model presentation lets borderless/windowed games present frames with lower latency and supports variable refresh rate and Auto HDR, similar to exclusive fullscreen.";

    public override string ExpectedBenefit => "Lower input latency and smoother frame pacing in windowed/borderless DX10/11 games. No effect on DX12 or exclusive fullscreen games.";

    public override OptimizationCategory Category => OptimizationCategory.Gaming;

    public override ImpactLevel Impact => ImpactLevel.Medium;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (os.Build < 22621)
        {
            return Task.FromResult(OptimizationState.Unavailable("Requires Windows 11 version 22H2 or later."));
        }

        var settings = Parse(ReadRaw());
        var enabled = settings.TryGetValue(SettingName, out var v) && v == "1";
        return Task.FromResult(new OptimizationState(enabled ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, enabled ? "On" : "Off"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        backup.Registry.Add(RegistrySnapshot.Capture(registry, RegistryRoot.CurrentUser, KeyPath, ValueName));
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        var settings = Parse(ReadRaw());
        settings[SettingName] = "1";
        registry.SetValue(RegistryRoot.CurrentUser, KeyPath, ValueName, RegistryValue.FromString(Format(settings)));
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
        var value = registry.GetValue(RegistryRoot.CurrentUser, KeyPath, ValueName);
        return value?.Type is RegistryValueType.String or RegistryValueType.ExpandString ? value.AsString() : string.Empty;
    }

    /// <summary>Parses "Key=Value;Key2=Value2;" preserving key order.</summary>
    public static Dictionary<string, string> Parse(string raw)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in (raw ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            if (eq <= 0)
            {
                continue;
            }

            result[part[..eq].Trim()] = part[(eq + 1)..].Trim();
        }

        return result;
    }

    public static string Format(IReadOnlyDictionary<string, string> settings)
        => string.Concat(settings.Select(kv => $"{kv.Key}={kv.Value};"));
}
