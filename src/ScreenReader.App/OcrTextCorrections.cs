using System.Text.RegularExpressions;

namespace ScreenReader.App;

public static class OcrTextCorrections
{
    // Literal, case-sensitive rules. Replacement output is never processed again.
    public static IReadOnlyDictionary<string,string> Parse(string rules)
    {
        var replacements=new Dictionary<string,string>(StringComparer.Ordinal);
        var lines=Regex.Split(rules,@"\r\n|\r|\n");
        for (int i=0;i<lines.Length;i++)
        {
            string line=lines[i].Trim();
            if (line.Length==0) continue;
            int separator=line.IndexOf("=>",StringComparison.Ordinal);
            string source=separator<0 ? "" : line[..separator].Trim();
            if (source.Length==0 || !replacements.TryAdd(source,line[(separator+2)..].Trim()))
                throw new ArgumentException(UiText.T("ocr.correctionsInvalid",i+1));
        }
        return replacements;
    }

    public static string Apply(string text,string rules)
    {
        var replacements=Parse(rules);
        if (replacements.Count==0) return text;
        // Prefer the longest matching source when rules overlap.
        string pattern=string.Join("|",replacements.Keys.OrderByDescending(key=>key.Length).Select(Regex.Escape));
        return Regex.Replace(text,pattern,match=>replacements[match.Value]);
    }
}
