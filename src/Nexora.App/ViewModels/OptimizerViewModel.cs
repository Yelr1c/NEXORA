using System.Collections.ObjectModel;
using System.Globalization;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;

using Nexora.App.Helpers;

namespace Nexora.App.ViewModels;

public sealed record CategoryFilter(OptimizationCategory? Category, string Label, string Glyph)
{
    public override string ToString() => Label;
}

public sealed class StartupItemViewModel : ObservableObject
{
    private readonly OptimizationCoordinator _coordinator;
    private readonly StartupEntryOptimization _optimization;
    private bool _isEnabled;
    private bool _isBusy;

    public StartupItemViewModel(StartupEntryOptimization optimization, OptimizationCoordinator coordinator)
    {
        _optimization = optimization;
        _coordinator = coordinator;
        _isEnabled = optimization.Entry.IsEnabled;
    }

    public string Name => _optimization.Entry.Name;

    public string Command => _optimization.Entry.Command;

    public string Source => _optimization.Entry.SourceDescription;

    public bool IsProtected => _optimization.Entry.IsProtected;

    public string ProtectionText => _optimization.Entry.ProtectionReason ?? (_optimization.Entry.RequiresAdministrator && !_coordinator.IsElevated ? "Requires administrator" : string.Empty);

    public bool CanToggle => !IsProtected && !_isBusy && (!_optimization.Entry.RequiresAdministrator || _coordinator.IsElevated);

    public string StateText => IsEnabled ? "Enabled" : "Disabled";

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (_isEnabled == value || _isBusy)
            {
                return;
            }

            ToggleAsync(value).Forget("toggle startup app");
        }
    }

    private async Task ToggleAsync(bool enable)
    {
        _isBusy = true;
        OnPropertyChanged(nameof(CanToggle));
        try
        {
            if (enable)
            {
                // Re-enabling restores the exact StartupApproved data captured before NEXORA disabled it.
                var result = await _coordinator.UndoLatestAsync(_optimization.Id);
                if (result is null)
                {
                    // Disabled outside NEXORA: there is no NEXORA backup to restore, so defer to Windows.
                    await App.GetService<DialogService>().InformAsync(
                        "Enable in Task Manager",
                        $"\"{Name}\" was disabled outside NEXORA, so there is no backup to restore. Enable it in Task Manager › Startup apps or Settings › Apps › Startup.");
                }
            }
            else
            {
                await _coordinator.ApplyAsync([_optimization]);
            }
        }
        finally
        {
            var state = await _optimization.GetStateAsync();
            _isEnabled = state.Status != OptimizationStatus.Applied;
            _isBusy = false;
            OnPropertyChanged(nameof(IsEnabled));
            OnPropertyChanged(nameof(StateText));
            OnPropertyChanged(nameof(CanToggle));
        }
    }
}

public sealed class HistoryItemViewModel(OptimizationHistoryEntry entry, bool canUndo, OptimizerViewModel owner)
{
    public OptimizationHistoryEntry Entry => entry;

    public string Date => entry.Timestamp.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);

    public string Name => entry.OptimizationName;

    public string PreviousValue => entry.PreviousValue;

    public string NewValue => entry.NewValue;

    public string Status => entry.Status switch
    {
        HistoryStatus.Applied => "Active",
        HistoryStatus.Reverted => "Reverted",
        HistoryStatus.RolledBack => "Rolled back",
        HistoryStatus.Failed => "Failed",
        HistoryStatus.Committed => "Finalized",
        _ => entry.Status.ToString(),
    };

    public StatusLevel Level => entry.Status switch
    {
        HistoryStatus.Applied => StatusLevel.Good,
        HistoryStatus.Failed => StatusLevel.Problem,
        HistoryStatus.RolledBack => StatusLevel.Warning,
        _ => StatusLevel.Neutral,
    };

    public string Message => entry.Message ?? string.Empty;

    public bool HasMessage => !string.IsNullOrEmpty(entry.Message);

    public bool CanUndo => canUndo;

    public bool CanPurge => canUndo && OptimizationCoordinator.IsTempCleanup(entry);

    public AsyncRelayCommand UndoCommand { get; } = new(() => owner.UndoEntryAsync(entry));

    public AsyncRelayCommand PurgeCommand { get; } = new(() => owner.PurgeEntryAsync(entry));
}

public sealed class OptimizerViewModel : ObservableObject, INavigationAware
{
    private readonly StandardOptimizationCatalog _catalog;
    private readonly OptimizationCoordinator _coordinator;
    private readonly NavigationService _navigation;
    private readonly UiDispatcher _dispatcher;
    private readonly List<OptimizationItemViewModel> _all = [];
    private CategoryFilter _selectedCategory;
    private int _section;
    private bool _isLoading;
    private string _summary = string.Empty;

    public OptimizerViewModel(StandardOptimizationCatalog catalog, OptimizationCoordinator coordinator, NavigationService navigation, UiDispatcher dispatcher)
    {
        _catalog = catalog;
        _coordinator = coordinator;
        _navigation = navigation;
        _dispatcher = dispatcher;
        Categories =
        [
            new(null, "All", ""),
            new(OptimizationCategory.Gaming, "Gaming", ""),
            new(OptimizationCategory.Windows, "Windows", ""),
            new(OptimizationCategory.Network, "Network", ""),
            new(OptimizationCategory.BackgroundProcesses, "Background", ""),
            new(OptimizationCategory.Power, "Power", ""),
            new(OptimizationCategory.Storage, "Storage", ""),
        ];
        _selectedCategory = Categories[0];
        ScanCommand = new RelayCommand(() => _navigation.Navigate("scan"));
        QuickOptimizeCommand = new AsyncRelayCommand(QuickOptimizeAsync);
        RestoreAllCommand = new AsyncRelayCommand(async () =>
        {
            await _coordinator.RestoreAllAsync();
            await LoadAsync();
        });
        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        _coordinator.Changed += (_, _) => _dispatcher.Post(() => LoadHistoryAsync().Forget("load history"));
    }

    public IReadOnlyList<CategoryFilter> Categories { get; }

    public ObservableCollection<OptimizationItemViewModel> Items { get; } = [];

    public ObservableCollection<StartupItemViewModel> StartupItems { get; } = [];

    public ObservableCollection<HistoryItemViewModel> HistoryItems { get; } = [];

    public RelayCommand ScanCommand { get; }

    public AsyncRelayCommand QuickOptimizeCommand { get; }

    public AsyncRelayCommand RestoreAllCommand { get; }

    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsLoading { get => _isLoading; private set => SetProperty(ref _isLoading, value); }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public bool HasHistory => HistoryItems.Count > 0;

    public bool HasNoHistory => HistoryItems.Count == 0;

    /// <summary>0 = Optimizations, 1 = Startup apps, 2 = History.</summary>
    public int Section
    {
        get => _section;
        set
        {
            if (SetProperty(ref _section, value))
            {
                OnPropertyChanged(nameof(IsOptimizationsSection));
                OnPropertyChanged(nameof(IsStartupSection));
                OnPropertyChanged(nameof(IsHistorySection));
            }
        }
    }

    public bool IsOptimizationsSection => Section == 0;

    public bool IsStartupSection => Section == 1;

    public bool IsHistorySection => Section == 2;

    public CategoryFilter SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (value is not null && SetProperty(ref _selectedCategory, value))
            {
                ApplyFilter();
            }
        }
    }

    public void OnNavigatedTo(object? parameter)
    {
        if (parameter is "history")
        {
            Section = 2;
        }
        else if (parameter is "startup")
        {
            Section = 1;
        }

        LoadAsync().Forget("load optimizer");
    }

    public void OnNavigatedFrom()
    {
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            if (_all.Count == 0)
            {
                foreach (var optimization in _catalog.StaticOptimizations)
                {
                    _all.Add(new OptimizationItemViewModel(optimization, _coordinator));
                }
            }

            await Task.WhenAll(_all.Select(i => i.RefreshAsync()));
            ApplyFilter();
            var optimized = _all.Count(i => i.IsApplied);
            var available = _all.Count(i => i.CanApply);
            Summary = $"{optimized} of {_all.Count(i => i.IsAvailable)} available optimizations active · {available} can be applied";

            var startup = await Task.Run(_catalog.GetStartupOptimizations);
            StartupItems.Clear();
            foreach (var item in startup.OrderByDescending(s => s.Entry.IsEnabled).ThenBy(s => s.Entry.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                StartupItems.Add(new StartupItemViewModel(item, _coordinator));
            }

            await LoadHistoryAsync();
        }
        finally
        {
            IsLoading = false;
        }
    }

    internal async Task UndoEntryAsync(OptimizationHistoryEntry entry)
    {
        await _coordinator.UndoAsync(entry.Id);
        await LoadAsync();
    }

    internal async Task PurgeEntryAsync(OptimizationHistoryEntry entry)
    {
        await _coordinator.PurgeAsync(entry);
        await LoadHistoryAsync();
    }

    private async Task LoadHistoryAsync()
    {
        var entries = await _coordinator.History.GetAllAsync();
        HistoryItems.Clear();
        foreach (var entry in entries.Take(300))
        {
            HistoryItems.Add(new HistoryItemViewModel(entry, OptimizationEngine.CanUndo(entry, entries), this));
        }

        OnPropertyChanged(nameof(HasHistory));
        OnPropertyChanged(nameof(HasNoHistory));
    }

    private void ApplyFilter()
    {
        Items.Clear();
        foreach (var item in _all.Where(i => SelectedCategory.Category is null || i.CategoryValue == SelectedCategory.Category))
        {
            Items.Add(item);
        }
    }

    private async Task QuickOptimizeAsync()
    {
        if (_all.Count == 0)
        {
            await LoadAsync();
        }

        var candidates = _all.Where(i => i.IsQuickOptimizeCandidate && i.CanApply).Select(i => i.Optimization).ToList();
        if (candidates.Count == 0)
        {
            await App.GetService<DialogService>().InformAsync("Already optimized", "All low-risk optimizations are already active. Review the remaining items individually.");
            return;
        }

        await _coordinator.ApplyAsync(candidates, forceConfirm: true);
        await LoadAsync();
    }
}
