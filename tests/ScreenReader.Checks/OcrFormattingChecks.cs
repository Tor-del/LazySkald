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
        Assert(!defaults.OcrCorrectionsEnabled && defaults.OcrCorrectionRules=="/ => l","OCR correction is opt-in for existing settings with the italic l rule prepared");
        const string misread="He//o, /itt/e wor/d!\r\nNext /ine.";
        Assert(OcrTextFormatter.Format(misread,defaults)=="He//o, /itt/e wor/d! Next /ine.","Disabled corrections preserve slashes");
        var corrected=defaults with { OcrCorrectionsEnabled=true };
        Assert(OcrTextFormatter.Format(misread,corrected)=="Hello, little world! Next line.","Italic l correction runs before OCR line formatting");
        Assert(OcrTextFormatter.Format(misread,corrected with { OcrReplaceLineBreaks=false })=="Hello, little world!\r\nNext line.","Corrections work independently of newline replacement");
        Assert(OcrTextCorrections.Apply("rn r m","r => x\nrn => m\nm => n")=="m x n","Longest match wins and replacement output is not corrected again");
        Assert(OcrTextCorrections.Apply("[x] . $1 /","[x] => $1\r. => !\r\n$1 => ok\n/ =>")=="$1 ! ok ","Rules are literal, support all newline styles and allow deletion");
        Assert(OcrTextCorrections.Apply("Il /", "\n I => L \n\n/ => l\n")=="Ll l","Rules ignore blank lines and trim edges but remain case-sensitive");
        Assert(OcrTextCorrections.Apply(misread," \r\n")==misread,"Empty rule lists leave OCR unchanged");
        Throws<ArgumentException>(()=>OcrTextCorrections.Parse("missing separator"),"Malformed correction rule rejected");
        Throws<ArgumentException>(()=>OcrTextCorrections.Parse("=> l"),"Empty correction source rejected");
        Throws<ArgumentException>(()=>OcrTextCorrections.Parse("/ => l\n/ => I"),"Duplicate correction sources rejected");
        try { OcrTextCorrections.Parse("/ => l\n\nbad"); }
        catch (ArgumentException e) { Assert(e.Message==UiText.T("ocr.correctionsInvalid",3),"Correction error identifies the original line number"); }
        Assert(OcrTextFormatter.Format(misread,defaults with { OcrCorrectionRules="bad" })==OcrTextFormatter.Format(misread),"Dormant invalid rules do not affect reading");
        var correctionRestored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(corrected with { OcrCorrectionRules="/ => l\nrn => m" }))!;
        Assert(correctionRestored.OcrCorrectionsEnabled && correctionRestored.OcrCorrectionRules=="/ => l\nrn => m","Correction toggle and custom rules survive settings serialization");
    }
}
