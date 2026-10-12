using System.Globalization;
using System.Text.Json;

namespace Govori;

public static class Localization
{
    private static readonly CultureInfo SystemCulture = CultureInfo.CurrentUICulture;
    private static readonly Dictionary<string, Dictionary<string, string>> catalogs = new()
    {
        ["en"] = Load("en"), ["ru"] = Load("ru")
    };
    public static string Language { get; private set; } = ResolveLanguage("system");
    public static CultureInfo Culture { get; private set; } = SystemCulture;
    public static event Action? Changed;
    public static bool IsSupported(string? preference) => preference is "system" or "en" or "ru";
    public static string ResolveLanguage(string preference, CultureInfo? systemCulture = null)
    {
        var language = preference == "system" ? (systemCulture ?? SystemCulture).TwoLetterISOLanguageName : preference;
        return language == "ru" ? "ru" : "en";
    }
    public static void Apply(string preference)
    {
        if (!IsSupported(preference)) throw new FormatException(T("InvalidLanguage"));
        Language = ResolveLanguage(preference);
        Culture = preference == "system" ? SystemCulture : CultureInfo.GetCultureInfo(Language);
        CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(Language);
        CultureInfo.DefaultThreadCurrentUICulture = CultureInfo.CurrentUICulture;
        Changed?.Invoke();
    }
    public static string T(string key, params object[] arguments)
    {
        var text = catalogs[Language].GetValueOrDefault(key) ?? catalogs["en"].GetValueOrDefault(key) ?? key;
        return arguments.Length == 0 ? text : string.Format(Culture, text, arguments);
    }
    // Migrate display strings from earlier releases to stable status codes.
    public static string NormalizeStatus(string value)
    {
        foreach (var catalog in catalogs.Values)
            foreach (var pair in catalog.Where(p => p.Key.StartsWith("History.", StringComparison.Ordinal)))
                if (pair.Value == value) return pair.Key[8..];
        return value;
    }
    public static string TranslateKnown(string value)
    {
        foreach (var catalog in catalogs.Values)
            foreach (var pair in catalog)
                if (pair.Value == value) return T(pair.Key);
        return value;
    }
    internal static IReadOnlyDictionary<string, string> Catalog(string language) => catalogs[language];
    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(Localization).Assembly.GetManifestResourceStream($"Govori.Localization.{language}.json")
            ?? throw new InvalidOperationException($"Missing language resource: {language}");
        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!;
    }
}
