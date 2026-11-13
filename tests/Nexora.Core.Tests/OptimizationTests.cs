using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Platform;

namespace Nexora.Core.Tests;

public class OptimizationEngineTests
{
    private const string Key = @"Software\Test";

    private static RegistryOptimization CreateRegistryOpt(InMemoryRegistry registry, string id = "test.opt", bool admin = false, string value = "Flag")
        => new(registry,
            new OptimizationDescriptor(id, "Test", "d", "w", "y", "b", OptimizationCategory.Gaming, RequiresAdministrator: admin),
            [new RegistryChange(RegistryRoot.CurrentUser, Key, value, RegistryValue.FromDWord(1))],
            current => current[0]?.ToDisplayString() ?? "absent");

    private static (OptimizationEngine Engine, InMemoryHistory History, InMemoryBackupStore Backups) CreateEngine(IOptimizationCatalog catalog, bool elevated = false)
    {
        var history = new InMemoryHistory();
        var backups = new InMemoryBackupStore();
        var engine = new OptimizationEngine(backups, history, new FakePrivileges(elevated), catalog, NullLogger<OptimizationEngine>.Instance, new SteppingTime());
        return (engine, history, backups);
    }

    [Fact]
    public async Task ApplyThenUndoRestoresAbsentValue()
    {
        var registry = new InMemoryRegistry();
        var opt = CreateRegistryOpt(registry);
        var (engine, history, backups) = CreateEngine(new ListCatalog(opt));

        var result = await engine.ApplyAsync(opt);
        Assert.Equal(OptimizationOutcome.Applied, result.Outcome);
        Assert.Equal(1, registry.GetValue(RegistryRoot.CurrentUser, Key, "Flag")!.AsDWord());
        var entry = Assert.Single(history.Entries);
        Assert.Equal("absent", entry.PreviousValue);
        Assert.Equal("1", entry.NewValue);
        Assert.Single(backups.Saved);

        var undo = await engine.UndoAsync(entry.Id);
        Assert.Equal(OptimizationOutcome.Reverted, undo.Outcome);
        Assert.Null(registry.GetValue(RegistryRoot.CurrentUser, Key, "Flag"));
        Assert.Equal(HistoryStatus.Reverted, history.Entries[0].Status);
    }

    [Fact]
    public async Task UndoRestoresPreviousValueExactly()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, Key, "Flag", RegistryValue.FromDWord(7));
        var opt = CreateRegistryOpt(registry);
        var (engine, history, _) = CreateEngine(new ListCatalog(opt));
        await engine.ApplyAsync(opt);
        await engine.UndoAsync(history.Entries[0].Id);
        Assert.Equal(7, registry.GetValue(RegistryRoot.CurrentUser, Key, "Flag")!.AsDWord());
    }

    [Fact]
    public async Task AlreadyAppliedIsNotWrittenAgain()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, Key, "Flag", RegistryValue.FromDWord(1));
        var writes = registry.WriteCount;
        var opt = CreateRegistryOpt(registry);
        var (engine, history, _) = CreateEngine(new ListCatalog(opt));
        var result = await engine.ApplyAsync(opt);
        Assert.Equal(OptimizationOutcome.AlreadyApplied, result.Outcome);
        Assert.Equal(writes, registry.WriteCount);
        Assert.Empty(history.Entries);
    }

    [Fact]
    public async Task AdminOptimizationIsRefusedWithoutElevation()
    {
        var registry = new InMemoryRegistry();
        var opt = CreateRegistryOpt(registry, admin: true);
        var (engine, history, backups) = CreateEngine(new ListCatalog(opt), elevated: false);
        var result = await engine.ApplyAsync(opt);
        Assert.Equal(OptimizationOutcome.RequiresElevation, result.Outcome);
        Assert.Equal(0, registry.WriteCount);
        Assert.Empty(history.Entries);
        Assert.Empty(backups.Saved);
    }

    [Fact]
    public async Task PartialFailureIsRolledBackCompletely()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, Key, "A", RegistryValue.FromDWord(5));
        registry.FailWritesFor = name => name == "B";
        var opt = new RegistryOptimization(registry,
            new OptimizationDescriptor("multi", "Multi", "d", "w", "y", "b", OptimizationCategory.Windows),
            [
                new RegistryChange(RegistryRoot.CurrentUser, Key, "A", RegistryValue.FromDWord(1)),
                new RegistryChange(RegistryRoot.CurrentUser, Key, "B", RegistryValue.FromDWord(1)),
            ],
            _ => "x");
        var (engine, history, _) = CreateEngine(new ListCatalog(opt));

        // Restore needs to delete "B" (absent) and rewrite "A"; let the rollback writes succeed.
        var result = await engine.ApplyAsync(opt);
        Assert.Equal(OptimizationOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(5, registry.GetValue(RegistryRoot.CurrentUser, Key, "A")!.AsDWord());
        Assert.Null(registry.GetValue(RegistryRoot.CurrentUser, Key, "B"));
        Assert.Equal(HistoryStatus.Failed, Assert.Single(history.Entries).Status);
    }

    [Fact]
    public async Task VerificationFailureRollsBack()
    {
        var spi = new FakeSystemParameters();
        var opt = new IgnoringMouseOptimization(spi);
        var (engine, history, _) = CreateEngine(new ListCatalog(opt));
        var result = await engine.ApplyAsync(opt);
        Assert.Equal(OptimizationOutcome.FailedAndRolledBack, result.Outcome);
        Assert.Equal(1, spi.Mouse.Acceleration);
        Assert.Contains("verification", history.Entries[0].Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task BatchFailureRollsBackEarlierItems()
    {
        var registry = new InMemoryRegistry();
        var first = CreateRegistryOpt(registry, "first", value: "First");
        var spi = new FakeSystemParameters();
        var failing = new IgnoringMouseOptimization(spi);
        var (engine, history, _) = CreateEngine(new ListCatalog(first, failing));

        var batch = await engine.ApplyAsync([first, failing], rollbackBatchOnFailure: true, progress: null);
        Assert.True(batch.BatchRolledBack);
        Assert.Null(registry.GetValue(RegistryRoot.CurrentUser, Key, "First"));
        Assert.Contains(history.Entries, e => e.OptimizationId == "first" && e.Status == HistoryStatus.RolledBack);
    }

    [Fact]
    public async Task OnlyNewestEntryCanBeUndone()
    {
        var registry = new InMemoryRegistry();
        var opt = CreateRegistryOpt(registry);
        var (engine, history, _) = CreateEngine(new ListCatalog(opt));
        await engine.ApplyAsync(opt);
        registry.DeleteValue(RegistryRoot.CurrentUser, Key, "Flag"); // something else reverted it
        await engine.ApplyAsync(opt);
        var all = await history.GetAllAsync();
        var older = all.Last();
        Assert.False(OptimizationEngine.CanUndo(older, all));
        Assert.True(OptimizationEngine.CanUndo(all.First(), all));
        Assert.Equal(OptimizationOutcome.NotAllowed, (await engine.UndoAsync(older.Id)).Outcome);
    }

    [Fact]
    public async Task RestoreAllRevertsEverything()
    {
        var registry = new InMemoryRegistry();
        var a = CreateRegistryOpt(registry, "a", value: "A");
        var b = CreateRegistryOpt(registry, "b", value: "B");
        var (engine, _, _) = CreateEngine(new ListCatalog(a, b));
        await engine.ApplyAsync([a, b], false, null);
        var results = await engine.RestoreAllAsync();
        Assert.All(results, r => Assert.Equal(OptimizationOutcome.Reverted, r.Outcome));
        Assert.Empty(registry.GetValueNames(RegistryRoot.CurrentUser, Key));
    }

    [Fact]
    public async Task BackupIsPersistedBeforeChange()
    {
        var registry = new InMemoryRegistry();
        var backups = new InMemoryBackupStore();
        var probe = new ProbeOptimization(() => backups.Saved.Count);
        var engine = new OptimizationEngine(backups, new InMemoryHistory(), new FakePrivileges(false), new ListCatalog(probe), NullLogger<OptimizationEngine>.Instance);
        await engine.ApplyAsync(probe);
        Assert.Equal(1, probe.BackupsAtApplyTime);
    }

    /// <summary>Pretends to apply but Windows "ignores" the change, so verification must fail.</summary>
    private sealed class IgnoringMouseOptimization(FakeSystemParameters spi) : OptimizationBase
    {
        private readonly MouseAccelerationOptimization _inner = new(spi);

        public override string Id => "ignored";

        public override string Name => "Ignored";

        public override string Description => "";

        public override string WhatItChanges => "";

        public override string WhyItHelps => "";

        public override string ExpectedBenefit => "";

        public override OptimizationCategory Category => OptimizationCategory.Gaming;

        public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default) => _inner.GetStateAsync(cancellationToken);

        public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default) => _inner.CreateBackupAsync(cancellationToken);

        public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
        {
            spi.Mouse = spi.Mouse with { Threshold1 = 99 }; // changes something, but not the target
            return Task.CompletedTask;
        }

        public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default) => _inner.RestoreAsync(backup, cancellationToken);
    }

    private sealed class ProbeOptimization(Func<int> backupCount) : OptimizationBase
    {
        private bool _applied;

        public int BackupsAtApplyTime { get; private set; } = -1;

        public override string Id => "probe";

        public override string Name => "Probe";

        public override string Description => "";

        public override string WhatItChanges => "";

        public override string WhyItHelps => "";

        public override string ExpectedBenefit => "";

        public override OptimizationCategory Category => OptimizationCategory.Windows;

        public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(new OptimizationState(_applied ? OptimizationStatus.Applied : OptimizationStatus.NotApplied, _applied ? "on" : "off"));

        public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default) => Task.FromResult(NewBackup());

        public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
        {
            BackupsAtApplyTime = backupCount();
            _applied = true;
            return Task.CompletedTask;
        }

        public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
        {
            _applied = false;
            return Task.CompletedTask;
        }
    }
}

public class ConcreteOptimizationTests
{
    [Fact]
    public async Task PowerPlanPrefersHighPerformanceAndRestores()
    {
        var power = new FakePower();
        power.Schemes.AddRange([new PowerScheme(PowerSchemes.Balanced, "Balanced", true), new PowerScheme(PowerSchemes.HighPerformance, "High performance", false)]);
        var opt = new PowerPlanOptimization(power);
        Assert.Equal(OptimizationStatus.NotApplied, (await opt.GetStateAsync()).Status);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.Equal(PowerSchemes.HighPerformance, power.Active);
        Assert.Equal(OptimizationStatus.Applied, (await opt.GetStateAsync()).Status);
        await opt.RestoreAsync(backup);
        Assert.Equal(PowerSchemes.Balanced, power.Active);
    }

    [Fact]
    public async Task PowerPlanUsesOverlayOnModernStandbySystems()
    {
        var power = new FakePower { Overlay = PowerSchemes.OverlayBalanced };
        power.Schemes.Add(new PowerScheme(PowerSchemes.Balanced, "Balanced", true));
        var opt = new PowerPlanOptimization(power);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.Equal(PowerSchemes.OverlayBestPerformance, power.Overlay);
        await opt.RestoreAsync(backup);
        Assert.Equal(PowerSchemes.OverlayBalanced, power.Overlay);
    }

    [Fact]
    public async Task PowerPlanUnavailableWithoutOptions()
    {
        var power = new FakePower();
        power.Schemes.Add(new PowerScheme(PowerSchemes.Balanced, "Balanced", true));
        Assert.Equal(OptimizationStatus.Unavailable, (await new PowerPlanOptimization(power).GetStateAsync()).Status);
    }

    [Fact]
    public async Task MouseAccelerationRoundTrips()
    {
        var spi = new FakeSystemParameters();
        var opt = new MouseAccelerationOptimization(spi);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.Equal(new Platform.MouseSettings(0, 0, 0), spi.Mouse);
        await opt.RestoreAsync(backup);
        Assert.Equal(new Platform.MouseSettings(6, 10, 1), spi.Mouse);
    }

    [Fact]
    public async Task WindowedGamesPreservesOtherDirectXSettings()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName, RegistryValue.FromString("VRROptimizeEnable=0;AutoHDREnable=1;"));
        var opt = new WindowedGamesOptimization(registry, new FakeOs(26100));
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        var parsed = WindowedGamesOptimization.Parse(registry.GetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName)!.AsString());
        Assert.Equal("0", parsed["VRROptimizeEnable"]);
        Assert.Equal("1", parsed["AutoHDREnable"]);
        Assert.Equal("1", parsed["SwapEffectUpgradeEnable"]);
        await opt.RestoreAsync(backup);
        Assert.Equal("VRROptimizeEnable=0;AutoHDREnable=1;", registry.GetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName)!.AsString());
    }

    [Fact]
    public async Task WindowedGamesUnavailableOnOldBuilds()
        => Assert.Equal(OptimizationStatus.Unavailable, (await new WindowedGamesOptimization(new InMemoryRegistry(), new FakeOs(19045)).GetStateAsync()).Status);

    [Fact]
    public void RegistryValuesRoundTripThroughBackupFormat()
    {
        RegistryValue[] values =
        [
            RegistryValue.FromDWord(-5), RegistryValue.FromQWord(long.MaxValue), RegistryValue.FromString("a;b"),
            RegistryValue.FromExpandString("%TEMP%"), RegistryValue.FromBinary([1, 2, 3]), RegistryValue.FromMultiString(["x", "y z"]),
        ];
        foreach (var value in values)
        {
            Assert.Equal(value, RegistryValue.Deserialize(value.Type, value.Serialize()));
        }
    }

    [Fact]
    public async Task CatalogGameModeTreatsAbsentAsWindowsDefault()
    {
        var registry = new InMemoryRegistry();
        var catalog = new StandardOptimizationCatalog(registry, new FakePower(), new FakeSystemParameters(), new FakeOs(26100), new FakeGraphics(true),
            new StartupAppsService(registry, null, null), new OptimizationCatalogOptions { TempDirectories = [], QuarantineDirectory = Path.GetTempPath() },
            NullLogger<StandardOptimizationCatalog>.Instance);
        var gameMode = catalog.Find(StandardOptimizationCatalog.GameModeId)!;
        Assert.Equal(OptimizationStatus.Applied, (await gameMode.GetStateAsync()).Status);
        registry.SetValue(RegistryRoot.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled", RegistryValue.FromDWord(0));
        Assert.Equal(OptimizationStatus.NotApplied, (await gameMode.GetStateAsync()).Status);
        Assert.Equal(OptimizationStatus.Unavailable, (await new StandardOptimizationCatalog(registry, new FakePower(), new FakeSystemParameters(), new FakeOs(26100), new FakeGraphics(null),
            new StartupAppsService(registry, null, null), new OptimizationCatalogOptions { TempDirectories = [], QuarantineDirectory = Path.GetTempPath() },
            NullLogger<StandardOptimizationCatalog>.Instance).Find(StandardOptimizationCatalog.HagsId)!.GetStateAsync()).Status);
        Assert.All(catalog.StaticOptimizations, o => Assert.False(string.IsNullOrWhiteSpace(o.WhatItChanges + o.WhyItHelps + o.ExpectedBenefit)));
        Assert.Equal(catalog.StaticOptimizations.Count, catalog.StaticOptimizations.Select(o => o.Id).Distinct().Count());
    }

    private sealed class FakeGraphics(bool? supported) : IGraphicsCapabilities
    {
        public bool? IsHardwareSchedulingSupported => supported;
    }
}

public class StartupAppsTests
{
    [Fact]
    public async Task DisableAndRestoreStartupEntry()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, StartupAppsService.RunKey, "Chat", RegistryValue.FromString(@"""C:\Apps\Chat\chat.exe"" --minimized"));
        var service = new StartupAppsService(registry, null, null);
        var entry = Assert.Single(service.GetEntries());
        Assert.True(entry.IsEnabled);
        Assert.False(entry.RequiresAdministrator);

        var opt = new StartupEntryOptimization(registry, service, entry);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.False(service.GetEntries()[0].IsEnabled);
        Assert.NotNull(registry.GetValue(RegistryRoot.CurrentUser, StartupAppsService.RunKey, "Chat")); // never deleted
        await opt.RestoreAsync(backup);
        Assert.True(service.GetEntries()[0].IsEnabled);
    }

    [Fact]
    public void SecuritySoftwareIsProtected()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.LocalMachine, StartupAppsService.RunKey, "SecurityHealth", RegistryValue.FromExpandString(@"%windir%\system32\SecurityHealthSystray.exe"));
        registry.SetValue(RegistryRoot.LocalMachine, StartupAppsService.RunKey, "AvastUI", RegistryValue.FromString(@"C:\Program Files\Avast\AvastUI.exe"));
        var entries = new StartupAppsService(registry, null, null).GetEntries();
        Assert.All(entries, e => Assert.True(e.IsProtected));
        Assert.All(entries, e => Assert.True(e.RequiresAdministrator));
    }

    [Theory]
    [InlineData(new byte[] { 0x02, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0 }, true)]
    [InlineData(new byte[] { 0x03, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, false)]
    public void ParsesStartupApprovedFlags(byte[] data, bool enabled)
        => Assert.Equal(enabled, StartupAppsService.IsEnabledFromApproved(RegistryValue.FromBinary(data)));

    [Fact]
    public void IdsRoundTrip()
    {
        var id = StartupAppsService.CreateId(StartupSource.MachineRun32, "Some:App");
        Assert.True(StartupAppsService.TryParseId(id, out var source, out var name));
        Assert.Equal(StartupSource.MachineRun32, source);
        Assert.Equal("Some:App", name);
        Assert.False(StartupAppsService.TryParseId("power.x", out _, out _));
    }
}

public class TempCleanupTests
{
    [Fact]
    public async Task QuarantinesOldFilesAndUndoRestoresThem()
    {
        using var temp = new TempDirectory();
        var source = Path.Combine(temp.Path, "src");
        var quarantine = Path.Combine(temp.Path, "q");
        Directory.CreateDirectory(Path.Combine(source, "sub"));
        var oldFile = Path.Combine(source, "sub", "old.tmp");
        var newFile = Path.Combine(source, "new.tmp");
        await File.WriteAllTextAsync(oldFile, new string('x', 1000));
        await File.WriteAllTextAsync(newFile, "fresh");
        File.SetLastWriteTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));
        File.SetLastAccessTimeUtc(oldFile, DateTime.UtcNow.AddDays(-3));

        var opt = new TempFilesCleanupOptimization([source], quarantine, TimeSpan.FromHours(24), NullLogger.Instance);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.False(File.Exists(oldFile));
        Assert.True(File.Exists(newFile));
        Assert.Equal("1", backup.Properties["movedFiles"]);

        await opt.RestoreAsync(backup);
        Assert.True(File.Exists(oldFile));
        Assert.Equal(1000, new FileInfo(oldFile).Length);
        Assert.False(Directory.Exists(backup.Properties["quarantinePath"]));
    }

    [Fact]
    public async Task RejectsBackupPointingOutsideQuarantine()
    {
        using var temp = new TempDirectory();
        var opt = new TempFilesCleanupOptimization([temp.Path], Path.Combine(temp.Path, "q"), TimeSpan.FromHours(24), NullLogger.Instance);
        var backup = new OptimizationBackup();
        backup.Properties["quarantinePath"] = Path.GetTempPath();
        await Assert.ThrowsAsync<InvalidOperationException>(() => opt.RestoreAsync(backup));
    }

    [Fact]
    public void FormatsBytes()
    {
        Assert.Equal("512 B", TempFilesCleanupOptimization.FormatBytes(512));
        Assert.EndsWith("MB", TempFilesCleanupOptimization.FormatBytes(5 * 1024 * 1024), StringComparison.Ordinal);
    }
}

public class PreferenceSafetyTests
{
    [Fact]
    public void PersonalPreferencesAreNeverAutoSelected()
    {
        Assert.False(new MouseAccelerationOptimization(new FakeSystemParameters()).IsQuickOptimizeCandidate);
        Assert.False(new UiAnimationOptimization(new FakeSystemParameters()).IsQuickOptimizeCandidate);
    }
}

public class NewTweakTests
{
    [Fact]
    public async Task AccessibilityShortcutsClearOnlyHotkeyFlagAndRestore()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags", RegistryValue.FromString("510"));
        // Toggle keys feature actively used (bit 1 set) must be left alone.
        registry.SetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags", RegistryValue.FromString("63"));
        var opt = new AccessibilityShortcutsOptimization(registry);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        Assert.Equal("506", registry.GetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags")!.AsString());
        Assert.Equal("122", registry.GetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags")!.AsString());
        Assert.Equal("63", registry.GetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\ToggleKeys", "Flags")!.AsString());
        await opt.RestoreAsync(backup);
        Assert.Equal("510", registry.GetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\StickyKeys", "Flags")!.AsString());
        Assert.Null(registry.GetValue(RegistryRoot.CurrentUser, @"Control Panel\Accessibility\Keyboard Response", "Flags"));
    }

    [Fact]
    public async Task VariableRefreshRatePreservesOtherSettings()
    {
        var registry = new InMemoryRegistry();
        registry.SetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName, RegistryValue.FromString("SwapEffectUpgradeEnable=1;"));
        var opt = new VariableRefreshRateOptimization(registry, new FakeOs(26100));
        Assert.Equal(OptimizationStatus.NotApplied, (await opt.GetStateAsync()).Status);
        var backup = await opt.CreateBackupAsync();
        await opt.ApplyAsync(backup);
        var parsed = WindowedGamesOptimization.Parse(registry.GetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName)!.AsString());
        Assert.Equal("1", parsed["SwapEffectUpgradeEnable"]);
        Assert.Equal("1", parsed["VRROptimizeEnable"]);
        await opt.RestoreAsync(backup);
        Assert.Equal("SwapEffectUpgradeEnable=1;", registry.GetValue(RegistryRoot.CurrentUser, WindowedGamesOptimization.KeyPath, WindowedGamesOptimization.ValueName)!.AsString());
    }

    [Fact]
    public void CatalogHasSeventeenDistinctTweaks()
    {
        var registry = new InMemoryRegistry();
        var catalog = new StandardOptimizationCatalog(registry, new FakePower(), new FakeSystemParameters(), new FakeOs(26100), new FakeGraphicsCaps(),
            new StartupAppsService(registry, null, null), new OptimizationCatalogOptions { TempDirectories = [], QuarantineDirectory = Path.GetTempPath() },
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StandardOptimizationCatalog>.Instance);
        Assert.Equal(17, catalog.StaticOptimizations.Count);
        Assert.Equal(17, catalog.StaticOptimizations.Select(o => o.Id).Distinct().Count());
    }

    private sealed class FakeGraphicsCaps : IGraphicsCapabilities
    {
        public bool? IsHardwareSchedulingSupported => true;
    }
}
