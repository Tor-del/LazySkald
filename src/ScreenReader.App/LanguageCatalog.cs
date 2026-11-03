using System.Globalization;

namespace ScreenReader.App;

public static class LanguageCatalog
{
    private static readonly (string Tag,string Key)[] languages =
    [
        ("ru-RU","language.ru"),("en-US","language.en"),("zh-CN","language.zh"),
        ("ja-JP","language.ja"),("es-ES","language.es"),("ko-KR","language.ko"),
        ("fr-FR","language.fr"),("de-DE","language.de")
    ];

    public static IReadOnlyList<string> Tags { get; }=Array.AsReadOnly(languages.Select(l=>l.Tag).ToArray());

    public static string DisplayName(string tag)
    {
        var known=languages.FirstOrDefault(l=>l.Tag.Equals(tag,StringComparison.OrdinalIgnoreCase));
        if (known.Key is not null) return UiText.T(known.Key);
        try
        {
            var culture=CultureInfo.GetCultureInfo(tag);
            return $"{(UiText.IsEnglish ? culture.EnglishName : culture.NativeName)} ({tag})";
        }
        catch (CultureNotFoundException) { return tag; }
    }

    public static LanguageChoice[] RecognitionChoices(IReadOnlyList<(string Tag,string Name)> installed,string selected) =>
        Tags.Concat(installed.Select(l=>l.Tag)).Append(selected).Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(tag=>new LanguageChoice(tag,DisplayName(tag),installed.Any(l=>l.Tag.Equals(tag,StringComparison.OrdinalIgnoreCase)))).ToArray();

    // The online catalog replaces these defaults after a successful refresh.
    public static IReadOnlyList<SpeechVoice> EdgeVoices { get; }=Array.AsReadOnly<SpeechVoice>(
    [
        new("ru-RU-SvetlanaNeural","Светлана","ru-RU","Female"),new("ru-RU-DmitryNeural","Дмитрий","ru-RU","Male"),
        new("en-US-JennyNeural","Jenny","en-US","Female"),new("en-US-GuyNeural","Guy","en-US","Male"),
        new("zh-CN-XiaoxiaoNeural","Xiaoxiao","zh-CN","Female"),new("zh-CN-YunxiNeural","Yunxi","zh-CN","Male"),
        new("ja-JP-NanamiNeural","Nanami","ja-JP","Female"),new("ja-JP-KeitaNeural","Keita","ja-JP","Male"),
        new("es-ES-ElviraNeural","Elvira","es-ES","Female"),new("es-ES-AlvaroNeural","Alvaro","es-ES","Male"),
        new("ko-KR-SunHiNeural","SunHi","ko-KR","Female"),new("ko-KR-InJoonNeural","InJoon","ko-KR","Male"),
        new("fr-FR-DeniseNeural","Denise","fr-FR","Female"),new("fr-FR-HenriNeural","Henri","fr-FR","Male"),
        new("de-DE-KatjaNeural","Katja","de-DE","Female"),new("de-DE-ConradNeural","Conrad","de-DE","Male")
    ]);
}
