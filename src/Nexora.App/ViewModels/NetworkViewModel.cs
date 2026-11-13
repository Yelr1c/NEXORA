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

    public NetworkViewModel(NetworkTestRunner runner, NetworkMonitor monitor, INetworkInfoProvider info, ISettingsService settings, UiDispatcher dispatcher, ILogger<NetworkViewModel> logger)
    {
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
        RefreshAdapterAsync().Forget("read network adapter");
        if (parameter is "run" && !IsTesting)
        {
            RunTestCommand.Execute(null);
        }
    }

    public void OnNavigatedFrom() => _monitor.SampleAvailable -= OnLiveSample;

    private void OnLiveSample(object? sender, LatencySample sample) => _dispatcher.Post(UpdateLive);

    private void UpdateLive()
    {
        var stats = _monitor.GetRecentStatistics(10);
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
