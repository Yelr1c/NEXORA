using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Monitoring;
using Nexora.Core.Network;
using Nexora.Core.Scoring;

namespace Nexora.App.ViewModels;

public sealed class ScoreComponentViewModel(ScoreComponent component)
{
    public string Name => component.Name;

    public string Detail => component.Detail;

    public double Score => component.Score;

    public string ScoreText => component.Score.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
}

public sealed record InfoRow(string Label, string Value, StatusLevel Level = StatusLevel.Neutral);

public sealed record RecommendationRow(string Name, string Impact, string Reason);

public sealed class DashboardViewModel : ObservableObject, INavigationAware
{
    private const int ChartPoints = 60;
    private readonly MetricsMonitor _monitor;
    private readonly NetworkMonitor _network;
    private readonly LiveScoreService _score;
    private readonly UiDispatcher _dispatcher;
    private readonly NavigationService _navigation;
    private readonly GameBoostService _boost;
    private DispatcherQueueTimer? _scoreTimer;
    private bool _boostBusy;
    private readonly GameWatcherService _watcher;
    private double _scoreValue;
    private string _scoreRating = "Measuring…";
    private string _scoreSummary = "Collecting live measurements";
    private double _cpu;
    private double _gpu;
    private double _ram;
    private string _cpuText = Format.Dash;
    private string _cpuDetail = string.Empty;
    private string _gpuText = Format.Dash;
    private string _gpuDetail = string.Empty;
    private string _ramText = Format.Dash;
    private string _ramDetail = string.Empty;
    private string _fpsText = Format.Dash;
    private string _fpsDetail = "Start a game to see live FPS";
    private string _pingText = Format.Dash;
    private string _networkDetail = "Measuring latency…";
    private string _healthText = Format.Dash;
    private string _healthDetail = string.Empty;
    private IReadOnlyList<double> _cpuHistory = [];
    private IReadOnlyList<double> _gpuHistory = [];
    private IReadOnlyList<double> _ramHistory = [];
    private IReadOnlyList<double> _pingHistory = [];
    private bool _gpuAvailable = true;
    private string _cpuTempText = Format.Dash;
    private string _gpuTempText = Format.Dash;

    private readonly Nexora.Core.Hardware.ISystemInfoProvider _systemInfo;
    private readonly PerformanceSessionService _sessions;
    private readonly Nexora.Core.Optimization.StandardOptimizationCatalog _catalog;
    private readonly Nexora.Core.Platform.IPowerManager _power;
    private readonly INetworkInfoProvider _networkInfo;
    private DateTimeOffset _slowRefreshAt = DateTimeOffset.MinValue;
    private IReadOnlyList<double> _cpuLong = [];
    private IReadOnlyList<double> _gpuLong = [];
    private IReadOnlyList<double> _ramLong = [];
    private string _healthHeadline = "Checking…";
    private StatusLevel _healthLevel = StatusLevel.Neutral;
    private string _recommendationSummary = "Analysing your PC…";

    public DashboardViewModel(MetricsMonitor monitor, NetworkMonitor network, LiveScoreService score, UiDispatcher dispatcher, NavigationService navigation, GameBoostService boost,
        Nexora.Core.Hardware.ISystemInfoProvider systemInfo, PerformanceSessionService sessions, Nexora.Core.Optimization.StandardOptimizationCatalog catalog,
        Nexora.Core.Platform.IPowerManager power, INetworkInfoProvider networkInfo)
    {
        _systemInfo = systemInfo;
        _sessions = sessions;
        _catalog = catalog;
        _power = power;
        _networkInfo = networkInfo;
        _boost = boost;
        _watcher = App.GetService<GameWatcherService>();
        _watcher.Changed += (_, _) =>
        {
            OnPropertyChanged(nameof(NowPlaying));
            OnPropertyChanged(nameof(HasNowPlaying));
            OnPropertyChanged(nameof(GamingHeadline));
        };
        ToggleBoostCommand = new AsyncRelayCommand(ToggleBoostAsync, () => !_boostBusy);
        _boost.Changed += (_, _) => dispatcher.Post(RaiseBoost);
        _monitor = monitor;
        _network = network;
        _score = score;
        _dispatcher = dispatcher;
        _navigation = navigation;
        ScanCommand = new RelayCommand(() => _navigation.Navigate("scan"));
        OpenPerformanceCommand = new RelayCommand(() => _navigation.Navigate("performance"));
        OpenNetworkCommand = new RelayCommand(() => _navigation.Navigate("network"));
        OpenOptimizerCommand = new RelayCommand(() => _navigation.Navigate("optimizer"));
    }

    public RelayCommand ScanCommand { get; }

    public AsyncRelayCommand ToggleBoostCommand { get; }

    public string NowPlaying => _watcher.CurrentGame is { } game ? $"Now playing: {game.Name}" : string.Empty;

    public bool HasNowPlaying => _watcher.CurrentGame is not null;

    public bool IsBoostActive => _boost.IsActive;

    public bool IsBoostInactive => !_boost.IsActive;

    public string BoostState => _boostBusy ? "WORKING…" : _boost.IsActive ? "ON" : "OFF";

    public string BoostButtonText => _boost.IsActive ? "TURN OFF BOOST" : "ACTIVATE BOOST";

    public string BoostDetail => _boost.IsActive
        ? (_boost.ActiveCount == 0 ? "Your PC was already fully set up for gaming." : $"{_boost.ActiveCount} gaming setting(s) switched on. Turning Boost off restores them.")
        : "One click switches on Game Mode, windowed-game optimizations, VRR, high performance power and disables background recording.";

    private async Task ToggleBoostAsync()
    {
        _boostBusy = true;
        RaiseBoost();
        try
        {
            await _boost.ToggleAsync();
        }
        finally
        {
            _boostBusy = false;
            RaiseBoost();
        }
    }

    private void RaiseBoost()
    {
        OnPropertyChanged(nameof(IsBoostActive));
        OnPropertyChanged(nameof(IsBoostInactive));
        OnPropertyChanged(nameof(BoostState));
        OnPropertyChanged(nameof(BoostButtonText));
        OnPropertyChanged(nameof(BoostDetail));
        ToggleBoostCommand.NotifyCanExecuteChanged();
    }

    public RelayCommand OpenPerformanceCommand { get; }

    public RelayCommand OpenNetworkCommand { get; }

    public RelayCommand OpenOptimizerCommand { get; }

    public ObservableCollection<ScoreComponentViewModel> ScoreComponents { get; } = [];

    public ObservableCollection<InfoRow> HealthRows { get; } = [];

    public ObservableCollection<InfoRow> HardwareRows { get; } = [];

    public ObservableCollection<SessionHistoryItem> RecentSessions { get; } = [];

    public ObservableCollection<RecommendationRow> Recommendations { get; } = [];

    public bool HasRecentSessions => RecentSessions.Count > 0;

    public bool HasNoRecentSessions => RecentSessions.Count == 0;

    public string HealthHeadline { get => _healthHeadline; private set => SetProperty(ref _healthHeadline, value); }

    public StatusLevel HealthLevel { get => _healthLevel; private set => SetProperty(ref _healthLevel, value); }

    public string RecommendationSummary { get => _recommendationSummary; private set => SetProperty(ref _recommendationSummary, value); }

    /// <summary>Last ~5 minutes for the wide history graph.</summary>
    public IReadOnlyList<double> CpuLong { get => _cpuLong; private set => SetProperty(ref _cpuLong, value); }

    public IReadOnlyList<double> GpuLong { get => _gpuLong; private set => SetProperty(ref _gpuLong, value); }

    public IReadOnlyList<double> RamLong { get => _ramLong; private set => SetProperty(ref _ramLong, value); }

    public string GamingHeadline => _watcher.CurrentGame is { } game ? game.Name : _boost.IsActive ? "Ready to play" : "No game running";

    /// <summary>Refreshes the slower dashboard sections (sessions, recommendations, hardware) at most every 30 s.</summary>
    private async Task RefreshSlowSectionsAsync(bool force = false)
    {
        if (!force && DateTimeOffset.UtcNow - _slowRefreshAt < TimeSpan.FromSeconds(30))
        {
            return;
        }

        _slowRefreshAt = DateTimeOffset.UtcNow;
        var sessions = await _sessions.GetHistoryAsync();
        RecentSessions.Clear();
        foreach (var session in sessions.Take(4))
        {
            RecentSessions.Add(new SessionHistoryItem(session));
        }

        OnPropertyChanged(nameof(HasRecentSessions));
        OnPropertyChanged(nameof(HasNoRecentSessions));

        var info = await Task.Run(_systemInfo.GetSystemInfo);
        var adapter = await Task.Run(_networkInfo.GetActiveAdapter);
        var battery = _power.GetBatteryStatus();
        var drive = Nexora.Core.Hardware.StorageInfo.GetFixedDrives().FirstOrDefault(d => d.IsSystemDrive);
        var context = new Nexora.Core.Optimization.AdvisorContext(
            info.PrimaryGpu is { DedicatedMemoryBytes: > 512L * 1024 * 1024 }, battery?.HasBattery, battery?.OnBattery, drive?.FreePercent,
            adapter is null ? null : adapter.Kind == AdapterKind.WiFi);
        var advice = new List<(Nexora.Core.Optimization.IOptimization Optimization, Nexora.Core.Optimization.Advice Advice)>();
        foreach (var optimization in _catalog.StaticOptimizations)
        {
            try
            {
                if ((await optimization.GetStateAsync()).Status == Nexora.Core.Optimization.OptimizationStatus.NotApplied)
                {
                    var a = Nexora.Core.Optimization.OptimizationAdvisor.Evaluate(optimization, context);
                    if (a.Recommended && (!optimization.RequiresAdministrator || App.GetService<OptimizationCoordinator>().IsElevated))
                    {
                        advice.Add((optimization, a));
                    }
                }
            }
            catch (Exception)
            {
                // A single unreadable setting must not break the dashboard.
            }
        }

        Recommendations.Clear();
        foreach (var (optimization, a) in advice.OrderByDescending(x => x.Optimization.Impact).Take(3))
        {
            Recommendations.Add(new RecommendationRow(optimization.Name, OptimizationCoordinator.ImpactText(optimization.Impact), a.Reason));
        }

        RecommendationSummary = advice.Count switch
        {
            0 => "Your PC is already set up the way NEXORA recommends.",
            1 => "1 change is recommended for your PC.",
            _ => $"{advice.Count} changes are recommended for your PC.",
        };

        HardwareRows.Clear();
        HardwareRows.Add(new InfoRow("Processor", info.CpuName));
        HardwareRows.Add(new InfoRow("Graphics", info.PrimaryGpu?.Name ?? "Integrated / not detected"));
        HardwareRows.Add(new InfoRow("Memory", Format.Gigabytes(info.TotalMemoryBytes)));
        HardwareRows.Add(new InfoRow("Video memory", info.PrimaryGpu is { } gpu ? Format.Bytes(gpu.DedicatedMemoryBytes) : Format.Dash));
        HardwareRows.Add(new InfoRow("System", $"{info.OsName} {info.OsVersion}".Trim()));
        HardwareRows.Add(new InfoRow("Connection", adapter?.KindDescription ?? "Offline"));
        if (_monitor.Latest is { } latest)
        {
            UpdateHealth(latest, drive);
        }
    }

    private void UpdateHealth(MetricSnapshot s, Nexora.Core.Hardware.DriveSnapshot? drive)
    {
        HealthRows.Clear();
        var problems = 0;
        void Row(string label, string value, StatusLevel level)
        {
            if (level is StatusLevel.Warning or StatusLevel.Problem)
            {
                problems++;
            }

            HealthRows.Add(new InfoRow(label, value, level));
        }

        Row("Memory", s.RamTotalBytes > 0 ? $"{s.RamUsagePercent:0}% used" : Format.Dash, s.RamUsagePercent > 90 ? StatusLevel.Problem : s.RamUsagePercent > 75 ? StatusLevel.Warning : StatusLevel.Good);
        if (drive is not null)
        {
            Row("System drive", $"{drive.FreePercent:0}% free", drive.FreePercent < 10 ? StatusLevel.Problem : drive.FreePercent < 20 ? StatusLevel.Warning : StatusLevel.Good);
        }

        Row("CPU temperature", s.CpuTemperatureCelsius is { } ct ? Format.Celsius(ct) : "Not exposed", s.CpuTemperatureCelsius is > 90 ? StatusLevel.Problem : s.CpuTemperatureCelsius is > 80 ? StatusLevel.Warning : s.CpuTemperatureCelsius is null ? StatusLevel.Neutral : StatusLevel.Good);
        Row("GPU temperature", s.GpuTemperatureCelsius is { } gt ? Format.Celsius(gt) : "Not exposed", s.GpuTemperatureCelsius is > 87 ? StatusLevel.Problem : s.GpuTemperatureCelsius is > 80 ? StatusLevel.Warning : s.GpuTemperatureCelsius is null ? StatusLevel.Neutral : StatusLevel.Good);
        var net = _network.GetRecentStatistics(20);
        if (net.Sent > 0)
        {
            Row("Connection", net.PacketLossPercent > 2 ? $"{net.PacketLossPercent:0}% loss" : $"{net.AverageMs:0} ms, stable", net.PacketLossPercent > 2 ? StatusLevel.Warning : StatusLevel.Good);
        }

        (HealthHeadline, HealthLevel) = problems switch
        {
            0 => ("All systems healthy", StatusLevel.Good),
            1 => ("1 thing needs attention", StatusLevel.Warning),
            _ => ($"{problems} things need attention", StatusLevel.Warning),
        };
    }

    public double ScoreValue { get => _scoreValue; private set => SetProperty(ref _scoreValue, value); }

    public string ScoreRating { get => _scoreRating; private set => SetProperty(ref _scoreRating, value); }

    public string ScoreSummary { get => _scoreSummary; private set => SetProperty(ref _scoreSummary, value); }

    public double Cpu { get => _cpu; private set => SetProperty(ref _cpu, value); }

    public double Gpu { get => _gpu; private set => SetProperty(ref _gpu, value); }

    public double Ram { get => _ram; private set => SetProperty(ref _ram, value); }

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }

    public string CpuDetail { get => _cpuDetail; private set => SetProperty(ref _cpuDetail, value); }

    public string GpuText { get => _gpuText; private set => SetProperty(ref _gpuText, value); }

    public string GpuDetail { get => _gpuDetail; private set => SetProperty(ref _gpuDetail, value); }

    public string RamText { get => _ramText; private set => SetProperty(ref _ramText, value); }

    public string RamDetail { get => _ramDetail; private set => SetProperty(ref _ramDetail, value); }

    public string FpsText { get => _fpsText; private set => SetProperty(ref _fpsText, value); }

    public string FpsDetail { get => _fpsDetail; private set => SetProperty(ref _fpsDetail, value); }

    public string PingText { get => _pingText; private set => SetProperty(ref _pingText, value); }

    public string NetworkDetail { get => _networkDetail; private set => SetProperty(ref _networkDetail, value); }

    public string HealthText { get => _healthText; private set => SetProperty(ref _healthText, value); }

    public string HealthDetail { get => _healthDetail; private set => SetProperty(ref _healthDetail, value); }

    public string CpuTempText { get => _cpuTempText; private set => SetProperty(ref _cpuTempText, value); }

    public string GpuTempText { get => _gpuTempText; private set => SetProperty(ref _gpuTempText, value); }

    public bool GpuAvailable { get => _gpuAvailable; private set => SetProperty(ref _gpuAvailable, value); }

    public IReadOnlyList<double> CpuHistory { get => _cpuHistory; private set => SetProperty(ref _cpuHistory, value); }

    public IReadOnlyList<double> GpuHistory { get => _gpuHistory; private set => SetProperty(ref _gpuHistory, value); }

    public IReadOnlyList<double> RamHistory { get => _ramHistory; private set => SetProperty(ref _ramHistory, value); }

    public IReadOnlyList<double> PingHistory { get => _pingHistory; private set => SetProperty(ref _pingHistory, value); }

    public void OnNavigatedTo(object? parameter)
    {
        _monitor.SnapshotAvailable += OnSnapshot;
        _network.SampleAvailable += OnLatency;
        if (_monitor.Latest is { } latest)
        {
            Update(latest);
        }

        UpdateLatency();
        RefreshSlowSectionsAsync(force: true).Forget("refresh dashboard");
        _scoreTimer ??= _dispatcher.CreateTimer(TimeSpan.FromSeconds(5), () =>
        {
            RefreshScoreAsync().Forget("refresh score");
            RefreshSlowSectionsAsync().Forget("refresh dashboard");
        });
        _scoreTimer.Start();
        RefreshScoreAsync().Forget("refresh score");
    }

    public void OnNavigatedFrom()
    {
        _monitor.SnapshotAvailable -= OnSnapshot;
        _network.SampleAvailable -= OnLatency;
        _scoreTimer?.Stop();
    }

    private async Task RefreshScoreAsync()
    {
        var result = await _score.ComputeAsync();
        _dispatcher.Post(() =>
        {
            if (result.Components.Count == 0)
            {
                ScoreRating = "Unavailable";
                ScoreSummary = "Not enough measurements yet";
                return;
            }

            ScoreValue = result.Score;
            ScoreRating = result.Rating;
            var weakest = result.Components.OrderBy(c => c.Score).First();
            ScoreSummary = weakest.Score >= 90 ? "Every measured area is in great shape" : $"Biggest opportunity: {weakest.Name.ToLowerInvariant()} ({weakest.Detail})";
            HealthText = result.Rating;
            HealthDetail = $"{result.Components.Count} areas measured";
            ScoreComponents.Clear();
            foreach (var component in result.Components.OrderBy(c => c.Score))
            {
                ScoreComponents.Add(new ScoreComponentViewModel(component));
            }
        });
    }

    private void OnSnapshot(object? sender, MetricSnapshot snapshot) => _dispatcher.Post(() => Update(snapshot));

    private void Update(MetricSnapshot snapshot)
    {
        Cpu = snapshot.CpuUsagePercent ?? 0;
        CpuText = Format.Percent(snapshot.CpuUsagePercent);
        CpuDetail = snapshot.CpuFrequencyMhz is { } f ? Format.Mhz(f) : "Frequency unavailable";
        GpuAvailable = snapshot.GpuUsagePercent.HasValue;
        Gpu = snapshot.GpuUsagePercent ?? 0;
        GpuText = snapshot.GpuUsagePercent.HasValue ? Format.Percent(snapshot.GpuUsagePercent) : "N/A";
        GpuDetail = snapshot.GpuUsagePercent.HasValue
            ? (snapshot.VramUsedBytes is { } used ? $"VRAM {Format.Bytes(used)}" + (snapshot.VramTotalBytes is { } total ? $" / {Format.Bytes(total)}" : string.Empty) : "VRAM unavailable")
            : Format.Unavailable;
        Ram = snapshot.RamUsagePercent;
        RamText = snapshot.RamTotalBytes > 0 ? Format.Percent(snapshot.RamUsagePercent) : Format.Dash;
        RamDetail = snapshot.RamTotalBytes > 0 ? $"{Format.Bytes(snapshot.RamUsedBytes)} of {Format.Bytes(snapshot.RamTotalBytes)}" : Format.Unavailable;
        CpuTempText = snapshot.CpuTemperatureCelsius is { } ct ? Format.Celsius(ct) : "N/A";
        GpuTempText = snapshot.GpuTemperatureCelsius is { } gt ? Format.Celsius(gt) : "N/A";

        if (snapshot.Fps is { } fps)
        {
            FpsText = fps.ToString("0", System.Globalization.CultureInfo.CurrentCulture);
            FpsDetail = $"{snapshot.FpsProcessName} · {Format.Ms(snapshot.FrameTimeMs)} frame time";
        }
        else
        {
            FpsText = Format.Dash;
            FpsDetail = "No game is rendering right now";
        }

        var history = _monitor.GetHistory();
        var recent = history.Skip(Math.Max(0, history.Count - ChartPoints)).ToList();
        CpuHistory = recent.Select(s => s.CpuUsagePercent ?? 0).ToList();
        GpuHistory = recent.Select(s => s.GpuUsagePercent ?? 0).ToList();
        RamHistory = recent.Select(s => s.RamUsagePercent).ToList();
        CpuLong = history.Select(s => s.CpuUsagePercent ?? 0).ToList();
        GpuLong = history.Select(s => s.GpuUsagePercent ?? 0).ToList();
        RamLong = history.Select(s => s.RamUsagePercent).ToList();
        OnPropertyChanged(nameof(GamingHeadline));
    }

    private void OnLatency(object? sender, LatencySample sample) => _dispatcher.Post(UpdateLatency);

    private void UpdateLatency()
    {
        var stats = _network.GetRecentStatistics(20);
        if (stats.Sent == 0)
        {
            PingText = Format.Dash;
            NetworkDetail = _network.IsRunning ? "Measuring latency…" : "Live network monitoring is off";
            return;
        }

        PingText = stats.AverageMs is { } avg ? avg.ToString("0", System.Globalization.CultureInfo.CurrentCulture) : "Lost";
        var quality = LatencyStatistics.Classify(stats);
        NetworkDetail = $"{quality} · jitter {Format.Ms(stats.JitterMs)} · loss {stats.PacketLossPercent:0.#}%";
        PingHistory = _network.GetSamples().TakeLast(ChartPoints).Select(s => s.RoundTripMs ?? 0).ToList();
    }
}
