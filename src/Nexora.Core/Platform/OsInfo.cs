namespace Nexora.Core.Platform;

public interface IOsInfo
{
    /// <summary>Windows build number, e.g. 26100.</summary>
    int Build { get; }

    /// <summary>e.g. "Windows 11 Pro".</summary>
    string ProductName { get; }

    /// <summary>e.g. "24H2".</summary>
    string DisplayVersion { get; }

    bool IsWindows11 => Build >= 22000;
}
