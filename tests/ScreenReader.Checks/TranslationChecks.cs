using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;

internal static partial class Program
{
    private static async Task RunTranslationChecksAsync()
    {
        var legacy=new TranslationSettings { Prompt=TranslationSettings.LegacyDefaultPrompt,AddStressMarks=true,CorrectionsEnabled=true,TargetLanguage="de-DE" };
        Assert(legacy.UpgradeDefaultPrompt()==legacy with { Prompt=TranslationSettings.DefaultPrompt },"Old default prompt upgrades without changing translation options");
        var custom=legacy with { Prompt="My custom prompt: {targetLanguage}" };
        Assert(custom.UpgradeDefaultPrompt()==custom,"Custom translation prompts survive migration");
        Assert(JsonSerializer.Deserialize<TranslationSettings>("{}")!.Prompt==TranslationSettings.DefaultPrompt,"Missing prompt uses literary default");
        Assert(ReadingCache.KeyFor("Source",new() { Translation=legacy with { Enabled=true } })!=ReadingCache.KeyFor("Source",new() { Translation=legacy.UpgradeDefaultPrompt() with { Enabled=true } }),"New default prompt invalidates old translation cache");
        foreach (string provider in new[] { "lmstudio","ollama","openai" })
        {
            bool ollama=provider=="ollama";
            bool expectStressMarks=false;
            var settings=new TranslationSettings { Enabled=true,Provider=provider,Providers=new() { [provider]=new() { BaseUrl=ollama ? "http://localhost:11434/prefix" : "http://localhost:1234/prefix/v1",Model="chosen-model",ApiKeyProtected=SecretProtection.Protect("test-key") } } };
            using var http=new HttpClient(new Handler(async (request,ct)=>
            {
                Assert(request.Headers.Authorization?.Parameter=="test-key","Translation bearer key: "+provider);
                if (request.Method==HttpMethod.Get)
                {
                    Assert(request.RequestUri!.AbsolutePath==(ollama ? "/prefix/api/tags" : "/prefix/v1/models"),"Model discovery endpoint: "+provider);
                    return JsonResponse(ollama ? "{\"models\":[{\"name\":\"chosen-model\"}]}" : "{\"data\":[{\"id\":\"chosen-model\"}]}");
                }
                Assert(request.RequestUri!.AbsolutePath==(ollama ? "/prefix/api/chat" : "/prefix/v1/chat/completions"),"Translation endpoint: "+provider);
                using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                var root=body.RootElement;
                Assert(root.GetProperty("model").GetString()=="chosen-model" && !root.GetProperty("stream").GetBoolean(),"Selected model and non-streaming translation: "+provider);
                Assert(root.GetProperty("messages")[0].GetProperty("content").GetString()!.Contains("ru-RU") && root.GetProperty("messages")[1].GetProperty("content").GetString()=="Hello", "Target prompt and separate source text: "+provider);
                string expectedPrompt=TranslationSettings.DefaultPrompt.Replace("{targetLanguage}","ru-RU");
                if (expectStressMarks) expectedPrompt+="\n\n"+TranslationSettings.StressMarksPrompt;
                Assert(root.GetProperty("messages")[0].GetProperty("content").GetString()==expectedPrompt,"Stress instruction is appended only when enabled: "+provider);
                Assert((ollama ? root.GetProperty("options") : root).GetProperty("temperature").GetDouble()==0.2,"Provider temperature format: "+provider);
                return JsonResponse(ollama ? "{\"message\":{\"content\":\"Привет\"}}" : "{\"choices\":[{\"message\":{\"content\":\"Привет\"},\"finish_reason\":\"stop\"}]}");
            }));
            var client=new LlmTranslationClient(http);
            Assert((await client.GetModelsAsync(settings,CancellationToken.None))[0]=="chosen-model","Model list parsed: "+provider);
            Assert(await client.TranslateAsync("Hello",settings,CancellationToken.None)=="Привет","Translation parsed: "+provider);
            expectStressMarks=true;
            Assert(await client.TranslateAsync("Hello",settings with { AddStressMarks=true },CancellationToken.None)=="Привет","Translation with stress marks parsed: "+provider);
            Assert(settings.Prompt==TranslationSettings.DefaultPrompt && !settings.AddStressMarks,"Stress option preserves saved base prompt and defaults to disabled");
            Assert(await client.TranslateAsync("Hello",settings with { Enabled=false },CancellationToken.None)=="Hello","Disabled translation bypass: "+provider);
            using var cancel=new CancellationTokenSource(); cancel.Cancel();
            bool cancelled=false;
            try { await client.TranslateAsync("Hello",settings,cancel.Token); } catch (OperationCanceledException) { cancelled=true; }
            Assert(cancelled,"Translation cancellation before HTTP: "+provider);
        }
        Assert(new AppSettings().Translation.Provider=="ollama" && new AppSettings().Genderization.Provider=="ollama","Translation and genderization default to Ollama");
        Assert(JsonSerializer.Deserialize<AppSettings>("{}") is { Translation.Provider:"ollama",Genderization.Provider:"ollama" },"Missing provider settings default to Ollama");
        Assert(JsonSerializer.Deserialize<TranslationSettings>("{\"Provider\":\"lmstudio\"}")!.Provider=="lmstudio","Explicit provider choice is preserved");
        var valid=new TranslationSettings { Enabled=true,Provider="lmstudio",Providers=new() { ["lmstudio"]=new() { BaseUrl="http://localhost/v1",Model="m" } } };
        Assert(valid.StripMarkdown,"Markdown removal defaults to enabled");
        Assert(JsonSerializer.Deserialize<TranslationSettings>("{}") is { StripMarkdown:true },"Existing settings without Markdown option enable cleanup");
        Assert(JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(valid with { StripMarkdown=false })) is { StripMarkdown:false },"Explicitly disabled Markdown cleanup survives serialization");
        Assert(JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(valid with { StripMarkdown=true })) is { StripMarkdown:true },"Markdown removal survives settings serialization");
        foreach (var (markdown,plain) in new[] {
            ("Ты входишь в замо́к.\n\n— Кто здесь? — спрашиваешь ты.\n\n«Неужели он вернулся?»","Ты входишь в замо́к.\n\n— Кто здесь? — спрашиваешь ты.\n\n«Неужели он вернулся?»"),
            ("# Заголовок\n\n**Жирный** и *курсив*, ~~удалённый~~.","Заголовок\nЖирный и курсив, удалённый."),
            ("> Цитата\n\n- Первый\n- Второй","Цитата\nПервый\nВторой"),
            ("[Ссылка](https://example.com) и ![описание](image.png)","Ссылка и описание"),
            ("[Ссылка][ref]\n\n[ref]: https://example.com","Ссылка"),
            ("```csharp\nvar x = a * b;\n```","var x = a * b;"),
            ("`a_b * c` и \\*звёздочки\\*","a_b * c и *звёздочки*"),
            ("Обычный текст: 2 * 3 = 6, snake_case, C# и замо́к.","Обычный текст: 2 * 3 = 6, snake_case, C# и замо́к."),
            ("| Имя | Значение |\n| --- | --- |\n| Тест | 42 |","Имя\tЗначение\nТест\t42"),
            ("- [x] Готово\n- [ ] В работе","Готово\nВ работе"),
            ("Заголовок\n===\n\n1. Один\n2. Два\n\n---","Заголовок\nОдин\nДва"),
            ("<b>Текст</b> &amp; &lt;пример&gt;","Текст & <пример>"),
            ("**за\u200Bмо́к**\uFEFF\0\u0001\u001B\u007F\u0085\uFFFF\uD800!","замо́к!"),
            ("Русский, English, 日本語, 中文, 한국어, café, العربية, हिन्‍दी 👩‍💻.","Русский, English, 日本語, 中文, 한국어, café, العربية, हिन्‍दी 👩‍💻."),
            ("Цена: 1 200 ₽; 25%, +3, −2, 10/2, A & B < C > D.\nНовая строка.","Цена: 1 200 ₽; 25%, +3, −2, 10/2, A & B < C > D.\nНовая строка."),
            ("\u202EПривет\u202C, мяг\u00ADкий текст\u2066!\u2069","Привет, мягкий текст!") })
        {
            string actual=TranslationPlainText.Convert(markdown).Replace("\r\n","\n");
            Assert(actual==plain,"Markdown plain text: "+markdown+" => "+actual);
        }
        foreach (string provider in new[] { "lmstudio","ollama","openai" })
        {
            string markdown="**Привет**, *мир*!";
            string payload=provider=="ollama" ? JsonSerializer.Serialize(new { message=new { content=markdown } }) : JsonSerializer.Serialize(new { choices=new[] { new { message=new { content=markdown } } } });
            using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse(payload))));
            var client=new LlmTranslationClient(http);
            var settings=valid with { Provider=provider,Providers=new() { [provider]=new() { BaseUrl="http://localhost/v1",Model="m" } } };
            Assert(await client.TranslateAsync("Hello",settings with { StripMarkdown=false },CancellationToken.None)==markdown,"Disabled cleanup preserves Markdown: "+provider);
            Assert(await client.TranslateAsync("Hello",settings with { StripMarkdown=true },CancellationToken.None)=="Привет, мир!","Enabled cleanup removes Markdown: "+provider);
            Assert(await client.TranslateAsync(markdown,settings with { Enabled=false,StripMarkdown=true },CancellationToken.None)==markdown,"Disabled translation preserves original Markdown: "+provider);
        }
        var restored=JsonSerializer.Deserialize<TranslationSettings>(JsonSerializer.Serialize(valid with { AddStressMarks=true }));
        using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse("{\"choices\":[{\"message\":{\"content\":\"---\"}}]}")))))
        {
            bool rejected=false;
            try { await new LlmTranslationClient(http).TranslateAsync("Hello",valid with { StripMarkdown=true },CancellationToken.None); }
            catch (InvalidOperationException) { rejected=true; }
            Assert(rejected,"Translation containing only Markdown is rejected after cleanup");
        }
        Assert(restored is { AddStressMarks:true },"Stress mark setting survives settings serialization");
        Throws<ArgumentException>(()=>LlmTranslationClient.Validate(valid with { Temperature=double.NaN }),"Invalid translation temperature rejected");
        foreach (string payload in new[] { "{}","{\"choices\":[{\"message\":{\"content\":\" \"}}]}","{\"choices\":[{\"message\":{\"content\":\"partial\"},\"finish_reason\":\"length\"}]}" })
        {
            using var http=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse(payload))));
            bool rejected=false;
            try { await new LlmTranslationClient(http).TranslateAsync("Hello",valid,CancellationToken.None); }
            catch (Exception e) when (e is System.IO.InvalidDataException or InvalidOperationException) { rejected=true; }
            Assert(rejected,"Invalid, empty or truncated translation is rejected");
        }
        using (var http=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized) { Content=new StringContent("private server details") }))))
        {
            bool safe=false;
            try { await new LlmTranslationClient(http).TranslateAsync("Hello",valid,CancellationToken.None); }
            catch (HttpRequestException e) { safe=e.StatusCode==HttpStatusCode.Unauthorized && !e.Message.Contains("private"); }
            Assert(safe,"Translation HTTP errors omit server body");
        }
        using (var http=new HttpClient(new Handler(async (_,ct)=> { await Task.Delay(Timeout.Infinite,ct); return JsonResponse("{}"); })))
        {
            bool timedOut=false;
            try { await new LlmTranslationClient(http).TranslateAsync("Hello",valid with { TimeoutSeconds=1 },CancellationToken.None); }
            catch (TimeoutException) { timedOut=true; }
            Assert(timedOut,"Translation enforces configured timeout");
        }
        using var registry=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var speech=new RecordingTranslationSpeech();
        registry.Register(new("translation-test","Test",false,_=>speech,false));
        var translator=new TestTranslator();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,new ConsumingAudio(),new NoFocus(),translator);
        var appSettings=new AppSettings { Language="en-US",SpeechProviderId="translation-test",ProviderVoices=new() { ["translation-test"]="ru-voice" },Translation=valid };
        string? preview=null; Exception? failed=null;
        reader.TextTranslated+=value=>preview=value; reader.Failed+=e=>failed=e;
        await reader.ReadTextAsync("Hello",appSettings);
        Assert(speech.Last is { Text:"Перевод",Language:"ru-RU",Voice:"ru-voice" } && preview=="Перевод","Pipeline sends translated text and target language to TTS");
        using (var cleanHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(JsonResponse(JsonSerializer.Serialize(new { choices=new[] { new { message=new { content="# **За\u200Bмо́к**\u0001\uFEFF &amp; <b>ключ</b>." } } } }))))))
        {
            var cleanReader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,new ConsumingAudio(),new NoFocus(),new LlmTranslationClient(cleanHttp));
            string? cleanPreview=null;
            cleanReader.TextTranslated+=value=>cleanPreview=value;
            speech.Last=null;
            await cleanReader.ReadTextAsync("Source",appSettings);
            Assert(speech.Last is { Text:"Замо́к & ключ." } && cleanPreview=="Замо́к & ключ.","Default cleanup sends identical clean Unicode text to preview and actual TTS request");
            System.Xml.XmlConvert.VerifyXmlChars(speech.Last!.Text);
        }
        translator.Fail=true; speech.Last=null;
        await reader.ReadTextAsync("Hello",appSettings);
        Assert(failed is not null && speech.Last is null && preview=="","Translation failure prevents speech and clears old preview");
        await reader.ReadTextAsync("Original",appSettings with { Translation=valid with { Enabled=false } });
        Assert(speech.Last is { Text:"Original",Language:"en-US" },"Disabled pipeline preserves original text and OCR language");
        using var cancelHttp=new HttpClient(new Handler(async (request,ct)=>
        {
            using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert(!body.RootElement.TryGetProperty("temperature",out _),"Temperature is omitted when disabled");
            await Task.Delay(Timeout.Infinite,ct);
            return JsonResponse("{}");
        }));
        var waiting=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var activeHttp=new HttpClient(new Handler(async (_,ct)=> { waiting.TrySetResult(); await Task.Delay(Timeout.Infinite,ct); return JsonResponse("{}"); }));
        reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,new ConsumingAudio(),new NoFocus(),new LlmTranslationClient(activeHttp));
        speech.Last=null; bool failure=false; reader.Failed+=_=>failure=true;
        var pending=reader.ReadTextAsync("Hello",appSettings);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(3)); reader.Stop();
        await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy && !failure && speech.Last is null,"Stop cancels in-flight translation and prevents speech");
        using var omitTimeout=new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        try { await new LlmTranslationClient(cancelHttp).TranslateAsync("Hello",valid with { Temperature=null },omitTimeout.Token); }
        catch (OperationCanceledException) { }
    }
    private static HttpResponseMessage JsonResponse(string json)=>new(HttpStatusCode.OK) { Content=new StringContent(json,Encoding.UTF8,"application/json") };
    private sealed class TestTranslator : ITranslationClient
    {
        public bool Fail;
        public Task<string> TranslateAsync(string text,TranslationSettings settings,CancellationToken token)=>Fail ? throw new InvalidOperationException("Test failure") : Task.FromResult("Перевод");
    }
    private sealed class RecordingTranslationSpeech : ISpeechProvider
    {
        public SpeechAudioFormat AudioFormat { get; set; }=SpeechAudioFormat.Mp3;
        public SpeechRequest? Last;
        public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
        public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        { Last=request; await Task.Yield(); yield return new byte[] { 1 }; }
    }
}
