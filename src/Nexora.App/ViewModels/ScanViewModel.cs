using System.Collections.ObjectModel;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Scanning;
using Nexora.Core.Settings;

namespace Nexora.App.ViewModels;

public sealed class ScanStepViewModel(string name) : ObservableObject
{
    private int _state;

    public string Name => name;

    /// <summary>0 pending, 1 active, 2 done.</summary>
    public int State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
            }
        }
    }

    public bool IsPending => State == 0;

    public bool IsActive => State == 1;

    public bool IsDone => State == 2;
}

public sealed class FindingViewModel(ScanFinding finding)
{
    public string Area => finding.Area;

    public string Title => finding.Title;

    public string Detail => finding.Detail;

    public StatusLevel Level => finding.Severity switch
    {
        FindingSeverity.Good => StatusLevel.Good,
        FindingSeverity.Info => StatusLevel.Neutral,
        FindingSeverity.Warning => StatusLevel.Warning,
        _ => StatusLevel.Problem,
    };

    public string Glyph => finding.Severity switch
    {
        FindingSeverity.Good => "",
        FindingSeverity.Info => "",
        FindingSeverity.Warning => "",
        _ => "",
    };
}

public sealed class RecommendationViewModel : ObservableObject
{
    private bool _isSelected;

    public RecommendationViewModel(ScanRecommendation recommendation, bool elevated)
    {
        Recommendation = recommendation;
        CanSelect = !recommendation.Optimization.RequiresAdministrator || elevated;
        // Pre-select only low-risk changes that need no elevation. Nothing is applied without the user's click.
        _isSelected = CanSelect && recommendation.IsRecommended && recommendation.Optimization.IsQuickOptimizeCandidate;
    }

    public ScanRecommendation Recommendation { get; }

    public string Name => Recommendation.Optimization.Name;

    public string Description => Recommendation.Optimization.Description;

    public string Benefit => Recommendation.Optimization.ExpectedBenefit;

    public string ImpactText => OptimizationCoordinator.ImpactText(Recommendation.Optimization.Impact);

    public string RiskText => OptimizationCoordinator.RiskText(Recommendation.Optimization.Risk);

    public string Current => "Current: " + Recommendation.State.CurrentValue;

    public string Reason => (Recommendation.IsRecommended ? "✓ Recommended: " : "Optional: ") + (Recommendation.Advice?.Reason ?? string.Empty);

    public bool IsRecommended => Recommendation.IsRecommended;

    public bool CanSelect { get; }

    public string Note => CanSelect ? string.Empty : "Requires administrator";

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }
}

public sealed class ScanViewModel : ObservableObject, INavigationAware
{
    private readonly SystemScanner _scanner;
    private readonly OptimizationCoordinator _coordinator;
    private readonly NavigationService _navigation;
    private readonly ISettingsService _settings;
    private CancellationTokenSource? _cts;
    private bool _isScanning;
    private bool _hasReport;
    private double _progress;
    private string _currentStep = string.Empty;
    private double _score;
    private string _rating = string.Empty;
    private string _improvementsText = string.Empty;
    private string _impactBreakdown = string.Empty;
    private bool _isFirstRun;

    public ScanViewModel(SystemScanner scanner, OptimizationCoordinator coordinator, NavigationService navigation, ISettingsService settings)
    {
        _scanner = scanner;
        _coordinator = coordinator;
        _navigation = navigation;
        _settings = settings;
        foreach (var step in SystemScanner.Steps)
        {
            Steps.Add(new ScanStepViewModel(step));
        }

        StartScanCommand = new AsyncRelayCommand(ScanAsync, () => !IsScanning);
        ApplySelectedCommand = new AsyncRelayCommand(ApplySelectedAsync, () => !IsScanning);
        ContinueCommand = new AsyncRelayCommand(ContinueAsync);
        CancelCommand = new RelayCommand(() => _cts?.Cancel());
    }

    public ObservableCollection<ScanStepViewModel> Steps { get; } = [];

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    public ObservableCollection<RecommendationViewModel> Recommendations { get; } = [];

    public AsyncRelayCommand StartScanCommand { get; }

    public AsyncRelayCommand ApplySelectedCommand { get; }

    public AsyncRelayCommand ContinueCommand { get; }

    public RelayCommand CancelCommand { get; }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetProperty(ref _isScanning, value))
            {
                OnPropertyChanged(nameof(IsIntro));
                StartScanCommand.NotifyCanExecuteChanged();
                ApplySelectedCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool HasReport
    {
        get => _hasReport;
        private set
        {
            if (SetProperty(ref _hasReport, value))
            {
                OnPropertyChanged(nameof(IsIntro));
            }
        }
    }

    public bool IsIntro => !IsScanning && !HasReport;

    public bool HasRecommendations => Recommendations.Count > 0;

    public bool HasNoRecommendations => HasReport && Recommendations.Count == 0;

    public bool IsFirstRun { get => _isFirstRun; private set => SetProperty(ref _isFirstRun, value); }

    public string ContinueLabel => IsFirstRun ? "Continue to Dashboard" : "Back to Dashboard";

    public double Progress { get => _progress; private set => SetProperty(ref _progress, value); }

    public string CurrentStep { get => _currentStep; private set => SetProperty(ref _currentStep, value); }

    public double Score
    {
        get => _score;
        private set
        {
            if (SetProperty(ref _score, value))
            {
                OnPropertyChanged(nameof(ScoreText));
            }
        }
    }

    public string ScoreText => Score.ToString("0", System.Globalization.CultureInfo.CurrentCulture);

    public string Rating { get => _rating; private set => SetProperty(ref _rating, value); }

    public string ImprovementsText { get => _improvementsText; private set => SetProperty(ref _improvementsText, value); }

    public string ImpactBreakdown { get => _impactBreakdown; private set => SetProperty(ref _impactBreakdown, value); }

    public void OnNavigatedTo(object? parameter)
    {
        IsFirstRun = parameter is "first-run";
        OnPropertyChanged(nameof(ContinueLabel));
        if ((parameter is "first-run" or "start") && !IsScanning)
        {
            StartScanCommand.Execute(null);
        }
    }

    public void OnNavigatedFrom() => _cts?.Cancel();

    private async Task ScanAsync()
    {
        IsScanning = true;
        HasReport = false;
        Progress = 0;
        foreach (var step in Steps)
        {
            step.State = 0;
        }

        _cts = new CancellationTokenSource();
        var progress = new Progress<ScanProgress>(p =>
        {
            Progress = p.Fraction * 100;
            CurrentStep = p.StepIndex < Steps.Count ? $"Analyzing {p.StepName.ToLowerInvariant()}…" : "Building report…";
            for (var i = 0; i < Steps.Count; i++)
            {
                Steps[i].State = i < p.StepIndex ? 2 : i == p.StepIndex ? 1 : 0;
            }
        });

        try
        {
            var report = await _scanner.ScanAsync(progress, _cts.Token);
            ShowReport(report);
        }
        catch (OperationCanceledException)
        {
            CurrentStep = "Scan cancelled";
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            IsScanning = false;
        }
    }

    private void ShowReport(ScanReport report)
    {
        Score = report.Score.Score;
        Rating = report.Score.Rating;
        Findings.Clear();
        foreach (var finding in report.Findings.OrderByDescending(f => f.Severity))
        {
            Findings.Add(new FindingViewModel(finding));
        }

        Recommendations.Clear();
        foreach (var recommendation in report.Recommendations)
        {
            Recommendations.Add(new RecommendationViewModel(recommendation, _coordinator.IsElevated));
        }

        var recommended = report.RecommendedCount;
        var optional = Recommendations.Count - recommended;
        ImprovementsText = Recommendations.Count == 0
            ? "No further optimizations available"
            : $"{recommended} recommended for your PC" + (optional > 0 ? $" · {optional} optional" : string.Empty);
        ImpactBreakdown = $"{report.HighImpactCount} high · {report.MediumImpactCount} medium · {report.LowImpactCount} low impact";
        OnPropertyChanged(nameof(HasRecommendations));
        OnPropertyChanged(nameof(HasNoRecommendations));
        HasReport = true;
    }

    private async Task ApplySelectedAsync()
    {
        var selected = Recommendations.Where(r => r.IsSelected && r.CanSelect).Select(r => r.Recommendation.Optimization).ToList();
        if (selected.Count == 0)
        {
            await App.GetService<DialogService>().InformAsync("Nothing selected", "Select the optimizations you want to apply. NEXORA never applies changes automatically.");
            return;
        }

        var result = await _coordinator.ApplyAsync(selected, forceConfirm: true);
        if (result is not null)
        {
            foreach (var applied in result.Results.Where(r => r.IsSuccess))
            {
                var item = Recommendations.FirstOrDefault(r => r.Recommendation.Optimization.Id == applied.OptimizationId);
                if (item is not null)
                {
                    Recommendations.Remove(item);
                }
            }

            ImprovementsText = Recommendations.Count == 0 ? "All selected optimizations applied" : $"{Recommendations.Count} optimization(s) remaining";
            OnPropertyChanged(nameof(HasRecommendations));
            OnPropertyChanged(nameof(HasNoRecommendations));
        }
    }

    private async Task ContinueAsync()
    {
        if (!_settings.Current.FirstRunCompleted)
        {
            await _settings.UpdateAsync(s => s.FirstRunCompleted = true);
        }

        App.GetService<MainWindow>().ShowNavigation(true);
        _navigation.Navigate("dashboard");
    }
}
