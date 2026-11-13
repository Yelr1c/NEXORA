using System.Collections.ObjectModel;
using Microsoft.Extensions.Logging;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Games;
using Nexora.Core.Monitoring;
using Nexora.Core.Optimization;
using Nexora.Core.Optimization.Optimizations;
using Nexora.Core.Processes;
using Nexora.Core.Security;
using Nexora.Platform.Windows.Games;

namespace Nexora.App.ViewModels;

public sealed record OptionItem<T>(T Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class GameProfileViewModel(GameProfile profile) : ObservableObject
{
    public GameProfile Profile => profile;

    public string Name => profile.Name;

    public string SourceText => profile.Source switch
    {
        GameSource.Steam => "Steam",
        GameSource.Epic => "Epic Games",
        GameSource.Gog => "GOG",
        GameSource.Riot => "Riot Games",
        GameSource.Custom => "Custom",
        _ => "Detected",
    };

    public string Initials => string.Concat(profile.Name.Split(' ', StringSplitOptions.RemoveEmptyEntries).Take(2).Select(w => char.ToUpperInvariant(w[0])));

    public string PathText => string.IsNullOrEmpty(profile.ExecutablePath) ? (profile.LaunchUri ?? "No executable selected") : profile.ExecutablePath;

    public string ProfileText => profile.OptimizationProfile switch
    {
        GameOptimizationProfile.None => "No optimizations",
        GameOptimizationProfile.Competitive => "Competitive profile",
        _ => "Balanced profile",
    };

    public void Refresh()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(PathText));
        OnPropertyChanged(nameof(ProfileText));
        OnPropertyChanged(nameof(Initials));
    }
}

public sealed class GamesViewModel : ObservableObject, INavigationAware
{
    private readonly GameProfileStore _store;
    private readonly IGameDetector _detector;
    private readonly GameLauncher _launcher;
    private readonly StandardOptimizationCatalog _catalog;
    private readonly OptimizationCoordinator _coordinator;
    private readonly IFrameRateSource _frames;
    private readonly NotificationService _notifications;
    private readonly UiDispatcher _dispatcher;
    private readonly ILogger<GamesViewModel> _logger;
    private GameProfileViewModel? _selected;
    private bool _isBusy;
    private bool _loaded;
    private string _statusText = string.Empty;
    private string _editName = string.Empty;
    private string _editPath = string.Empty;
    private string _editArguments = string.Empty;
    private string _editProcessName = string.Empty;
    private OptionItem<GameOptimizationProfile>? _editProfile;
    private OptionItem<ProcessPriorityLevel>? _editPriority;
    private bool _editMonitor = true;
    private bool _editRevert = true;
    private string _validationMessage = string.Empty;

    public GamesViewModel(GameProfileStore store, IGameDetector detector, GameLauncher launcher, StandardOptimizationCatalog catalog, OptimizationCoordinator coordinator,
        IFrameRateSource frames, NotificationService notifications, UiDispatcher dispatcher, ILogger<GamesViewModel> logger)
    {
        _store = store;
        _detector = detector;
        _launcher = launcher;
        _catalog = catalog;
        _coordinator = coordinator;
        _frames = frames;
        _notifications = notifications;
        _dispatcher = dispatcher;
        _logger = logger;
        ProfileOptions =
        [
            new(GameOptimizationProfile.None, "None — launch without changes"),
            new(GameOptimizationProfile.Balanced, "Balanced — Game Mode, windowed optimizations, no background recording"),
            new(GameOptimizationProfile.Competitive, "Competitive — Balanced + high performance power mode"),
        ];
        PriorityOptions =
        [
            new(ProcessPriorityLevel.Normal, "Normal (recommended)"),
            new(ProcessPriorityLevel.AboveNormal, "Above normal"),
            new(ProcessPriorityLevel.High, "High"),
        ];
        DetectCommand = new AsyncRelayCommand(DetectAsync, () => !IsBusy);
        AddCustomCommand = new RelayCommand(AddCustom);
        BrowseCommand = new AsyncRelayCommand(BrowseAsync);
        SaveCommand = new AsyncRelayCommand(SaveAsync, () => Selected is not null);
        DeleteCommand = new AsyncRelayCommand(DeleteAsync, () => Selected is not null);
        LaunchCommand = new AsyncRelayCommand(LaunchAsync, () => Selected is not null && !IsBusy);
    }

    public ObservableCollection<GameProfileViewModel> Profiles { get; } = [];

    public IReadOnlyList<OptionItem<GameOptimizationProfile>> ProfileOptions { get; }

    public IReadOnlyList<OptionItem<ProcessPriorityLevel>> PriorityOptions { get; }

    public AsyncRelayCommand DetectCommand { get; }

    public RelayCommand AddCustomCommand { get; }

    public AsyncRelayCommand BrowseCommand { get; }

    public AsyncRelayCommand SaveCommand { get; }

    public AsyncRelayCommand DeleteCommand { get; }

    public AsyncRelayCommand LaunchCommand { get; }

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasNoProfiles => Profiles.Count == 0;

    public bool HasSelection => Selected is not null;

    public bool HasNoSelection => Selected is null;

    public GameProfileViewModel? Selected
    {
        get => _selected;
        set
        {
            if (SetProperty(ref _selected, value))
            {
                LoadEditor();
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
                SaveCommand.NotifyCanExecuteChanged();
                DeleteCommand.NotifyCanExecuteChanged();
                LaunchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetProperty(ref _isBusy, value))
            {
                DetectCommand.NotifyCanExecuteChanged();
                LaunchCommand.NotifyCanExecuteChanged();
            }
        }
    }

    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    public string EditName { get => _editName; set => SetProperty(ref _editName, value); }

    public string EditPath { get => _editPath; set => SetProperty(ref _editPath, value); }

    public string EditArguments { get => _editArguments; set => SetProperty(ref _editArguments, value); }

    public string EditProcessName { get => _editProcessName; set => SetProperty(ref _editProcessName, value); }

    public OptionItem<GameOptimizationProfile>? EditProfile { get => _editProfile; set => SetProperty(ref _editProfile, value); }

    public OptionItem<ProcessPriorityLevel>? EditPriority { get => _editPriority; set => SetProperty(ref _editPriority, value); }

    public bool EditMonitor { get => _editMonitor; set => SetProperty(ref _editMonitor, value); }

    public bool EditRevert { get => _editRevert; set => SetProperty(ref _editRevert, value); }

    public string ValidationMessage { get => _validationMessage; private set => SetProperty(ref _validationMessage, value); }

    public string? EditLaunchUri => Selected?.Profile.LaunchUri;

    public void OnNavigatedTo(object? parameter)
    {
        if (!_loaded)
        {
            _loaded = true;
            InitialLoadAsync().Forget("load games");
        }
    }

    public void OnNavigatedFrom()
    {
    }

    private async Task InitialLoadAsync()
    {
        var profiles = await _store.LoadAsync();
        if (profiles.Count == 0)
        {
            await DetectAsync();
            return;
        }

        SetProfiles(profiles);
    }

    private void SetProfiles(IEnumerable<GameProfile> profiles)
    {
        var selectedId = Selected?.Profile.Id;
        Profiles.Clear();
        foreach (var profile in profiles)
        {
            Profiles.Add(new GameProfileViewModel(profile));
        }

        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private async Task DetectAsync()
    {
        IsBusy = true;
        StatusText = "Searching Steam, Epic Games, GOG, Riot and Minecraft libraries…";
        try
        {
            var detected = await Task.Run(_detector.Detect);
            var merged = GameProfileStore.Merge(Profiles.Select(p => p.Profile).ToList(), detected);
            await _store.SaveAsync(merged);
            SetProfiles(merged);
            StatusText = detected.Count == 0 ? "No supported launchers found. Add games manually with \"Add game\"." : $"Found {detected.Count} installed game(s).";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game detection failed");
            StatusText = "Game detection failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void AddCustom()
    {
        var profile = new GameProfile { Name = "New game", Source = GameSource.Custom };
        var vm = new GameProfileViewModel(profile);
        Profiles.Add(vm);
        Selected = vm;
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
        BrowseAsync().Forget("browse for game");
    }

    private async Task BrowseAsync()
    {
        var path = await App.GetService<MainWindow>().PickExecutableAsync();
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        EditPath = path;
        if (string.IsNullOrWhiteSpace(EditName) || EditName == "New game")
        {
            EditName = Path.GetFileNameWithoutExtension(path);
        }
    }

    private void LoadEditor()
    {
        ValidationMessage = string.Empty;
        if (Selected is not { } vm)
        {
            return;
        }

        var p = vm.Profile;
        EditName = p.Name;
        EditPath = p.ExecutablePath;
        EditArguments = p.LaunchArguments;
        EditProcessName = p.ProcessName ?? string.Empty;
        EditProfile = ProfileOptions.FirstOrDefault(o => o.Value == p.OptimizationProfile) ?? ProfileOptions[1];
        EditPriority = PriorityOptions.FirstOrDefault(o => o.Value == p.Priority) ?? PriorityOptions[0];
        EditMonitor = p.MonitorPerformance;
        EditRevert = p.RevertOptimizationsOnExit;
        OnPropertyChanged(nameof(EditLaunchUri));
    }

    private bool ValidateEditor()
    {
        if (string.IsNullOrWhiteSpace(EditName))
        {
            ValidationMessage = "Enter a name for the game.";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(EditPath))
        {
            var (ok, error) = LaunchValidator.ValidateExecutable(EditPath.Trim());
            if (!ok)
            {
                ValidationMessage = error!;
                return false;
            }
        }
        else if (!LaunchValidator.IsAllowedLaunchUri(Selected?.Profile.LaunchUri))
        {
            ValidationMessage = "Select the game's executable.";
            return false;
        }

        var (argsOk, argsError) = LaunchValidator.ValidateArguments(EditArguments);
        if (!argsOk)
        {
            ValidationMessage = argsError!;
            return false;
        }

        ValidationMessage = string.Empty;
        return true;
    }

    private async Task SaveAsync()
    {
        if (Selected is not { } vm || !ValidateEditor())
        {
            return;
        }

        var p = vm.Profile;
        p.Name = EditName.Trim();
        p.ExecutablePath = EditPath.Trim();
        p.LaunchArguments = EditArguments.Trim();
        p.ProcessName = string.IsNullOrWhiteSpace(EditProcessName) ? null : Path.GetFileNameWithoutExtension(EditProcessName.Trim());
        p.OptimizationProfile = EditProfile?.Value ?? GameOptimizationProfile.Balanced;
        p.Priority = EditPriority?.Value ?? ProcessPriorityLevel.Normal;
        p.MonitorPerformance = EditMonitor;
        p.RevertOptimizationsOnExit = EditRevert;
        if (!string.IsNullOrEmpty(p.ExecutablePath) && p.Source is GameSource.Steam or GameSource.Epic && !string.IsNullOrEmpty(EditArguments))
        {
            // Arguments only apply when launching the executable directly.
            p.LaunchUri = null;
        }

        vm.Refresh();
        await _store.SaveAsync(Profiles.Select(x => x.Profile).ToList());
        App.GetService<GameWatcherService>().InvalidateProfiles();
        StatusText = $"Saved {p.Name}.";
    }

    private async Task DeleteAsync()
    {
        if (Selected is not { } vm)
        {
            return;
        }

        if (!await App.GetService<DialogService>().ConfirmAsync("Remove game profile?", $"\"{vm.Name}\" will be removed from NEXORA. The game itself is not affected.", "Remove"))
        {
            return;
        }

        Profiles.Remove(vm);
        Selected = Profiles.FirstOrDefault();
        await _store.SaveAsync(Profiles.Select(x => x.Profile).ToList());
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(HasNoProfiles));
    }

    private async Task LaunchAsync()
    {
        if (Selected is not { } vm)
        {
            return;
        }

        await SaveAsync();
        if (!string.IsNullOrEmpty(ValidationMessage))
        {
            return;
        }

        var profile = vm.Profile;
        IsBusy = true;
        var appliedIds = new List<string>();
        try
        {
            var optimizations = ProfileOptimizations(profile.OptimizationProfile);
            if (optimizations.Count > 0)
            {
                var pending = new List<IOptimization>();
                foreach (var o in optimizations)
                {
                    if ((await o.GetStateAsync()).Status == OptimizationStatus.NotApplied)
                    {
                        pending.Add(o);
                    }
                }

                if (pending.Count > 0)
                {
                    var result = await _coordinator.ApplyAsync(pending);
                    appliedIds.AddRange(result?.Results.Where(r => r.Outcome == OptimizationOutcome.Applied).Select(r => r.OptimizationId) ?? []);
                }
            }

            StatusText = $"Launching {profile.Name}…";
            var launch = await _launcher.LaunchAsync(profile);
            StatusText = launch.Message;
            if (!launch.Success)
            {
                await RevertAsync(appliedIds, profile);
                return;
            }

            profile.LastPlayed = DateTimeOffset.Now;
            await _store.SaveAsync(Profiles.Select(x => x.Profile).ToList());
            if (launch.GameProcess is { } process)
            {
                WatchSessionAsync(profile, process, appliedIds).Forget("track game session");
            }
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task WatchSessionAsync(GameProfile profile, System.Diagnostics.Process process, List<string> appliedIds)
    {
        try
        {
            if (profile.MonitorPerformance)
            {
                _frames.TargetProcessId = process.Id;
                _frames.TryStart();
            }

            await process.WaitForExitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Game session tracking failed");
        }
        finally
        {
            process.Dispose();
            if (profile.MonitorPerformance)
            {
                _frames.TargetProcessId = null;
            }

            _dispatcher.Post(() => RevertAsync(appliedIds, profile).Forget("revert game profile"));
        }
    }

    private async Task RevertAsync(List<string> appliedIds, GameProfile profile)
    {
        if (!profile.RevertOptimizationsOnExit || appliedIds.Count == 0)
        {
            return;
        }

        foreach (var id in Enumerable.Reverse(appliedIds))
        {
            await _coordinator.UndoLatestAsync(id);
        }

        StatusText = $"{profile.Name} closed. Profile optimizations were reverted.";
        _notifications.Show("Game session ended", $"{profile.Name} closed. NEXORA restored your previous settings.");
    }

    private List<IOptimization> ProfileOptimizations(GameOptimizationProfile profile)
    {
        var ids = profile switch
        {
            GameOptimizationProfile.Balanced => new[] { StandardOptimizationCatalog.GameModeId, WindowedGamesOptimization.OptimizationId, StandardOptimizationCatalog.GameDvrId },
            GameOptimizationProfile.Competitive => [StandardOptimizationCatalog.GameModeId, WindowedGamesOptimization.OptimizationId, StandardOptimizationCatalog.GameDvrId, PowerPlanOptimization.OptimizationId],
            _ => [],
        };
        return ids.Select(_catalog.Find).Where(o => o is not null).Select(o => o!).ToList();
    }
}
