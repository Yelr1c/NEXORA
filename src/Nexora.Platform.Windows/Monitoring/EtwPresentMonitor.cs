using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Nexora.Core.Monitoring;
using Nexora.Platform.Windows.Interop;

namespace Nexora.Platform.Windows.Monitoring;

/// <summary>
/// Measures FPS and frame times by listening to the DXGI and Direct3D 9 "Present" ETW events that
/// Windows emits for every presented frame (the same data source PresentMon uses). Nothing is injected
/// into games, so anti-cheat systems are not affected.
/// Real-time ETW sessions require administrator rights or membership in "Performance Log Users".
/// </summary>
public sealed unsafe class EtwPresentMonitor : IFrameRateSource
{
    private const string SessionName = "NEXORA-FrameCapture";
    private static readonly Guid DxgiProvider = new("CA11C036-0102-4A2D-A6AD-F03CFED5D3C9");
    private static readonly Guid D3D9Provider = new("783ACA0A-790E-4D7F-8451-AA850511C6B9");
    private const ushort DxgiPresentStart = 42;
    private const ushort DxgiPresentMpoStart = 55;
    private const ushort D3D9PresentStart = 1;

    private const uint ERROR_SUCCESS = 0;
    private const uint ERROR_ACCESS_DENIED = 5;
    private const uint ERROR_ALREADY_EXISTS = 183;
    private const uint EVENT_TRACE_CONTROL_STOP = 1;
    private const uint EVENT_TRACE_REAL_TIME_MODE = 0x00000100;
    private const uint WNODE_FLAG_TRACED_GUID = 0x00020000;
    private const uint PROCESS_TRACE_MODE_REAL_TIME = 0x00000100;
    private const uint PROCESS_TRACE_MODE_EVENT_RECORD = 0x10000000;
    private const ulong INVALID_PROCESSTRACE_HANDLE = ulong.MaxValue;
    private const int PropertiesSize = 120;
    private const int LogFileSize = 448;

    private readonly ILogger<EtwPresentMonitor> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<int, ProcessFrames> _processes = [];
    private readonly Dictionary<int, string> _names = [];
    private readonly EventRecordCallback _callback;
    private readonly int _ownProcessId = Environment.ProcessId;
    private ulong _sessionHandle;
    private ulong _traceHandle = INVALID_PROCESSTRACE_HANDLE;
    private Thread? _thread;
    private bool _recording;
    private int? _recordPid;
    private List<double> _recorded = [];

    public EtwPresentMonitor(ILogger<EtwPresentMonitor> logger)
    {
        _logger = logger;
        _callback = OnEvent;
    }

    public event EventHandler? StatusChanged;

    public FrameCaptureStatus Status { get; private set; } = FrameCaptureStatus.Stopped;

    public string StatusMessage { get; private set; } = "Frame capture is not running.";

    public int? TargetProcessId { get; set; }

    private delegate void EventRecordCallback(IntPtr eventRecord);

    public bool TryStart()
    {
        lock (_sync)
        {
            if (Status == FrameCaptureStatus.Running)
            {
                return true;
            }

            var status = StartSession();
            if (status == ERROR_ALREADY_EXISTS)
            {
                // A previous NEXORA instance crashed and left the session running; stop it and retry.
                StopSessionByName();
                status = StartSession();
            }

            if (status != ERROR_SUCCESS)
            {
                SetStatus(status == ERROR_ACCESS_DENIED ? FrameCaptureStatus.RequiresElevation : FrameCaptureStatus.Unavailable,
                    status == ERROR_ACCESS_DENIED
                        ? "FPS capture requires administrator rights. Restart NEXORA as administrator to measure FPS."
                        : $"FPS capture is unavailable on this system (ETW error {status}).");
                _logger.LogWarning("ETW session could not be started: {Status}", status);
                return false;
            }

            var level = (byte)4;
            var dxgi = DxgiProvider;
            var d3d9 = D3D9Provider;
            var e1 = EnableTraceEx2(_sessionHandle, ref dxgi, 1, level, ulong.MaxValue, 0, 0, IntPtr.Zero);
            var e2 = EnableTraceEx2(_sessionHandle, ref d3d9, 1, level, ulong.MaxValue, 0, 0, IntPtr.Zero);
            if (e1 != ERROR_SUCCESS && e2 != ERROR_SUCCESS)
            {
                StopSessionByName();
                SetStatus(FrameCaptureStatus.Unavailable, $"Graphics event providers could not be enabled (error {e1}).");
                return false;
            }

            if (!OpenConsumer())
            {
                StopSessionByName();
                SetStatus(FrameCaptureStatus.Unavailable, "The frame capture consumer could not be opened.");
                return false;
            }

            var handle = _traceHandle;
            _thread = new Thread(() =>
            {
                var handles = new[] { handle };
                var result = ProcessTrace(handles, 1, IntPtr.Zero, IntPtr.Zero);
                if (result != ERROR_SUCCESS)
                {
                    _logger.LogDebug("ProcessTrace ended with {Result}", result);
                }
            })
            {
                IsBackground = true,
                Name = "NEXORA frame capture",
            };
            _thread.Start();
            SetStatus(FrameCaptureStatus.Running, "Capturing DirectX present events.");
            return true;
        }
    }

    public void Stop()
    {
        lock (_sync)
        {
            if (Status != FrameCaptureStatus.Running)
            {
                return;
            }

            StopSessionByName();
            if (_traceHandle != INVALID_PROCESSTRACE_HANDLE)
            {
                _ = CloseTrace(_traceHandle);
                _traceHandle = INVALID_PROCESSTRACE_HANDLE;
            }

            _processes.Clear();
            SetStatus(FrameCaptureStatus.Stopped, "Frame capture is not running.");
        }

        _thread?.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    public FrameRateReading? GetCurrent()
    {
        lock (_sync)
        {
            if (Status != FrameCaptureStatus.Running)
            {
                return null;
            }

            var pid = ResolveTarget();
            if (pid is not { } id || !_processes.TryGetValue(id, out var frames))
            {
                return null;
            }

            var (fps, frameTime) = frames.Current(NowFileTime());
            return fps > 0 ? new FrameRateReading(id, NameOf(id), fps, frameTime) : null;
        }
    }

    public IReadOnlyList<RenderingProcess> GetRenderingProcesses()
    {
        lock (_sync)
        {
            var now = NowFileTime();
            return _processes
                .Select(p => (p.Key, Fps: p.Value.Current(now).Fps))
                .Where(p => p.Fps > 0 && p.Key != _ownProcessId)
                .OrderByDescending(p => p.Fps)
                .Select(p => new RenderingProcess(p.Key, NameOf(p.Key), p.Fps))
                .ToList();
        }
    }

    public void BeginRecording()
    {
        lock (_sync)
        {
            _recording = true;
            _recordPid = ResolveTarget();
            _recorded = [];
        }
    }

    public IReadOnlyList<double> DrainRecordedFrameTimes()
    {
        lock (_sync)
        {
            _recordPid ??= ResolveTarget();
            var drained = _recorded;
            _recorded = [];
            return drained;
        }
    }

    public void EndRecording()
    {
        lock (_sync)
        {
            _recording = false;
            _recordPid = null;
            _recorded = [];
        }
    }

    private int? ResolveTarget()
    {
        if (TargetProcessId is { } explicitTarget)
        {
            return explicitTarget;
        }

        var now = NowFileTime();
        var foreground = ForegroundProcessId();
        if (foreground is { } fg && fg != _ownProcessId && _processes.TryGetValue(fg, out var f) && f.Current(now).Fps > 0)
        {
            return fg;
        }

        // NEXORA itself is in front (e.g. on a second monitor): pick the busiest renderer.
        return _processes
            .Where(p => p.Key != _ownProcessId && !IsCompositor(p.Key))
            .Select(p => (p.Key, p.Value.Current(now).Fps))
            .Where(p => p.Fps > 0)
            .OrderByDescending(p => p.Fps)
            .Select(p => (int?)p.Key)
            .FirstOrDefault();
    }

    private bool IsCompositor(int pid) => NameOf(pid).Equals("dwm", StringComparison.OrdinalIgnoreCase);

    private static int? ForegroundProcessId()
    {
        var window = NativeMethods.GetForegroundWindow();
        if (window == IntPtr.Zero)
        {
            return null;
        }

        _ = NativeMethods.GetWindowThreadProcessId(window, out var pid);
        return pid == 0 ? null : (int)pid;
    }

    private string NameOf(int pid)
    {
        if (_names.TryGetValue(pid, out var name))
        {
            return name;
        }

        try
        {
            using var process = Process.GetProcessById(pid);
            name = process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            name = $"PID {pid}";
        }

        if (_names.Count > 512)
        {
            _names.Clear();
        }

        _names[pid] = name;
        return name;
    }

    private static long NowFileTime() => DateTime.UtcNow.ToFileTimeUtc();

    private void OnEvent(IntPtr record)
    {
        // EVENT_RECORD.EventHeader: ProcessId @12, TimeStamp @16, ProviderId @24, EventDescriptor.Id @40.
        var header = (byte*)record;
        var id = *(ushort*)(header + 40);
        var provider = *(Guid*)(header + 24);
        var isPresent = (provider == DxgiProvider && id is DxgiPresentStart or DxgiPresentMpoStart)
                        || (provider == D3D9Provider && id == D3D9PresentStart);
        if (!isPresent)
        {
            return;
        }

        var pid = (int)*(uint*)(header + 12);
        var timestamp = *(long*)(header + 16);
        lock (_sync)
        {
            if (!_processes.TryGetValue(pid, out var frames))
            {
                if (_processes.Count > 256)
                {
                    var stale = NowFileTime() - (10 * TimeSpan.TicksPerSecond);
                    foreach (var key in _processes.Where(p => p.Value.LastTimestamp < stale).Select(p => p.Key).ToList())
                    {
                        _processes.Remove(key);
                    }
                }

                frames = new ProcessFrames();
                _processes[pid] = frames;
            }

            var delta = frames.Add(timestamp);
            if (_recording && delta is { } ms && _recordPid == pid && _recorded.Count < 2_000_000)
            {
                _recorded.Add(ms);
            }
        }
    }

    private uint StartSession()
    {
        var size = PropertiesSize + ((SessionName.Length + 1) * 2) + 1024;
        var props = Marshal.AllocHGlobal(size);
        try
        {
            new Span<byte>((void*)props, size).Clear();
            var p = (byte*)props;
            *(uint*)(p + 0) = (uint)size;                       // Wnode.BufferSize
            *(Guid*)(p + 24) = Guid.NewGuid();                  // Wnode.Guid
            *(uint*)(p + 40) = 1;                               // Wnode.ClientContext = QPC
            *(uint*)(p + 44) = WNODE_FLAG_TRACED_GUID;          // Wnode.Flags
            *(uint*)(p + 48) = 64;                              // BufferSize (KB)
            *(uint*)(p + 52) = 4;                               // MinimumBuffers
            *(uint*)(p + 56) = 32;                              // MaximumBuffers
            *(uint*)(p + 64) = EVENT_TRACE_REAL_TIME_MODE;      // LogFileMode
            *(uint*)(p + 68) = 1;                               // FlushTimer (s)
            *(uint*)(p + 116) = PropertiesSize;                 // LoggerNameOffset
            return StartTraceW(out _sessionHandle, SessionName, props);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }
    }

    private void StopSessionByName()
    {
        var size = PropertiesSize + ((SessionName.Length + 1) * 2) + 1024;
        var props = Marshal.AllocHGlobal(size);
        try
        {
            new Span<byte>((void*)props, size).Clear();
            var p = (byte*)props;
            *(uint*)(p + 0) = (uint)size;
            *(uint*)(p + 116) = PropertiesSize;
            *(uint*)(p + 112) = (uint)(PropertiesSize + ((SessionName.Length + 1) * 2));
            _ = ControlTraceW(0, SessionName, props, EVENT_TRACE_CONTROL_STOP);
        }
        finally
        {
            Marshal.FreeHGlobal(props);
        }

        _sessionHandle = 0;
    }

    private bool OpenConsumer()
    {
        var logFile = Marshal.AllocHGlobal(LogFileSize);
        var name = Marshal.StringToHGlobalUni(SessionName);
        try
        {
            new Span<byte>((void*)logFile, LogFileSize).Clear();
            var p = (byte*)logFile;
            *(IntPtr*)(p + 8) = name;                                                   // LoggerName
            *(uint*)(p + 28) = PROCESS_TRACE_MODE_REAL_TIME | PROCESS_TRACE_MODE_EVENT_RECORD; // ProcessTraceMode
            *(IntPtr*)(p + 424) = Marshal.GetFunctionPointerForDelegate(_callback);     // EventRecordCallback
            _traceHandle = OpenTraceW(logFile);
            return _traceHandle != INVALID_PROCESSTRACE_HANDLE;
        }
        finally
        {
            Marshal.FreeHGlobal(name);
            Marshal.FreeHGlobal(logFile);
        }
    }

    private void SetStatus(FrameCaptureStatus status, string message)
    {
        Status = status;
        StatusMessage = message;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose() => Stop();

    /// <summary>Recent present timestamps (FILETIME, 100 ns) for one process.</summary>
    private sealed class ProcessFrames
    {
        private readonly Queue<long> _recent = new();

        public long LastTimestamp { get; private set; }

        public double? Add(long timestamp)
        {
            double? delta = null;
            if (LastTimestamp > 0 && timestamp > LastTimestamp)
            {
                delta = (timestamp - LastTimestamp) / 10_000.0;
            }

            LastTimestamp = timestamp;
            _recent.Enqueue(timestamp);
            Trim(timestamp);
            return delta;
        }

        public (double Fps, double FrameTimeMs) Current(long now)
        {
            Trim(now);
            if (_recent.Count < 2 || now - LastTimestamp > TimeSpan.TicksPerSecond)
            {
                return (0, 0);
            }

            var first = _recent.Peek();
            var spanMs = (LastTimestamp - first) / 10_000.0;
            if (spanMs <= 0)
            {
                return (0, 0);
            }

            var frameTime = spanMs / (_recent.Count - 1);
            return (1000.0 / frameTime, frameTime);
        }

        private void Trim(long now)
        {
            var cutoff = now - TimeSpan.TicksPerSecond;
            while (_recent.Count > 0 && _recent.Peek() < cutoff)
            {
                _recent.Dequeue();
            }
        }
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint StartTraceW(out ulong sessionHandle, string sessionName, IntPtr properties);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ControlTraceW(ulong sessionHandle, string? sessionName, IntPtr properties, uint controlCode);

    [DllImport("advapi32.dll")]
    private static extern uint EnableTraceEx2(ulong traceHandle, ref Guid providerId, uint controlCode, byte level, ulong matchAnyKeyword, ulong matchAllKeyword, uint timeout, IntPtr enableParameters);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode)]
    private static extern ulong OpenTraceW(IntPtr logFile);

    [DllImport("advapi32.dll")]
    private static extern uint ProcessTrace(ulong[] handleArray, uint handleCount, IntPtr startTime, IntPtr endTime);

    [DllImport("advapi32.dll")]
    private static extern uint CloseTrace(ulong traceHandle);
}
