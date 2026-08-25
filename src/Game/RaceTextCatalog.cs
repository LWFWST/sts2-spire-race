using System.Reflection;
using System.Text.Json;
using MegaCrit.Sts2.Core.Localization;

namespace Sts2SpireRace.Game;

public static class RaceTextCatalog
{
    private static readonly IReadOnlyDictionary<string, string> English = Load("eng");
    private static readonly IReadOnlyDictionary<string, string> Chinese = Load("zhs");

    public static string CurrentLanguage
    {
        get
        {
            try { return LocManager.Instance?.Language ?? "eng"; }
            catch { return "eng"; }
        }
    }

    public static string Get(string key)
    {
        var table = CurrentLanguage == "zhs" ? Chinese : English;
        return table.TryGetValue(key, out var value)
            ? value
            : English.TryGetValue(key, out value) ? value : key;
    }

    public static string Format(string key, params object[] args) =>
        string.Format(LocManager.Instance?.CultureInfo ?? System.Globalization.CultureInfo.InvariantCulture, Get(key), args);

    private static IReadOnlyDictionary<string, string> Load(string language)
    {
        var assembly = typeof(RaceTextCatalog).Assembly;
        var suffix = $".localization.{language}.json";
        var resource = assembly.GetManifestResourceNames().Single(name => name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? new Dictionary<string, string>();
    }
}
