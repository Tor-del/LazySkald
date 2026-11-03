using System;
using System.Text.Json;
using ScreenReader.App;

internal static partial class Program
{
    private static void RunOcrFormattingChecks()
    {
        Assert(OcrTextFormatter.Format("Это строка\r\n   Продолжение\rтекста.\nНовая фраза.")=="Это строка Продолжение текста. Новая фраза.","OCR replaces every newline style by default, including uppercase starts");
        Assert(OcrTextFormatter.Format("  A  \t B\n\n  C\u00a0 D  ")=="A B C D","OCR collapses repeated whitespace, tabs and empty lines without double spaces");
        Assert(OcrTextFormatter.Format("Первая строка\nПродолжение.\nНовая фраза.\nещё текст",true,true)=="Первая строка Продолжение."+Environment.NewLine+"Новая фраза. ещё текст","Paragraph preservation requires both a period and an uppercase start");
        Assert(OcrTextFormatter.Format("End!\nNext?\nAgain\nUpper",true,true)=="End! Next? Again Upper","Other punctuation and uppercase alone do not preserve breaks");
        Assert(OcrTextFormatter.Format("End. Next.\n\n  Ёжик.\r\n\U00010400Test",true,true)=="End. Next."+Environment.NewLine+"Ёжик."+Environment.NewLine+"\U00010400Test","Paragraphs support Cyrillic and supplementary Unicode uppercase letters");
        Assert(OcrTextFormatter.Format("End. Next sentence.",true,true)=="End. Next sentence.","Formatting never invents a paragraph within an original line");
        Assert(OcrTextFormatter.Format("End.\n2026\n«Quote»",true,true)=="End. 2026 «Quote»","Digits and punctuation at the start join with a space");
        string raw="  Original  text\r\n\n Next\tline  ";
        Assert(OcrTextFormatter.Format(raw,false,false)==raw && OcrTextFormatter.Format(raw,false,true)==raw,"Disabled replacement preserves the original OCR text regardless of paragraph setting");
        Assert(OcrTextFormatter.Format(" \n \r\n ")=="","Empty OCR stays empty");
        foreach (bool paragraphs in new[] { false,true })
        {
            string once=OcrTextFormatter.Format(raw+".\nNext",true,paragraphs);
            Assert(OcrTextFormatter.Format(once,true,paragraphs)==once,"OCR formatting is stable on repeated application");
        }
        var defaults=JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert(defaults.OcrReplaceLineBreaks && !defaults.OcrPreserveParagraphs,"Existing settings default to joining all OCR lines");
        var settings=defaults with { OcrReplaceLineBreaks=false,OcrPreserveParagraphs=true };
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert(!restored.OcrReplaceLineBreaks && restored.OcrPreserveParagraphs,"OCR formatting preferences survive settings serialization");
    }
}
