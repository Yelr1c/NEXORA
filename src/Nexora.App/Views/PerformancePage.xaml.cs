using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class PerformancePage : Page
{
    public PerformancePage()
    {
        InitializeComponent();
    }

    public PerformanceViewModel ViewModel { get; } = App.GetService<PerformanceViewModel>();

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

    private void OnTargetsOpened(object sender, object e) => ViewModel.RefreshTargetsCommand.Execute(null);
}
