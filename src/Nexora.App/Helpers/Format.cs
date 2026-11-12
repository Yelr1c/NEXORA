using System.Globalization;
using Microsoft.UI.Xaml;

namespace Nexora.App.Helpers;

/// <summary>Consistent value formatting. Missing values render as "Unavailable" or an em dash, never as fake numbers.</summary>
public static class Format
{
    public const string Unavailable = "Unavailable on this system";
    public const string Dash = "—";

    public static string Percent(double? value) => value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + "%" : Dash;

    public static string Ms(double? value) => value is { } v ? (v < 10 ? v.ToString("0.0", CultureInfo.CurrentCulture) : v.ToString("0", CultureInfo.CurrentCulture)) + " ms" : Dash;

    public static string Number(double? value, string format = "0") => value is { } v ? v.ToString(format, CultureInfo.CurrentCulture) : Dash;

    public static string Mhz(double? value) => value is { } v
        ? (v >= 1000 ? (v / 1000).ToString("0.00", CultureInfo.CurrentCulture) + " GHz" : v.ToString("0", CultureInfo.CurrentCulture) + " MHz")
        : Dash;

    public static string Celsius(double? value) => value is { } v ? v.ToString("0", CultureInfo.CurrentCulture) + " °C" : Dash;

    public static string Bytes(long? bytes)
    {
        if (bytes is not { } b || b < 0)
        {
            return Dash;
        }

        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = b;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return value.ToString(unit >= 3 ? "0.0" : "0", CultureInfo.CurrentCulture) + " " + units[unit];
    }

    public static string Gigabytes(long bytes) => (bytes / 1024d / 1024 / 1024).ToString("0.#", CultureInfo.CurrentCulture) + " GB";

    public static string LinkSpeed(long? bitsPerSecond) => bitsPerSecond is { } b && b > 0
        ? (b >= 1_000_000_000 ? (b / 1_000_000_000d).ToString("0.#", CultureInfo.CurrentCulture) + " Gbps" : (b / 1_000_000d).ToString("0", CultureInfo.CurrentCulture) + " Mbps")
        : Dash;

    public static string Relative(DateTimeOffset when)
    {
        var span = DateTimeOffset.Now - when;
        if (span.TotalMinutes < 1)
        {
            return "just now";
        }

        if (span.TotalHours < 1)
        {
            return $"{(int)span.TotalMinutes} min ago";
        }

        if (span.TotalDays < 1)
        {
            return $"{(int)span.TotalHours} h ago";
        }

        return when.ToLocalTime().ToString("g", CultureInfo.CurrentCulture);
    }

    public static Visibility Show(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    public static Visibility Hide(bool value) => value ? Visibility.Collapsed : Visibility.Visible;
}
