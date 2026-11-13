using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Nexora.App.Controls;

namespace Nexora.App.Helpers;

/// <summary>
/// Attached behaviour: when the panel loads, its children fade and glide in one after another.
/// Uses implicit compositor transitions, so the animation runs off the UI thread.
/// </summary>
public static class Entrance
{
    public static readonly DependencyProperty StaggerProperty =
        DependencyProperty.RegisterAttached("Stagger", typeof(bool), typeof(Entrance), new PropertyMetadata(false, OnChanged));

    public static bool GetStagger(DependencyObject element) => (bool)element.GetValue(StaggerProperty);

    public static void SetStagger(DependencyObject element, bool value) => element.SetValue(StaggerProperty, value);

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is Panel panel && e.NewValue is true)
        {
            panel.Loaded += (_, _) => Play(panel);
        }
    }

    public static void Play(Panel panel)
    {
        if (!Motion.Enabled)
        {
            return;
        }

        var children = panel.Children.OfType<UIElement>().Where(c => c.Visibility == Visibility.Visible).Take(12).ToList();
        foreach (var child in children)
        {
            child.Opacity = 0;
            child.Translation = new Vector3(0, 24, 0);
        }

        var index = 0;
        var timer = panel.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(55);
        timer.Tick += (t, _) =>
        {
            if (index >= children.Count)
            {
                t.Stop();
                return;
            }

            var child = children[index++];
            child.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(380) };
            child.TranslationTransition = new Vector3Transition { Duration = TimeSpan.FromMilliseconds(420) };
            child.Opacity = 1;
            child.Translation = Vector3.Zero;
        };
        timer.Start();
    }
}
