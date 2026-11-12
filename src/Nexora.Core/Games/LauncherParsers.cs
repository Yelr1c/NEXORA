using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Nexora.Core.Games;

/// <summary>Minimal parser for Valve KeyValues (VDF/ACF) text files.</summary>
public static class VdfParser
{
    public sealed class Node
    {
        public Dictionary<string, string> Values { get; } = new(StringComparer.OrdinalIgnoreCase);

        public Dictionary<string, Node> Children { get; } = new(StringComparer.OrdinalIgnoreCase);

        public string? Get(string key) => Values.TryGetValue(key, out var v) ? v : null;

        public Node? Child(string key) => Children.TryGetValue(key, out var c) ? c : null;
    }

    public static Node Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var root = new Node();
        var stack = new Stack<Node>();
        stack.Push(root);
        string? pendingKey = null;
        var i = 0;
        while (i < text.Length)
        {
            var c = text[i];
            if (char.IsWhiteSpace(c))
            {
                i++;
                continue;
            }

            if (c == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                while (i < text.Length && text[i] != '\n')
                {
                    i++;
                }

                continue;
            }

            if (c == '{')
            {
                var child = new Node();
                if (pendingKey is not null)
                {
                    stack.Peek().Children[pendingKey] = child;
                }

                stack.Push(child);
                pendingKey = null;
                i++;
                continue;
            }

            if (c == '}')
            {
                if (stack.Count > 1)
                {
                    stack.Pop();
                }

                pendingKey = null;
                i++;
                continue;
            }

            var token = c == '"' ? ReadQuoted(text, ref i) : ReadBare(text, ref i);
            if (pendingKey is null)
            {
                pendingKey = token;
            }
            else
            {
                stack.Peek().Values[pendingKey] = token;
                pendingKey = null;
            }
        }

        return root;
    }

    private static string ReadQuoted(string text, ref int i)
    {
        var sb = new StringBuilder();
        i++; // opening quote
        while (i < text.Length && text[i] != '"')
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                var next = text[i + 1];
                sb.Append(next switch { 'n' => '\n', 't' => '\t', _ => next });
                i += 2;
                continue;
            }

            sb.Append(text[i++]);
        }

        i++; // closing quote
        return sb.ToString();
    }

    private static string ReadBare(string text, ref int i)
    {
        var start = i;
        while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] is not '{' and not '}' and not '"')
        {
            i++;
        }

        return text[start..i];
    }
}

public static partial class SteamLibrary
{
    /// <summary>Steam tools/runtimes that are not games.</summary>
    private static readonly HashSet<string> NonGameAppIds = ["228980", "1070560", "1391110", "1628350", "1493710", "250820", "1826330", "2180100"];

    public static IReadOnlyList<string> ParseLibraryFolders(string libraryFoldersVdf)
    {
        var root = VdfParser.Parse(libraryFoldersVdf);
        var folders = root.Child("libraryfolders") ?? root;
        var result = new List<string>();
        foreach (var child in folders.Children.Values)
        {
            if (child.Get("path") is { Length: > 0 } path)
            {
                result.Add(path);
            }
        }

        // Legacy format: "1" "D:\\SteamLibrary"
        foreach (var (key, value) in folders.Values)
        {
            if (key.All(char.IsDigit) && value.Contains('\\', StringComparison.Ordinal))
            {
                result.Add(value);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public sealed record AppManifest(string AppId, string Name, string InstallDir, bool FullyInstalled);

    public static AppManifest? ParseAppManifest(string acf)
    {
        var state = VdfParser.Parse(acf).Child("AppState");
        if (state?.Get("appid") is not { } appId || state.Get("name") is not { } name || state.Get("installdir") is not { } dir)
        {
            return null;
        }

        // StateFlags bit 4 (value 4) = fully installed.
        var installed = int.TryParse(state.Get("StateFlags"), out var flags) && (flags & 4) == 4;
        return new AppManifest(appId, name, dir, installed);
    }

    public static bool IsGame(AppManifest manifest)
        => !NonGameAppIds.Contains(manifest.AppId)
           && !manifest.Name.Contains("Redistributable", StringComparison.OrdinalIgnoreCase)
           && !manifest.Name.StartsWith("Proton", StringComparison.OrdinalIgnoreCase)
           && !manifest.Name.StartsWith("Steam Linux Runtime", StringComparison.OrdinalIgnoreCase)
           && !manifest.Name.Contains("Dedicated Server", StringComparison.OrdinalIgnoreCase);

    public static string LaunchUri(string appId) => $"steam://rungameid/{appId}";
}

public static class EpicManifest
{
    public sealed record Item(string DisplayName, string InstallLocation, string LaunchExecutable, string AppName, bool IsGame);

    public static Item? Parse(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string? Str(string name) => root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            var incomplete = root.TryGetProperty("bIsIncompleteInstall", out var inc) && inc.ValueKind == JsonValueKind.True;
            if (incomplete || Str("DisplayName") is not { } name || Str("InstallLocation") is not { } location || Str("LaunchExecutable") is not { } exe)
            {
                return null;
            }

            var isGame = true;
            if (root.TryGetProperty("AppCategories", out var categories) && categories.ValueKind == JsonValueKind.Array)
            {
                isGame = categories.EnumerateArray().Any(c => c.ValueKind == JsonValueKind.String && c.GetString() == "games");
            }

            return new Item(name, location, exe, Str("AppName") ?? string.Empty, isGame);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}

public static partial class RiotManifest
{
    /// <summary>Reads product_install_full_path from a Riot product_settings.yaml file.</summary>
    public static string? ParseInstallPath(string yaml)
    {
        var match = InstallPathRegex().Match(yaml ?? string.Empty);
        return match.Success ? match.Groups[1].Value.Trim().Replace('/', '\\') : null;
    }

    /// <summary>Reads the Riot Client path ("rc_default"/"rc_live") from RiotClientInstalls.json.</summary>
    public static string? ParseClientPath(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var key in new[] { "rc_default", "rc_live", "rc_beta" })
            {
                if (doc.RootElement.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } path)
                {
                    return path.Replace('/', '\\');
                }
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return null;
    }

    [GeneratedRegex(@"^\s*product_install_full_path:\s*""?([^""\r\n]+)""?\s*$", RegexOptions.Multiline)]
    private static partial Regex InstallPathRegex();
}
