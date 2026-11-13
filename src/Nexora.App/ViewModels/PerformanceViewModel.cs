using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Dispatching;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Monitoring;
using Nexora.Core.Processes;
using Nexora.Core.Settings;
using Nexora.Platform.Windows.Services;

namespace Nexora.App.ViewModels;

public sealed record DurationOption(int Seconds, string Label)
{
    public override string ToString() => Label;
}

public sealed record TargetOption(int? ProcessId, string Label)
{
    public override string ToString() => Label;
}

public sealed class ProcessUsageViewModel(ProcessSnapshot snapshot)
{
    public string Name => snapshot.Name;

    public string CpuText => Format.Percent(snapshot.CpuPercent);

    public string MemoryText => Format.Bytes(snapshot.MemoryBytes);

    public string GpuText => snapshot.GpuPercent is { } g && g >= 0.5 ? Format.Percent(g) : Format.Dash;
}

public sealed record SessionChoice(PerformanceSessionResult Result)
{
    public override string ToString()
        => $"{Result.StartedAt.ToLocalTime():g} · {Result.ProcessName ?? "System"}" + (Result.Frames is { } f ? $" · {f.AverageFps:0} FPS" : string.Empty);
}

public sealed class ComparisonRowViewModel(ComparisonRow row)
{
    public string Metric => row.Metric;

    public string Before => row.Before;

    public string After => row.After;

    public string Verdict => row.VerdictText;

    public string Note => row.Note;

    public StatusLevel Level => row.Verdict switch
    {
        ComparisonVerdict.Improved => StatusLevel.Good,
        ComparisonVerdict.Worse => StatusLevel.Problem,
        _ => StatusLevel.Neutral,
    };
}

public sealed class SessionHistoryItem(PerformanceSessionResult result)
{
    public string Date => result.StartedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Title => result.ProcessName ?? "System";

    public string AvgFps => result.Frames is { } f ? f.AverageFps.ToString("0", CultureInfo.CurrentCulture) : Format.Dash;

    public string Low1 => result.Frames is { } f ? f.OnePercentLowFps.ToString("0", CultureInfo.CurrentCulture) : Format.Dash;

    public string Duration => $"{result.DurationSeconds:0} s";

    public string Load => $"CPU {Format.Percent(result.Cpu.Average)} · GPU {Format.Percent(result.Gpu.Average)}";
}

public sealed class PerformanceViewModel : ObservableObject, INavigationAware
{
    private const int ChartPoints = 120;
    private readonly MetricsMonitor _monitor;
    private readonly IFrameRateSource _frames;
    private readonly PerformanceSessionService _sessions;
    private readonly IProcessService _processes;
    private readonly ISettingsService _settings;
    private readonly UiDispatcher _dispatcher;
    private DispatcherQueueTimer? _processTimer;
    private string _fpsText = Format.Dash;
    private string _frameTimeText = Format.Dash;
    private string _fpsProcess = string.Empty;
    private string _cpuText = Format.Dash;
    private string _gpuText = Format.Dash;
    private string _ramText = Format.Dash;
    private string _vramText = Format.Dash;
    private string _cpuFreqText = Format.Dash;
    private string _gpuFreqText = Format.Dash;
    private string _cpuTempText = Format.Dash;
    private string _gpuTempText = Format.Dash;
    private string _processCountText = Format.Dash;
    private IReadOnlyList<double> _fpsHistory = [];
    private IReadOnlyList<double> _frameTimeHistory = [];
    private IReadOnlyList<double> _cpuHistory = [];
    private IReadOnlyList<double> _gpuHistory = [];
    private IReadOnlyList<double> _ramHistory = [];
    private string _captureStatus = string.Empty;
    private bool _captureRunning;
    private bool _captureNeedsElevation;
    private DurationOption _selectedDuration;
    private TargetOption _selectedTarget = new(null, "Automatic (foreground game)");
    private bool _isSessionRunning;
    private double _sessionProgress;
    private string _sessionRemaining = string.Empty;
    private bool _hasResult;
    private string _resultTitle = string.Empty;
    private string _avgFps = Format.Dash;
    private string _onePercentLow = Format.Dash;
    private string _pointOnePercentLow = Format.Dash;
    private string _avgFrameTime = Format.Dash;
    private string _resultCpu = Format.Dash;
    private string _resultGpu = Format.Dash;
    private string _resultRam = Format.Dash;
    private string _resultFrameNote = string.Empty;

    public PerformanceViewModel(MetricsMonitor monitor, IFrameRateSource frames, PerformanceSessionService sessions, IProcessService processes, ISettingsService settings, UiDispatcher dispatcher)
    {
        _monitor = monitor;
        _frames = frames;
        _sessions = sessions;
        _processes = processes;
        _settings = settings;
        _dispatcher = dispatcher;
        Durations = [new(15, "15 seconds"), new(30, "30 seconds"), new(60, "1 minute"), new(120, "2 minutes"), new(300, "5 minutes"), new(600, "10 minutes")];
        _selectedDuration = Durations.FirstOrDefault(d => d.Seconds == settings.Current.DefaultSessionDurationSeconds) ?? Durations[2];
        Targets.Add(_selectedTarget);
        StartSessionCommand = new AsyncRelayCommand(RunSessionAsync, () => !IsSessionRunning);
        StopSessionCommand = new RelayCommand(() => _sessions.Stop(), () => IsSessionRunning);
        StartCaptureCommand = new RelayCommand(StartCapture);
        RestartElevatedCommand = new RelayCommand(() =>
        {
            if (ElevationHelper.TryRestartElevated("--elevated-restart --page performance"))
            {
                App.Shutdown();
            }
        });
        RefreshTargetsCommand = new RelayCommand(RefreshTargets);
        _sessions.ProgressChanged += (_, p) => _dispatcher.Post(() =>
        {
            SessionProgress = p * 100;
            var remaining = Math.Max(0, _sessions.Duration.TotalSeconds * (1 - p));
            SessionRemaining = $"{remaining:0} s remaining";
        });
        _frames.StatusChanged += (_, _) => _dispatcher.Post(UpdateCaptureStatus);
        UpdateCaptureStatus();
    }

    public IReadOnlyList<DurationOption> Durations { get; }

    public ObservableCollection<TargetOption> Targets { get; } = [];

    public ObservableCollection<ProcessUsageViewModel> TopProcesses { get; } = [];

    public ObservableCollection<string> ResultInsights { get; } = [];

    public ObservableCollection<SessionHistoryItem> SessionHistory { get; } = [];

    // ----- Bottleneck -----
    private string _liveBottleneck = "Start a game to see what limits your performance.";
    private string _resultBottleneckTitle = string.Empty;
    private string _resultBottleneckText = string.Empty;
    private string _gpuFanText = Format.Dash;

    public string LiveBottleneck { get => _liveBottleneck; private set => SetProperty(ref _liveBottleneck, value); }

    public string ResultBottleneckTitle { get => _resultBottleneckTitle; private set => SetProperty(ref _resultBottleneckTitle, value); }

    public string ResultBottleneckText { get => _resultBottleneckText; private set => SetProperty(ref _resultBottleneckText, value); }

    public string GpuFanText { get => _gpuFanText; private set => SetProperty(ref _gpuFanText, value); }

    // ----- Performance report (before / after) -----
    private SessionChoice? _compareBefore;
    private SessionChoice? _compareAfter;
    private string _compareBeforeFps = Format.Dash;
    private string _compareAfterFps = Format.Dash;
    private string _compareSummary = string.Empty;
    private string _compareWarning = string.Empty;

    public ObservableCollection<SessionChoice> SessionChoices { get; } = [];

    public ObservableCollection<ComparisonRowViewModel> ComparisonRows { get; } = [];

    public bool CanCompare => SessionChoices.Count >= 2;

    public bool CannotCompare => !CanCompare;

    public SessionChoice? CompareBefore
    {
        get => _compareBefore;
        set
        {
            if (SetProperty(ref _compareBefore, value))
            {
                UpdateComparison();
            }
        }
    }

    public SessionChoice? CompareAfter
    {
        get => _compareAfter;
        set
        {
            if (SetProperty(ref _compareAfter, value))
            {
                UpdateComparison();
            }
        }
    }

    public string CompareBeforeFps { get => _compareBeforeFps; private set => SetProperty(ref _compareBeforeFps, value); }

    public string CompareAfterFps { get => _compareAfterFps; private set => SetProperty(ref _compareAfterFps, value); }

    public string CompareSummary { get => _compareSummary; private set => SetProperty(ref _compareSummary, value); }

    public string CompareWarning
    {
        get => _compareWarning;
        private set
        {
            if (SetProperty(ref _compareWarning, value))
            {
                OnPropertyChanged(nameof(HasCompareWarning));
            }
        }
    }

    public bool HasCompareWarning => CompareWarning.Length > 0;

    private void UpdateComparison()
    {
        ComparisonRows.Clear();
        if (CompareBefore is not { } before || CompareAfter is not { } after || ReferenceEquals(before, after))
        {
            CompareSummary = "Choose two different tests to compare.";
            CompareBeforeFps = CompareAfterFps = Format.Dash;
            CompareWarning = string.Empty;
            return;
        }

        var comparison = SessionComparer.Compare(before.Result, after.Result);
        CompareBeforeFps = before.Result.Frames is { } bf ? bf.AverageFps.ToString("0", CultureInfo.CurrentCulture) : Format.Dash;
        CompareAfterFps = after.Result.Frames is { } af ? af.AverageFps.ToString("0", CultureInfo.CurrentCulture) : Format.Dash;
        foreach (var row in comparison.Rows)
        {
            ComparisonRows.Add(new ComparisonRowViewModel(row));
        }

        CompareSummary = comparison.Summary;
        CompareWarning = string.Join(" ", comparison.Warnings);
    }

    public bool HasSessionHistory => SessionHistory.Count > 0;

    public RelayCommand ToggleOverlayCommand { get; } = new(App.ToggleOverlay);

    private async Task LoadHistoryAsync()
    {
        var history = await _sessions.GetHistoryAsync();
        SessionHistory.Clear();
        foreach (var item in history.Take(10))
        {
            SessionHistory.Add(new SessionHistoryItem(item));
        }

        SessionChoices.Clear();
        foreach (var item in history.Take(20))
        {
            SessionChoices.Add(new SessionChoice(item));
        }

        // Default report: newest test vs. the previous test of the same application.
        if (SessionChoices.Count >= 2)
        {
            var newest = SessionChoices[0];
            var previous = SessionChoices.Skip(1).FirstOrDefault(c => string.Equals(c.Result.ProcessName, newest.Result.ProcessName, StringComparison.OrdinalIgnoreCase))
                           ?? SessionChoices[1];
            _compareAfter = newest;
            _compareBefore = previous;
            OnPropertyChanged(nameof(CompareAfter));
            OnPropertyChanged(nameof(CompareBefore));
            UpdateComparison();
        }

        OnPropertyChanged(nameof(HasSessionHistory));
        OnPropertyChanged(nameof(CanCompare));
        OnPropertyChanged(nameof(CannotCompare));
    }

    public AsyncRelayCommand StartSessionCommand { get; }

    public RelayCommand StopSessionCommand { get; }

    public RelayCommand StartCaptureCommand { get; }

    public RelayCommand RestartElevatedCommand { get; }

    public RelayCommand RefreshTargetsCommand { get; }

    public string FpsText { get => _fpsText; private set => SetProperty(ref _fpsText, value); }

    public string FrameTimeText { get => _frameTimeText; private set => SetProperty(ref _frameTimeText, value); }

    public string FpsProcess { get => _fpsProcess; private set => SetProperty(ref _fpsProcess, value); }

    public string CpuText { get => _cpuText; private set => SetProperty(ref _cpuText, value); }

    public string GpuText { get => _gpuText; private set => SetProperty(ref _gpuText, value); }

    public string RamText { get => _ramText; private set => SetProperty(ref _ramText, value); }

    public string VramText { get => _vramText; private set => SetProperty(ref _vramText, value); }

    public string CpuFreqText { get => _cpuFreqText; private set => SetProperty(ref _cpuFreqText, value); }

    public string GpuFreqText { get => _gpuFreqText; private set => SetProperty(ref _gpuFreqText, value); }

    public string CpuTempText { get => _cpuTempText; private set => SetProperty(ref _cpuTempText, value); }

    public string GpuTempText { get => _gpuTempText; private set => SetProperty(ref _gpuTempText, value); }

    public string ProcessCountText { get => _processCountText; private set => SetProperty(ref _processCountText, value); }

    public IReadOnlyList<double> FpsHistory { get => _fpsHistory; private set => SetProperty(ref _fpsHistory, value); }

    public IReadOnlyList<double> FrameTimeHistory { get => _frameTimeHistory; private set => SetProperty(ref _frameTimeHistory, value); }

    public IReadOnlyList<double> CpuHistory { get => _cpuHistory; private set => SetProperty(ref _cpuHistory, value); }

    public IReadOnlyList<double> GpuHistory { get => _gpuHistory; private set => SetProperty(ref _gpuHistory, value); }

    public IReadOnlyList<double> RamHistory { get => _ramHistory; private set => SetProperty(ref _ramHistory, value); }

    public string CaptureStatus { get => _captureStatus; private set => SetProperty(ref _captureStatus, value); }

    public bool CaptureRunning { get => _captureRunning; private set => SetProperty(ref _captureRunning, value); }

    public bool CaptureNeedsElevation { get => _captureNeedsElevation; private set => SetProperty(ref _captureNeedsElevation, value); }

    public bool CaptureCanStart => !CaptureRunning && !CaptureNeedsElevation;

    public DurationOption SelectedDuration
    {
        get => _selectedDuration;
        set
        {
            if (value is not null && SetProperty(ref _selectedDuration, value))
            {
                _settings.UpdateAsync(s => s.DefaultSessionDurationSeconds = value.Seconds).Forget("save session duration");
            }
        }
    }

    public TargetOption SelectedTarget
    {
        get => _selectedTarget;
        set
        {
            if (value is not null && SetProperty(ref _selectedTarget, value))
            {
                _frames.TargetProcessId = value.ProcessId;
            }
        }
    }

    public bool IsSessionRunning
    {
        get => _isSessionRunning;
        private set
        {
            if (SetProperty(ref _isSessionRunning, value))
            {
                OnPropertyChanged(nameof(IsSessionIdle));
                StartSessionCommand.NotifyCanExecuteChanged();
                StopSessionCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsSessionIdle => !IsSessionRunning;

    public double SessionProgress { get => _sessionProgress; private set => SetProperty(ref _sessionProgress, value); }

    public string SessionRemaining { get => _sessionRemaining; private set => SetProperty(ref _sessionRemaining, value); }

    public bool HasResult { get => _hasResult; private set => SetProperty(ref _hasResult, value); }

    public string ResultTitle { get => _resultTitle; private set => SetProperty(ref _resultTitle, value); }

    public string AvgFps { get => _avgFps; private set => SetProperty(ref _avgFps, value); }

    public string OnePercentLow { get => _onePercentLow; private set => SetProperty(ref _onePercentLow, value); }

    public string PointOnePercentLow { get => _pointOnePercentLow; private set => SetProperty(ref _pointOnePercentLow, value); }

    public string AvgFrameTime { get => _avgFrameTime; private set => SetProperty(ref _avgFrameTime, value); }

    public string ResultCpu { get => _resultCpu; private set => SetProperty(ref _resultCpu, value); }

    public string ResultGpu { get => _resultGpu; private set => SetProperty(ref _resultGpu, value); }

    public string ResultRam { get => _resultRam; private set => SetProperty(ref _resultRam, value); }

    public string ResultFrameNote { get => _resultFrameNote; private set => SetProperty(ref _resultFrameNote, value); }

    public void OnNavigatedTo(object? parameter)
    {
        _monitor.SnapshotAvailable += OnSnapshot;
        if (_settings.Current.FpsCaptureEnabled && _frames.Status == FrameCaptureStatus.Stopped)
        {
            StartCapture();
        }

        RefreshTargets();
        RefreshProcesses();
        LoadHistoryAsync().Forget("load test history");
        _processTimer ??= _dispatcher.CreateTimer(TimeSpan.FromSeconds(3), () =>
        {
            RefreshProcesses();
            RefreshTargets();
        });
        _processTimer.Start();
        if (_monitor.Latest is { } latest)
        {
            Update(latest);
        }
    }

    public void OnNavigatedFrom()
    {
        _monitor.SnapshotAvailable -= OnSnapshot;
        _processTimer?.Stop();
    }

    private void StartCapture()
    {
        _frames.TryStart();
        UpdateCaptureStatus();
    }

    private void UpdateCaptureStatus()
    {
        CaptureRunning = _frames.Status == FrameCaptureStatus.Running;
        CaptureNeedsElevation = _frames.Status == FrameCaptureStatus.RequiresElevation;
        CaptureStatus = _frames.Status switch
        {
            FrameCaptureStatus.Running => "FPS capture active · DirectX 9/10/11/12 games",
            FrameCaptureStatus.Stopped => "FPS capture is off",
            _ => _frames.StatusMessage,
        };
        OnPropertyChanged(nameof(CaptureCanStart));
    }

    private void RefreshTargets()
    {
        var rendering = _frames.GetRenderingProcesses();
        var selectedId = SelectedTarget.ProcessId;
        for (var i = Targets.Count - 1; i >= 1; i--)
        {
            if (Targets[i].ProcessId != selectedId && rendering.All(r => r.ProcessId != Targets[i].ProcessId))
            {
                Targets.RemoveAt(i);
            }
        }

        foreach (var process in rendering)
        {
            if (Targets.All(t => t.ProcessId != process.ProcessId))
            {
                Targets.Add(new TargetOption(process.ProcessId, $"{process.ProcessName} (PID {process.ProcessId})"));
            }
        }
    }

    private void RefreshProcesses()
    {
        Task.Run(() =>
        {
            try
            {
                var top = _processes.GetProcesses().Where(p => p.CpuPercent is not null || p.MemoryBytes > 0)
                    .OrderByDescending(p => p.CpuPercent ?? 0).ThenByDescending(p => p.MemoryBytes).Take(6).ToList();
                _dispatcher.Post(() =>
                {
                    TopProcesses.Clear();
                    foreach (var p in top)
                    {
                        TopProcesses.Add(new ProcessUsageViewModel(p));
                    }
                });
            }
            catch (InvalidOperationException)
            {
                // Process list changed while reading.
            }
        }).Forget("read top processes");
    }

    private void OnSnapshot(object? sender, MetricSnapshot snapshot) => _dispatcher.Post(() => Update(snapshot));

    private void Update(MetricSnapshot s)
    {
        FpsText = s.Fps is { } fps ? fps.ToString("0", CultureInfo.CurrentCulture) : Format.Dash;
        FrameTimeText = Format.Ms(s.FrameTimeMs);
        FpsProcess = s.FpsProcessName is { } name ? name : CaptureRunning ? "Waiting for a DirectX game to render…" : CaptureStatus;
        CpuText = Format.Percent(s.CpuUsagePercent);
        GpuText = s.GpuUsagePercent.HasValue ? Format.Percent(s.GpuUsagePercent) : Format.Unavailable;
        RamText = s.RamTotalBytes > 0 ? $"{Format.Bytes(s.RamUsedBytes)} / {Format.Bytes(s.RamTotalBytes)}" : Format.Unavailable;
        VramText = s.VramUsedBytes is { } v ? Format.Bytes(v) + (s.VramTotalBytes is { } t ? " / " + Format.Bytes(t) : string.Empty) : Format.Unavailable;
        CpuFreqText = s.CpuFrequencyMhz is { } cf ? Format.Mhz(cf) : Format.Unavailable;
        GpuFreqText = s.GpuFrequencyMhz is { } gf ? Format.Mhz(gf) : Format.Unavailable;
        CpuTempText = s.CpuTemperatureCelsius is { } ct ? Format.Celsius(ct) : Format.Unavailable;
        GpuTempText = s.GpuTemperatureCelsius is { } gt ? Format.Celsius(gt) : Format.Unavailable;
        ProcessCountText = s.ProcessCount > 0 ? s.ProcessCount.ToString(CultureInfo.CurrentCulture) : Format.Dash;

        GpuFanText = s.GpuFanRpm is { } rpm ? $"{rpm:N0} RPM" : Format.Unavailable;

        var history = _monitor.GetHistory();
        var lastTen = history.Skip(Math.Max(0, history.Count - 10)).ToList();
        LiveBottleneck = lastTen.Count(x => x.Fps.HasValue) >= 5
            ? BottleneckAnalyzer.Analyze(lastTen) is var live ? $"{live.Title} — {live.Explanation}" : string.Empty
            : "Start a game to see what limits your performance.";
        var recent = history.Skip(Math.Max(0, history.Count - ChartPoints)).ToList();
        FpsHistory = recent.Select(x => x.Fps ?? double.NaN).ToList();
        FrameTimeHistory = recent.Select(x => x.FrameTimeMs ?? double.NaN).ToList();
        CpuHistory = recent.Select(x => x.CpuUsagePercent ?? double.NaN).ToList();
        GpuHistory = recent.Select(x => x.GpuUsagePercent ?? double.NaN).ToList();
        RamHistory = recent.Select(x => x.RamUsagePercent).ToList();
    }

    private async Task RunSessionAsync()
    {
        IsSessionRunning = true;
        SessionProgress = 0;
        HasResult = false;
        try
        {
            if (_settings.Current.FpsCaptureEnabled)
            {
                StartCapture();
            }

            var result = await _sessions.RunAsync(TimeSpan.FromSeconds(SelectedDuration.Seconds));
            ShowResult(result);
            await LoadHistoryAsync();
        }
        finally
        {
            IsSessionRunning = false;
        }
    }

    private void ShowResult(PerformanceSessionResult r)
    {
        ResultTitle = $"{(r.ProcessName is { } p ? p + " · " : string.Empty)}{r.DurationSeconds:0} s session · {r.StartedAt.ToLocalTime():t}";
        if (r.Frames is { } f)
        {
            AvgFps = f.AverageFps.ToString("0", CultureInfo.CurrentCulture);
            OnePercentLow = f.OnePercentLowFps.ToString("0", CultureInfo.CurrentCulture);
            PointOnePercentLow = f.PointOnePercentLowFps.ToString("0", CultureInfo.CurrentCulture);
            AvgFrameTime = Format.Ms(f.AverageFrameTimeMs);
            ResultFrameNote = $"{f.FrameCount:N0} frames analysed · worst frame {Format.Ms(f.MaxFrameTimeMs)}";
        }
        else
        {
            AvgFps = OnePercentLow = PointOnePercentLow = AvgFrameTime = Format.Dash;
            ResultFrameNote = r.FrameUnavailableReason ?? string.Empty;
        }

        ResultCpu = r.Cpu.Average is { } c ? $"{Format.Percent(c)} avg · {Format.Percent(r.Cpu.Max)} peak" : Format.Unavailable;
        ResultGpu = r.Gpu.Average is { } g ? $"{Format.Percent(g)} avg · {Format.Percent(r.Gpu.Max)} peak" : Format.Unavailable;
        ResultRam = r.Ram.Average is { } m ? $"{Format.Percent(m)} avg · {Format.Percent(r.Ram.Max)} peak" : Format.Unavailable;
        ResultBottleneckTitle = r.BottleneckTitle is { } bt ? $"Limiting factor: {bt}" : string.Empty;
        ResultBottleneckText = r.BottleneckExplanation ?? string.Empty;
        ResultInsights.Clear();
        foreach (var insight in r.Insights)
        {
            ResultInsights.Add(insight);
        }

        HasResult = true;
    }
}
