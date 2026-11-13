using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;

namespace Nexora.App.Controls;

/// <summary>
/// Lightweight time-series chart: smooth polyline, soft gradient fill and three subtle grid lines.
/// Redraws only when the data or size changes (no per-frame work).
/// </summary>
public sealed class LineChart : UserControl
{
    public static readonly DependencyProperty ValuesProperty =
        DependencyProperty.Register(nameof(Values), typeof(object), typeof(LineChart), new PropertyMetadata(null, (d, _) => ((LineChart)d).Redraw()));

    public static readonly DependencyProperty MaximumProperty =
        DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LineChart), new PropertyMetadata(0d, (d, _) => ((LineChart)d).Redraw()));

    public static readonly DependencyProperty StrokeProperty =
        DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(LineChart), new PropertyMetadata(null, (d, _) => ((LineChart)d).Redraw()));

    public static readonly DependencyProperty CapacityProperty =
        DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(LineChart), new PropertyMetadata(60, (d, _) => ((LineChart)d).Redraw()));

    public static readonly DependencyProperty ShowGridProperty =
        DependencyProperty.Register(nameof(ShowGrid), typeof(bool), typeof(LineChart), new PropertyMetadata(true, (d, _) => ((LineChart)d).Redraw()));

    public static readonly DependencyProperty UnitProperty =
        DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart), new PropertyMetadata(string.Empty, (d, _) => ((LineChart)d).Redraw()));

    private readonly Canvas _canvas = new();
    private readonly Polyline _line = new() { StrokeThickness = 2, StrokeLineJoin = PenLineJoin.Round };
    private readonly Polyline _glow = new() { StrokeThickness = 7, StrokeLineJoin = PenLineJoin.Round, Opacity = 0.22 };
    private bool _revealed;
    private readonly Polygon _fill = new();
    private readonly Line[] _grid = [new(), new(), new()];
    private readonly TextBlock _maxLabel = new() { FontSize = 10, Opacity = 0.55 };

    public LineChart()
    {
        IsTabStop = false;
        foreach (var line in _grid)
        {
            line.StrokeThickness = 1;
            line.StrokeDashArray = [3, 4];
            _canvas.Children.Add(line);
        }

        _canvas.Children.Add(_fill);
        _canvas.Children.Add(_glow);
        _canvas.Children.Add(_line);
        _canvas.Children.Add(_maxLabel);
        Content = _canvas;
        MinHeight = 40;
        SizeChanged += (_, _) => Redraw();
        ActualThemeChanged += (_, _) => Redraw();
    }

    /// <summary>IReadOnlyList&lt;double&gt; (or IEnumerable&lt;double&gt;) of the samples, oldest first.</summary>
    public object? Values { get => GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }

    /// <summary>Fixed Y maximum; 0 = automatic.</summary>
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }

    public Brush? Stroke { get => (Brush?)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }

    /// <summary>Number of samples spanning the full width.</summary>
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    public bool ShowGrid { get => (bool)GetValue(ShowGridProperty); set => SetValue(ShowGridProperty, value); }

    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
        {
            return;
        }

        // Non-finite values (NaN) mean "not measured" and are left out: the chart never invents data.
        var data = (Values as IEnumerable<double>)?.ToList() ?? [];
        var measured = data.Where(double.IsFinite).ToList();
        var color = (Stroke as SolidColorBrush)?.Color
                    ?? (Application.Current.Resources.TryGetValue("SystemAccentColor", out var a) && a is Color c ? c : Color.FromArgb(255, 0x1F, 0xB6, 0xFF));
        var gridColor = ActualTheme == ElementTheme.Light ? Color.FromArgb(0x22, 0, 0, 0) : Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF);

        for (var i = 0; i < _grid.Length; i++)
        {
            var y = Math.Round(height * (i + 1) / 4) + 0.5;
            _grid[i].X1 = 0;
            _grid[i].X2 = width;
            _grid[i].Y1 = _grid[i].Y2 = y;
            _grid[i].Stroke = new SolidColorBrush(gridColor);
            _grid[i].Visibility = ShowGrid ? Visibility.Visible : Visibility.Collapsed;
        }

        _line.Points.Clear();
        _glow.Points.Clear();
        _fill.Points.Clear();
        if (measured.Count < 2)
        {
            _maxLabel.Text = string.Empty;
            return;
        }

        var max = Maximum > 0 ? Maximum : NiceMax(measured.Max());
        var capacity = Math.Max(Capacity, data.Count);
        var step = width / (capacity - 1);
        var offset = (capacity - data.Count) * step;
        const double top = 4;
        var usable = height - top - 1;

        var first = data.FindIndex(double.IsFinite);
        var last = data.FindLastIndex(double.IsFinite);
        _fill.Points.Add(new Point(offset + (first * step), height));
        for (var i = 0; i < data.Count; i++)
        {
            if (!double.IsFinite(data[i]))
            {
                continue;
            }

            var v = Math.Clamp(data[i], 0, max);
            var point = new Point(offset + (i * step), top + (usable * (1 - (v / max))));
            _line.Points.Add(point);
            _glow.Points.Add(point);
            _fill.Points.Add(point);
        }

        _fill.Points.Add(new Point(offset + (last * step), height));
        _line.Stroke = new SolidColorBrush(color);
        _glow.Stroke = new SolidColorBrush(color);
        if (!_revealed)
        {
            // First data: fade the chart in.
            _revealed = true;
            if (Motion.Enabled)
            {
                _canvas.Opacity = 0;
                _canvas.OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(600) };
                DispatcherQueue.TryEnqueue(() => _canvas.Opacity = 1);
            }
        }
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x55, color.R, color.G, color.B), Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x00, color.R, color.G, color.B), Offset = 1 });
        _fill.Fill = gradient;

        _maxLabel.Text = ShowGrid && !string.IsNullOrEmpty(Unit) ? $"{max:0} {Unit}" : string.Empty;
        _maxLabel.Foreground = new SolidColorBrush(gridColor.A == 0x22 && ActualTheme == ElementTheme.Light ? Color.FromArgb(0xFF, 0x40, 0x40, 0x40) : Color.FromArgb(0xFF, 0xC0, 0xC8, 0xD0));
        Canvas.SetLeft(_maxLabel, 2);
        Canvas.SetTop(_maxLabel, 0);
    }

    private static double NiceMax(double value)
    {
        if (value <= 0)
        {
            return 1;
        }

        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }
}
