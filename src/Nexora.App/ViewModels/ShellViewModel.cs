using Microsoft.Extensions.Logging;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Hardware;
using Nexora.Core.Monitoring;
using Nexora.Core.Platform;
using Nexora.Platform.Windows.Services;

namespace Nexora.App.ViewModels;

/// <summary>Header/title bar state: system identity and live status.</summary>
public sealed class ShellViewModel : ObservableObject
{
    private readonly MetricsMonitor _monitor;
    private readonly UiDispatcher _dispatcher;
    private string _osText = Format.Dash;
    private string _cpuText = Format.Dash;
    private string _gpuText = Format.Dash;
    private string _ramText = Format.Dash;
    private string _systemStatus = "Checking…";
    private string _systemStatusDetail = string.Empty;
    private StatusLevel _systemLevel = StatusLevel.Neutral;
    private string _appStatus = "Monitoring";

    public ShellViewModel(MetricsMonitor monitor, ISystemInfoProvider systemInfo, IPrivilegeService privileges, UiDispatcher dispatcher, ILogger<ShellViewModel> logger)
    {
        _monitor = monitor;
        _dispatcher = dispatcher;
        IsElevated = privileges.IsElevated;
        RestartElevatedCommand = new RelayCommand(RestartElevated, () => !IsElevated);
        _monitor.SnapshotAvailable += OnSnapshot;
        _ = LoadSystemInfoAsync(systemInfo, logger);
    }

    public bool IsElevated { get; }

    public bool IsNotElevated => !IsElevated;

    public string ElevationText => IsElevated ? "Administrator" : "Standard user";

    public RelayCommand RestartElevatedCommand { get; }

    public string OsText { get => _osText; private set => SetProperty(ref _osText, value); }

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }

    public string GpuText { get => _gpuText; private set => SetProperty(ref _gpuText, value); }

    public string RamText { get => _ramText; private set => SetProperty(ref _ramText, value); }

    public string SystemStatus { get => _systemStatus; private set => SetProperty(ref _systemStatus, value); }

    public string SystemStatusDetail { get => _systemStatusDetail; private set => SetProperty(ref _systemStatusDetail, value); }

    public StatusLevel SystemLevel { get => _systemLevel; private set => SetProperty(ref _systemLevel, value); }

    public string AppStatus { get => _appStatus; set => SetProperty(ref _appStatus, value); }

    private async Task LoadSystemInfoAsync(ISystemInfoProvider provider, ILogger logger)
    {
        try
        {
            var info = await Task.Run(provider.GetSystemInfo);
            _dispatcher.Post(() =>
            {
                OsText = string.IsNullOrEmpty(info.OsVersion) ? info.OsName : $"{info.OsName} {info.OsVersion}";
                CpuText = Shorten(info.CpuName);
                GpuText = info.PrimaryGpu is { } gpu ? Shorten(gpu.Name) : "No dedicated GPU detected";
                RamText = Format.Gigabytes(info.TotalMemoryBytes) + " RAM";
            });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "System information unavailable");
            _dispatcher.Post(() => OsText = "Windows");
        }
    }

    private void OnSnapshot(object? sender, MetricSnapshot snapshot) => _dispatcher.Post(() =>
    {
        var cpu = snapshot.CpuUsagePercent ?? 0;
        var ram = snapshot.RamUsagePercent;
        var hot = snapshot.CpuTemperatureCelsius is > 90 || snapshot.GpuTemperatureCelsius is > 87;
        (SystemStatus, SystemLevel, SystemStatusDetail) = (cpu, ram, hot) switch
        {
            (_, _, true) => ("Running hot", StatusLevel.Warning, "High temperature measured"),
            ( > 90, _, _) => ("Under heavy load", StatusLevel.Warning, $"CPU {cpu:0}%"),
            (_, > 90, _) => ("Low memory", StatusLevel.Warning, $"RAM {ram:0}% used"),
            _ => ("Healthy", StatusLevel.Good, $"CPU {cpu:0}% · RAM {ram:0}%"),
        };
    });

    private static string Shorten(string name) => name
        .Replace("(R)", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace("(TM)", string.Empty, StringComparison.OrdinalIgnoreCase)
        .Replace(" CPU", string.Empty, StringComparison.Ordinal)
        .Replace("  ", " ", StringComparison.Ordinal)
        .Trim();

    private static void RestartElevated()
    {
        if (ElevationHelper.TryRestartElevated("--elevated-restart"))
        {
            App.Shutdown();
        }
    }
}

public enum StatusLevel
{
    Neutral,
    Good,
    Warning,
    Problem,
}
