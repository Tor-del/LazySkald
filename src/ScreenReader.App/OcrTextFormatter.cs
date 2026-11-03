using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace ScreenReader.App;

public static class OcrTextFormatter
{
    public static string Format(string text)
    {
        var result=new StringBuilder();
        foreach (string rawLine in Regex.Split(text,@"\r\n|\r|\n"))
        {
            string line=rawLine.Trim();
            if (line.Length==0) continue;
            if (result.Length>0)
            {
                var category=Rune.GetUnicodeCategory(Rune.GetRuneAt(line,0));
                bool startsUppercase=category is UnicodeCategory.UppercaseLetter or UnicodeCategory.TitlecaseLetter;
                result.Append(startsUppercase ? Environment.NewLine : " ");
            }
            result.Append(line);
        }
        return result.ToString();
    }
}
