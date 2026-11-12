using Microsoft.Extensions.Logging;
using Nexora.Platform.Windows.Services;
using Nexora.Platform.Windows.Shell;
using Windows.ApplicationModel;

namespace Nexora.App.Services;

/// <summary>Start with Windows: StartupTask for MSIX installs, the per-user Run key for portable builds.</summary>
public sealed class StartupService(ILogger<StartupService> logger)
{
    private const string TaskId = "NexoraStartup";

    public static bool IsPackaged { get; } = ElevationHelper.IsPackaged();

    public async Task<(bool Enabled, string? Message)> SetEnabledAsync(bool enabled)
    {
        try
        {
            if (IsPackaged)
            {
                var task = await StartupTask.GetAsync(TaskId);
                if (!enabled)
                {
                    task.Disable();
                    return (false, null);
                }

                var state = await task.RequestEnableAsync();
                return state switch
                {
                    StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy => (true, null),
                    StartupTaskState.DisabledByUser => (false, "Startup was disabled in Task Manager or Settings › Apps › Startup. Enable NEXORA there."),
                    StartupTaskState.DisabledByPolicy => (false, "Startup is disabled by policy on this PC."),
                    _ => (false, null),
                };
            }

            var path = Environment.ProcessPath;
            if (string.IsNullOrEmpty(path))
            {
                return (false, "The application path could not be determined.");
            }

            RunKeyStartupRegistration.SetEnabled(enabled, path);
            return (enabled, null);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Changing startup registration failed");
            return (!enabled, "Startup registration could not be changed: " + ex.Message);
        }
    }
}
