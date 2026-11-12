using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Nexora.App.Views;

namespace Nexora.App.Services;

public interface INavigationAware
{
    void OnNavigatedTo(object? parameter);

    void OnNavigatedFrom();
}

public sealed class NavigationService
{
    public static readonly IReadOnlyDictionary<string, Type> Pages = new Dictionary<string, Type>(StringComparer.Ordinal)
    {
        ["dashboard"] = typeof(DashboardPage),
        ["performance"] = typeof(PerformancePage),
        ["network"] = typeof(NetworkPage),
        ["optimizer"] = typeof(OptimizerPage),
        ["scan"] = typeof(ScanPage),
        ["games"] = typeof(GamesPage),
        ["processes"] = typeof(ProcessesPage),
        ["settings"] = typeof(SettingsPage),
        ["about"] = typeof(AboutPage),
        ["welcome"] = typeof(WelcomePage),
    };

    private Frame? _frame;

    public event EventHandler<string>? Navigated;

    public string? CurrentKey { get; private set; }

    public bool AnimationsEnabled { get; set; } = true;

    public void Initialize(Frame frame) => _frame = frame;

    public bool Navigate(string key, object? parameter = null)
    {
        if (_frame is null || !Pages.TryGetValue(key, out var type))
        {
            return false;
        }

        if (CurrentKey == key && parameter is null)
        {
            return false;
        }

        NavigationTransitionInfo transition = AnimationsEnabled
            ? new EntranceNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo();
        var ok = _frame.Navigate(type, parameter, transition);
        if (ok)
        {
            CurrentKey = key;
            _frame.BackStack.Clear();
            Navigated?.Invoke(this, key);
        }

        return ok;
    }
}
