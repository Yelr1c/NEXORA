namespace Nexora.App.Controls;

/// <summary>Global animation switch (Settings › Enable animations) shared by all custom controls.</summary>
public static class Motion
{
    public static bool Enabled { get; set; } = true;

    /// <summary>Cubic ease-out.</summary>
    public static double Ease(double t) => 1 - Math.Pow(1 - Math.Clamp(t, 0, 1), 3);
}
