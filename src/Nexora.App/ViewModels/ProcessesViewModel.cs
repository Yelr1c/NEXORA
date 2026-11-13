using System.Collections.ObjectModel;
using Microsoft.UI.Dispatching;
using Nexora.App.Helpers;
using Nexora.App.Mvvm;
using Nexora.App.Services;
using Nexora.Core.Processes;

namespace Nexora.App.ViewModels;

public sealed class ProcessItemViewModel(ProcessSnapshot snapshot, ProcessesViewModel owner)
{
    public ProcessSnapshot Snapshot => snapshot;

    public int Id => snapshot.Id;

    public string Name => snapshot.Name;

    public string PidText => snapshot.Id.ToString(System.Globalization.CultureInfo.CurrentCulture);

    public string CpuText => snapshot.CpuPercent is { } c ? c.ToString(c < 10 ? "0.0" : "0", System.Globalization.CultureInfo.CurrentCulture) + "%" : Format.Dash;

    public string MemoryText => Format.Bytes(snapshot.MemoryBytes);

    public string GpuText => snapshot.GpuPercent is { } g && g >= 0.1 ? g.ToString("0.0", System.Globalization.CultureInfo.CurrentCulture) + "%" : Format.Dash;

    public string PriorityText => snapshot.Priority switch
    {
        ProcessPriorityLevel.Unknown => Format.Dash,
        ProcessPriorityLevel.BelowNormal => "Below normal",
        ProcessPriorityLevel.AboveNormal => "Above normal",
        ProcessPriorityLevel.RealTime => "Realtime",
        _ => snapshot.Priority.ToString(),
    };

    public string Status => snapshot.Status;

    public bool IsProtected => snapshot.IsProtected;

    public bool CanEnd => !snapshot.IsProtected;

    public string ProtectionText => snapshot.ProtectionReason ?? string.Empty;

    public double CpuSort => snapshot.CpuPercent ?? -1;

    public AsyncRelayCommand EndCommand { get; } = new(() => owner.EndProcessAsync(snapshot));

    public RelayCommand AboveNormalCommand { get; } = new(() => owner.SetPriority(snapshot, ProcessPriorityLevel.AboveNormal));

    public RelayCommand NormalCommand { get; } = new(() => owner.SetPriority(snapshot, ProcessPriorityLevel.Normal));

    public RelayCommand BelowNormalCommand { get; } = new(() => owner.SetPriority(snapshot, ProcessPriorityLevel.BelowNormal));
}

public sealed class ProcessesViewModel : ObservableObject, INavigationAware
{
    private readonly IProcessService _processes;
    private readonly UiDispatcher _dispatcher;
    private readonly DialogService _dialogs;
    private DispatcherQueueTimer? _timer;
    private IReadOnlyList<ProcessSnapshot> _latest = [];
    private string _search = string.Empty;
    private OptionItem<string> _sort;
    private string _summary = string.Empty;
    private string _message = string.Empty;
    private bool _hideProtected;
    private bool _refreshing;

    public ProcessesViewModel(IProcessService processes, UiDispatcher dispatcher, DialogService dialogs)
    {
        _processes = processes;
        _dispatcher = dispatcher;
        _dialogs = dialogs;
        SortOptions = [new("cpu", "CPU"), new("memory", "Memory"), new("gpu", "GPU"), new("name", "Name")];
        _sort = SortOptions[0];
        RefreshCommand = new RelayCommand(Refresh);
    }

    public ObservableCollection<ProcessItemViewModel> Items { get; } = [];

    public IReadOnlyList<OptionItem<string>> SortOptions { get; }

    public RelayCommand RefreshCommand { get; }

    public string Summary { get => _summary; private set => SetProperty(ref _summary, value); }

    public string Message
    {
        get => _message;
        private set
        {
            if (SetProperty(ref _message, value))
            {
                OnPropertyChanged(nameof(HasMessage));
            }
        }
    }

    public bool HasMessage => Message.Length > 0;

    public string Search
    {
        get => _search;
        set
        {
            if (SetProperty(ref _search, value ?? string.Empty))
            {
                Rebuild();
            }
        }
    }

    public OptionItem<string> Sort
    {
        get => _sort;
        set
        {
            if (value is not null && SetProperty(ref _sort, value))
            {
                Rebuild();
            }
        }
    }

    public bool HideProtected
    {
        get => _hideProtected;
        set
        {
            if (SetProperty(ref _hideProtected, value))
            {
                Rebuild();
            }
        }
    }

    public void OnNavigatedTo(object? parameter)
    {
        Refresh();
        _timer ??= _dispatcher.CreateTimer(TimeSpan.FromSeconds(2), Refresh);
        _timer.Start();
    }

    public void OnNavigatedFrom() => _timer?.Stop();

    internal async Task EndProcessAsync(ProcessSnapshot snapshot)
    {
        if (snapshot.IsProtected)
        {
            await _dialogs.InformAsync("Protected process", $"{snapshot.Name} is protected ({snapshot.ProtectionReason}). Ending it could make Windows unstable, so NEXORA will not do it.");
            return;
        }

        var confirmed = await _dialogs.ConfirmAsync(
            $"End {snapshot.Name}?",
            $"{snapshot.Name} (PID {snapshot.Id}) will be closed immediately. Any unsaved work in this application will be lost. This cannot be undone.",
            "End process",
            destructive: true);
        if (!confirmed)
        {
            return;
        }

        var result = await _processes.TerminateAsync(snapshot.Id);
        Message = result.Message;
        Refresh();
    }

    internal void SetPriority(ProcessSnapshot snapshot, ProcessPriorityLevel priority)
    {
        Message = _processes.SetPriority(snapshot.Id, priority).Message;
        Refresh();
    }

    private void Refresh()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        Task.Run(() =>
        {
            try
            {
                var list = _processes.GetProcesses();
                _dispatcher.Post(() =>
                {
                    _latest = list;
                    Rebuild();
                });
            }
            finally
            {
                _refreshing = false;
            }
        }).Forget("refresh processes");
    }

    private void Rebuild()
    {
        IEnumerable<ProcessSnapshot> query = _latest;
        if (!string.IsNullOrWhiteSpace(Search))
        {
            query = query.Where(p => p.Name.Contains(Search.Trim(), StringComparison.OrdinalIgnoreCase) || p.Id.ToString(System.Globalization.CultureInfo.InvariantCulture) == Search.Trim());
        }

        if (HideProtected)
        {
            query = query.Where(p => !p.IsProtected);
        }

        query = Sort.Value switch
        {
            "memory" => query.OrderByDescending(p => p.MemoryBytes),
            "gpu" => query.OrderByDescending(p => p.GpuPercent ?? -1),
            "name" => query.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase),
            _ => query.OrderByDescending(p => p.CpuPercent ?? -1).ThenByDescending(p => p.MemoryBytes),
        };

        var list = query.Take(400).ToList();
        // Update in place to keep scroll position and avoid flicker.
        for (var i = 0; i < list.Count; i++)
        {
            var vm = new ProcessItemViewModel(list[i], this);
            if (i < Items.Count)
            {
                if (Items[i].Id != vm.Id || Items[i].CpuText != vm.CpuText || Items[i].MemoryText != vm.MemoryText || Items[i].GpuText != vm.GpuText || Items[i].Status != vm.Status)
                {
                    Items[i] = vm;
                }
            }
            else
            {
                Items.Add(vm);
            }
        }

        while (Items.Count > list.Count)
        {
            Items.RemoveAt(Items.Count - 1);
        }

        var totalMemory = _latest.Sum(p => p.MemoryBytes);
        Summary = $"{_latest.Count} processes · {Format.Bytes(totalMemory)} working set · {_latest.Count(p => p.IsProtected)} protected";
    }
}
