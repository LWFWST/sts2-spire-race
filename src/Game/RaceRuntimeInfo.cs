using System.Text.Json;
using MegaCrit.Sts2.Core.Debug;

namespace Sts2SpireRace.Game;

public static class RaceRuntimeInfo
{
    public const string OfficialServerUrl = "https://spirerace.xyz/";
    public const string DefaultServerUrl = OfficialServerUrl;

    public static string GameVersion
    {
        get
        {
            var version = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "0.111.0";
            return version.StartsWith('v') ? version : "v" + version;
        }
    }

    public static Uri ServerUri
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SPIRE_RACE_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(configured))
                return new Uri(configured.TrimEnd('/') + "/");
            var saved = LoadServerUrl();
            return new Uri(string.IsNullOrWhiteSpace(saved) ? DefaultServerUrl : saved.TrimEnd('/') + "/");
        }
    }

    public static void SaveServerUrl(string url)
    {
        try
        {
            var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_server.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(new ServerSettings(url?.Trim() ?? string.Empty)));
        }
        catch
        {
            // Best-effort persistence; the environment variable still takes priority next launch.
        }
    }

    public static string LoadServerUrl()
    {
        try
        {
            var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_server.json");
            if (!File.Exists(path))
                return string.Empty;
            var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path));
            return settings?.ServerUrl?.Trim() ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record ServerSettings(string ServerUrl);

    public static bool DevelopmentAuthentication =>
        Godot.OS.GetCmdlineArgs().Contains("--spire-race-dev-auth");

    public static ulong? DevelopmentPlatformId =>
        TryGetArgument("--spire-race-dev-id=", out var value) && ulong.TryParse(value, out var id) ? id : null;

    public static string? DevelopmentDisplayName =>
        TryGetArgument("--spire-race-dev-name=", out var value) ? Uri.UnescapeDataString(value) : null;

    public static bool DemoMode =>
        Godot.OS.GetCmdlineArgs().Contains("--spire-race-demo");

    private static bool TryGetArgument(string prefix, out string value)
    {
        var argument = Godot.OS.GetCmdlineArgs().FirstOrDefault(x => x.StartsWith(prefix, StringComparison.Ordinal));
        value = argument is null ? string.Empty : argument[prefix.Length..];
        return argument is not null && value.Length > 0;
    }
}
