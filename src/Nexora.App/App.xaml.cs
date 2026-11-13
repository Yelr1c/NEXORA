using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using Nexora.App.Controls;
using Nexora.App.Services;
using Nexora.App.ViewModels;
using Nexora.Core.Games;
using Nexora.Core.Hardware;
using Nexora.Core.Logging;
using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Platform;
using Nexora.Core.Processes;
using Nexora.Core.Scanning;
using Nexora.Core.Settings;
using Nexora.Platform.Windows.Games;
using Nexora.Platform.Windows.Monitoring;
using Nexora.Platform.Windows.Services;
using Nexora.Platform.Windows.Shell;

using Nexora.App.Helpers;

namespace Nexora.App;

public partial class App : Application
{
    private static App? _current;
    private readonly string[] _args;
    private readonly FileLoggerProvider _logProvider;
    private readonly ILogger<App> _logger;
    private MainWindow? _window;
    private TrayIcon? _tray;
    private bool _shuttingDown;
    private DateTimeOffset _lastNetworkAlert = DateTimeOffset.MinValue;
    private bool _networkWasHealthy = true;

    public App(string[] args)
    {
        _current = this;
        _args = args;
        var paths = AppPaths.CreateDefault();
        paths.EnsureCreated();
        _logProvider = new FileLoggerProvider(paths.LogsDirectory);
        Services = ConfigureServices(paths, _logProvider);
        _logger = GetService<ILogger<App>>();
        _logger.LogInformation("NEXORA {Version} starting (elevated: {Elevated})", typeof(App).Assembly.GetName().Version, GetService<IPrivilegeService>().IsElevated);

        InitializeComponent();
        UnhandledException += OnUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += (_, e) => _logger.LogCritical(e.ExceptionObject as Exception, "Fatal unhandled exception");
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            _logger.LogError(e.Exception, "Unobserved task exception");
            e.SetObserved();
        };

        var settings = GetService<ISettingsService>();
        settings.LoadAsync().GetAwaiter().GetResult();
        Motion.Enabled = settings.Current.AnimationsEnabled;
        // Note: Application.Resources must not be touched in the constructor (WinUI throws E_UNEXPECTED);
        // the accent palette is applied in OnLaunched, before any window or control is created.
    }

    public static IServiceProvider Services { get; private set; } = null!;

    public static T GetService<T>()
        where T : class => Services.GetRequiredService<T>();

    /// <summary>Reports an exception from a command to the log and to the user without crashing.</summary>
    public static void ReportUnhandled(Exception exception)
    {
        _current?._logger.LogError(exception, "Unhandled exception in command");
        _current?._window?.ShowError("Something went wrong", exception.Message);
    }

    /// <summary>Logs a failed background operation (with full details) and informs the user without crashing.</summary>
    public static void ReportBackgroundError(string operation, Exception exception)
    {
        _current?._logger.LogError(exception, "Background operation failed: {Operation}", operation);
        _current?._window?.DispatcherQueue.TryEnqueue(() => _current._window?.ShowError($"Could not {operation}", exception.Message));
    }

    public static void OnRedirectedActivation(AppActivationArguments args)
        => _current?._window?.DispatcherQueue.TryEnqueue(() => _current._window.ShowAndActivate());

    public static void Shutdown() => _current?.ShutdownCore();

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            OnLaunchedCore();
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "Startup failed");
            StartupGuard.Fatal("window creation", ex);
        }
    }

    private void OnLaunchedCore()
    {
        var settings = GetService<ISettingsService>();
        ThemeService.ApplyAccentResources(Resources, settings.Current.AccentColor);
        var dispatcher = GetService<UiDispatcher>();
        _window = GetService<MainWindow>();
        dispatcher.Initialize(_window.DispatcherQueue);
        _window.Initialize();

        StartBackgroundServices(settings.Current);
        settings.Changed += (_, s) => dispatcher.Post(() => ApplySettings(s));
        CreateTray();

        var notifications = GetService<NotificationService>();
        notifications.Fallback = (title, message) => _tray?.ShowBalloon(title, message);
        notifications.Initialize();

        var startPage = PageFromArgs() ?? (settings.Current.FirstRunCompleted ? "dashboard" : "welcome");
        _window.NavigateTo(startPage);

        var launchedAtStartup = _args.Contains("--startup", StringComparer.OrdinalIgnoreCase) || IsStartupTaskActivation();
        if (launchedAtStartup && settings.Current.StartMinimized && settings.Current.FirstRunCompleted)
        {
            GetService<MetricsMonitor>().SetForeground(false);
            _window.HasBeenShown = true;
        }
        else
        {
            _window.ShowAndActivate();
        }

        Task.Run(async () =>
        {
            try
            {
                var purged = await GetService<QuarantineService>().PurgeExpiredAsync();
                if (purged > 0)
                {
                    _logger.LogInformation("Purged {Count} expired temp-file quarantines", purged);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Quarantine purge failed");
            }
        }).Forget("purge quarantine");
    }

    private static bool IsStartupTaskActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind == ExtendedActivationKind.StartupTask;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private string? PageFromArgs()
    {
        var index = Array.FindIndex(_args, a => a.Equals("--page", StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < _args.Length && NavigationService.Pages.ContainsKey(_args[index + 1]) ? _args[index + 1] : null;
    }

    private void StartBackgroundServices(AppSettings settings)
    {
        var monitor = GetService<MetricsMonitor>();
        monitor.Interval = TimeSpan.FromMilliseconds(settings.MonitoringIntervalMs);
        monitor.SetBackgroundMonitoring(settings.MonitorInBackground);
        monitor.SetForeground(true);

        var network = GetService<NetworkMonitor>();
        network.SampleAvailable += OnNetworkSample;
        ApplyNetworkSettings(settings);

        if (settings.FpsCaptureEnabled)
        {
            // Succeeds when elevated; otherwise reports "requires administrator" without side effects.
            Task.Run(() => GetService<IFrameRateSource>().TryStart()).Forget("start FPS capture");
        }
    }

    private void ApplySettings(AppSettings settings)
    {
        Motion.Enabled = settings.AnimationsEnabled;
        GetService<NavigationService>().AnimationsEnabled = settings.AnimationsEnabled;
        var monitor = GetService<MetricsMonitor>();
        monitor.Interval = TimeSpan.FromMilliseconds(settings.MonitoringIntervalMs);
        monitor.SetBackgroundMonitoring(settings.MonitorInBackground);
        if (!settings.FpsCaptureEnabled)
        {
            GetService<IFrameRateSource>().Stop();
        }

        ApplyNetworkSettings(settings);
        GetService<ThemeService>().Apply(settings);
        _window?.ApplyBackdrop(settings.HardwareAcceleration);
    }

    private static void ApplyNetworkSettings(AppSettings settings)
    {
        var network = GetService<NetworkMonitor>();
        network.Interval = TimeSpan.FromSeconds(settings.NetworkMonitorIntervalSeconds);
        network.Target = IPAddress.TryParse(settings.NetworkTarget, out var target) ? target : IPAddress.Parse("1.1.1.1");
        if (settings.NetworkMonitoringEnabled)
        {
            network.Start();
        }
        else
        {
            network.Stop();
        }
    }

    private void OnNetworkSample(object? sender, LatencySample sample)
    {
        var settings = GetService<ISettingsService>().Current;
        if (!settings.NotifyOnNetworkDegradation)
        {
            return;
        }

        var stats = GetService<NetworkMonitor>().GetRecentStatistics(10);
        if (stats.Sent < 10)
        {
            return;
        }

        var degraded = stats.PacketLossPercent >= 20 || stats.AverageMs is > 150 || stats.JitterMs is > 40;
        if (degraded && _networkWasHealthy && DateTimeOffset.UtcNow - _lastNetworkAlert > TimeSpan.FromMinutes(10))
        {
            _lastNetworkAlert = DateTimeOffset.UtcNow;
            var detail = stats.AverageMs is { } avg ? $"{avg:0} ms average, {stats.PacketLossPercent:0}% loss over the last {stats.Sent} probes." : "The internet test server stopped responding.";
            GetService<UiDispatcher>().Post(() => GetService<NotificationService>().Show("Network quality dropped", detail));
        }

        _networkWasHealthy = !degraded;
    }

    private void CreateTray()
    {
        try
        {
            var icon = Path.Combine(AppContext.BaseDirectory, "Assets", "Nexora.ico");
            _tray = new TrayIcon(icon, "NEXORA — Optimize. Measure. Dominate.");
            _tray.CommandInvoked += OnTrayCommand;
            _tray.Show();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Tray icon unavailable");
            _tray = null;
        }

        _window!.TrayAvailable = _tray?.IsVisible == true;
        _window.TrayBalloon = (title, message) => _tray?.ShowBalloon(title, message);
    }

    private void OnTrayCommand(object? sender, TrayCommand command)
    {
        if (_window is null)
        {
            return;
        }

        switch (command)
        {
            case TrayCommand.Open:
                _window.ShowAndActivate();
                break;
            case TrayCommand.PerformanceMonitor:
                _window.ShowAndActivate();
                _window.NavigateTo("performance");
                break;
            case TrayCommand.NetworkMonitor:
                _window.ShowAndActivate();
                _window.NavigateTo("network");
                break;
            case TrayCommand.QuickOptimize:
                _window.ShowAndActivate();
                _window.NavigateTo("optimizer");
                GetService<OptimizerViewModel>().QuickOptimizeCommand.Execute(null);
                break;
            case TrayCommand.Exit:
                ShutdownCore();
                break;
        }
    }

    private void ShutdownCore()
    {
        if (_shuttingDown)
        {
            return;
        }

        _shuttingDown = true;
        _logger.LogInformation("NEXORA shutting down");
        try
        {
            // The ETW session must be stopped explicitly; it would otherwise outlive the process.
            GetService<IFrameRateSource>().Stop();
            GetService<NetworkMonitor>().Stop();
            GetService<MetricsMonitor>().Dispose();
            GetService<NotificationService>().Shutdown();
            _tray?.Dispose();
            _tray = null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error during shutdown");
        }

        _window?.CloseForExit();
        (Services as IDisposable)?.Dispose();
        _logProvider.Dispose();
        Exit();
    }

    private void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        _logger.LogError(e.Exception, "Unhandled UI exception");
        if (_window is null || !_window.HasBeenShown)
        {
            // Never keep running without a visible window.
            StartupGuard.Fatal("startup", e.Exception);
        }

        e.Handled = true;
        _window?.ShowError("Unexpected error", e.Message);
    }

    private static ServiceProvider ConfigureServices(AppPaths paths, FileLoggerProvider logProvider)
    {
        var services = new ServiceCollection();
        services.AddLogging(builder => builder.ClearProviders().SetMinimumLevel(LogLevel.Information).AddProvider(logProvider));

        // Core infrastructure
        services.AddSingleton<IAppPaths>(paths);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<ISettingsService>(sp => new SettingsService(Path.Combine(paths.DataDirectory, "settings.json"), sp.GetRequiredService<ILogger<SettingsService>>()));

        // Platform services (native Windows APIs)
        services.AddSingleton<IRegistryAccessor, WindowsRegistryAccessor>();
        services.AddSingleton<IPrivilegeService, WindowsPrivilegeService>();
        services.AddSingleton<IOsInfo, WindowsOsInfo>();
        services.AddSingleton<IPowerManager, WindowsPowerManager>();
        services.AddSingleton<ISystemParameters, WindowsSystemParameters>();
        services.AddSingleton<IGraphicsCapabilities, WindowsGraphicsCapabilities>();
        services.AddSingleton<ISystemInfoProvider, WindowsSystemInfoProvider>();
        services.AddSingleton<IFrameRateSource, EtwPresentMonitor>();
        services.AddSingleton<WindowsMetricsProvider>();
        services.AddSingleton<IMetricsProvider>(sp => sp.GetRequiredService<WindowsMetricsProvider>());
        services.AddSingleton<IProcessService, WindowsProcessService>();
        services.AddSingleton<INetworkInfoProvider, WindowsNetworkInfoProvider>();

        // Monitoring
        services.AddSingleton<MetricsMonitor>();
        services.AddSingleton<IPinger, IcmpPinger>();
        services.AddSingleton<IDnsProbe, UdpDnsProbe>();
        services.AddSingleton<NetworkTestRunner>();
        services.AddSingleton<NetworkMonitor>();
        services.AddSingleton(sp => new PerformanceSessionService(
            sp.GetRequiredService<MetricsMonitor>(), sp.GetRequiredService<IFrameRateSource>(), Path.Combine(paths.DataDirectory, "sessions.json"),
            sp.GetRequiredService<ILogger<PerformanceSessionService>>()));

        // Optimization
        services.AddSingleton(sp => new StartupAppsService(
            sp.GetRequiredService<IRegistryAccessor>(),
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup)));
        services.AddSingleton(new OptimizationCatalogOptions { TempDirectories = [Path.GetTempPath()], QuarantineDirectory = paths.QuarantineDirectory });
        services.AddSingleton<StandardOptimizationCatalog>();
        services.AddSingleton<IOptimizationCatalog>(sp => sp.GetRequiredService<StandardOptimizationCatalog>());
        services.AddSingleton<IOptimizationBackupStore>(new FileBackupStore(paths.BackupsDirectory));
        services.AddSingleton<IOptimizationHistoryStore>(sp => new FileHistoryStore(Path.Combine(paths.DataDirectory, "history.json"), sp.GetRequiredService<ILogger<FileHistoryStore>>()));
        services.AddSingleton<OptimizationEngine>();
        services.AddSingleton(sp => new QuarantineService(paths.QuarantineDirectory, sp.GetRequiredService<IOptimizationHistoryStore>(),
            sp.GetRequiredService<IOptimizationBackupStore>(), sp.GetRequiredService<ILogger<QuarantineService>>()));
        services.AddSingleton<SystemScanner>();

        // Games
        services.AddSingleton(sp => new GameProfileStore(Path.Combine(paths.DataDirectory, "games.json"), sp.GetRequiredService<ILogger<GameProfileStore>>()));
        services.AddSingleton<IGameDetector, WindowsGameDetector>();
        services.AddSingleton<GameLauncher>();

        // App services
        services.AddSingleton<UiDispatcher>();
        services.AddSingleton<NavigationService>();
        services.AddSingleton<ThemeService>();
        services.AddSingleton<NotificationService>();
        services.AddSingleton<StartupService>();
        services.AddSingleton<DialogService>();
        services.AddSingleton<LiveScoreService>();
        services.AddSingleton<OptimizationCoordinator>();

        // View models (singletons keep live charts and results while navigating)
        services.AddSingleton<ShellViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<PerformanceViewModel>();
        services.AddSingleton<NetworkViewModel>();
        services.AddSingleton<OptimizerViewModel>();
        services.AddSingleton<ScanViewModel>();
        services.AddSingleton<GamesViewModel>();
        services.AddSingleton<ProcessesViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<WelcomeViewModel>();

        services.AddSingleton<MainWindow>();
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
