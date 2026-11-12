using Microsoft.Extensions.Logging.Abstractions;
using Nexora.Core.Games;
using Nexora.Core.Logging;
using Nexora.Core.Monitoring;
using Nexora.Core.Optimization;
using Nexora.Core.Processes;
using Nexora.Core.Security;
using Nexora.Core.Settings;

namespace Nexora.Core.Tests;

public class StorageTests
{
    [Fact]
    public async Task BackupStoreRoundTripsAndRejectsTraversal()
    {
        using var temp = new TempDirectory();
        var store = new FileBackupStore(temp.Path);
        var backup = new OptimizationBackup { OptimizationId = "x", PreviousValue = "before" };
        backup.Registry.Add(new RegistrySnapshot { KeyPath = "k", ValueName = "v", Existed = true, Type = Platform.RegistryValueType.DWord, SerializedData = "3" });
        await store.SaveAsync(backup);
        var loaded = await store.LoadAsync(backup.Id);
        Assert.NotNull(loaded);
        Assert.Equal("before", loaded.PreviousValue);
        Assert.Equal(3, loaded.Registry[0].ToValue()!.AsDWord());
        await Assert.ThrowsAsync<ArgumentException>(() => store.LoadAsync(@"..\..\evil"));
    }

    [Fact]
    public async Task HistorySurvivesRestartAndCorruption()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "history.json");
        var store = new FileHistoryStore(path, NullLogger<FileHistoryStore>.Instance);
        await store.AddAsync(new OptimizationHistoryEntry { OptimizationId = "a", Status = HistoryStatus.Applied });
        var reopened = new FileHistoryStore(path, NullLogger<FileHistoryStore>.Instance);
        Assert.Single(await reopened.GetAllAsync());

        await File.WriteAllTextAsync(path, "{ not json");
        var corrupt = new FileHistoryStore(path, NullLogger<FileHistoryStore>.Instance);
        Assert.Empty(await corrupt.GetAllAsync()); // no crash
        Assert.Single(Directory.GetFiles(temp.Path, "history.json.corrupt-*"));
    }

    [Fact]
    public async Task SettingsAreNormalizedAndPersisted()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
        await service.LoadAsync();
        Assert.Equal(AppTheme.Dark, service.Current.Theme);
        AppSettings? raised = null;
        service.Changed += (_, s) => raised = s;
        await service.UpdateAsync(s =>
        {
            s.MonitoringIntervalMs = 1;
            s.NetworkTarget = "not-an-ip";
            s.AccentColor = "blue";
            s.Theme = AppTheme.Light;
        });
        Assert.NotNull(raised);
        var reloaded = new SettingsService(path, NullLogger<SettingsService>.Instance);
        await reloaded.LoadAsync();
        Assert.Equal(500, reloaded.Current.MonitoringIntervalMs);
        Assert.Equal("1.1.1.1", reloaded.Current.NetworkTarget);
        Assert.Equal("#1FB6FF", reloaded.Current.AccentColor);
        Assert.Equal(AppTheme.Light, reloaded.Current.Theme);
    }

    [Fact]
    public async Task CorruptSettingsFallBackToDefaults()
    {
        using var temp = new TempDirectory();
        var path = Path.Combine(temp.Path, "settings.json");
        await File.WriteAllTextAsync(path, "<<<");
        var service = new SettingsService(path, NullLogger<SettingsService>.Instance);
        await service.LoadAsync();
        Assert.True(service.Current.AnimationsEnabled);
    }
}

public class ErrorHandlingTests
{
    [Fact]
    public void MonitorSurvivesFailingSensors()
    {
        using var monitor = new MetricsMonitor(new ThrowingMetricsProvider(), NullLogger<MetricsMonitor>.Instance);
        var snapshot = monitor.SampleNow();
        Assert.Null(snapshot.CpuUsagePercent);
        Assert.Null(snapshot.Fps);
        Assert.Single(monitor.GetHistory());
    }

    [Fact]
    public void LogSanitizerRemovesPersonalData()
    {
        var text = LogSanitizer.Sanitize(@"Failed C:\Users\JaneDoe\AppData\x.json for jane.doe@example.com");
        Assert.DoesNotContain("JaneDoe", text, StringComparison.Ordinal);
        Assert.DoesNotContain("example.com", text, StringComparison.Ordinal);
    }

    [Fact]
    public void FileLoggerWritesStructuredLines()
    {
        using var temp = new TempDirectory();
        using (var provider = new FileLoggerProvider(temp.Path))
        {
            var logger = provider.CreateLogger("Test");
            Microsoft.Extensions.Logging.LoggerExtensions.LogInformation(logger, "Applied {OptimizationId}", "gaming.game-mode");
        }

        var line = File.ReadAllLines(Directory.GetFiles(temp.Path, "*.log").Single()).Single();
        using var json = System.Text.Json.JsonDocument.Parse(line);
        Assert.Equal("Information", json.RootElement.GetProperty("level").GetString());
        Assert.Equal("gaming.game-mode", json.RootElement.GetProperty("props").GetProperty("OptimizationId").GetString());
    }
}

public class SystemDetectionTests
{
    [Fact]
    public void ParsesSteamLibraryFolders()
    {
        const string vdf = """
            "libraryfolders"
            {
                "0"
                {
                    "path"		"C:\\Program Files (x86)\\Steam"
                    "apps" { "730" "123" }
                }
                "1"
                {
                    "path"		"D:\\SteamLibrary"
                }
            }
            """;
        Assert.Equal([@"C:\Program Files (x86)\Steam", @"D:\SteamLibrary"], SteamLibrary.ParseLibraryFolders(vdf));
    }

    [Fact]
    public void ParsesSteamManifestAndFiltersTools()
    {
        var game = SteamLibrary.ParseAppManifest("""
            "AppState" { "appid" "730" "name" "Counter-Strike 2" "installdir" "Counter-Strike Global Offensive" "StateFlags" "4" }
            """)!;
        Assert.Equal("730", game.AppId);
        Assert.True(game.FullyInstalled);
        Assert.True(SteamLibrary.IsGame(game));
        var redist = SteamLibrary.ParseAppManifest("""
            "AppState" { "appid" "228980" "name" "Steamworks Common Redistributables" "installdir" "Steamworks Shared" "StateFlags" "4" }
            """)!;
        Assert.False(SteamLibrary.IsGame(redist));
        Assert.Null(SteamLibrary.ParseAppManifest("garbage"));
    }

    [Fact]
    public void ParsesEpicManifest()
    {
        var item = EpicManifest.Parse("""
            { "DisplayName": "Fortnite", "InstallLocation": "C:\\Epic\\Fortnite", "LaunchExecutable": "FortniteGame/Binaries/Win64/FortniteLauncher.exe",
              "AppName": "Fortnite", "AppCategories": ["public", "games"], "bIsIncompleteInstall": false }
            """)!;
        Assert.Equal("Fortnite", item.DisplayName);
        Assert.True(item.IsGame);
        Assert.Null(EpicManifest.Parse("""{ "DisplayName": "X", "bIsIncompleteInstall": true }"""));
        Assert.Null(EpicManifest.Parse("not json"));
    }

    [Fact]
    public void ParsesRiotMetadata()
    {
        Assert.Equal(@"C:\Riot Games\VALORANT\live", RiotManifest.ParseInstallPath("product_install_full_path: \"C:/Riot Games/VALORANT/live\"\nother: 1"));
        Assert.Equal(@"C:\Riot Games\Riot Client\RiotClientServices.exe", RiotManifest.ParseClientPath("""{ "rc_default": "C:/Riot Games/Riot Client/RiotClientServices.exe" }"""));
    }

    [Fact]
    public void MergeKeepsUserCustomisations()
    {
        var existing = new List<GameProfile> { new() { Name = "My CS", Source = GameSource.Steam, StoreId = "730", LaunchArguments = "-novid" } };
        var merged = GameProfileStore.Merge(existing, [
            new DetectedGame("Counter-Strike 2", GameSource.Steam, null, "dir", "730", "steam://rungameid/730", null),
            new DetectedGame("Fortnite", GameSource.Epic, @"C:\f.exe", "dir", "Fortnite", null, null),
        ]);
        Assert.Equal(2, merged.Count);
        var cs = merged.Single(p => p.StoreId == "730");
        Assert.Equal("My CS", cs.Name);
        Assert.Equal("-novid", cs.LaunchArguments);
    }

    [Fact]
    public void LaunchValidationBlocksShellsAndUnsafeUris()
    {
        Assert.True(LaunchValidator.IsAllowedLaunchUri("steam://rungameid/730"));
        Assert.True(LaunchValidator.IsAllowedLaunchUri("com.epicgames.launcher://apps/Fortnite?action=launch&silent=true"));
        Assert.False(LaunchValidator.IsAllowedLaunchUri("steam://rungameid/730;calc"));
        Assert.False(LaunchValidator.IsAllowedLaunchUri("file:///C:/Windows/System32/cmd.exe"));
        Assert.False(LaunchValidator.ValidateExecutable(@"C:\Windows\System32\cmd.exe").IsValid);
        Assert.False(LaunchValidator.ValidateExecutable("relative\\game.exe").IsValid);
        Assert.False(LaunchValidator.ValidateExecutable(@"\\server\share\game.exe").IsValid);
        Assert.False(LaunchValidator.ValidateArguments("-a\n-b").IsValid);
        Assert.True(LaunchValidator.ValidateArguments("-novid -high").IsValid);
    }

    [Theory]
    [InlineData("lsass", 700, 0, true)]
    [InlineData("csrss", 600, 1, true)]
    [InlineData("explorer", 5000, 1, true)]
    [InlineData("MsMpEng", 3000, 0, true)]
    [InlineData("SomeService", 3000, 0, true)]
    [InlineData("System", 4, 0, true)]
    [InlineData("chrome", 9000, 1, false)]
    [InlineData("Discord", 9001, 1, false)]
    public void ProtectsCriticalProcesses(string name, int pid, int session, bool expected)
        => Assert.Equal(expected, ProcessProtectionPolicy.Evaluate(name, pid, session, currentProcessId: 1).IsProtected);

    [Fact]
    public void NexoraProtectsItself() => Assert.True(ProcessProtectionPolicy.Evaluate("NEXORA", 42, 1, 42).IsProtected);

    [Fact]
    public void CpuTrackerComputesShareOfMachine()
    {
        var tracker = new ProcessCpuTracker(4);
        var start = DateTimeOffset.UtcNow.AddMinutes(-5);
        var t0 = DateTimeOffset.UtcNow;
        Assert.Null(tracker.Update(1, start, TimeSpan.FromSeconds(10), t0));
        // 1 s of CPU in 1 s wall time on 4 cores = 25 %
        Assert.Equal(25, tracker.Update(1, start, TimeSpan.FromSeconds(11), t0.AddSeconds(1))!.Value, 6);
        // PID reuse (different start time) resets
        Assert.Null(tracker.Update(1, start.AddMinutes(1), TimeSpan.FromSeconds(1), t0.AddSeconds(2)));
    }
}
