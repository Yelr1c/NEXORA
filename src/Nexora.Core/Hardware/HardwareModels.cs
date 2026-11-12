namespace Nexora.Core.Hardware;

public sealed record GpuInfo(string Name, long DedicatedMemoryBytes, uint VendorId, bool IsSoftware)
{
    public string VendorName => VendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x5143 => "Qualcomm",
        0x1414 => "Microsoft",
        _ => "Unknown",
    };
}

public sealed record SystemInfo(
    string OsName,
    string OsVersion,
    int OsBuild,
    string CpuName,
    int LogicalProcessors,
    double? CpuBaseMhz,
    long TotalMemoryBytes,
    IReadOnlyList<GpuInfo> Gpus,
    string ComputerModel)
{
    public GpuInfo? PrimaryGpu => Gpus.Where(g => !g.IsSoftware).OrderByDescending(g => g.DedicatedMemoryBytes).FirstOrDefault();
}

public interface ISystemInfoProvider
{
    SystemInfo GetSystemInfo();
}

public sealed record DriveSnapshot(string Name, string Label, long TotalBytes, long FreeBytes, bool IsSystemDrive)
{
    public double FreePercent => TotalBytes > 0 ? 100.0 * FreeBytes / TotalBytes : 0;
}

public static class StorageInfo
{
    public static IReadOnlyList<DriveSnapshot> GetFixedDrives()
    {
        var systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.System)) ?? string.Empty;
        var list = new List<DriveSnapshot>();
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady)
                {
                    continue;
                }

                list.Add(new DriveSnapshot(drive.Name, string.IsNullOrWhiteSpace(drive.VolumeLabel) ? "Local Disk" : drive.VolumeLabel,
                    drive.TotalSize, drive.AvailableFreeSpace, string.Equals(drive.Name, systemRoot, StringComparison.OrdinalIgnoreCase)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Drive became unavailable while enumerating.
            }
        }

        return list;
    }
}
