using System;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;

internal static partial class Program
{
    private static async Task RunTranslationCorrectionChecksAsync()
    {
        var settings=new TranslationSettings { Enabled=true,CorrectionsEnabled=true,
            CorrectionRules=" Skyrim \r\nDragonborn => Довакин\nHigh Hrothgar => Высокий Хротгар\ncat\nC++\ncafé\nзамок\nunused",
            Providers=new() { ["lmstudio"]=new() { BaseUrl="http://localhost/v1",Model="test" } } };
        var rules=TranslationCorrections.Parse(settings.CorrectionRules);
        Assert(rules["skyrim"] is null && rules["DRAGONBORN"]=="Довакин","Glossary supports preserve and replacement rules with trimmed case-insensitive sources");
        Assert(TranslationCorrections.PromptFor("A cathedral, cats, caféine, подзамок and C++Builder.",settings)=="","Glossary does not match word fragments or extended terms");
        Assert(TranslationCorrections.PromptFor("Unrelated text",settings)=="","No glossary instruction without matching terms");
        Assert(TranslationCorrections.PromptFor("Skyrim",settings with { CorrectionsEnabled=false,CorrectionRules="=>" })=="","Disabled glossary bypasses even invalid drafts");
        foreach (string invalid in new[] { "=> value","word =>","Word\nword => replacement","Word => a\nWORD => b" })
            Throws<ArgumentException>(()=>TranslationCorrections.Parse(invalid),"Invalid or duplicate glossary entry rejected: "+invalid);
        var restored=JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(settings));
        Assert(restored is { CorrectionsEnabled:true } && restored.CorrectionRules==settings.CorrectionRules,"Glossary survives settings serialization");
        Assert(JsonSerializer.Deserialize<TranslationSettings>("{}") is { CorrectionsEnabled:false,CorrectionRules:"" },"Existing settings default to disabled empty glossary");
        var migrated=TranslationCorrections.EntriesFor(settings);
        Assert(migrated.Count==8 && migrated[0] is { Source:"Skyrim",Preserve:true,IgnoreCase:true } && migrated[1] is { Replacement:"Довакин",Preserve:false },"Legacy glossary migrates to typed entries preserving previous behavior");
        var typed=settings with { CorrectionRules="",CorrectionEntries=[new() { Source="Hero",Replacement="Герой",IgnoreCase=false },new() { Source="hero",Preserve=true,IgnoreCase=false }] };
        Assert(TranslationCorrections.PromptFor("HERO",typed)=="","Case-sensitive entries do not match another case");
        string typedPrompt=TranslationCorrections.PromptFor("Hero",typed);
        using (var data=JsonDocument.Parse(typedPrompt[(typedPrompt.IndexOf('\n')+1)..]))
            Assert(data.RootElement.GetArrayLength()==1 && !data.RootElement[0].GetProperty("ignoreCase").GetBoolean() && data.RootElement[0].GetProperty("action").GetString()=="replace","Each entry controls matching and sends case preference to model");
        var insensitive=typed with { CorrectionEntries=[new() { Source="Hero",Preserve=true,IgnoreCase=true }] };
        Assert(TranslationCorrections.PromptFor("HERO",insensitive)!="","Case-insensitive typed entry matches uppercase source");
        Throws<ArgumentException>(()=>TranslationCorrections.ValidateEntries([new() { Source="Hero",Preserve=true },new() { Source="hero",Preserve=true,IgnoreCase=false }]),"Overlapping case rules rejected");
        Throws<ArgumentException>(()=>TranslationCorrections.ValidateEntries([new() { Source="Hero" }]),"Empty replacement rejected unless preserve is enabled");
        var typedRestored=JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(typed))!;
        Assert(typedRestored.CorrectionEntries is { Count:2 } && !typedRestored.CorrectionEntries[0].IgnoreCase && typedRestored.CorrectionEntries[1].Preserve,"Per-entry flags survive serialization");
        Assert(TranslationCorrections.EntriesFor(settings with { CorrectionEntries=[] }).Count==0,"Explicit empty list overrides old text after deleting all entries");
        Assert(ReadingCache.KeyFor("HERO",new() { Translation=insensitive })!=ReadingCache.KeyFor("HERO",new() { Translation=typed }),"Per-entry case setting affects cached translation");
        var overlap=settings with { CorrectionRules="High\nHigh Hrothgar => HH" };
        string overlapPrompt=TranslationCorrections.PromptFor("High Hrothgar",overlap);
        using (var data=JsonDocument.Parse(overlapPrompt[(overlapPrompt.IndexOf('\n')+1)..]))
            Assert(data.RootElement[0].GetProperty("source").GetString()=="High Hrothgar","Longer overlapping glossary phrases precede shorter entries");
        string source="SKYRIM: Dragonborn visits High Hrothgar with a cat, C++, café and замок.";
        foreach (string provider in new[] { "lmstudio","ollama","openai" })
        foreach (bool customPrompt in new[] { false,true })
        foreach (bool stressMarks in new[] { false,true })
        {
            var selected=settings with { Provider=provider,AddStressMarks=stressMarks,Prompt=customPrompt ? "Custom prompt: {targetLanguage}" : TranslationSettings.DefaultPrompt,TargetLanguage="de-DE",Providers=new() { [provider]=settings.Selected } };
            using var http=new HttpClient(new Handler(async (request,ct)=>
            {
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var messages=body.RootElement.GetProperty("messages");
                string prompt=messages[0].GetProperty("content").GetString()!;
                string glossary=TranslationCorrections.PromptFor(source,selected);
                string expected=selected.Prompt.Replace("{targetLanguage}","de-DE")+(stressMarks ? "\n\n"+TranslationSettings.StressMarksPrompt : "")+"\n\n"+glossary;
                Assert(prompt==expected && !prompt.Contains("{targetLanguage}"),"Glossary appends to literary/custom prompt with optional stress instructions and selected language: "+provider);
                Assert(glossary.Contains("take priority") && glossary.Contains("without adding stress marks"),"Exact glossary spelling takes priority over literary translation and stress marks: "+provider);
                Assert(messages[1].GetProperty("content").GetString()==source,"Glossary leaves source text intact: "+provider);
                using var entries=JsonDocument.Parse(glossary[(glossary.IndexOf('\n')+1)..]);
                Assert(entries.RootElement.GetArrayLength()==7,"Only matching glossary entries reach the server: "+provider);
                bool preserved=false,replaced=false;
                foreach (var entry in entries.RootElement.EnumerateArray())
                {
                    if (entry.GetProperty("source").GetString()=="Skyrim") preserved=entry.GetProperty("action").GetString()=="preserve";
                    if (entry.GetProperty("source").GetString()=="Dragonborn") replaced=entry.GetProperty("replacement").GetString()=="Довакин";
                }
                Assert(preserved && replaced,"Preserve and preferred spelling reach the server: "+provider);
                string response="Skyrim: Довакин\n\n— Замо"+(stressMarks ? "\u0301" : "")+"к.";
                return JsonResponse(provider=="ollama" ? JsonSerializer.Serialize(new { message=new { content=response } }) : JsonSerializer.Serialize(new { choices=new[] { new { message=new { content=response } } } }));
            }));
            string expectedResponse="Skyrim: Довакин\n\n— Замо"+(stressMarks ? "\u0301" : "")+"к.";
            Assert((await new LlmTranslationClient(http).TranslateAsync(source,selected,CancellationToken.None)).Replace("\r\n","\n")==expectedResponse,"Cleanup preserves glossary spelling, paragraphs, dialogue and combining accents: "+provider);
        }
        var app=new AppSettings { Translation=settings };
        string Key(AppSettings value)=>ReadingCache.KeyFor("Skyrim",value);
        Assert(Key(app)!=Key(app with { Translation=settings with { CorrectionRules="Skyrim => Скайрим" } }),"Matched glossary changes invalidate cached readings");
        Assert(Key(app)==Key(app with { Translation=settings with { CorrectionRules=settings.CorrectionRules+"\nabsent => value" } }),"Unmatched glossary entries do not invalidate cached readings");
        Assert(Key(app)!=Key(app with { Translation=settings with { CorrectionsEnabled=false } }),"Disabling matching glossary invalidates cache");
        Assert(Key(app with { Translation=settings with { Enabled=false } })==Key(app with { Translation=settings with { Enabled=false,CorrectionRules="changed" } }),"Glossary does not affect cache when translation is disabled");
    }
}
