using Microsoft.UI.Xaml.Controls;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
    }

    public AboutViewModel ViewModel { get; } = App.GetService<AboutViewModel>();
}
