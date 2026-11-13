using System.Diagnostics;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Platform;
using Nexora.Core.Settings;

using Nexora.App.Helpers;

namespace Nexora.App.ViewModels;

public sealed record AccentOption(string Hex, string Name)
{
    public Microsoft.UI.Xaml.Media.SolidColorBrush Brush => new(ThemeService.ParseColor(Hex));
}

public sealed class SettingsViewModel : ObservableObject
{
    private readonly ISettingsService _settings;
    private readonly StartupService _startup;
    private readonly IAppPaths _paths;
    private readonly OptimizationCoordinator _coordinator;
    private bool _loading;
    private string _startupMessage = string.Empty;

    public SettingsViewModel(ISettingsService settings, StartupService startup, IAppPaths paths, OptimizationCoordinator coordinator)
    {
        _settings = settings;
        _startup = startup;
        _paths = paths;
        _coordinator = coordinator;
        ThemeOptions = [new(AppTheme.Dark, "Dark"), new(AppTheme.Light, "Light"), new(AppTheme.System, "Use Windows setting")];
        IntervalOptions = [new(500, "0.5 seconds (most responsive)"), new(1000, "1 second (recommended)"), new(2000, "2 seconds"), new(5000, "5 seconds (lowest overhead)")];
        NetworkIntervalOptions = [new(2, "Every 2 seconds"), new(5, "Every 5 seconds (recommended)"), new(15, "Every 15 seconds"), new(60, "Every minute")];
        SampleOptions = [new(20, "20 probes (quick)"), new(40, "40 probes (recommended)"), new(100, "100 probes (thorough)")];
        Accents =
        [
            new("#1FB6FF", "Electric cyan"), new("#3D8BFF", "Azure"), new("#00C2A8", "Teal"), new("#7C6CFF", "Indigo"), new("#FF5C8A", "Rose"), new("#FFB020", "Amber"),
        ];
        OpenLogsCommand = new RelayCommand(() => OpenFolder(_paths.LogsDirectory));
        OpenDataCommand = new RelayCommand(() => OpenFolder(_paths.DataDirectory));
        RestoreAllCommand = new AsyncRelayCommand(_coordinator.RestoreAllAsync);
        SelectAccentCommand = new RelayCommand(p =>
        {
            if (p is string hex)
            {
                Save(s => s.AccentColor = hex);
                OnPropertyChanged(nameof(AccentColor));
            }
        });
    }

    public IReadOnlyList<OptionItem<AppTheme>> ThemeOptions { get; }

    public IReadOnlyList<OptionItem<int>> IntervalOptions { get; }

    public IReadOnlyList<OptionItem<int>> NetworkIntervalOptions { get; }

    public IReadOnlyList<OptionItem<int>> SampleOptions { get; }

    public IReadOnlyList<AccentOption> Accents { get; }

    public RelayCommand OpenLogsCommand { get; }

    public RelayCommand OpenDataCommand { get; }

    public AsyncRelayCommand RestoreAllCommand { get; }

    public RelayCommand SelectAccentCommand { get; }

    public string LogsPath => _paths.LogsDirectory;

    public string StartupMessage { get => _startupMessage; private set => SetProperty(ref _startupMessage, value); }

    public bool HasStartupMessage => StartupMessage.Length > 0;

    private AppSettings S => _settings.Current;

    public OptionItem<AppTheme> SelectedTheme
    {
        get => ThemeOptions.First(o => o.Value == S.Theme);
        set
        {
            if (value is not null && value.Value != S.Theme)
            {
                Save(s => s.Theme = value.Value);
            }
        }
    }

    public string AccentColor => S.AccentColor;

    public bool AnimationsEnabled { get => S.AnimationsEnabled; set => Save(s => s.AnimationsEnabled = value); }

    public bool HardwareAcceleration { get => S.HardwareAcceleration; set => Save(s => s.HardwareAcceleration = value); }

    public bool StartWithWindows
    {
        get => S.StartWithWindows;
        set
        {
            if (value != S.StartWithWindows && !_loading)
            {
                ApplyStartupAsync(value).Forget("change startup registration");
            }
        }
    }

    public bool StartMinimized { get => S.StartMinimized; set => Save(s => s.StartMinimized = value); }

    public bool MinimizeToTray { get => S.MinimizeToTray; set => Save(s => s.MinimizeToTray = value); }

    public bool CloseToTray { get => S.CloseToTray; set => Save(s => s.CloseToTray = value); }

    public bool NotificationsEnabled { get => S.NotificationsEnabled; set => Save(s => s.NotificationsEnabled = value); }

    public bool NotifyOnNetworkDegradation { get => S.NotifyOnNetworkDegradation; set => Save(s => s.NotifyOnNetworkDegradation = value); }

    public OptionItem<int> SelectedInterval
    {
        get => IntervalOptions.FirstOrDefault(o => o.Value == S.MonitoringIntervalMs) ?? IntervalOptions[1];
        set
        {
            if (value is not null)
            {
                Save(s => s.MonitoringIntervalMs = value.Value);
            }
        }
    }

    public bool MonitorInBackground { get => S.MonitorInBackground; set => Save(s => s.MonitorInBackground = value); }

    public bool FpsCaptureEnabled { get => S.FpsCaptureEnabled; set => Save(s => s.FpsCaptureEnabled = value); }

    public bool NetworkMonitoringEnabled { get => S.NetworkMonitoringEnabled; set => Save(s => s.NetworkMonitoringEnabled = value); }

    public OptionItem<int> SelectedNetworkInterval
    {
        get => NetworkIntervalOptions.FirstOrDefault(o => o.Value == S.NetworkMonitorIntervalSeconds) ?? NetworkIntervalOptions[1];
        set
        {
            if (value is not null)
            {
                Save(s => s.NetworkMonitorIntervalSeconds = value.Value);
            }
        }
    }

    public OptionItem<int> SelectedSamples
    {
        get => SampleOptions.FirstOrDefault(o => o.Value == S.NetworkTestSamples) ?? SampleOptions[1];
        set
        {
            if (value is not null)
            {
                Save(s => s.NetworkTestSamples = value.Value);
            }
        }
    }

    public string NetworkTarget
    {
        get => S.NetworkTarget;
        set
        {
            if (System.Net.IPAddress.TryParse(value, out _))
            {
                Save(s => s.NetworkTarget = value.Trim());
            }
        }
    }

    public bool ConfirmBeforeApplying { get => S.ConfirmBeforeApplying; set => Save(s => s.ConfirmBeforeApplying = value); }

    public bool RollbackBatchOnFailure { get => S.RollbackBatchOnFailure; set => Save(s => s.RollbackBatchOnFailure = value); }

    private void Save(Action<AppSettings> mutate)
    {
        if (_loading)
        {
            return;
        }

        _settings.UpdateAsync(mutate).Forget("save settings");
    }

    private async Task ApplyStartupAsync(bool enable)
    {
        var (enabled, message) = await _startup.SetEnabledAsync(enable);
        await _settings.UpdateAsync(s => s.StartWithWindows = enabled);
        StartupMessage = message ?? string.Empty;
        OnPropertyChanged(nameof(HasStartupMessage));
        OnPropertyChanged(nameof(StartWithWindows));
    }

    public void Reload()
    {
        _loading = true;
        try
        {
            OnPropertyChanged(string.Empty);
        }
        finally
        {
            _loading = false;
        }
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        // explorer.exe with a validated, existing directory path only.
        Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"))
        {
            UseShellExecute = false,
            ArgumentList = { path },
        })?.Dispose();
    }
}

public sealed class AboutViewModel(IAppPaths paths, IPrivilegeService privileges, IOsInfo os)
{
    public string Version => typeof(AboutViewModel).Assembly.GetName().Version?.ToString(3) ?? "1.0.0";

    public string Runtime => $".NET {Environment.Version} · Windows App SDK · {(StartupService.IsPackaged ? "MSIX package" : "Portable build")}";

    public string Platform => $"{os.ProductName} {os.DisplayVersion} (build {os.Build}) · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";

    public string Elevation => privileges.IsElevated ? "Running as administrator" : "Running as standard user (least privilege)";

    public string DataPath => paths.DataDirectory;
}

public sealed class WelcomeViewModel(NavigationService navigation)
{
    public RelayCommand StartCommand { get; } = new(() => navigation.Navigate("scan", "first-run"));
}
