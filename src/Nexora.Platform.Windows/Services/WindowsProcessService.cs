using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Nexora.Core.Processes;
using Nexora.Platform.Windows.Interop;
using Nexora.Platform.Windows.Monitoring;

namespace Nexora.Platform.Windows.Services;

public sealed class WindowsProcessService(WindowsMetricsProvider metrics, ILogger<WindowsProcessService> logger) : IProcessService
{
    private readonly ProcessCpuTracker _cpu = new(Environment.ProcessorCount);
    private readonly object _sync = new();
    private readonly int _ownPid = Environment.ProcessId;

    public IReadOnlyList<ProcessSnapshot> GetProcesses()
    {
        var windows = CollectWindowStates();
        var gpu = metrics.PerProcessGpu;
        var now = DateTimeOffset.UtcNow;
        var result = new List<ProcessSnapshot>();
        var live = new HashSet<int>();
        var processes = Process.GetProcesses();
        lock (_sync)
        {
            foreach (var process in processes)
            {
                using (process)
                {
                    try
                    {
                        var id = process.Id;
                        live.Add(id);
                        var name = process.ProcessName;
                        var session = SafeGet(() => process.SessionId, -1);
                        double? cpu = null;
                        try
                        {
                            cpu = _cpu.Update(id, process.StartTime, process.TotalProcessorTime, now);
                        }
                        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
                        {
                            // Access denied for protected/system processes.
                        }

                        var (isProtected, reason) = ProcessProtectionPolicy.Evaluate(name, id, session, _ownPid);
                        var status = windows.TryGetValue(id, out var hung) ? (hung ? "Not responding" : "App") : "Background";
                        result.Add(new ProcessSnapshot(
                            id,
                            name,
                            cpu,
                            SafeGet(() => process.WorkingSet64, 0L),
                            gpu.TryGetValue(id, out var g) ? g : null,
                            SafeGet(() => Map(process.PriorityClass), ProcessPriorityLevel.Unknown),
                            status,
                            isProtected,
                            reason,
                            session,
                            null));
                    }
                    catch (InvalidOperationException)
                    {
                        // Process exited while enumerating.
                    }
                }
            }

            _cpu.Retain(live);
        }

        return result;
    }

    public async Task<ProcessActionResult> TerminateAsync(int processId, CancellationToken cancellationToken = default)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return new ProcessActionResult(ProcessActionOutcome.NotFound, "The process has already exited.");
        }

        using (process)
        {
            var (isProtected, reason) = ProcessProtectionPolicy.Evaluate(process.ProcessName, processId, SafeGet(() => process.SessionId, 0), _ownPid);
            if (isProtected)
            {
                logger.LogWarning("Refused to terminate protected process {Name}", process.ProcessName);
                return new ProcessActionResult(ProcessActionOutcome.Protected, $"{process.ProcessName} is protected ({reason}) and cannot be ended by NEXORA.");
            }

            try
            {
                process.Kill(entireProcessTree: false);
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(5));
                await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
                logger.LogInformation("Terminated process {Name} at user request", process.ProcessName);
                return new ProcessActionResult(ProcessActionOutcome.Success, $"{process.ProcessName} was ended.");
            }
            catch (System.ComponentModel.Win32Exception)
            {
                return new ProcessActionResult(ProcessActionOutcome.AccessDenied, "Access denied. The process may belong to another user or require administrator rights.");
            }
            catch (OperationCanceledException)
            {
                return new ProcessActionResult(ProcessActionOutcome.Failed, "The process did not exit in time.");
            }
            catch (InvalidOperationException)
            {
                return new ProcessActionResult(ProcessActionOutcome.Success, "The process has exited.");
            }
        }
    }

    public ProcessActionResult SetPriority(int processId, ProcessPriorityLevel priority)
    {
        if (priority is ProcessPriorityLevel.RealTime or ProcessPriorityLevel.Unknown)
        {
            return new ProcessActionResult(ProcessActionOutcome.Protected, "Realtime priority can freeze Windows and is not allowed.");
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            var (isProtected, reason) = ProcessProtectionPolicy.Evaluate(process.ProcessName, processId, SafeGet(() => process.SessionId, 0), _ownPid);
            if (isProtected)
            {
                return new ProcessActionResult(ProcessActionOutcome.Protected, $"{process.ProcessName} is protected ({reason}).");
            }

            process.PriorityClass = priority switch
            {
                ProcessPriorityLevel.Idle => ProcessPriorityClass.Idle,
                ProcessPriorityLevel.BelowNormal => ProcessPriorityClass.BelowNormal,
                ProcessPriorityLevel.AboveNormal => ProcessPriorityClass.AboveNormal,
                ProcessPriorityLevel.High => ProcessPriorityClass.High,
                _ => ProcessPriorityClass.Normal,
            };
            return new ProcessActionResult(ProcessActionOutcome.Success, $"Priority of {process.ProcessName} set to {priority}.");
        }
        catch (ArgumentException)
        {
            return new ProcessActionResult(ProcessActionOutcome.NotFound, "The process has exited.");
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return new ProcessActionResult(ProcessActionOutcome.AccessDenied, "Access denied.");
        }
    }

    public static ProcessPriorityLevel Map(ProcessPriorityClass priority) => priority switch
    {
        ProcessPriorityClass.Idle => ProcessPriorityLevel.Idle,
        ProcessPriorityClass.BelowNormal => ProcessPriorityLevel.BelowNormal,
        ProcessPriorityClass.Normal => ProcessPriorityLevel.Normal,
        ProcessPriorityClass.AboveNormal => ProcessPriorityLevel.AboveNormal,
        ProcessPriorityClass.High => ProcessPriorityLevel.High,
        ProcessPriorityClass.RealTime => ProcessPriorityLevel.RealTime,
        _ => ProcessPriorityLevel.Unknown,
    };

    /// <summary>One EnumWindows pass: pid → whether any visible top-level window is hung.</summary>
    private static Dictionary<int, bool> CollectWindowStates()
    {
        var map = new Dictionary<int, bool>();
        NativeMethods.EnumWindows((hwnd, lParam) =>
        {
            if (NativeMethods.IsWindowVisible(hwnd) && NativeMethods.GetWindow(hwnd, 4 /* GW_OWNER */) == IntPtr.Zero)
            {
                _ = NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
                var hung = NativeMethods.IsHungAppWindow(hwnd);
                map[(int)pid] = map.GetValueOrDefault((int)pid) || hung;
            }

            return true;
        }, IntPtr.Zero);
        return map;
    }

    private static T SafeGet<T>(Func<T> getter, T fallback)
    {
        try
        {
            return getter();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            return fallback;
        }
    }
}
