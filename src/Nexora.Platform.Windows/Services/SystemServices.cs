using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Nexora.Core.Hardware;
using Nexora.Core.Optimization;
using Nexora.Core.Platform;
using Nexora.Platform.Windows.Interop;

namespace Nexora.Platform.Windows.Services;

public sealed class WindowsPrivilegeService : IPrivilegeService
{
    private readonly Lazy<bool> _elevated = new(() =>
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    });

    public bool IsElevated => _elevated.Value;
}

/// <summary>Relaunches NEXORA elevated (UAC prompt) only when the user explicitly asks for it.</summary>
public static class ElevationHelper
{
    public static bool TryRestartElevated(string arguments = "")
    {
        var path = Environment.ProcessPath;
        if (IsPackaged())
        {
            // Files inside the package folder cannot be launched directly; use the app execution alias.
            var alias = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "nexora.exe");
            if (File.Exists(alias))
            {
                path = alias;
            }
        }

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(path)
            {
                UseShellExecute = true,
                Verb = "runas",
                Arguments = arguments,
            });
            return true;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled the UAC prompt.
            return false;
        }
    }

    public static bool IsPackaged()
    {
        try
        {
            var length = 0;
            // APPMODEL_ERROR_NO_PACKAGE = 15700
            return NativeMethods.GetCurrentPackageFullName(ref length, null) != 15700;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
    }
}

public sealed class WindowsOsInfo : IOsInfo
{
    public WindowsOsInfo()
    {
        var info = new NativeMethods.OSVERSIONINFOEX { dwOSVersionInfoSize = Marshal.SizeOf<NativeMethods.OSVERSIONINFOEX>(), szCSDVersion = string.Empty };
        Build = NativeMethods.RtlGetVersion(ref info) == 0 ? info.dwBuildNumber : Environment.OSVersion.Version.Build;

        using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
        DisplayVersion = key?.GetValue("DisplayVersion") as string ?? key?.GetValue("ReleaseId") as string ?? string.Empty;
        var edition = key?.GetValue("EditionID") as string ?? string.Empty;
        var family = Build >= 22000 ? "Windows 11" : "Windows 10";
        ProductName = $"{family} {EditionName(edition)}".Trim();
    }

    public int Build { get; }

    public string ProductName { get; }

    public string DisplayVersion { get; }

    private static string EditionName(string edition) => edition switch
    {
        "Professional" => "Pro",
        "ProfessionalWorkstation" => "Pro for Workstations",
        "ProfessionalEducation" => "Pro Education",
        "Core" => "Home",
        "CoreSingleLanguage" => "Home Single Language",
        "Enterprise" => "Enterprise",
        "Education" => "Education",
        "" => string.Empty,
        _ => edition,
    };
}

public sealed class WindowsSystemParameters : ISystemParameters
{
    private const uint Persist = NativeMethods.SPIF_UPDATEINIFILE | NativeMethods.SPIF_SENDCHANGE;

    public bool GetClientAreaAnimation()
    {
        var value = 0;
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETCLIENTAREAANIMATION, 0, ref value, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return value != 0;
    }

    public void SetClientAreaAnimation(bool enabled)
    {
        // For SPI_SETCLIENTAREAANIMATION the BOOL is passed directly as pvParam.
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SPI_SETCLIENTAREAANIMATION, 0, enabled ? 1 : IntPtr.Zero, Persist))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }

    public MouseSettings GetMouseSettings()
    {
        var values = new int[3];
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SPI_GETMOUSE, 0, values, 0))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }

        return new MouseSettings(values[0], values[1], values[2]);
    }

    public void SetMouseSettings(MouseSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var values = new[] { settings.Threshold1, settings.Threshold2, settings.Acceleration };
        if (!NativeMethods.SystemParametersInfoW(NativeMethods.SPI_SETMOUSE, 0, values, Persist))
        {
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
    }
}

/// <summary>Power plan management via the documented powrprof API (no powercfg.exe invocation).</summary>
public sealed class WindowsPowerManager(ILogger<WindowsPowerManager> logger) : IPowerManager
{
    private const uint ACCESS_SCHEME = 16;

    public Guid? GetActiveScheme()
    {
        if (PowerGetActiveScheme(IntPtr.Zero, out var pointer) != 0 || pointer == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            return Marshal.PtrToStructure<Guid>(pointer);
        }
        finally
        {
            LocalFree(pointer);
        }
    }

    public IReadOnlyList<PowerScheme> GetSchemes()
    {
        var active = GetActiveScheme();
        var result = new List<PowerScheme>();
        var buffer = new byte[16];
        for (uint index = 0; index < 64; index++)
        {
            var size = (uint)buffer.Length;
            if (PowerEnumerate(IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, ACCESS_SCHEME, index, buffer, ref size) != 0)
            {
                break;
            }

            var id = new Guid(buffer);
            result.Add(new PowerScheme(id, ReadName(id), id == active));
        }

        return result;
    }

    public void SetActiveScheme(Guid scheme)
    {
        var status = PowerSetActiveScheme(IntPtr.Zero, ref scheme);
        if (status != 0)
        {
            throw new System.ComponentModel.Win32Exception((int)status, "The power plan could not be activated.");
        }
    }

    public bool IsOverlaySupported => GetActiveOverlay() is not null;

    public Guid? GetActiveOverlay()
    {
        try
        {
            return PowerGetEffectiveOverlayScheme(out var overlay) == 0 ? overlay : null;
        }
        catch (EntryPointNotFoundException)
        {
            return null;
        }
    }

    public void SetActiveOverlay(Guid overlay)
    {
        try
        {
            var status = PowerSetActiveOverlayScheme(overlay);
            if (status != 0)
            {
                throw new System.ComponentModel.Win32Exception((int)status, "The power mode could not be changed.");
            }
        }
        catch (EntryPointNotFoundException ex)
        {
            logger.LogWarning(ex, "Power mode overlays are not supported");
            throw new NotSupportedException("Power modes are not supported on this version of Windows.", ex);
        }
    }

    public (bool HasBattery, bool OnBattery)? GetBatteryStatus()
    {
        if (!GetSystemPowerStatus(out var status))
        {
            return null;
        }

        // BatteryFlag 128 = no system battery, 255 = unknown. ACLineStatus 0 = offline (on battery).
        if (status.BatteryFlag == 255)
        {
            return null;
        }

        var hasBattery = (status.BatteryFlag & 128) == 0;
        return (hasBattery, hasBattery && status.ACLineStatus == 0);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SYSTEM_POWER_STATUS
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public uint BatteryLifeTime;
        public uint BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SYSTEM_POWER_STATUS status);

    private static string ReadName(Guid scheme)
    {
        uint size = 0;
        _ = PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, null, ref size);
        if (size == 0)
        {
            return scheme.ToString();
        }

        var buffer = new byte[size];
        return PowerReadFriendlyName(IntPtr.Zero, ref scheme, IntPtr.Zero, IntPtr.Zero, buffer, ref size) == 0
            ? System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0')
            : scheme.ToString();
    }

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetActiveScheme(IntPtr userRootPowerKey, out IntPtr activePolicyGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveScheme(IntPtr userRootPowerKey, ref Guid schemeGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerEnumerate(IntPtr rootPowerKey, IntPtr schemeGuid, IntPtr subGroupOfPowerSettingsGuid, uint accessFlags, uint index, byte[] buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerReadFriendlyName(IntPtr rootPowerKey, ref Guid schemeGuid, IntPtr subGroup, IntPtr powerSetting, byte[]? buffer, ref uint bufferSize);

    [DllImport("powrprof.dll")]
    private static extern uint PowerGetEffectiveOverlayScheme(out Guid effectiveOverlayGuid);

    [DllImport("powrprof.dll")]
    private static extern uint PowerSetActiveOverlayScheme(Guid overlaySchemeGuid);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

public sealed class WindowsGraphicsCapabilities : IGraphicsCapabilities
{
    private readonly Lazy<bool?> _hags = new(() =>
    {
        var adapter = GpuInterop.EnumerateAdapters().Where(a => !a.Info.IsSoftware).OrderByDescending(a => a.Info.DedicatedMemoryBytes).FirstOrDefault();
        return adapter is null ? false : GpuInterop.IsHardwareSchedulingSupported(adapter.Luid);
    });

    public bool? IsHardwareSchedulingSupported => _hags.Value;
}

public sealed class WindowsSystemInfoProvider(IOsInfo os) : ISystemInfoProvider
{
    private SystemInfo? _cached;

    public SystemInfo GetSystemInfo()
    {
        if (_cached is not null)
        {
            return _cached;
        }

        using var cpuKey = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
        var cpuName = (cpuKey?.GetValue("ProcessorNameString") as string)?.Trim() ?? "Unknown processor";
        double? mhz = cpuKey?.GetValue("~MHz") is int m && m > 0 ? m : null;

        using var bios = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
        var model = $"{bios?.GetValue("SystemManufacturer") as string} {bios?.GetValue("SystemProductName") as string}".Trim();

        var memory = new NativeMethods.MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<NativeMethods.MEMORYSTATUSEX>() };
        var totalMemory = NativeMethods.GlobalMemoryStatusEx(ref memory) ? (long)memory.ullTotalPhys : 0;

        var gpus = GpuInterop.EnumerateAdapters().Select(a => a.Info)
            .GroupBy(g => g.Name).Select(g => g.First()).ToList();

        _cached = new SystemInfo(os.ProductName, os.DisplayVersion, os.Build, cpuName, Environment.ProcessorCount, mhz, totalMemory, gpus,
            string.IsNullOrWhiteSpace(model) ? "PC" : model);
        return _cached;
    }
}
