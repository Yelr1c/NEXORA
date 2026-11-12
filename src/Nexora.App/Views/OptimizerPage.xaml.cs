using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class OptimizerPage : Page
{
    public OptimizerPage()
    {
        InitializeComponent();
    }

    public OptimizerViewModel ViewModel { get; } = App.GetService<OptimizerViewModel>();

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        ViewModel.OnNavigatedTo(e.Parameter);
        var index = ViewModel.Section;
        for (var i = 0; i < Sections.Items.Count; i++)
        {
            Sections.Items[i].IsSelected = i == index;
        }
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        ViewModel.OnNavigatedFrom();
    }

    private void OnSectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (sender.SelectedItem?.Tag is string tag && int.TryParse(tag, out var index))
        {
            ViewModel.Section = index;
        }
    }
}
