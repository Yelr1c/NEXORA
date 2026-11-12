using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexora.App.Controls;

namespace Nexora.App.Helpers;

/// <summary>Attached behaviour: subtle lift and accent border on hover, using GPU-driven implicit transitions.</summary>
public static class Hover
{
    public static readonly DependencyProperty LiftProperty =
        DependencyProperty.RegisterAttached("Lift", typeof(bool), typeof(Hover), new PropertyMetadata(false, OnLiftChanged));

    public static bool GetLift(DependencyObject element) => (bool)element.GetValue(LiftProperty);

    public static void SetLift(DependencyObject element, bool value) => element.SetValue(LiftProperty, value);

    private static void OnLiftChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element || e.NewValue is not true)
        {
            return;
        }

        element.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(160) };
        Brush? original = null;
        element.PointerEntered += (_, _) =>
        {
            if (Motion.Enabled)
            {
                element.Translation = new Vector3(0, -2, 0);
            }

            if (element is Border border && Application.Current.Resources.TryGetValue("NexoraAccentSoftBrush", out var hover) && hover is Brush brush)
            {
                original ??= border.BorderBrush;
                border.BorderBrush = brush;
            }
        };
        element.PointerExited += (_, _) =>
        {
            element.Translation = Vector3.Zero;
            if (element is Border border && original is not null)
            {
                border.BorderBrush = original;
            }
        };
    }
}
