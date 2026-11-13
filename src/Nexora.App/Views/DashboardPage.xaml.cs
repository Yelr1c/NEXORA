using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using Nexora.App.Controls;
using Nexora.App.ViewModels;

namespace Nexora.App.Views;

public sealed partial class DashboardPage : Page
{
    private Storyboard? _pulse;

    public DashboardPage()
    {
        InitializeComponent();
        Loaded += (_, _) => UpdatePulse();
        Unloaded += (_, _) => _pulse?.Stop();
        ViewModel.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(DashboardViewModel.IsBoostActive))
            {
                UpdatePulse();
            }
        };
    }

    /// <summary>A glowing ring radiates from the Boost icon while Game Boost is active.</summary>
    private void UpdatePulse()
    {
        if (!ViewModel.IsBoostActive || !Motion.Enabled)
        {
            _pulse?.Stop();
            BoostPulse.Opacity = 0;
            return;
        }

        if (_pulse is null)
        {
            _pulse = new Storyboard { RepeatBehavior = RepeatBehavior.Forever };
            Add("Opacity", 0.9, 0);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleX)", 0.9, 1.5);
            Add("(UIElement.RenderTransform).(CompositeTransform.ScaleY)", 0.9, 1.5);
        }

        _pulse.Begin();

        void Add(string path, double from, double to)
        {
            var animation = new DoubleAnimation
            {
                From = from,
                To = to,
                Duration = new Duration(TimeSpan.FromMilliseconds(1600)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            };
            Storyboard.SetTarget(animation, BoostPulse);
            Storyboard.SetTargetProperty(animation, path);
            _pulse!.Children.Add(animation);
        }
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

}
