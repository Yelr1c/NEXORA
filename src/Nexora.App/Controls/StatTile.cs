using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nexora.App.Controls;

/// <summary>Compact label/value tile used inside cards.</summary>
public sealed class StatTile : UserControl
{
    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(StatTile), new PropertyMetadata(string.Empty, (d, e) => ((StatTile)d)._label.Text = ((string)e.NewValue ?? string.Empty).ToUpperInvariant()));

    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(string), typeof(StatTile), new PropertyMetadata("—", (d, e) => ((StatTile)d).SetValueText((string)e.NewValue)));

    public static readonly DependencyProperty LargeProperty =
        DependencyProperty.Register(nameof(Large), typeof(bool), typeof(StatTile), new PropertyMetadata(false, (d, _) => ((StatTile)d).ApplySize()));

    private readonly TextBlock _label = new();
    private readonly TextBlock _value = new() { TextTrimming = TextTrimming.CharacterEllipsis, TextWrapping = TextWrapping.NoWrap };

    public StatTile()
    {
        IsTabStop = false;
        _label.Style = Resource<Style>("CardLabelStyle");
        _value.Style = Resource<Style>("SmallMetricValueStyle");
        var stack = new StackPanel { Spacing = 6 };
        stack.Children.Add(_label);
        stack.Children.Add(_value);
        Content = new Border { Child = stack, Style = Resource<Style>("InsetStyle") };
    }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Value { get => (string)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public bool Large { get => (bool)GetValue(LargeProperty); set => SetValue(LargeProperty, value); }

    private void SetValueText(string? text)
    {
        _value.Text = text ?? "—";
        var unavailable = text == Helpers.Format.Unavailable;
        _value.FontSize = unavailable ? 12 : Large ? 28 : 18;
        _value.Opacity = unavailable ? 0.6 : 1;
        ToolTipService.SetToolTip(this, unavailable ? "This sensor is not exposed by your hardware or driver." : null);
    }

    private void ApplySize() => SetValueText(_value.Text);

    private static T? Resource<T>(string key)
        where T : class
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;
}
