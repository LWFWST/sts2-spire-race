using System.Text.Json;
using MegaCrit.Sts2.Core.Debug;

namespace Sts2SpireRace.Game;

public static class RaceRuntimeInfo
{
    public const string OfficialServerUrl = "https://spirerace.xyz/";
    public const string DefaultServerUrl = "";

    public static string GameVersion
    {
        get
        {
            var version = ReleaseInfoManager.Instance.ReleaseInfo?.Version ?? "0.111.0";
            return version.StartsWith('v') ? version : "v" + version;
        }
    }

    public static Uri? ServerUri
    {
        get
        {
            var configured = Environment.GetEnvironmentVariable("SPIRE_RACE_SERVER_URL");
            if (!string.IsNullOrWhiteSpace(configured))
                return new Uri(configured.TrimEnd('/') + "/");
            var saved = LoadServerUrl();
            return string.IsNullOrWhiteSpace(saved) ? null : new Uri(saved.TrimEnd('/') + "/");
        }
    }

    public static bool HasConfiguredServer => ServerUri is not null;

    public static void SaveServerUrl(string url)
    {
        try
        {
            var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_server.json");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var normalized = url?.Trim() ?? string.Empty;
            File.WriteAllText(path, JsonSerializer.Serialize(new ServerSettings(normalized, normalized.Length > 0)));
        }
        catch
        {
            // Best-effort persistence; the environment variable still takes priority next launch.
        }
    }

    public static bool IsOfficialServer(Uri? uri = null) =>
        (uri ?? ServerUri) is { } selected &&
        string.Equals(selected.Host, new Uri(OfficialServerUrl).Host, StringComparison.OrdinalIgnoreCase);

    public static string LoadServerUrl()
    {
        try
        {
            var path = Godot.ProjectSettings.GlobalizePath("user://stsrace_server.json");
            if (!File.Exists(path))
                return string.Empty;
            var settings = JsonSerializer.Deserialize<ServerSettings>(File.ReadAllText(path));
            return settings?.ConnectOnStartup == true ? settings.ServerUrl?.Trim() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private sealed record ServerSettings(string ServerUrl, bool ConnectOnStartup = false);

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
