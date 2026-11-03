namespace ScreenReader.Speech;

public static class SpeechText
{
    public static bool UseEnglish { get; set; }
    public static Func<string,object[],string>? Translator { get; set; }
    public static string Get(string key,string russian,string english,params object[] args)=>
        Translator is { } translate ? translate(key,args) : string.Format(Pick(russian,english),args);
    public static string Pick(string russian,string english)=>UseEnglish ? english : russian;
}
