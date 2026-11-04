using System.Text.RegularExpressions;

namespace ScreenReader.App;

public sealed record OcrCorrectionEntry
{
    public string Source { get; init; }="";
    public string Replacement { get; init; }="";
    public bool IgnoreCase { get; init; }
}

public static class OcrTextCorrections
{
    public static IReadOnlyList<OcrCorrectionEntry> EntriesFor(AppSettings settings)
        =>settings.OcrCorrectionEntries ?? Parse(settings.OcrCorrectionRules).Select(entry=>new OcrCorrectionEntry { Source=entry.Key,Replacement=entry.Value }).ToList();

    public static void ValidateEntries(IReadOnlyList<OcrCorrectionEntry> entries)
    {
        for (int i=0;i<entries.Count;i++)
        {
            var entry=entries[i];
            if (entry is null || string.IsNullOrWhiteSpace(entry.Source) || entry.Source!=entry.Source.Trim()
                || entry.Replacement is null || entry.Source.IndexOfAny(['\r','\n'])>=0 || entry.Replacement.IndexOfAny(['\r','\n'])>=0)
                throw new ArgumentException(UiText.T("ocr.entryInvalid"));
            for (int j=0;j<i;j++)
                if (string.Equals(entry.Source,entries[j].Source,entry.IgnoreCase || entries[j].IgnoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
                    throw new ArgumentException(UiText.T("translation.entryDuplicate"));
        }
    }

    // Legacy text rules are literal and case-sensitive; typed rules set case matching individually.
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
        =>Apply(text,Parse(rules).Select(entry=>new OcrCorrectionEntry { Source=entry.Key,Replacement=entry.Value }).ToArray());

    public static string Apply(string text,IReadOnlyList<OcrCorrectionEntry> entries)
    {
        ValidateEntries(entries);
        if (entries.Count==0) return text;
        // Prefer the longest matching source when rules overlap.
        var ordered=entries.OrderByDescending(entry=>entry.Source.Length).ToArray();
        string pattern=string.Join("|",ordered.Select((entry,i)=>$"(?<r{i}>"+(entry.IgnoreCase ? "(?i:"+Regex.Escape(entry.Source)+")" : Regex.Escape(entry.Source))+")"));
        return Regex.Replace(text,pattern,match=>
        {
            for (int i=0;i<ordered.Length;i++) if (match.Groups[$"r{i}"].Success) return ordered[i].Replacement;
            return match.Value;
        },RegexOptions.CultureInvariant);
    }
}
