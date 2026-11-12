using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;

namespace Nexora.App.Controls;

/// <summary>Circular 0–100 score gauge with an animated gradient arc.</summary>
public sealed class ScoreRing : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(ScoreRing), new PropertyMetadata(0d, (d, _) => ((ScoreRing)d).AnimateTo()));

    public static readonly DependencyProperty LabelProperty =
        DependencyProperty.Register(nameof(Label), typeof(string), typeof(ScoreRing), new PropertyMetadata(string.Empty, (d, e) => ((ScoreRing)d)._label.Text = (string)e.NewValue ?? string.Empty));

    public static readonly DependencyProperty CaptionProperty =
        DependencyProperty.Register(nameof(Caption), typeof(string), typeof(ScoreRing), new PropertyMetadata("NEXORA PERFORMANCE SCORE", (d, e) => ((ScoreRing)d)._caption.Text = (string)e.NewValue ?? string.Empty));

    public static readonly DependencyProperty ThicknessProperty =
        DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(ScoreRing), new PropertyMetadata(14d, (d, _) => ((ScoreRing)d).Layout()));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(900);
    private readonly Grid _root = new();
    private readonly Ellipse _track = new();
    private readonly Ellipse _glow = new() { Opacity = 0.18 };
    private readonly Path _arc = new() { StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round };
    private readonly TextBlock _number = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, TextLineBounds = TextLineBounds.Tight };
    private readonly TextBlock _outOf = new() { Text = "/ 100", Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 14 };
    private readonly TextBlock _label = new() { FontWeight = FontWeights.SemiBold, HorizontalAlignment = HorizontalAlignment.Center, FontSize = 16 };
    private readonly TextBlock _caption = new() { Text = "NEXORA PERFORMANCE SCORE", FontSize = 10, CharacterSpacing = 120, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly Stopwatch _clock = new();
    private double _from;
    private double _displayed;
    private bool _subscribed;

    public ScoreRing()
    {
        IsTabStop = false;
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Spacing = 6 };
        text.Children.Add(_caption);
        text.Children.Add(_number);
        text.Children.Add(_outOf);
        text.Children.Add(_label);
        _root.Children.Add(_glow);
        _root.Children.Add(_track);
        _root.Children.Add(_arc);
        _root.Children.Add(text);
        Content = _root;
        SizeChanged += (_, _) => Layout();
        Loaded += (_, _) =>
        {
            ApplyBrushes();
            AnimateTo();
        };
        ActualThemeChanged += (_, _) => ApplyBrushes();
        Unloaded += (_, _) => Unsubscribe();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public string Label { get => (string)GetValue(LabelProperty); set => SetValue(LabelProperty, value); }

    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }

    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }

    /// <summary>Re-reads the accent colour (after the user changes it in Settings).</summary>
    public void ApplyBrushes()
    {
        var accent = Application.Current.Resources.TryGetValue("SystemAccentColor", out var a) && a is Color c ? c : Color.FromArgb(255, 0x1F, 0xB6, 0xFF);
        var second = Color.FromArgb(255, 0x7C, 0x6C, 0xFF);
        var gradient = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1) };
        gradient.GradientStops.Add(new GradientStop { Color = accent, Offset = 0 });
        gradient.GradientStops.Add(new GradientStop { Color = second, Offset = 1 });
        _arc.Stroke = gradient;
        _track.Stroke = new SolidColorBrush(ActualTheme == ElementTheme.Light ? Color.FromArgb(0x18, 0, 0, 0) : Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF));
        var glow = new RadialGradientBrush();
        glow.GradientStops.Add(new GradientStop { Color = Color.FromArgb(0x80, accent.R, accent.G, accent.B), Offset = 0 });
        glow.GradientStops.Add(new GradientStop { Color = Colors.Transparent, Offset = 1 });
        _glow.Fill = glow;
    }

    private void Layout()
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        _track.Width = _track.Height = size;
        _track.StrokeThickness = Thickness;
        _glow.Width = _glow.Height = size * 0.92;
        _arc.StrokeThickness = Thickness;
        _number.FontSize = Math.Max(24, size * 0.26);
        Draw();
    }

    private void AnimateTo()
    {
        if (!Motion.Enabled || !IsLoaded)
        {
            _displayed = Value;
            Draw();
            return;
        }

        _from = _displayed;
        _clock.Restart();
        if (!_subscribed)
        {
            CompositionTarget.Rendering += OnRendering;
            _subscribed = true;
        }
    }

    private void OnRendering(object? sender, object e)
    {
        var t = _clock.Elapsed.TotalMilliseconds / Duration.TotalMilliseconds;
        _displayed = _from + ((Value - _from) * Motion.Ease(t));
        if (t >= 1)
        {
            _displayed = Value;
            Unsubscribe();
        }

        Draw();
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void Draw()
    {
        _number.Text = Math.Round(_displayed).ToString(System.Globalization.CultureInfo.CurrentCulture);
        var size = Math.Min(ActualWidth, ActualHeight);
        if (size <= 0)
        {
            return;
        }

        var value = Math.Clamp(_displayed, 0, 100);
        if (value <= 0.05)
        {
            _arc.Data = null;
            return;
        }

        var radius = (size - Thickness) / 2;
        var center = new Point(size / 2, size / 2);
        var angle = Math.Min(359.99, value / 100 * 360) * Math.PI / 180;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = start, IsClosed = false, IsFilled = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Size(radius, radius),
            IsLargeArc = angle > Math.PI,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        _arc.Data = geometry;
        _arc.Width = _arc.Height = size;
    }
}
