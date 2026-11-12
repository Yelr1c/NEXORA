using System.Numerics;
using Microsoft.UI.Xaml.Controls;
using Nexora.App.Controls;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class WelcomePage : Page
{
    public WelcomePage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!Motion.Enabled)
            {
                return;
            }

            // Gentle entrance: fade and rise.
            Hero.Opacity = 0;
            Hero.Translation = new Vector3(0, 16, 0);
            Hero.OpacityTransition = new Microsoft.UI.Xaml.ScalarTransition { Duration = TimeSpan.FromMilliseconds(600) };
            Hero.TranslationTransition = new Microsoft.UI.Xaml.Vector3Transition { Duration = TimeSpan.FromMilliseconds(600) };
            DispatcherQueue.TryEnqueue(() =>
            {
                Hero.Opacity = 1;
                Hero.Translation = Vector3.Zero;
            });
        };
    }

    public WelcomeViewModel ViewModel { get; } = App.GetService<WelcomeViewModel>();
}
