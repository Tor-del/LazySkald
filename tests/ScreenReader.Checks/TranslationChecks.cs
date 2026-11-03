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
        foreach (string provider in new[] { "lmstudio","ollama","openai" })
        {
            bool ollama=provider=="ollama";
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
                Assert((ollama ? root.GetProperty("options") : root).GetProperty("temperature").GetDouble()==0.2,"Provider temperature format: "+provider);
                return JsonResponse(ollama ? "{\"message\":{\"content\":\"Привет\"}}" : "{\"choices\":[{\"message\":{\"content\":\"Привет\"},\"finish_reason\":\"stop\"}]}");
            }));
            var client=new LlmTranslationClient(http);
            Assert((await client.GetModelsAsync(settings,CancellationToken.None))[0]=="chosen-model","Model list parsed: "+provider);
            Assert(await client.TranslateAsync("Hello",settings,CancellationToken.None)=="Привет","Translation parsed: "+provider);
            Assert(await client.TranslateAsync("Hello",settings with { Enabled=false },CancellationToken.None)=="Hello","Disabled translation bypass: "+provider);
            using var cancel=new CancellationTokenSource(); cancel.Cancel();
            bool cancelled=false;
            try { await client.TranslateAsync("Hello",settings,cancel.Token); } catch (OperationCanceledException) { cancelled=true; }
            Assert(cancelled,"Translation cancellation before HTTP: "+provider);
        }
        var valid=new TranslationSettings { Enabled=true,Providers=new() { ["lmstudio"]=new() { BaseUrl="http://localhost/v1",Model="m" } } };
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
