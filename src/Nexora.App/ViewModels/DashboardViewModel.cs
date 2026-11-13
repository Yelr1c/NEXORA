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

    public DashboardViewModel(MetricsMonitor monitor, NetworkMonitor network, LiveScoreService score, UiDispatcher dispatcher, NavigationService navigation, GameBoostService boost)
    {
        _boost = boost;
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
        _scoreTimer ??= _dispatcher.CreateTimer(TimeSpan.FromSeconds(5), () => RefreshScoreAsync().Forget("refresh score"));
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
