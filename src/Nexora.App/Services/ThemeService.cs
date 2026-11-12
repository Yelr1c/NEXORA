using System.Globalization;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Nexora.Core.Settings;
using Windows.UI;

namespace Nexora.App.Services;

/// <summary>Applies theme, accent colour and backdrop settings.</summary>
public sealed class ThemeService
{
    private FrameworkElement? _root;

    public event EventHandler? ThemeApplied;

    public static Color ParseColor(string hex)
    {
        var value = hex.TrimStart('#');
        if (value.Length != 6 || !uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var rgb))
        {
            return Color.FromArgb(255, 0x1F, 0xB6, 0xFF);
        }

        return Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
    }

    /// <summary>Overrides the system accent palette. Must run before UI is created to affect all Fluent controls.</summary>
    public static void ApplyAccentResources(ResourceDictionary resources, string hex)
    {
        ArgumentNullException.ThrowIfNull(resources);
        var accent = ParseColor(hex);
        resources["SystemAccentColor"] = accent;
        resources["SystemAccentColorLight1"] = Blend(accent, Colors.White, 0.20);
        resources["SystemAccentColorLight2"] = Blend(accent, Colors.White, 0.38);
        resources["SystemAccentColorLight3"] = Blend(accent, Colors.White, 0.55);
        resources["SystemAccentColorDark1"] = Blend(accent, Colors.Black, 0.18);
        resources["SystemAccentColorDark2"] = Blend(accent, Colors.Black, 0.36);
        resources["SystemAccentColorDark3"] = Blend(accent, Colors.Black, 0.52);
        UpdateBrush(resources, "NexoraAccentBrush", accent);
        UpdateBrush(resources, "NexoraAccentSoftBrush", Color.FromArgb(0x33, accent.R, accent.G, accent.B));
    }

    public void Attach(FrameworkElement root) => _root = root;

    public void Apply(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (_root is not null)
        {
            _root.RequestedTheme = settings.Theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }

        ApplyAccentResources(Application.Current.Resources, settings.AccentColor);
        ThemeApplied?.Invoke(this, EventArgs.Empty);
    }

    public ElementTheme ActualTheme => _root?.ActualTheme ?? ElementTheme.Dark;

    private static void UpdateBrush(ResourceDictionary resources, string key, Color color)
    {
        if (resources.TryGetValue(key, out var existing) && existing is SolidColorBrush brush)
        {
            brush.Color = color;
        }
        else
        {
            resources[key] = new SolidColorBrush(color);
        }
    }

    private static Color Blend(Color a, Color b, double amount) => Color.FromArgb(255,
        (byte)(a.R + ((b.R - a.R) * amount)),
        (byte)(a.G + ((b.G - a.G) * amount)),
        (byte)(a.B + ((b.B - a.B) * amount)));
}
