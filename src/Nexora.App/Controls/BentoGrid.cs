using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace Nexora.App.Controls;

/// <summary>
/// 12-column responsive dashboard grid. Each child declares how many columns it spans (<see cref="SpanProperty"/>)
/// and optionally a different span for narrow windows (<see cref="CompactSpanProperty"/>). Children flow left to
/// right, wrap into rows, and all cards in a row share the row's height so edges line up like a bento layout.
/// </summary>
public sealed partial class BentoGrid : Panel
{
    public const int Columns = 12;

    public static readonly DependencyProperty SpanProperty =
        DependencyProperty.RegisterAttached("Span", typeof(int), typeof(BentoGrid), new PropertyMetadata(12, OnLayoutPropertyChanged));

    public static readonly DependencyProperty CompactSpanProperty =
        DependencyProperty.RegisterAttached("CompactSpan", typeof(int), typeof(BentoGrid), new PropertyMetadata(0, OnLayoutPropertyChanged));

    public static readonly DependencyProperty ColumnSpacingProperty =
        DependencyProperty.Register(nameof(ColumnSpacing), typeof(double), typeof(BentoGrid), new PropertyMetadata(16d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty RowSpacingProperty =
        DependencyProperty.Register(nameof(RowSpacing), typeof(double), typeof(BentoGrid), new PropertyMetadata(16d, OnLayoutPropertyChanged));

    public static readonly DependencyProperty CompactWidthProperty =
        DependencyProperty.Register(nameof(CompactWidth), typeof(double), typeof(BentoGrid), new PropertyMetadata(1080d, OnLayoutPropertyChanged));

    private readonly List<List<UIElement>> _rows = [];

    public double ColumnSpacing { get => (double)GetValue(ColumnSpacingProperty); set => SetValue(ColumnSpacingProperty, value); }

    public double RowSpacing { get => (double)GetValue(RowSpacingProperty); set => SetValue(RowSpacingProperty, value); }

    /// <summary>Below this width, <see cref="CompactSpanProperty"/> is used (default: span 12 = full width).</summary>
    public double CompactWidth { get => (double)GetValue(CompactWidthProperty); set => SetValue(CompactWidthProperty, value); }

    public static int GetSpan(DependencyObject element) => (int)element.GetValue(SpanProperty);

    public static void SetSpan(DependencyObject element, int value) => element.SetValue(SpanProperty, value);

    public static int GetCompactSpan(DependencyObject element) => (int)element.GetValue(CompactSpanProperty);

    public static void SetCompactSpan(DependencyObject element, int value) => element.SetValue(CompactSpanProperty, value);

    private static void OnLayoutPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is BentoGrid grid)
        {
            grid.InvalidateMeasure();
        }
        else if (d is FrameworkElement { Parent: BentoGrid parent })
        {
            parent.InvalidateMeasure();
        }
    }

    private int EffectiveSpan(UIElement child, double width)
    {
        var span = width < CompactWidth ? GetCompactSpan(child) is > 0 and var c ? c : Columns : GetSpan(child);
        return Math.Clamp(span, 1, Columns);
    }

    private double CellWidth(double width, int span)
    {
        var column = (width - ((Columns - 1) * ColumnSpacing)) / Columns;
        return Math.Max(0, (column * span) + ((span - 1) * ColumnSpacing));
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 1200 : availableSize.Width;
        _rows.Clear();
        var row = new List<UIElement>();
        var used = 0;
        double total = 0;
        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
            {
                child.Measure(new Size(0, 0));
                continue;
            }

            var span = EffectiveSpan(child, width);
            if (used + span > Columns && row.Count > 0)
            {
                _rows.Add(row);
                row = [];
                used = 0;
            }

            child.Measure(new Size(CellWidth(width, span), double.PositiveInfinity));
            row.Add(child);
            used += span;
        }

        if (row.Count > 0)
        {
            _rows.Add(row);
        }

        foreach (var r in _rows)
        {
            total += r.Max(c => c.DesiredSize.Height);
        }

        total += Math.Max(0, _rows.Count - 1) * RowSpacing;
        return new Size(width, total);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double y = 0;
        foreach (var row in _rows)
        {
            var height = row.Max(c => c.DesiredSize.Height);
            double x = 0;
            foreach (var child in row)
            {
                var w = CellWidth(finalSize.Width, EffectiveSpan(child, finalSize.Width));
                child.Arrange(new Rect(x, y, w, height));
                x += w + ColumnSpacing;
            }

            y += height + RowSpacing;
        }

        return finalSize;
    }
}
