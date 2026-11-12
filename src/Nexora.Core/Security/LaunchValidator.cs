using System.Text.RegularExpressions;

namespace Nexora.Core.Security;

/// <summary>
/// Validates every external launch request. NEXORA never runs shell commands; it only starts
/// executables the user chose (or a launcher detected) and a small allow-list of store URIs.
/// </summary>
public static partial class LaunchValidator
{
    private static readonly string[] BlockedFileNames =
    [
        "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe", "mshta.exe", "rundll32.exe", "regsvr32.exe",
        "reg.exe", "regedit.exe", "bcdedit.exe", "diskpart.exe", "format.com", "net.exe", "sc.exe", "schtasks.exe", "wmic.exe",
        "msiexec.exe", "bitsadmin.exe", "certutil.exe", "taskkill.exe", "vssadmin.exe", "wbadmin.exe",
    ];

    public static (bool IsValid, string? Error) ValidateExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (false, "No executable selected.");
        }

        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.Contains('"', StringComparison.Ordinal))
        {
            return (false, "The path contains invalid characters.");
        }

        if (!Path.IsPathFullyQualified(path) || path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return (false, "Only executables on a local drive can be launched.");
        }

        if (!string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            return (false, "Only .exe files can be launched.");
        }

        if (BlockedFileNames.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase))
        {
            return (false, "System tools cannot be launched from a game profile.");
        }

        if (!File.Exists(path))
        {
            return (false, "The executable no longer exists. Browse for its new location.");
        }

        return (true, null);
    }

    /// <summary>Launch arguments are passed verbatim to the game (never to a shell); reject control characters.</summary>
    public static (bool IsValid, string? Error) ValidateArguments(string? arguments)
    {
        if (string.IsNullOrEmpty(arguments))
        {
            return (true, null);
        }

        if (arguments.Length > 2048)
        {
            return (false, "Launch arguments are too long.");
        }

        if (arguments.Any(char.IsControl))
        {
            return (false, "Launch arguments contain control characters.");
        }

        return (true, null);
    }

    public static bool IsAllowedLaunchUri(string? uri)
        => !string.IsNullOrEmpty(uri) && (SteamUriRegex().IsMatch(uri) || EpicUriRegex().IsMatch(uri));

    [GeneratedRegex(@"^steam://rungameid/\d{1,10}$")]
    private static partial Regex SteamUriRegex();

    [GeneratedRegex(@"^com\.epicgames\.launcher://apps/[A-Za-z0-9%:._-]{1,200}\?action=launch&silent=true$")]
    private static partial Regex EpicUriRegex();
}
