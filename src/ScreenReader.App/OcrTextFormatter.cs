using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenReader.App;

public static class OcrTextFormatter
{
    public static string Format(string text,AppSettings settings)
    {
        if (settings.OcrCorrectionsEnabled)
            text=OcrTextCorrections.Apply(text,OcrTextCorrections.EntriesFor(settings));
        return Format(text,settings.OcrReplaceLineBreaks,settings.OcrPreserveParagraphs);
    }

    public static string Format(string text,bool replaceLineBreaks=true,bool preserveParagraphs=false)
    {
        if (!replaceLineBreaks) return text;
        var result=new StringBuilder();
        foreach (string rawLine in Regex.Split(text,@"\r\n|\r|\n"))
        {
            string line=Regex.Replace(rawLine,@"[^\S\r\n]+"," ").Trim();
            if (line.Length==0) continue;
            if (result.Length>0)
            {
                var category=Rune.GetUnicodeCategory(Rune.GetRuneAt(line,0));
                bool startsUppercase=category is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter;
                bool paragraph=preserveParagraphs && result[^1]=='.' && startsUppercase;
                result.Append(paragraph ? Environment.NewLine : " ");
            }
            result.Append(line);
        }
        return result.ToString();
    }
}
