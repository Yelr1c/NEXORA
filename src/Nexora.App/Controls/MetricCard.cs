using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Nexora.App.Helpers;

namespace Nexora.App.Controls;

/// <summary>Dashboard metric tile: icon + label, large (optionally animated) value, detail line and sparkline.</summary>
public sealed class MetricCard : UserControl
{
    public static readonly DependencyProperty TitleProperty = Register(nameof(Title), typeof(string), string.Empty);
    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), typeof(string), "");
    public static readonly DependencyProperty ValueTextProperty = Register(nameof(ValueText), typeof(string), "—");
    public static readonly DependencyProperty NumericValueProperty = Register(nameof(NumericValue), typeof(double), 0d);
    public static readonly DependencyProperty UseNumericProperty = Register(nameof(UseNumeric), typeof(bool), false);
    public static readonly DependencyProperty IsAvailableProperty = Register(nameof(IsAvailable), typeof(bool), true);
    public static readonly DependencyProperty SuffixProperty = Register(nameof(Suffix), typeof(string), string.Empty);
    public static readonly DependencyProperty UnitProperty = Register(nameof(Unit), typeof(string), string.Empty);
    public static readonly DependencyProperty DetailProperty = Register(nameof(Detail), typeof(string), string.Empty);
    public static readonly DependencyProperty SeriesProperty = Register(nameof(Series), typeof(object), null);
    public static readonly DependencyProperty SeriesMaximumProperty = Register(nameof(SeriesMaximum), typeof(double), 0d);
    public static readonly DependencyProperty AccentProperty = Register(nameof(Accent), typeof(Brush), null);

    private readonly FontIcon _icon = new() { FontSize = 14 };
    private readonly TextBlock _title = new();
    private readonly TextBlock _value = new() { FontSize = 30, FontWeight = FontWeights.SemiBold, TextLineBounds = TextLineBounds.Tight };
    private readonly AnimatedNumber _number = new() { FontSize = 30, FontWeight = FontWeights.SemiBold };
    private readonly TextBlock _unit = new() { FontSize = 14, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(4, 0, 0, 2) };
    private readonly TextBlock _detail = new() { FontSize = 12, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly LineChart _chart = new() { Height = 44, ShowGrid = false, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Border _card;

    public MetricCard()
    {
        IsTabStop = false;
        _title.Style = Resource<Style>("CardLabelStyle");
        _detail.Opacity = 0.65;
        _icon.FontFamily = Resource<FontFamily>("NexoraIconFont");

        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        header.Children.Add(_icon);
        header.Children.Add(_title);

        var valueRow = new StackPanel { Orientation = Orientation.Horizontal };
        valueRow.Children.Add(_value);
        valueRow.Children.Add(_number);
        valueRow.Children.Add(_unit);

        var stack = new StackPanel { Spacing = 10 };
        stack.Children.Add(header);
        stack.Children.Add(valueRow);
        stack.Children.Add(_detail);
        stack.Children.Add(_chart);

        _card = new Border { Child = stack, Style = Resource<Style>("CardStyle") };
        Hover.SetLift(_card, true);
        Content = _card;
        Sync();
    }

    public string Title { get => (string)GetValue(TitleProperty); set => SetValue(TitleProperty, value); }

    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }

    public string ValueText { get => (string)GetValue(ValueTextProperty); set => SetValue(ValueTextProperty, value); }

    public double NumericValue { get => (double)GetValue(NumericValueProperty); set => SetValue(NumericValueProperty, value); }

    public bool UseNumeric { get => (bool)GetValue(UseNumericProperty); set => SetValue(UseNumericProperty, value); }

    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    public string Detail { get => (string)GetValue(DetailProperty); set => SetValue(DetailProperty, value); }

    public object? Series { get => GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }

    public double SeriesMaximum { get => (double)GetValue(SeriesMaximumProperty); set => SetValue(SeriesMaximumProperty, value); }

    public Brush? Accent { get => (Brush?)GetValue(AccentProperty); set => SetValue(AccentProperty, value); }

    private static DependencyProperty Register(string name, Type type, object? defaultValue)
        => DependencyProperty.Register(name, type, typeof(MetricCard), new PropertyMetadata(defaultValue, (d, _) => ((MetricCard)d).Sync()));

    private static T? Resource<T>(string key)
        where T : class
        => Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;

    private void Sync()
    {
        _icon.Glyph = Glyph;
        _icon.Foreground = Accent ?? Resource<Brush>("NexoraAccentBrush");
        _title.Text = Title.ToUpperInvariant();
        _value.Text = ValueText;
        _value.Visibility = UseNumeric ? Visibility.Collapsed : Visibility.Visible;
        _number.Visibility = UseNumeric ? Visibility.Visible : Visibility.Collapsed;
        _number.Value = NumericValue;
        _number.Suffix = Suffix;
        _number.IsAvailable = IsAvailable;
        _unit.Text = Unit;
        _unit.Visibility = string.IsNullOrEmpty(Unit) ? Visibility.Collapsed : Visibility.Visible;
        _detail.Text = Detail;
        _chart.Values = Series;
        _chart.Maximum = SeriesMaximum;
        _chart.Stroke = Accent;
        _chart.Visibility = Series is null ? Visibility.Collapsed : Visibility.Visible;
    }
}
