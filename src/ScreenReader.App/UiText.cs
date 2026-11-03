using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace ScreenReader.App;

public static class UiText
{
    private static readonly Dictionary<string,Dictionary<string,string>> strings=Load();
    public static IReadOnlyList<UiLanguageChoice> Languages { get; }=Array.AsReadOnly<UiLanguageChoice>(
    [
        new("ru","Русский"),new("en","English"),new("zh","简体中文"),new("ja","日本語"),
        new("es","Español"),new("ko","한국어"),new("fr","Français"),new("de","Deutsch")
    ]);
    public static string Language { get; private set; }="ru";
    public static bool IsEnglish=>Language=="en";
    public static IReadOnlyCollection<string> Keys=>strings.Keys;
    public static event Action? LanguageChanged;
    private static Dictionary<string,Dictionary<string,string>> Load()
    {
        var assembly=Assembly.GetExecutingAssembly();
        string name=assembly.GetManifestResourceNames().Single(n=>n.EndsWith("Localization.strings.json",StringComparison.Ordinal));
        using var stream=assembly.GetManifestResourceStream(name)!;
        var catalog=JsonSerializer.Deserialize<Dictionary<string,Dictionary<string,string>>>(stream)!;
        foreach (string locale in new[] { "zh","ja","es","ko","fr","de" })
        {
            string resource=assembly.GetManifestResourceNames().Single(n=>n.EndsWith($"Localization.strings.{locale}.json",StringComparison.Ordinal));
            using var localized=assembly.GetManifestResourceStream(resource)!;
            foreach (var entry in JsonSerializer.Deserialize<Dictionary<string,string>>(localized)!)
                catalog[entry.Key][locale]=entry.Value;
        }
        return catalog;
    }
    public static bool SupportsLanguage(string? language)=>Languages.Any(l=>l.Id.Equals(language,StringComparison.OrdinalIgnoreCase));
    public static bool HasTranslation(string key,string language)=>strings.TryGetValue(key,out var pair)
        && pair.TryGetValue(language,out var text) && !string.IsNullOrWhiteSpace(text);
    public static string Get(string key,string language)=>strings.TryGetValue(key,out var pair)
        ? pair.GetValueOrDefault(language,pair.GetValueOrDefault("en",key)) : key;
    public static string T(string key,params object[] args)
    {
        string value=Get(key,Language);
        return args.Length==0 ? value : string.Format(CultureInfo.CurrentUICulture,value,args);
    }
    public static void SetLanguage(string language)
    {
        Language=SupportsLanguage(language) ? language.ToLowerInvariant() : "ru";
        CultureInfo.CurrentUICulture=CultureInfo.GetCultureInfo(Language=="zh" ? "zh-CN" : Language);
        CultureInfo.DefaultThreadCurrentUICulture=CultureInfo.CurrentUICulture;
        SpeechText.UseEnglish=Language!="ru";
        SpeechText.Translator=(key,args)=>T(key,args);
        ApplyResources(); LanguageChanged?.Invoke();
    }
    public static void ApplyResources()
    {
        if (Application.Current is not { } app) return;
        foreach (string key in strings.Keys) app.Resources[key]=T(key);
    }
}
