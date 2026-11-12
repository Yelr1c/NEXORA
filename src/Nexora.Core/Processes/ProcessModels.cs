namespace Nexora.Core.Processes;

public enum ProcessPriorityLevel
{
    Unknown,
    Idle,
    BelowNormal,
    Normal,
    AboveNormal,
    High,
    RealTime,
}

public sealed record ProcessSnapshot(
    int Id,
    string Name,
    double? CpuPercent,
    long MemoryBytes,
    double? GpuPercent,
    ProcessPriorityLevel Priority,
    string Status,
    bool IsProtected,
    string? ProtectionReason,
    int SessionId,
    string? Description);

public enum ProcessActionOutcome
{
    Success,
    Protected,
    NotFound,
    AccessDenied,
    Failed,
}

public sealed record ProcessActionResult(ProcessActionOutcome Outcome, string Message)
{
    public bool IsSuccess => Outcome == ProcessActionOutcome.Success;
}

public interface IProcessService
{
    IReadOnlyList<ProcessSnapshot> GetProcesses();

    Task<ProcessActionResult> TerminateAsync(int processId, CancellationToken cancellationToken = default);

    ProcessActionResult SetPriority(int processId, ProcessPriorityLevel priority);
}

/// <summary>
/// Decides which processes must never be terminated. NEXORA refuses to end anything on this list,
/// any process in session 0 (services), and itself.
/// </summary>
public static class ProcessProtectionPolicy
{
    private static readonly HashSet<string> CriticalNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "System Idle Process", "Registry", "Secure System", "Memory Compression", "smss", "csrss", "wininit",
        "winlogon", "services", "lsass", "LsaIso", "svchost", "dwm", "fontdrvhost", "sihost", "ctfmon", "explorer",
        "taskhostw", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "TextInputHost",
        "conhost", "dllhost", "audiodg", "spoolsv", "WUDFHost", "wlanext", "dasHost", "SgrmBroker", "WmiPrvSE",
        "MsMpEng", "NisSrv", "MpDefenderCoreService", "SecurityHealthService", "SecurityHealthSystray", "smartscreen",
        "MsSense", "SenseCncProxy", "LogonUI", "userinit", "consent", "TrustedInstaller", "TiWorker", "UserOOBEBroker",
        "ApplicationFrameHost", "SystemSettingsBroker", "AggregatorHost", "LockApp", "Widgets", "WidgetService",
        "MoUsoCoreWorker", "usocoreworker", "wuauclt", "SgrmLpac", "vmmem", "vmcompute", "csrss.exe",
    };

    private static readonly string[] SecurityVendorPrefixes =
    [
        "avast", "avg", "norton", "mcafee", "bdservicehost", "bitdefender", "vsserv", "avp", "kav", "ekrn", "egui",
        "mbam", "malwarebytes", "sophos", "savservice", "tmbmsrv", "ntrtscan", "wrsa", "csfalcon", "sentinel", "fsav",
    ];

    public static (bool IsProtected, string? Reason) Evaluate(string processName, int processId, int sessionId, int currentProcessId)
    {
        if (processId is 0 or 4)
        {
            return (true, "Core Windows process");
        }

        if (processId == currentProcessId)
        {
            return (true, "NEXORA itself");
        }

        var name = Path.GetFileNameWithoutExtension(processName ?? string.Empty);
        if (CriticalNames.Contains(name))
        {
            return (true, "Critical Windows process");
        }

        var lower = name.ToLowerInvariant();
        if (SecurityVendorPrefixes.Any(p => lower.StartsWith(p, StringComparison.Ordinal)))
        {
            return (true, "Security software");
        }

        if (sessionId == 0)
        {
            return (true, "Windows service process");
        }

        return (false, null);
    }
}

/// <summary>Computes per-process CPU % from successive total processor time readings.</summary>
public sealed class ProcessCpuTracker
{
    private readonly Dictionary<int, (TimeSpan Cpu, DateTimeOffset At, DateTimeOffset StartTime)> _previous = [];
    private readonly int _processorCount;

    public ProcessCpuTracker(int processorCount)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(processorCount, 1);
        _processorCount = processorCount;
    }

    /// <summary>Returns CPU % (0–100 of total machine capacity) or null on the first observation.</summary>
    public double? Update(int processId, DateTimeOffset startTime, TimeSpan totalProcessorTime, DateTimeOffset now)
    {
        double? result = null;
        if (_previous.TryGetValue(processId, out var prev) && prev.StartTime == startTime)
        {
            var wall = (now - prev.At).TotalMilliseconds;
            if (wall > 0)
            {
                var cpu = (totalProcessorTime - prev.Cpu).TotalMilliseconds;
                result = Math.Clamp(100.0 * cpu / (wall * _processorCount), 0, 100);
            }
        }

        _previous[processId] = (totalProcessorTime, now, startTime);
        return result;
    }

    /// <summary>Forgets processes that no longer exist.</summary>
    public void Retain(IReadOnlySet<int> liveProcessIds)
    {
        foreach (var id in _previous.Keys.Where(k => !liveProcessIds.Contains(k)).ToList())
        {
            _previous.Remove(id);
        }
    }
}
