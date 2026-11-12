namespace Nexora.Core.Platform;

/// <summary>Information about the privileges of the current process.</summary>
public interface IPrivilegeService
{
    bool IsElevated { get; }
}

/// <summary>Locations used by NEXORA to persist data. All are under the user's local app data.</summary>
public interface IAppPaths
{
    string DataDirectory { get; }

    string LogsDirectory { get; }

    string BackupsDirectory { get; }

    string QuarantineDirectory { get; }
}

public sealed class AppPaths : IAppPaths
{
    public AppPaths(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        DataDirectory = dataDirectory;
        LogsDirectory = Path.Combine(dataDirectory, "Logs");
        BackupsDirectory = Path.Combine(dataDirectory, "Backups");
        QuarantineDirectory = Path.Combine(dataDirectory, "Quarantine");
    }

    public string DataDirectory { get; }

    public string LogsDirectory { get; }

    public string BackupsDirectory { get; }

    public string QuarantineDirectory { get; }

    public static AppPaths CreateDefault()
    {
        var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create);
        return new AppPaths(Path.Combine(root, "NEXORA"));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(DataDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(BackupsDirectory);
        Directory.CreateDirectory(QuarantineDirectory);
    }
}

public sealed record PowerScheme(Guid Id, string Name, bool IsActive);

/// <summary>Native power management (powrprof) abstraction.</summary>
public interface IPowerManager
{
    Guid? GetActiveScheme();

    IReadOnlyList<PowerScheme> GetSchemes();

    void SetActiveScheme(Guid scheme);

    /// <summary>Windows 11 "Power mode" overlay. Null if not supported on this system.</summary>
    Guid? GetActiveOverlay();

    bool IsOverlaySupported { get; }

    void SetActiveOverlay(Guid overlay);
}

public static class PowerSchemes
{
    public static readonly Guid Balanced = new("381b4222-f694-41f0-9685-ff5bb260df2e");
    public static readonly Guid HighPerformance = new("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
    public static readonly Guid PowerSaver = new("a1841308-3541-4fab-bc81-f71556f20b4a");
    public static readonly Guid UltimatePerformance = new("e9a42b02-d5df-448d-aa00-03f14749eb61");

    /// <summary>Windows 11 power mode overlays (Settings &gt; System &gt; Power &gt; Power mode).</summary>
    public static readonly Guid OverlayBestPerformance = new("ded574b5-45a0-4f42-8737-46345c09c238");
    public static readonly Guid OverlayBalanced = Guid.Empty;
    public static readonly Guid OverlayBestPowerEfficiency = new("961cc777-2547-4f9d-8174-7d86181b8a7a");

    public static string DescribeOverlay(Guid overlay)
    {
        if (overlay == OverlayBestPerformance)
        {
            return "Best performance";
        }

        if (overlay == OverlayBestPowerEfficiency)
        {
            return "Best power efficiency";
        }

        return overlay == Guid.Empty ? "Balanced" : $"Custom ({overlay})";
    }
}

public sealed record MouseSettings(int Threshold1, int Threshold2, int Acceleration);

/// <summary>SystemParametersInfo-backed user settings.</summary>
public interface ISystemParameters
{
    bool GetClientAreaAnimation();

    void SetClientAreaAnimation(bool enabled);

    MouseSettings GetMouseSettings();

    void SetMouseSettings(MouseSettings settings);
}
