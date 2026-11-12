using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace Nexora.App.Controls;

/// <summary>Number that glides smoothly to new values (≈350 ms ease-out), driven by the compositor frame clock.</summary>
public sealed class AnimatedNumber : UserControl
{
    public static readonly DependencyProperty ValueProperty =
        DependencyProperty.Register(nameof(Value), typeof(double), typeof(AnimatedNumber), new PropertyMetadata(0d, (d, _) => ((AnimatedNumber)d).AnimateTo()));

    public static readonly DependencyProperty DecimalsProperty =
        DependencyProperty.Register(nameof(Decimals), typeof(int), typeof(AnimatedNumber), new PropertyMetadata(0, (d, _) => ((AnimatedNumber)d).Render()));

    public static readonly DependencyProperty SuffixProperty =
        DependencyProperty.Register(nameof(Suffix), typeof(string), typeof(AnimatedNumber), new PropertyMetadata(string.Empty, (d, _) => ((AnimatedNumber)d).Render()));

    public static readonly DependencyProperty IsAvailableProperty =
        DependencyProperty.Register(nameof(IsAvailable), typeof(bool), typeof(AnimatedNumber), new PropertyMetadata(true, (d, _) => ((AnimatedNumber)d).Render()));

    private static readonly TimeSpan Duration = TimeSpan.FromMilliseconds(350);
    private readonly TextBlock _text = new() { TextLineBounds = TextLineBounds.Tight };
    private readonly Stopwatch _clock = new();
    private double _from;
    private double _displayed;
    private bool _subscribed;

    public AnimatedNumber()
    {
        Content = _text;
        IsTabStop = false;
        Unloaded += (_, _) => Unsubscribe();
        Render();
    }

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }

    public int Decimals { get => (int)GetValue(DecimalsProperty); set => SetValue(DecimalsProperty, value); }

    public string Suffix { get => (string)GetValue(SuffixProperty); set => SetValue(SuffixProperty, value); }

    /// <summary>When false, shows an em dash instead of a number (sensor unavailable).</summary>
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    public TextAlignment TextAlignment { get => _text.TextAlignment; set => _text.TextAlignment = value; }

    private void AnimateTo()
    {
        if (!Motion.Enabled || !IsLoaded || double.IsNaN(Value) || Math.Abs(Value - _displayed) < 0.01)
        {
            _displayed = Value;
            Render();
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

        Render();
    }

    private void Unsubscribe()
    {
        if (_subscribed)
        {
            CompositionTarget.Rendering -= OnRendering;
            _subscribed = false;
        }
    }

    private void Render()
    {
        _text.Text = IsAvailable && !double.IsNaN(_displayed)
            ? _displayed.ToString("F" + Math.Clamp(Decimals, 0, 3).ToString(CultureInfo.InvariantCulture), CultureInfo.CurrentCulture) + Suffix
            : "—";
    }
}
