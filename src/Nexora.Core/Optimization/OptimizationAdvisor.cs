using Nexora.Core.Optimization.Optimizations;

namespace Nexora.Core.Optimization;

/// <summary>What NEXORA knows about this PC when deciding which changes are worth making.</summary>
public sealed record AdvisorContext(
    bool? HasDedicatedGpu = null,
    bool? HasBattery = null,
    bool? OnBattery = null,
    double? SystemDriveFreePercent = null,
    bool? IsWifi = null);

public sealed record Advice(bool Recommended, string Reason);

/// <summary>
/// Recommends only changes that make sense for the detected hardware and situation, and explains why.
/// Items that are personal preferences or irrelevant on this PC are offered as optional instead.
/// </summary>
public static class OptimizationAdvisor
{
    public static Advice Evaluate(IOptimization optimization, AdvisorContext context)
    {
        ArgumentNullException.ThrowIfNull(optimization);
        ArgumentNullException.ThrowIfNull(context);
        switch (optimization.Id)
        {
            case StandardOptimizationCatalog.TransparencyId:
                return context.HasDedicatedGpu switch
                {
                    false => new Advice(true, "Your PC uses integrated graphics, which shares memory bandwidth with desktop effects."),
                    true => new Advice(false, "Your dedicated graphics card handles transparency effortlessly — only change this if you prefer the look."),
                    _ => new Advice(false, "Mainly useful with integrated graphics."),
                };

            case PowerPlanOptimization.OptimizationId:
                if (context.OnBattery == true)
                {
                    return new Advice(false, "You are on battery power: a performance plan would drain the battery much faster. Plug in before gaming.");
                }

                return context.HasBattery == true
                    ? new Advice(true, "Laptop on mains power: lets the CPU boost fully while plugged in (uses more power and heat).")
                    : new Advice(true, "Desktop PC: no battery to save, so the faster CPU response is free.");

            case StandardOptimizationCatalog.StorageSenseId:
                return context.SystemDriveFreePercent is < 25
                    ? new Advice(true, $"Your system drive has only {context.SystemDriveFreePercent:0}% free; automatic cleanup keeps it from filling up.")
                    : new Advice(false, "You have plenty of free space right now.");

            case TempFilesCleanupOptimization.OptimizationId:
                return new Advice(true, "Temporary files are taking up noticeable space on your drive.");

            case StandardOptimizationCatalog.DeliveryOptimizationId:
                return context.IsWifi == true
                    ? new Advice(true, "On Wi-Fi your upload bandwidth is limited; background update uploads can add lag.")
                    : new Advice(true, "Stops Windows from uploading updates to strangers while you play online.");

            case VariableRefreshRateOptimization.OptimizationId:
                return new Advice(true, "Helps if your monitor supports G-SYNC or FreeSync; harmless otherwise.");

            case MouseAccelerationOptimization.OptimizationId:
                return new Advice(false, "Personal preference — only change it if you want raw, acceleration-free aiming.");

            case UiAnimationOptimization.OptimizationId:
            case StandardOptimizationCatalog.MenuDelayId:
            case StandardOptimizationCatalog.StartupDelayId:
            case StandardOptimizationCatalog.TipsId:
                return new Advice(false, "Comfort tweak for the desktop — no effect on in-game performance.");

            default:
                return new Advice(true, optimization.Category switch
                {
                    OptimizationCategory.Gaming => "Relevant for every gaming PC.",
                    OptimizationCategory.Network => "Keeps background traffic away from your games.",
                    _ => "Recommended for this PC.",
                });
        }
    }
}
