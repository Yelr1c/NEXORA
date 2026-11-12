using Microsoft.Extensions.Logging;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Platform;

namespace Nexora.Core.Optimization;

/// <summary>Capabilities that can only be detected by the platform layer.</summary>
public interface IGraphicsCapabilities
{
    /// <summary>True/false when known, null when the driver capability could not be queried.</summary>
    bool? IsHardwareSchedulingSupported { get; }
}

public sealed class OptimizationCatalogOptions
{
    public required IReadOnlyList<string> TempDirectories { get; init; }

    public required string QuarantineDirectory { get; init; }

    public TimeSpan TempFileMinimumAge { get; init; } = TimeSpan.FromHours(24);
}

/// <summary>
/// The complete, curated list of NEXORA optimizations. Every item is documented, reversible and
/// limited to settings that Windows itself exposes in Settings or Group Policy.
/// </summary>
public sealed class StandardOptimizationCatalog : IOptimizationCatalog
{
    public const string GameModeId = "gaming.game-mode";
    public const string GameDvrId = "gaming.background-recording";
    public const string HagsId = "gaming.hardware-gpu-scheduling";
    public const string TransparencyId = "windows.transparency";
    public const string StorageSenseId = "storage.storage-sense";
    public const string DeliveryOptimizationId = "network.delivery-optimization-lan";

    private const string GameBarKey = @"Software\Microsoft\GameBar";
    private const string GameDvrKey = @"Software\Microsoft\Windows\CurrentVersion\GameDVR";
    private const string GraphicsDriversKey = @"SYSTEM\CurrentControlSet\Control\GraphicsDrivers";
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string StorageSenseKey = @"Software\Microsoft\Windows\CurrentVersion\StorageSense\Parameters\StoragePolicy";
    private const string DeliveryOptimizationPolicyKey = @"SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization";

    private readonly IRegistryAccessor _registry;
    private readonly StartupAppsService _startup;
    private readonly IReadOnlyList<IOptimization> _static;

    public StandardOptimizationCatalog(
        IRegistryAccessor registry,
        IPowerManager power,
        ISystemParameters systemParameters,
        IOsInfo os,
        IGraphicsCapabilities graphics,
        StartupAppsService startup,
        OptimizationCatalogOptions options,
        ILogger<StandardOptimizationCatalog> logger)
    {
        _registry = registry;
        _startup = startup;
        _static =
        [
            CreateGameMode(registry),
            new WindowedGamesOptimization(registry, os),
            CreateBackgroundRecording(registry),
            CreateHardwareScheduling(registry, graphics),
            new MouseAccelerationOptimization(systemParameters),
            new PowerPlanOptimization(power),
            CreateTransparency(registry),
            new UiAnimationOptimization(systemParameters),
            CreateDeliveryOptimization(registry),
            new TempFilesCleanupOptimization(options.TempDirectories, options.QuarantineDirectory, options.TempFileMinimumAge, logger),
            CreateStorageSense(registry),
        ];
    }

    public IReadOnlyList<IOptimization> StaticOptimizations => _static;

    public IReadOnlyList<IOptimization> GetAll()
    {
        var all = new List<IOptimization>(_static);
        all.AddRange(GetStartupOptimizations());
        return all;
    }

    public IReadOnlyList<StartupEntryOptimization> GetStartupOptimizations()
        => _startup.GetEntries().Select(e => new StartupEntryOptimization(_registry, _startup, e)).ToList();

    public IOptimization? Find(string id)
    {
        var item = _static.FirstOrDefault(o => o.Id == id);
        if (item is not null)
        {
            return item;
        }

        if (StartupAppsService.TryParseId(id, out var source, out var name))
        {
            var entry = _startup.GetEntries().FirstOrDefault(e => e.Id == id);
            // The entry may have been removed from Run after we disabled it; we can still restore StartupApproved.
            entry ??= new StartupEntry(id, name, string.Empty, source, false, source is not (StartupSource.UserRun or StartupSource.UserStartupFolder), false, null);
            return new StartupEntryOptimization(_registry, _startup, entry);
        }

        return null;
    }

    private static string OnOff(RegistryValue? value, int onValue, string defaultText)
        => value is null ? defaultText : value.Type == RegistryValueType.DWord ? (value.AsDWord() == onValue ? "On" : "Off") : "Custom";

    private static bool IsDWord(RegistryValue? value, int expected)
        => value is { Type: RegistryValueType.DWord } && value.AsDWord() == expected;

    private static RegistryOptimization CreateGameMode(IRegistryAccessor registry) => new(
        registry,
        new OptimizationDescriptor(
            GameModeId,
            "Windows Game Mode",
            "Make sure Windows Game Mode is turned on.",
            "Sets Settings › Gaming › Game Mode to On for your user account (AutoGameModeEnabled).",
            "Game Mode tells Windows to prioritise the foreground game: it limits Windows Update driver installs and restart notifications during play and gives the game preference for CPU scheduling.",
            "More consistent frame times when background activity occurs. Average FPS usually changes very little.",
            OptimizationCategory.Gaming,
            RiskLevel.Low,
            ImpactLevel.Medium),
        [new RegistryChange(RegistryRoot.CurrentUser, GameBarKey, "AutoGameModeEnabled", RegistryValue.FromDWord(1))],
        current => current[0] is null ? "On (Windows default)" : OnOff(current[0], 1, "On"),
        isApplied: current => current[0] is null || IsDWord(current[0], 1));

    private static RegistryOptimization CreateBackgroundRecording(IRegistryAccessor registry) => new(
        registry,
        new OptimizationDescriptor(
            GameDvrId,
            "Disable background game recording",
            "Turn off \"Record what happened\" in the Xbox Game Bar.",
            "Sets Settings › Gaming › Captures › Record what happened to Off (HistoricalCaptureEnabled). Manual screenshots and recordings with Win+Alt+R keep working.",
            "Background recording continuously encodes the last minutes of gameplay on the GPU and writes it to disk, which costs GPU encoder time, memory and disk bandwidth.",
            "Removes the constant overhead of background capture; most noticeable on lower-end GPUs and laptops.",
            OptimizationCategory.Gaming,
            RiskLevel.Low,
            ImpactLevel.Medium),
        [new RegistryChange(RegistryRoot.CurrentUser, GameDvrKey, "HistoricalCaptureEnabled", RegistryValue.FromDWord(0))],
        current => current[0] is null ? "Off (Windows default)" : OnOff(current[0], 1, "Off"),
        isApplied: current => current[0] is null || IsDWord(current[0], 0));

    private static RegistryOptimization CreateHardwareScheduling(IRegistryAccessor registry, IGraphicsCapabilities graphics) => new(
        registry,
        new OptimizationDescriptor(
            HagsId,
            "Hardware-accelerated GPU scheduling",
            "Let the graphics card manage its own video memory scheduling.",
            "Turns on Settings › System › Display › Graphics › Hardware-accelerated GPU scheduling (HwSchMode in the graphics driver configuration).",
            "With HAGS the GPU schedules its own work instead of the CPU, which can reduce CPU overhead and latency and is required for DLSS Frame Generation.",
            "Small latency and CPU overhead reduction on supported GPUs; required for some frame generation features. Results vary per game and driver.",
            OptimizationCategory.Gaming,
            RiskLevel.Medium,
            ImpactLevel.Medium,
            RequiresAdministrator: true,
            RequiresRestart: true),
        [new RegistryChange(RegistryRoot.LocalMachine, GraphicsDriversKey, "HwSchMode", RegistryValue.FromDWord(2))],
        current => current[0] is null ? "Driver default" : IsDWord(current[0], 2) ? "On" : "Off",
        unavailableReason: () => graphics.IsHardwareSchedulingSupported switch
        {
            false => "Your graphics card or driver does not support hardware-accelerated GPU scheduling.",
            null => "GPU scheduling support could not be determined for this graphics driver.",
            _ => null,
        });

    private static RegistryOptimization CreateTransparency(IRegistryAccessor registry) => new(
        registry,
        new OptimizationDescriptor(
            TransparencyId,
            "Turn off transparency effects",
            "Disable acrylic/blur transparency in the Windows shell.",
            "Sets Settings › Personalization › Colors › Transparency effects to Off for your user account.",
            "Transparency requires the desktop compositor to blur content behind windows and the taskbar. On integrated graphics this uses some GPU time and memory bandwidth.",
            "Slightly less GPU work on the desktop, mainly helpful on integrated graphics. No measurable effect on most dedicated GPUs.",
            OptimizationCategory.Windows,
            RiskLevel.Low,
            ImpactLevel.Low),
        [new RegistryChange(RegistryRoot.CurrentUser, PersonalizeKey, "EnableTransparency", RegistryValue.FromDWord(0))],
        current => current[0] is null ? "On (Windows default)" : OnOff(current[0], 1, "On"));

    private static RegistryOptimization CreateStorageSense(IRegistryAccessor registry) => new(
        registry,
        new OptimizationDescriptor(
            StorageSenseId,
            "Turn on Storage Sense",
            "Let Windows automatically free up space when the drive is low.",
            "Turns on Settings › System › Storage › Storage Sense for your user account, using Windows' default cleanup schedule.",
            "Storage Sense removes temporary files and empties the Recycle Bin on a schedule, preventing the system drive from filling up over time.",
            "Keeps free space healthy automatically. No effect on FPS.",
            OptimizationCategory.Storage,
            RiskLevel.Low,
            ImpactLevel.Low),
        [new RegistryChange(RegistryRoot.CurrentUser, StorageSenseKey, "01", RegistryValue.FromDWord(1))],
        current => current[0] is null ? "Off (not configured)" : OnOff(current[0], 1, "Off"));

    private static RegistryOptimization CreateDeliveryOptimization(IRegistryAccessor registry) => new(
        registry,
        new OptimizationDescriptor(
            DeliveryOptimizationId,
            "Limit update sharing to local network",
            "Stop Windows from uploading updates to other PCs on the internet.",
            "Sets the documented Delivery Optimization policy (DODownloadMode = 1, \"LAN\"). Windows Update keeps working normally and downloads from Microsoft; peer sharing is limited to PCs on your own network. Settings will show the option as managed by policy.",
            "Uploading update data to internet peers uses your upstream bandwidth in the background. Saturated upload can increase latency (bufferbloat) during online games.",
            "Prevents background update uploads from competing with game traffic. Does not lower your base ping.",
            OptimizationCategory.Network,
            RiskLevel.Low,
            ImpactLevel.Low,
            RequiresAdministrator: true),
        [new RegistryChange(RegistryRoot.LocalMachine, DeliveryOptimizationPolicyKey, "DODownloadMode", RegistryValue.FromDWord(1))],
        current => current[0] is null ? "Windows default (may share with internet PCs)" : current[0]!.Type == RegistryValueType.DWord
            ? current[0]!.AsDWord() switch
            {
                0 => "HTTP only (no peering)",
                1 => "Local network only",
                2 => "Group peering",
                3 => "Internet peering",
                99 or 100 => "Simple/bypass mode",
                _ => "Custom",
            }
            : "Custom",
        isApplied: current => current[0] is { Type: RegistryValueType.DWord } v && v.AsDWord() is 0 or 1 or 99 or 100);
}
