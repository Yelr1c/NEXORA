using Nexora.Core.Platform;

namespace Nexora.Core.Optimization.Optimizations;

public enum StartupSource
{
    UserRun,
    MachineRun,
    MachineRun32,
    UserStartupFolder,
    CommonStartupFolder,
}

public sealed record StartupEntry(
    string Id,
    string Name,
    string Command,
    StartupSource Source,
    bool IsEnabled,
    bool RequiresAdministrator,
    bool IsProtected,
    string? ProtectionReason)
{
    public string SourceDescription => Source switch
    {
        StartupSource.UserRun => "Registry (current user)",
        StartupSource.MachineRun => "Registry (all users)",
        StartupSource.MachineRun32 => "Registry (all users, 32-bit)",
        StartupSource.UserStartupFolder => "Startup folder (current user)",
        StartupSource.CommonStartupFolder => "Startup folder (all users)",
        _ => Source.ToString(),
    };
}

/// <summary>
/// Enumerates startup applications exactly like Task Manager does and toggles them via the
/// documented StartupApproved mechanism. The Run entries themselves are never deleted.
/// </summary>
public sealed class StartupAppsService(IRegistryAccessor registry, string? userStartupFolder, string? commonStartupFolder)
{
    internal const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    internal const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    internal const string ApprovedRoot = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";

    private static readonly string[] ProtectedKeywords =
    [
        "securityhealth", "windowsdefender", "windows defender", "msmpeng", "msascui", "smartscreen",
        "avast", "avg", "norton", "mcafee", "bitdefender", "kaspersky", "eset", "malwarebytes", "sophos",
        "trend micro", "trendmicro", "webroot", "crowdstrike", "sentinel", "f-secure", "panda", "gdata", "g data",
    ];

    public IReadOnlyList<StartupEntry> GetEntries()
    {
        var entries = new List<StartupEntry>();
        AddRegistryEntries(entries, RegistryRoot.CurrentUser, RunKey, StartupSource.UserRun, "Run");
        AddRegistryEntries(entries, RegistryRoot.LocalMachine, RunKey, StartupSource.MachineRun, "Run");
        AddRegistryEntries(entries, RegistryRoot.LocalMachine, Run32Key, StartupSource.MachineRun32, "Run32");
        AddFolderEntries(entries, userStartupFolder, RegistryRoot.CurrentUser, StartupSource.UserStartupFolder);
        AddFolderEntries(entries, commonStartupFolder, RegistryRoot.LocalMachine, StartupSource.CommonStartupFolder);
        return entries.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public static (RegistryRoot Root, string KeyPath) ApprovedLocation(StartupSource source) => source switch
    {
        StartupSource.UserRun => (RegistryRoot.CurrentUser, ApprovedRoot + @"\Run"),
        StartupSource.MachineRun => (RegistryRoot.LocalMachine, ApprovedRoot + @"\Run"),
        StartupSource.MachineRun32 => (RegistryRoot.LocalMachine, ApprovedRoot + @"\Run32"),
        StartupSource.UserStartupFolder => (RegistryRoot.CurrentUser, ApprovedRoot + @"\StartupFolder"),
        StartupSource.CommonStartupFolder => (RegistryRoot.LocalMachine, ApprovedRoot + @"\StartupFolder"),
        _ => throw new ArgumentOutOfRangeException(nameof(source)),
    };

    /// <summary>StartupApproved data: first byte with the low bit set means disabled; absent means enabled.</summary>
    public static bool IsEnabledFromApproved(RegistryValue? approved)
    {
        if (approved is null || approved.Type != RegistryValueType.Binary)
        {
            return true;
        }

        var bytes = approved.AsBinary();
        return bytes.Length == 0 || (bytes[0] & 0x01) == 0;
    }

    /// <summary>Builds the 12-byte "disabled" record (0x03 + reserved + FILETIME of the change) used by Task Manager.</summary>
    public static RegistryValue CreateDisabledValue(DateTimeOffset when)
    {
        var data = new byte[12];
        data[0] = 0x03;
        BitConverter.TryWriteBytes(data.AsSpan(4), when.UtcDateTime.ToFileTimeUtc());
        return RegistryValue.FromBinary(data);
    }

    public static string CreateId(StartupSource source, string valueName) => $"startup:{source}:{valueName}";

    public static bool TryParseId(string id, out StartupSource source, out string valueName)
    {
        source = default;
        valueName = string.Empty;
        if (!id.StartsWith("startup:", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = id["startup:".Length..];
        var colon = rest.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0 || !Enum.TryParse(rest[..colon], out source))
        {
            return false;
        }

        valueName = rest[(colon + 1)..];
        return valueName.Length > 0;
    }

    public static (bool IsProtected, string? Reason) EvaluateProtection(string name, string command)
    {
        var haystack = (name + " " + command).ToLowerInvariant();
        foreach (var keyword in ProtectedKeywords)
        {
            if (haystack.Contains(keyword, StringComparison.Ordinal))
            {
                return (true, "Security software is never disabled by NEXORA.");
            }
        }

        var windir = Environment.GetEnvironmentVariable("WINDIR");
        if (!string.IsNullOrEmpty(windir) && haystack.Contains(windir.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return (true, "Windows component — protected.");
        }

        if (haystack.Contains(@"\windows\system32\", StringComparison.Ordinal) || haystack.Contains("ctfmon", StringComparison.Ordinal))
        {
            return (true, "Windows component — protected.");
        }

        return (false, null);
    }

    private void AddRegistryEntries(List<StartupEntry> entries, RegistryRoot root, string runKey, StartupSource source, string approvedSubKey)
    {
        IReadOnlyList<string> names;
        try
        {
            names = registry.GetValueNames(root, runKey);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return;
        }

        var (approvedRoot, approvedKey) = (root, ApprovedRoot + "\\" + approvedSubKey);
        foreach (var name in names)
        {
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var command = registry.GetValue(root, runKey, name);
            if (command is null || command.Type is not (RegistryValueType.String or RegistryValueType.ExpandString))
            {
                continue;
            }

            var enabled = IsEnabledFromApproved(registry.GetValue(approvedRoot, approvedKey, name));
            var (isProtected, reason) = EvaluateProtection(name, command.AsString());
            entries.Add(new StartupEntry(CreateId(source, name), name, command.AsString(), source, enabled, root != RegistryRoot.CurrentUser, isProtected, reason));
        }
    }

    private void AddFolderEntries(List<StartupEntry> entries, string? folder, RegistryRoot approvedRoot, StartupSource source)
    {
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
        {
            return;
        }

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(folder).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var (_, approvedKey) = ApprovedLocation(source);
        foreach (var file in files)
        {
            var fileName = Path.GetFileName(file);
            if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var enabled = IsEnabledFromApproved(registry.GetValue(approvedRoot, approvedKey, fileName));
            var (isProtected, reason) = EvaluateProtection(fileName, file);
            entries.Add(new StartupEntry(CreateId(source, fileName), Path.GetFileNameWithoutExtension(fileName), file, source, enabled, approvedRoot != RegistryRoot.CurrentUser, isProtected, reason));
        }
    }
}

/// <summary>Disables one startup application via StartupApproved (exactly what Task Manager's "Disable" does).</summary>
public sealed class StartupEntryOptimization : OptimizationBase
{
    private readonly IRegistryAccessor _registry;
    private readonly StartupAppsService _service;
    private readonly TimeProvider _time;

    public StartupEntryOptimization(IRegistryAccessor registry, StartupAppsService service, StartupEntry entry, TimeProvider? time = null)
    {
        _registry = registry;
        _service = service;
        Entry = entry;
        _time = time ?? TimeProvider.System;
    }

    public StartupEntry Entry { get; }

    public override string Id => Entry.Id;

    public override string Name => $"Disable startup app: {Entry.Name}";

    public override string Description => $"Stop \"{Entry.Name}\" from starting automatically when you sign in.";

    public override string WhatItChanges => $"Marks the startup entry as disabled in Windows' StartupApproved list ({Entry.SourceDescription}). The application itself and its startup entry are not deleted; Task Manager shows it as Disabled.";

    public override string WhyItHelps => "Fewer applications launching at sign-in means a faster startup and fewer background processes competing for CPU, memory and network while you play.";

    public override string ExpectedBenefit => "Faster sign-in and less background resource usage. The app still works when you open it manually.";

    public override OptimizationCategory Category => OptimizationCategory.BackgroundProcesses;

    public override bool RequiresAdministrator => Entry.RequiresAdministrator;

    public override ImpactLevel Impact => ImpactLevel.Low;

    public override bool IsQuickOptimizeCandidate => false;

    public override Task<OptimizationState> GetStateAsync(CancellationToken cancellationToken = default)
    {
        if (Entry.IsProtected)
        {
            return Task.FromResult(OptimizationState.Unavailable(Entry.ProtectionReason ?? "Protected."));
        }

        var current = _service.GetEntries().FirstOrDefault(e => e.Id == Entry.Id);
        if (current is null)
        {
            return Task.FromResult(OptimizationState.Unavailable("This startup entry no longer exists."));
        }

        return Task.FromResult(new OptimizationState(current.IsEnabled ? OptimizationStatus.NotApplied : OptimizationStatus.Applied, current.IsEnabled ? "Enabled" : "Disabled"));
    }

    public override Task<OptimizationBackup> CreateBackupAsync(CancellationToken cancellationToken = default)
    {
        var backup = NewBackup();
        var (root, key) = StartupAppsService.ApprovedLocation(Entry.Source);
        backup.Registry.Add(RegistrySnapshot.Capture(_registry, root, key, ValueName));
        return Task.FromResult(backup);
    }

    public override Task ApplyAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        if (Entry.IsProtected)
        {
            throw new InvalidOperationException("Protected startup entries cannot be disabled.");
        }

        var (root, key) = StartupAppsService.ApprovedLocation(Entry.Source);
        _registry.SetValue(root, key, ValueName, StartupAppsService.CreateDisabledValue(_time.GetUtcNow()));
        return Task.CompletedTask;
    }

    public override Task RestoreAsync(OptimizationBackup backup, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(backup);
        foreach (var snapshot in backup.Registry)
        {
            snapshot.Restore(_registry);
        }

        return Task.CompletedTask;
    }

    private string ValueName => StartupAppsService.TryParseId(Entry.Id, out _, out var name) ? name : Entry.Name;
}
