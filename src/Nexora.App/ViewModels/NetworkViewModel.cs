using System.Collections.ObjectModel;
using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Network;
using Nexora.Core.Settings;

namespace Nexora.App.ViewModels;

public sealed class InsightViewModel(NetworkInsight insight)
{
    public string Title => insight.Title;

    public string Detail => insight.Detail;

    public string Glyph => insight.Severity switch
    {
        InsightSeverity.Good => "",
        InsightSeverity.Info => "",
        InsightSeverity.Warning => "",
        _ => "",
    };

    public StatusLevel Level => insight.Severity switch
    {
        InsightSeverity.Good => StatusLevel.Good,
        InsightSeverity.Info => StatusLevel.Neutral,
        InsightSeverity.Warning => StatusLevel.Warning,
        _ => StatusLevel.Problem,
    };
}

public sealed class DnsResultViewModel(DnsBenchmarkResult result, double slowest, bool fastest)
{
    public string Name => result.Provider.Name;

    public string Address => result.Provider.Address.ToString();

    public string Time => result.MedianMs is { } ms ? $"{ms:0} ms" : "No answer";

    public double Bar => result.MedianMs is { } ms && slowest > 0 ? Math.Max(4, 100 * ms / slowest) : 0;

    public bool IsFastest => fastest;

    public string Badge => fastest ? "FASTEST" : result.Provider.IsCurrent ? "CURRENT" : string.Empty;

    public bool HasBadge => Badge.Length > 0;
}

public sealed class NetworkViewModel : ObservableObject, INavigationAware
{
    private readonly NetworkTestRunner _runner;
    private readonly NetworkMonitor _monitor;
    private readonly INetworkInfoProvider _info;
    private readonly ISettingsService _settings;
    private readonly UiDispatcher _dispatcher;
    private readonly ILogger<NetworkViewModel> _logger;
    private readonly List<double> _testSamples = [];
    private CancellationTokenSource? _cts;
    private string _ping = Format.Dash;
    private string _jitter = Format.Dash;
    private string _loss = Format.Dash;
    private string _quality = "Not tested";
    private StatusLevel _qualityLevel = StatusLevel.Neutral;
    private string _gatewayLatency = Format.Dash;
    private string _internetLatency = Format.Dash;
    private string _dnsLatency = Format.Dash;
    private string _stability = Format.Dash;
    private string _minMax = string.Empty;
    private string _adapterName = Format.Dash;
    private string _connectionType = Format.Dash;
    private string _linkSpeed = Format.Dash;
    private string _linkStatus = Format.Dash;
    private string _gateway = Format.Dash;
    private string _dnsServers = Format.Dash;
    private string _ipVersions = Format.Dash;
    private string _wifiSignal = string.Empty;
    private bool _isWifi;
    private bool _isTesting;
    private double _progress;
    private string _progressText = string.Empty;
    private IReadOnlyList<double> _latencyHistory = [];
    private string _lastTested = "Run a test to measure your connection.";
    private string _liveText = Format.Dash;
    private string? _adapterInterfaceName;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _throughputTimer;
    private (long Rx, long Tx, DateTimeOffset At)? _lastBytes;
    private readonly List<double> _downHistory = [];
    private readonly List<double> _upHistory = [];
    private string _downloadText = Format.Dash;
    private string _uploadText = Format.Dash;
    private IReadOnlyList<double> _downloadSeries = [];
    private IReadOnlyList<double> _uploadSeries = [];
    private double _jitterBar;
    private double _lossBar;
    private double _qualityScore;
    private string _qualityDetail = "Run a network test for a full analysis.";
    private readonly DnsBenchmark _dnsBenchmark;
    private bool _isDnsRunning;
    private double _dnsProgress;
    private string _dnsSummary = "Compare how fast your DNS resolver answers against popular public resolvers.";

    public NetworkViewModel(NetworkTestRunner runner, NetworkMonitor monitor, INetworkInfoProvider info, ISettingsService settings, UiDispatcher dispatcher, ILogger<NetworkViewModel> logger, DnsBenchmark dnsBenchmark)
    {
        _dnsBenchmark = dnsBenchmark;
        RunDnsBenchmarkCommand = new AsyncRelayCommand(RunDnsBenchmarkAsync, () => !IsDnsRunning);
        _runner = runner;
        _monitor = monitor;
        _info = info;
        _settings = settings;
        _dispatcher = dispatcher;
        _logger = logger;
        RunTestCommand = new AsyncRelayCommand(RunTestAsync, () => !IsTesting);
        CancelTestCommand = new RelayCommand(() => _cts?.Cancel(), () => IsTesting);
        RefreshAdapterCommand = new AsyncRelayCommand(RefreshAdapterAsync);
    }

    public AsyncRelayCommand RunTestCommand { get; }

    public RelayCommand CancelTestCommand { get; }

    public AsyncRelayCommand RefreshAdapterCommand { get; }

    public AsyncRelayCommand RunDnsBenchmarkCommand { get; }

    public string DownloadText { get => _downloadText; private set => SetProperty(ref _downloadText, value); }

    public string UploadText { get => _uploadText; private set => SetProperty(ref _uploadText, value); }

    public IReadOnlyList<double> DownloadSeries { get => _downloadSeries; private set => SetProperty(ref _downloadSeries, value); }

    public IReadOnlyList<double> UploadSeries { get => _uploadSeries; private set => SetProperty(ref _uploadSeries, value); }

    /// <summary>Jitter on a 0–100 bar where 30 ms or more fills it.</summary>
    public double JitterBar { get => _jitterBar; private set => SetProperty(ref _jitterBar, value); }

    /// <summary>Packet loss on a 0–100 bar where 5% or more fills it.</summary>
    public double LossBar { get => _lossBar; private set => SetProperty(ref _lossBar, value); }

    /// <summary>Latency stability score (0–100) shown in the quality ring.</summary>
    public double QualityScore { get => _qualityScore; private set => SetProperty(ref _qualityScore, value); }

    public string QualityDetail { get => _qualityDetail; private set => SetProperty(ref _qualityDetail, value); }

    /// <summary>Live adapter traffic (bytes actually sent/received by this PC), sampled once per second while the page is open.</summary>
    private void SampleThroughput()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces().FirstOrDefault(n => n.Name == _adapterInterfaceName);
            if (nic is null)
            {
                DownloadText = UploadText = Format.Dash;
                return;
            }

            var stats = nic.GetIPStatistics();
            var now = DateTimeOffset.UtcNow;
            if (_lastBytes is { } last && now > last.At)
            {
                var seconds = (now - last.At).TotalSeconds;
                var down = Math.Max(0, stats.BytesReceived - last.Rx) * 8 / seconds / 1_000_000;
                var up = Math.Max(0, stats.BytesSent - last.Tx) * 8 / seconds / 1_000_000;
                Push(_downHistory, down);
                Push(_upHistory, up);
                DownloadText = FormatRate(down);
                UploadText = FormatRate(up);
                DownloadSeries = _downHistory.ToList();
                UploadSeries = _upHistory.ToList();
            }

            _lastBytes = (stats.BytesReceived, stats.BytesSent, now);
        }
        catch (System.Net.NetworkInformation.NetworkInformationException)
        {
            DownloadText = UploadText = Format.Dash;
        }

        static void Push(List<double> list, double value)
        {
            list.Add(value);
            if (list.Count > 60)
            {
                list.RemoveAt(0);
            }
        }
    }

    private static string FormatRate(double mbps) => mbps switch
    {
        >= 100 => $"{mbps:0}",
        >= 1 => $"{mbps:0.0}",
        _ => $"{mbps:0.00}",
    };

    private void ShowLiveQuality(LatencyStatisticsResult stats)
    {
        if (stats.Sent == 0)
        {
            return;
        }

        if (!IsTesting)
        {
            Ping = stats.AverageMs is { } avg ? avg.ToString("0", CultureInfo.CurrentCulture) : "—";
            Jitter = stats.JitterMs is { } j ? j.ToString("0.#", CultureInfo.CurrentCulture) : "—";
            Loss = stats.PacketLossPercent.ToString("0.#", CultureInfo.CurrentCulture);
            var quality = LatencyStatistics.Classify(stats);
            Quality = quality.ToString();
            QualityLevel = quality switch
            {
                ConnectionQuality.Excellent or ConnectionQuality.Good => StatusLevel.Good,
                ConnectionQuality.Fair => StatusLevel.Warning,
                ConnectionQuality.Poor => StatusLevel.Problem,
                _ => StatusLevel.Neutral,
            };
        }

        JitterBar = Math.Clamp((stats.JitterMs ?? 0) / 30 * 100, 2, 100);
        LossBar = Math.Clamp(stats.PacketLossPercent / 5 * 100, 2, 100);
        QualityScore = stats.StabilityScore;
        QualityDetail = $"Stability {stats.StabilityScore:0}/100 over the last {stats.Sent} probes";
    }

    public ObservableCollection<DnsResultViewModel> DnsResults { get; } = [];

    public bool IsDnsRunning
    {
        get => _isDnsRunning;
        private set
        {
            if (SetProperty(ref _isDnsRunning, value))
            {
                RunDnsBenchmarkCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public double DnsProgress { get => _dnsProgress; private set => SetProperty(ref _dnsProgress, value); }

    public string DnsSummary { get => _dnsSummary; private set => SetProperty(ref _dnsSummary, value); }

    private async Task RunDnsBenchmarkAsync()
    {
        IsDnsRunning = true;
        DnsProgress = 0;
        try
        {
            var adapter = await Task.Run(_info.GetActiveAdapter);
            var providers = DnsBenchmark.BuildProviderList(adapter?.DnsServers ?? []);
            var results = await _dnsBenchmark.RunAsync(providers, new Progress<double>(p => DnsProgress = p * 100));
            var slowest = results.Where(r => r.MedianMs.HasValue).Select(r => r.MedianMs!.Value).DefaultIfEmpty(0).Max();
            DnsResults.Clear();
            for (var i = 0; i < results.Count; i++)
            {
                DnsResults.Add(new DnsResultViewModel(results[i], slowest, i == 0 && results[i].Reachable));
            }

            var best = results.FirstOrDefault(r => r.Reachable);
            var current = results.FirstOrDefault(r => r.Provider.IsCurrent);
            DnsSummary = best is null
                ? "No DNS server answered NEXORA's direct queries (some routers, VPNs and firewalls block this)."
                : current is null || ReferenceEquals(best, current) || current.MedianMs is null
                    ? $"{best.Provider.Name} answered fastest ({best.MedianMs:0} ms). Your current setup is already a good choice."
                    : $"{best.Provider.Name} answers {current.MedianMs - best.MedianMs:0} ms faster than your current DNS. That speeds up looking up websites and game servers — it does not change in-game ping.";
        }
        finally
        {
            IsDnsRunning = false;
        }
    }

    public ObservableCollection<InsightViewModel> Insights { get; } = [];

    public string Ping { get => _ping; private set => SetProperty(ref _ping, value); }

    public string Jitter { get => _jitter; private set => SetProperty(ref _jitter, value); }

    public string Loss { get => _loss; private set => SetProperty(ref _loss, value); }

    public string Quality { get => _quality; private set => SetProperty(ref _quality, value); }

    public StatusLevel QualityLevel { get => _qualityLevel; private set => SetProperty(ref _qualityLevel, value); }

    public string GatewayLatency { get => _gatewayLatency; private set => SetProperty(ref _gatewayLatency, value); }

    public string InternetLatency { get => _internetLatency; private set => SetProperty(ref _internetLatency, value); }

    public string DnsLatency { get => _dnsLatency; private set => SetProperty(ref _dnsLatency, value); }

    public string Stability { get => _stability; private set => SetProperty(ref _stability, value); }

    public string MinMax { get => _minMax; private set => SetProperty(ref _minMax, value); }

    public string AdapterName { get => _adapterName; private set => SetProperty(ref _adapterName, value); }

    public string ConnectionType { get => _connectionType; private set => SetProperty(ref _connectionType, value); }

    public string LinkSpeed { get => _linkSpeed; private set => SetProperty(ref _linkSpeed, value); }

    public string LinkStatus { get => _linkStatus; private set => SetProperty(ref _linkStatus, value); }

    public string Gateway { get => _gateway; private set => SetProperty(ref _gateway, value); }

    public string DnsServers { get => _dnsServers; private set => SetProperty(ref _dnsServers, value); }

    public string IpVersions { get => _ipVersions; private set => SetProperty(ref _ipVersions, value); }

    public string WifiSignal { get => _wifiSignal; private set => SetProperty(ref _wifiSignal, value); }

    public bool IsWifi { get => _isWifi; private set => SetProperty(ref _isWifi, value); }

    public string LiveText { get => _liveText; private set => SetProperty(ref _liveText, value); }

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetProperty(ref _isTesting, value))
            {
                OnPropertyChanged(nameof(IsIdle));
                RunTestCommand.NotifyCanExecuteChanged();
                CancelTestCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsIdle => !IsTesting;

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string ProgressText { get => _progressText; private set => SetProperty(ref _progressText, value); }

    public IReadOnlyList<double> LatencyHistory { get => _latencyHistory; private set => SetProperty(ref _latencyHistory, value); }

    public string LastTested { get => _lastTested; private set => SetProperty(ref _lastTested, value); }

    public void OnNavigatedTo(object? parameter)
    {
        _monitor.SampleAvailable += OnLiveSample;
        UpdateLive();
        _throughputTimer ??= _dispatcher.CreateTimer(TimeSpan.FromSeconds(1), SampleThroughput);
        _lastBytes = null;
        _throughputTimer.Start();
        RefreshAdapterAsync().Forget("read network adapter");
        if (parameter is "run" && !IsTesting)
        {
            RunTestCommand.Execute(null);
        }
    }

    public void OnNavigatedFrom()
    {
        _monitor.SampleAvailable -= OnLiveSample;
        _throughputTimer?.Stop();
    }

    private void OnLiveSample(object? sender, LatencySample sample) => _dispatcher.Post(UpdateLive);

    private void UpdateLive()
    {
        var stats = _monitor.GetRecentStatistics(10);
        ShowLiveQuality(_monitor.GetRecentStatistics(20));
        LiveText = stats.Sent == 0
            ? (_monitor.IsRunning ? "Live monitor starting…" : "Live monitoring is off (Settings › Network monitoring)")
            : $"Live: {Format.Ms(stats.AverageMs)} · jitter {Format.Ms(stats.JitterMs)} · loss {stats.PacketLossPercent:0.#}% (last {stats.Sent} probes)";
        if (!IsTesting && _testSamples.Count == 0)
        {
            LatencyHistory = _monitor.GetSamples().Select(s => s.RoundTripMs ?? 0).ToList();
        }
    }

    private async Task RefreshAdapterAsync()
    {
        NetworkAdapterInfo? adapter = null;
        try
        {
            adapter = await Task.Run(_info.GetActiveAdapter);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Adapter information unavailable");
        }

        ShowAdapter(adapter);
    }

    private void ShowAdapter(NetworkAdapterInfo? adapter)
    {
        if (adapter is null)
        {
            AdapterName = "No active connection";
            ConnectionType = LinkSpeed = Gateway = DnsServers = IpVersions = Format.Dash;
            LinkStatus = "Disconnected";
            IsWifi = false;
            return;
        }

        AdapterName = adapter.Description;
        _adapterInterfaceName = adapter.Name;
        ConnectionType = adapter.KindDescription;
        LinkSpeed = adapter.Kind == AdapterKind.WiFi && adapter.WifiLinkSpeedMbps is { } rate ? $"{rate:0} Mbps (Wi-Fi link)" : Format.LinkSpeed(adapter.SpeedBitsPerSecond);
        LinkStatus = adapter.IsUp ? "Connected" : "Disconnected";
        Gateway = adapter.Gateway?.ToString() ?? "None";
        DnsServers = adapter.DnsServers.Count == 0 ? "None" : string.Join(", ", adapter.DnsServers.Take(3));
        IpVersions = (adapter.HasIPv4, adapter.HasIPv6) switch
        {
            (true, true) => "IPv4 + IPv6",
            (true, false) => "IPv4",
            (false, true) => "IPv6",
            _ => Format.Dash,
        };
        IsWifi = adapter.Kind == AdapterKind.WiFi;
        WifiSignal = adapter.WifiSignalQualityPercent is { } signal
            ? $"{signal}% signal" + (adapter.WifiStandard is { } std ? $" · {std}" : string.Empty)
            : "Signal unavailable";
    }

    private async Task RunTestAsync()
    {
        IsTesting = true;
        Progress = 0;
        _testSamples.Clear();
        LatencyHistory = [];
        _cts = new CancellationTokenSource();
        var target = IPAddress.TryParse(_settings.Current.NetworkTarget, out var ip) ? ip : IPAddress.Parse("1.1.1.1");
        var options = new NetworkTestOptions { InternetTarget = target, SampleCount = _settings.Current.NetworkTestSamples };
        var progress = new Progress<NetworkTestProgress>(p =>
        {
            Progress = p.Fraction * 100;
            ProgressText = p.Stage;
            if (p.InternetSample is { } sample)
            {
                _testSamples.Add(sample.RoundTripMs ?? 0);
                LatencyHistory = _testSamples.ToList();
                Ping = sample.RoundTripMs is { } rtt ? rtt.ToString("0", CultureInfo.CurrentCulture) : "Lost";
            }
        });

        try
        {
            var result = await _runner.RunAsync(options, progress, _cts.Token);
            ShowResult(result);
        }
        catch (OperationCanceledException)
        {
            ProgressText = "Test cancelled";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsTesting = false;
        }
    }

    private void ShowResult(NetworkTestResult result)
    {
        var s = result.Internet;
        Ping = s.AverageMs is { } avg ? avg.ToString("0", CultureInfo.CurrentCulture) : "—";
        Jitter = s.JitterMs is { } j ? j.ToString("0.#", CultureInfo.CurrentCulture) : "—";
        Loss = s.PacketLossPercent.ToString("0.#", CultureInfo.CurrentCulture);
        Quality = result.Quality.ToString();
        QualityLevel = result.Quality switch
        {
            ConnectionQuality.Excellent or ConnectionQuality.Good => StatusLevel.Good,
            ConnectionQuality.Fair => StatusLevel.Warning,
            ConnectionQuality.Poor => StatusLevel.Problem,
            _ => StatusLevel.Neutral,
        };
        InternetLatency = $"{Format.Ms(s.AverageMs)} to {result.InternetTarget}";
        GatewayLatency = result.Gateway is { } g ? $"{Format.Ms(g.AverageMs)} · {g.PacketLossPercent:0.#}% loss" : "No gateway";
        DnsLatency = result.Dns is { } d ? (d.AverageMs is { } dnsAvg ? $"{Format.Ms(dnsAvg)} ({result.DnsServer})" : "No response") : "No DNS server";
        Stability = $"{s.StabilityScore:0} / 100";
        JitterBar = Math.Clamp((s.JitterMs ?? 0) / 30 * 100, 2, 100);
        LossBar = Math.Clamp(s.PacketLossPercent / 5 * 100, 2, 100);
        QualityScore = s.StabilityScore;
        QualityDetail = $"Stability {s.StabilityScore:0}/100 from the last network test";
        MinMax = s.MinimumMs is { } min ? $"min {Format.Ms(min)} · median {Format.Ms(s.MedianMs)} · max {Format.Ms(s.MaximumMs)}" : string.Empty;
        LastTested = $"Tested {result.StartedAt.ToLocalTime():t} · {s.Sent} probes";
        ProgressText = "Test complete";
        Progress = 100;
        ShowAdapter(result.Adapter);
        Insights.Clear();
        foreach (var insight in result.Insights)
        {
            Insights.Add(new InsightViewModel(insight));
        }
    }
}
