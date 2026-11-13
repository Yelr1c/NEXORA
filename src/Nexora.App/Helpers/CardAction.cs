using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Nexora.App.Controls;
using Nexora.App.Services;

namespace Nexora.App.Helpers;

/// <summary>
/// Makes a card interactive: hover lift + glow border, press-down scale, and on click a smooth navigation to
/// the related details page (<c>helpers:CardAction.NavigateTo="network"</c>).
/// </summary>
public static class CardAction
{
    public static readonly DependencyProperty NavigateToProperty =
        DependencyProperty.RegisterAttached("NavigateTo", typeof(string), typeof(CardAction), new PropertyMetadata(null, OnChanged));

    public static string? GetNavigateTo(DependencyObject element) => (string?)element.GetValue(NavigateToProperty);

    public static void SetNavigateTo(DependencyObject element, string? value) => element.SetValue(NavigateToProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.OldValue is not null || e.NewValue is not string)
        {
            return;
        }

        Hover.SetLift(element, true);
        element.ScaleTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(140) };
        element.SizeChanged += (_, args) => element.CenterPoint = new Vector3((float)(args.NewSize.Width / 2), (float)(args.NewSize.Height / 2), 0);
        element.PointerPressed += (_, _) =>
        {
            if (Motion.Enabled)
            {
                element.Scale = new Vector3(0.975f, 0.975f, 1);
            }
        };
        element.PointerReleased += (_, _) => element.Scale = Vector3.One;
        element.PointerCaptureLost += (_, _) => element.Scale = Vector3.One;
        element.PointerExited += (_, _) => element.Scale = Vector3.One;
        element.Tapped += OnTapped;
        element.IsTabStop = true;
        element.KeyDown += (_, args) =>
        {
            if (args.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space)
            {
                Navigate(element);
            }
        };
    }

    private static void OnTapped(object sender, TappedRoutedEventArgs e)
    {
        // Clicks on buttons inside the card are handled by the buttons themselves.
        if (e.OriginalSource is DependencyObject source && IsInsideButton(source, (DependencyObject)sender))
        {
            return;
        }

        Navigate((FrameworkElement)sender);
    }

    private static bool IsInsideButton(DependencyObject source, DependencyObject card)
    {
        for (var current = source; current is not null && current != card; current = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetParent(current))
        {
            if (current is Microsoft.UI.Xaml.Controls.Primitives.ButtonBase or Microsoft.UI.Xaml.Controls.ComboBox or Microsoft.UI.Xaml.Controls.ToggleSwitch)
            {
                return true;
            }
        }

        return false;
    }

    private static void Navigate(FrameworkElement element)
    {
        if (GetNavigateTo(element) is { Length: > 0 } key)
        {
            App.GetService<MainWindow>().NavigateTo(key);
        }
    }
}
