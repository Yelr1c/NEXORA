using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexora.Core.Games;
using Nexora.Core.Processes;
using Nexora.Core.Security;

namespace Nexora.Platform.Windows.Games;

public sealed record GameLaunchResult(bool Success, string Message, Process? GameProcess);

/// <summary>Launches game profiles safely: validated executables or allow-listed store URIs only, never a shell.</summary>
public sealed class GameLauncher(IProcessService processes, ILogger<GameLauncher> logger)
{
    public async Task<GameLaunchResult> LaunchAsync(GameProfile profile, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var (argsOk, argsError) = LaunchValidator.ValidateArguments(profile.LaunchArguments);
        if (!argsOk)
        {
            return new GameLaunchResult(false, argsError!, null);
        }

        try
        {
            if (LaunchValidator.IsAllowedLaunchUri(profile.LaunchUri) && string.IsNullOrEmpty(profile.LaunchArguments))
            {
                // Store URIs are handed to the registered launcher by the shell; the URI itself is strictly validated.
                Process.Start(new ProcessStartInfo(profile.LaunchUri!) { UseShellExecute = true })?.Dispose();
            }
            else
            {
                var (exeOk, exeError) = LaunchValidator.ValidateExecutable(profile.ExecutablePath);
                if (!exeOk)
                {
                    return new GameLaunchResult(false, exeError!, null);
                }

                var start = new ProcessStartInfo(profile.ExecutablePath)
                {
                    UseShellExecute = false,
                    Arguments = profile.LaunchArguments ?? string.Empty,
                    WorkingDirectory = Path.GetDirectoryName(profile.ExecutablePath)!,
                };
                Process.Start(start)?.Dispose();
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            logger.LogWarning(ex, "Game launch failed");
            return new GameLaunchResult(false, "Windows could not start the game: " + ex.Message, null);
        }

        // Launchers start the real game process later; wait for it for up to two minutes.
        var processName = profile.EffectiveProcessName;
        var game = string.IsNullOrEmpty(processName) ? null : await WaitForProcessAsync(processName, TimeSpan.FromMinutes(2), cancellationToken).ConfigureAwait(false);
        if (game is not null && profile.Priority is not ProcessPriorityLevel.Normal and not ProcessPriorityLevel.Unknown)
        {
            var result = processes.SetPriority(game.Id, profile.Priority);
            logger.LogInformation("Priority for game process: {Result}", result.Message);
        }

        return new GameLaunchResult(true, game is null ? "Launched. NEXORA could not identify the game process; set its process name in the profile to enable monitoring." : $"{profile.Name} is running.", game);
    }

    private static async Task<Process?> WaitForProcessAsync(string processName, TimeSpan timeout, CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var found = Process.GetProcessesByName(processName);
            if (found.Length > 0)
            {
                foreach (var extra in found.Skip(1))
                {
                    extra.Dispose();
                }

                return found[0];
            }

            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        return null;
    }
}
