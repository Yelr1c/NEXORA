using Microsoft.Extensions.Logging;
using Microsoft.Win32;
using Nexora.Core.Games;

namespace Nexora.Platform.Windows.Games;

/// <summary>Finds installed games from launcher metadata. No paths are hard-coded to a specific PC.</summary>
public sealed class WindowsGameDetector(ILogger<WindowsGameDetector> logger) : IGameDetector
{
    private static readonly string[] ExcludedExeNames =
    [
        "unins", "setup", "install", "crash", "report", "launcher_helper", "vcredist", "dxsetup", "dotnet", "ue4prereq",
        "easyanticheat", "battleye", "beservice", "redist", "update", "cefprocess", "helper",
    ];

    public IReadOnlyList<DetectedGame> Detect()
    {
        var games = new List<DetectedGame>();
        Run(() => DetectSteam(games), "Steam");
        Run(() => DetectEpic(games), "Epic");
        Run(() => DetectGog(games), "GOG");
        Run(() => DetectRiot(games), "Riot");
        Run(() => DetectMinecraft(games), "Minecraft");
        return games.GroupBy(g => (g.Source, g.StoreId ?? g.ExecutablePath ?? g.Name)).Select(g => g.First()).ToList();
    }

    private void Run(Action action, string source)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException or FormatException)
        {
            logger.LogWarning(ex, "Game detection for {Source} failed", source);
        }
    }

    private static void DetectSteam(List<DetectedGame> games)
    {
        using var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
        var steamPath = (key?.GetValue("SteamPath") as string)?.Replace('/', '\\');
        if (string.IsNullOrEmpty(steamPath) || !Directory.Exists(steamPath))
        {
            return;
        }

        var libraries = new List<string> { steamPath };
        var vdf = Path.Combine(steamPath, "steamapps", "libraryfolders.vdf");
        if (File.Exists(vdf))
        {
            libraries.AddRange(SteamLibrary.ParseLibraryFolders(File.ReadAllText(vdf)));
        }

        foreach (var library in libraries.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var apps = Path.Combine(library, "steamapps");
            if (!Directory.Exists(apps))
            {
                continue;
            }

            foreach (var manifestPath in Directory.EnumerateFiles(apps, "appmanifest_*.acf"))
            {
                var manifest = SteamLibrary.ParseAppManifest(File.ReadAllText(manifestPath));
                if (manifest is null || !manifest.FullyInstalled || !SteamLibrary.IsGame(manifest))
                {
                    continue;
                }

                var dir = Path.Combine(apps, "common", manifest.InstallDir);
                if (!Directory.Exists(dir))
                {
                    continue;
                }

                games.Add(new DetectedGame(manifest.Name, GameSource.Steam, GuessExecutable(dir, manifest.Name), dir, manifest.AppId, SteamLibrary.LaunchUri(manifest.AppId), null));
            }
        }
    }

    private static void DetectEpic(List<DetectedGame> games)
    {
        var manifests = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Epic", "EpicGamesLauncher", "Data", "Manifests");
        if (!Directory.Exists(manifests))
        {
            return;
        }

        foreach (var file in Directory.EnumerateFiles(manifests, "*.item"))
        {
            var item = EpicManifest.Parse(File.ReadAllText(file));
            if (item is null || !item.IsGame || !Directory.Exists(item.InstallLocation))
            {
                continue;
            }

            var exe = Path.GetFullPath(Path.Combine(item.InstallLocation, item.LaunchExecutable));
            var uri = string.IsNullOrEmpty(item.AppName) ? null : $"com.epicgames.launcher://apps/{Uri.EscapeDataString(item.AppName)}?action=launch&silent=true";
            games.Add(new DetectedGame(item.DisplayName, GameSource.Epic, File.Exists(exe) ? exe : null, item.InstallLocation, item.AppName, uri, null));
        }
    }

    private static void DetectGog(List<DetectedGame> games)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry32).OpenSubKey(@"SOFTWARE\GOG.com\Games");
        if (root is null)
        {
            return;
        }

        foreach (var id in root.GetSubKeyNames())
        {
            using var game = root.OpenSubKey(id);
            var name = game?.GetValue("gameName") as string;
            var exe = game?.GetValue("exe") as string;
            var path = game?.GetValue("path") as string;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                continue;
            }

            games.Add(new DetectedGame(name, GameSource.Gog, exe, path ?? Path.GetDirectoryName(exe)!, id, null, game?.GetValue("launchParam") as string));
        }
    }

    private static void DetectRiot(List<DetectedGame> games)
    {
        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        var installs = Path.Combine(programData, "Riot Games", "RiotClientInstalls.json");
        var client = File.Exists(installs) ? RiotManifest.ParseClientPath(File.ReadAllText(installs)) : null;
        if (client is null || !File.Exists(client))
        {
            return;
        }

        var metadata = Path.Combine(programData, "Riot Games", "Metadata");
        if (!Directory.Exists(metadata))
        {
            return;
        }

        foreach (var product in Directory.EnumerateDirectories(metadata))
        {
            var productId = Path.GetFileName(product); // e.g. valorant.live
            var settings = Directory.EnumerateFiles(product, "*.product_settings.yaml").FirstOrDefault();
            if (settings is null || RiotManifest.ParseInstallPath(File.ReadAllText(settings)) is not { } installPath || !Directory.Exists(installPath))
            {
                continue;
            }

            var parts = productId.Split('.');
            if (parts.Length != 2 || parts[0] == "riot_client")
            {
                continue;
            }

            var name = parts[0] switch
            {
                "valorant" => "VALORANT",
                "league_of_legends" => "League of Legends",
                "bacon" => "Legends of Runeterra",
                _ => System.Globalization.CultureInfo.CurrentCulture.TextInfo.ToTitleCase(parts[0].Replace('_', ' ')),
            };
            games.Add(new DetectedGame(name, GameSource.Riot, client, installPath, productId, null, $"--launch-product={parts[0]} --launch-patchline={parts[1]}"));
        }
    }

    private static void DetectMinecraft(List<DetectedGame> games)
    {
        foreach (var folder in new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles })
        {
            var exe = Path.Combine(Environment.GetFolderPath(folder), "Minecraft Launcher", "MinecraftLauncher.exe");
            if (File.Exists(exe))
            {
                games.Add(new DetectedGame("Minecraft", GameSource.Other, exe, Path.GetDirectoryName(exe)!, "minecraft-launcher", null, null));
                return;
            }
        }
    }

    /// <summary>Picks the most likely game executable in an install folder (largest non-helper .exe near the root).</summary>
    internal static string? GuessExecutable(string directory, string gameName)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 3, IgnoreInaccessible = true };
        var candidates = new DirectoryInfo(directory).EnumerateFiles("*.exe", options)
            .Where(f => !ExcludedExeNames.Any(x => f.Name.Contains(x, StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }

        var compactName = new string(gameName.Where(char.IsLetterOrDigit).ToArray());
        var named = candidates.FirstOrDefault(f => new string(Path.GetFileNameWithoutExtension(f.Name).Where(char.IsLetterOrDigit).ToArray())
            .Equals(compactName, StringComparison.OrdinalIgnoreCase));
        return (named ?? candidates.OrderByDescending(f => f.Length).First()).FullName;
    }
}
