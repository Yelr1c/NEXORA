using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;

namespace Nexora.App.ViewModels;

public sealed class OptimizationItemViewModel : ObservableObject
{
    private readonly OptimizationCoordinator _coordinator;
    private OptimizationState? _state;
    private bool _isBusy;
    private bool _canUndo;
    private bool _isSelected;
    private bool _isExpanded;

    public OptimizationItemViewModel(IOptimization optimization, OptimizationCoordinator coordinator)
    {
        Optimization = optimization;
        _coordinator = coordinator;
        ApplyCommand = new AsyncRelayCommand(ApplyAsync, () => CanApply);
        UndoCommand = new AsyncRelayCommand(UndoAsync, () => CanUndo && !IsBusy);
        ToggleDetailsCommand = new RelayCommand(() => IsExpanded = !IsExpanded);
    }

    public IOptimization Optimization { get; }

    public string Id => Optimization.Id;

    public string Name => Optimization.Name;

    public string Description => Optimization.Description;

    public string WhatItChanges => Optimization.WhatItChanges;

    public string WhyItHelps => Optimization.WhyItHelps;

    public string ExpectedBenefit => Optimization.ExpectedBenefit;

    public string Category => OptimizationCoordinator.CategoryText(Optimization.Category);

    public OptimizationCategory CategoryValue => Optimization.Category;

    public string RiskText => OptimizationCoordinator.RiskText(Optimization.Risk);

    public StatusLevel RiskLevel => Optimization.Risk switch
    {
        Core.Optimization.RiskLevel.Low => StatusLevel.Good,
        Core.Optimization.RiskLevel.Medium => StatusLevel.Warning,
        _ => StatusLevel.Problem,
    };

    public string ImpactText => OptimizationCoordinator.ImpactText(Optimization.Impact);

    public bool RequiresAdministrator => Optimization.RequiresAdministrator;

    public bool RequiresRestart => Optimization.RequiresRestart;

    public string Badges
    {
        get
        {
            var parts = new List<string>();
            if (Optimization.RequiresAdministrator)
            {
                parts.Add("Requires administrator");
            }

            if (Optimization.RequiresRestart)
            {
                parts.Add("Restart required");
            }

            if (Optimization.Kind == OptimizationKind.Action)
            {
                parts.Add("One-time action");
            }

            return string.Join(" · ", parts);
        }
    }

    public bool HasBadges => Badges.Length > 0;

    public string StatusText => _state?.Status switch
    {
        OptimizationStatus.Applied when Optimization.Kind == OptimizationKind.Action => "Clean",
        OptimizationStatus.Applied => "Optimized",
        OptimizationStatus.NotApplied when Optimization.Kind == OptimizationKind.Action => "Available",
        OptimizationStatus.NotApplied => "Not optimized",
        OptimizationStatus.Unavailable => "Unavailable",
        _ => "Checking…",
    };

    public StatusLevel StatusLevel => _state?.Status switch
    {
        OptimizationStatus.Applied => StatusLevel.Good,
        OptimizationStatus.NotApplied => StatusLevel.Warning,
        _ => StatusLevel.Neutral,
    };

    public string CurrentValue => _state is null ? "Reading current setting…" : _state.Status == OptimizationStatus.Unavailable ? _state.UnavailableReason ?? Helpers.Format.Unavailable : "Current: " + _state.CurrentValue;

    public bool IsApplied => _state?.Status == OptimizationStatus.Applied;

    public bool IsAvailable => _state is not null && _state.Status != OptimizationStatus.Unavailable;

    public bool CanApply => !IsBusy && _state?.Status == OptimizationStatus.NotApplied;

    public string ApplyLabel => Optimization.Kind == OptimizationKind.Action ? "Run" : "Apply";

    public bool IsSelected { get => _isSelected; set => SetProperty(ref _isSelected, value); }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (SetProperty(ref _isExpanded, value))
            {
                OnPropertyChanged(nameof(DetailsLabel));
            }
        }
    }

    public string DetailsLabel => IsExpanded ? "Hide details" : "Details";

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                RaiseState();
            }
        }
    }

    public bool CanUndo
    {
        get => _canUndo;
        private set
        {
            if (SetProperty(ref _canUndo, value))
            {
                UndoCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public AsyncRelayCommand ApplyCommand { get; }

    public AsyncRelayCommand UndoCommand { get; }

    public RelayCommand ToggleDetailsCommand { get; }

    public bool IsQuickOptimizeCandidate => Optimization.IsQuickOptimizeCandidate;

    public bool IsTempCleanup => Optimization.Id == TempFilesCleanupOptimization.OptimizationId;

    public async Task RefreshAsync()
    {
        try
        {
            _state = await Optimization.GetStateAsync();
        }
        catch (Exception ex)
        {
            _state = OptimizationState.Unavailable("The current state could not be read: " + ex.Message);
        }

        CanUndo = await _coordinator.HasUndoableAsync(Optimization.Id);
        RaiseState();
    }

    private async Task ApplyAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.ApplyAsync([Optimization]);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private async Task UndoAsync()
    {
        IsBusy = true;
        try
        {
            await _coordinator.UndoLatestAsync(Optimization.Id);
        }
        finally
        {
            IsBusy = false;
            await RefreshAsync();
        }
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(StatusLevel));
        OnPropertyChanged(nameof(CurrentValue));
        OnPropertyChanged(nameof(IsApplied));
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(CanApply));
        ApplyCommand.NotifyCanExecuteChanged();
        UndoCommand.NotifyCanExecuteChanged();
    }
}
