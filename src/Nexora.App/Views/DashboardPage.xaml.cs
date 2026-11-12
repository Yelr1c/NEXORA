using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class DashboardPage : Page
{
    public DashboardPage()
    {
        InitializeComponent();
    }

    public DashboardViewModel ViewModel { get; } = App.GetService<DashboardViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo(e.Parameter);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void OnFpsTapped(object sender, TappedRoutedEventArgs e) => ViewModel.OpenPerformanceCommand.Execute(null);

    private void OnNetworkTapped(object sender, TappedRoutedEventArgs e) => ViewModel.OpenNetworkCommand.Execute(null);

    private void OnHealthTapped(object sender, TappedRoutedEventArgs e) => ViewModel.ScanCommand.Execute(null);
}
