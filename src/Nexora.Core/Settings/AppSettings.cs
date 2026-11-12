using Microsoft.Extensions.Logging;
using Nexora.Core.Storage;

namespace Nexora.Core.Settings;

public enum AppTheme
{
    Dark,
    Light,
    System,
}

public sealed class AppSettings
{
    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.Dark;

    public string AccentColor { get; set; } = "#1FB6FF";

    public bool AnimationsEnabled { get; set; } = true;

    /// <summary>Enables Mica/Acrylic backdrops and composition effects. Disable on very low-end GPUs or remote sessions.</summary>
    public bool HardwareAcceleration { get; set; } = true;

    // Startup behaviour
    public bool StartWithWindows { get; set; }

    public bool StartMinimized { get; set; } = true;

    public bool MinimizeToTray { get; set; } = true;

    public bool CloseToTray { get; set; } = true;

    // Notifications
    public bool NotificationsEnabled { get; set; } = true;

    public bool NotifyOnNetworkDegradation { get; set; } = true;

    // Performance monitoring
    public int MonitoringIntervalMs { get; set; } = 1000;

    public bool MonitorInBackground { get; set; }

    public bool FpsCaptureEnabled { get; set; } = true;

    public int DefaultSessionDurationSeconds { get; set; } = 60;

    // Network monitoring
    public bool NetworkMonitoringEnabled { get; set; } = true;

    public int NetworkMonitorIntervalSeconds { get; set; } = 5;

    public string NetworkTarget { get; set; } = "1.1.1.1";

    public int NetworkTestSamples { get; set; } = 40;

    // Optimization preferences
    public bool ConfirmBeforeApplying { get; set; } = true;

    public bool RollbackBatchOnFailure { get; set; } = true;

    public bool ShowAdvancedOptimizations { get; set; } = true;

    // State
    public bool FirstRunCompleted { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Clamps values loaded from disk into safe ranges.</summary>
    public void Normalize()
    {
        MonitoringIntervalMs = Math.Clamp(MonitoringIntervalMs, 500, 10_000);
        NetworkMonitorIntervalSeconds = Math.Clamp(NetworkMonitorIntervalSeconds, 2, 60);
        NetworkTestSamples = Math.Clamp(NetworkTestSamples, 10, 200);
        DefaultSessionDurationSeconds = Math.Clamp(DefaultSessionDurationSeconds, 10, 1800);
        if (!System.Net.IPAddress.TryParse(NetworkTarget, out _))
        {
            NetworkTarget = "1.1.1.1";
        }

        if (string.IsNullOrWhiteSpace(AccentColor) || AccentColor.Length != 7 || AccentColor[0] != '#' || !AccentColor[1..].All(Uri.IsHexDigit))
        {
            AccentColor = "#1FB6FF";
        }
    }
}

public interface ISettingsService
{
    AppSettings Current { get; }

    event EventHandler<AppSettings>? Changed;

    Task LoadAsync(CancellationToken cancellationToken = default);

    /// <summary>Applies a mutation, persists and raises <see cref="Changed"/>.</summary>
    Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default);
}

public sealed class SettingsService(string path, ILogger<SettingsService> logger) : ISettingsService
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AppSettings Current { get; private set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Current = await JsonFile.ReadAsync<AppSettings>(path, cancellationToken).ConfigureAwait(false) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            logger.LogError(ex, "Settings could not be loaded; defaults are used");
            Current = new AppSettings();
        }

        Current.Normalize();
    }

    public async Task UpdateAsync(Action<AppSettings> mutate, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        AppSettings snapshot;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var next = Current.Clone();
            mutate(next);
            next.Normalize();
            await JsonFile.WriteAsync(path, next, cancellationToken).ConfigureAwait(false);
            Current = next;
            snapshot = next;
        }
        finally
        {
            _gate.Release();
        }

        Changed?.Invoke(this, snapshot);
    }
}
