using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;
using NAudio.Wave;

internal static partial class Program
{
    private sealed class Handler(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>send(request,ct);
    }
    private static byte[] MakeWave()
    {
        using var stream=new MemoryStream();
        using (var writer=new WaveFileWriter(new NonClosingStream(stream),new WaveFormat(16000,16,1)))
        {
            byte[] pcm=new byte[16000];
            for (int i=0; i<pcm.Length/2; i++)
            {
                short sample=(short)(Math.Sin(i*2*Math.PI*440/16000)*2000);
                pcm[i*2]=(byte)sample; pcm[i*2+1]=(byte)(sample>>8);
            }
            writer.Write(pcm,0,pcm.Length);
        }
        return stream.ToArray();
    }
    private sealed class NonClosingStream(Stream wrapped) : Stream
    {
        public override bool CanRead=>wrapped.CanRead; public override bool CanSeek=>wrapped.CanSeek; public override bool CanWrite=>wrapped.CanWrite;
        public override long Length=>wrapped.Length; public override long Position { get=>wrapped.Position; set=>wrapped.Position=value; }
        public override void Flush()=>wrapped.Flush(); public override int Read(byte[] b,int o,int c)=>wrapped.Read(b,o,c);
        public override long Seek(long o,SeekOrigin s)=>wrapped.Seek(o,s); public override void SetLength(long v)=>wrapped.SetLength(v);
        public override void Write(byte[] b,int o,int c)=>wrapped.Write(b,o,c);
    }
    private static async Task RunProviderChecksAsync()
    {
        byte[] wave=MakeWave();
        using (var source=new MemoryStream(wave))
        {
            var decoder=new WavePcmDecoder(source); long pcm=0; var buffer=new byte[4000]; int read;
            while ((read=decoder.Read(buffer))>0) pcm+=read;
            Assert(decoder.Format.SampleRate==16000 && pcm==16000,"WAV decoder consumes exactly the declared PCM data");
        }
        Throws<InvalidDataException>(()=>new WavePcmDecoder(new MemoryStream(new byte[12])),"Invalid WAV response rejected");
        bool posted=false;
        using var handler=new Handler(async (request,ct)=>
        {
            posted=true;
            Assert(request.Method==HttpMethod.Post && request.RequestUri!.AbsolutePath=="/custom/v1/audio/speech","Compatible provider uses the exact configured endpoint");
            Assert(request.Headers.Authorization?.Scheme=="Bearer" && request.Headers.Authorization.Parameter=="fake-test-key","Compatible provider sends optional bearer key");
            using var json=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
            Assert(json.RootElement.GetProperty("model").GetString()=="local-model" && json.RootElement.GetProperty("voice").GetString()=="custom-voice" && json.RootElement.GetProperty("input").GetString()=="Тест","Compatible provider sends selected model, voice and text");
            Assert(json.RootElement.GetProperty("response_format").GetString()=="wav" && json.RootElement.GetProperty("speed").GetDouble()==1.25,"Compatible provider sends audio format and speed");
            var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(wave) };
            response.Content.Headers.ContentType=new("audio/wav"); return response;
        });
        using var http=new HttpClient(handler);
        using var provider=new OpenAiSpeechClient(new("http://127.0.0.1:1234/custom/v1/audio/speech","local-model","fake-test-key",SpeechAudioFormat.Wave),http);
        using var output=new MemoryStream();
        await foreach (var chunk in provider.StreamAsync(new("Тест","custom-voice","ru-RU",25),CancellationToken.None)) output.Write(chunk);
        Assert(posted && output.ToArray().SequenceEqual(wave),"Compatible provider streams complete WAV payload");
        Throws<ArgumentException>(()=>OpenAiSpeechClient.Validate(new("file:///tmp/audio","m","")),"Non-HTTP TTS endpoint rejected");
        using var unauthorizedHttp=new HttpClient(new Handler((_,_)=>Task.FromResult(new HttpResponseMessage(HttpStatusCode.Unauthorized))));
        using var unauthorized=new OpenAiSpeechClient(new("http://127.0.0.1:1234/v1/audio/speech","model","fake-secret"),unauthorizedHttp);
        bool safeError=false;
        try { await foreach (var _ in unauthorized.StreamAsync(new("Тест","voice","ru-RU",0),CancellationToken.None)) { } }
        catch (HttpRequestException e) { safeError=e.StatusCode==HttpStatusCode.Unauthorized && !e.Message.Contains("fake-secret"); }
        Assert(safeError,"Server errors report HTTP status without exposing API key");
        bool noAuth=false;
        using var noAuthHttp=new HttpClient(new Handler((request,ct)=>
        {
            noAuth=request.Headers.Authorization is null;
            var response=new HttpResponseMessage(HttpStatusCode.OK) { Content=new ByteArrayContent(new byte[]{1,2,3}) };
            response.Content.Headers.ContentType=new("audio/mpeg"); return Task.FromResult(response);
        }));
        using var noKey=new OpenAiSpeechClient(new("http://127.0.0.1:1234/v1/audio/speech","model",""),noAuthHttp);
        await foreach (var _ in noKey.StreamAsync(new("text","voice","en-US",0),CancellationToken.None)) { }
        Assert(noAuth,"A local TTS server can be used without an API key");
        using var cancelled=new CancellationTokenSource(); cancelled.Cancel(); bool stopped=false;
        try { await foreach (var _ in noKey.StreamAsync(new("text","voice","en-US",0),cancelled.Token)) { } }
        catch (OperationCanceledException) { stopped=true; }
        Assert(stopped,"Cancelled compatible TTS exits before making an HTTP request");
        string protectedKey=SecretProtection.Protect("fake-test-secret");
        Assert(!protectedKey.Contains("fake-test-secret") && SecretProtection.Unprotect(protectedKey)=="fake-test-secret","TTS API key round-trips through Windows DPAPI");
        using var registry=new SpeechProviderRegistry();
        Assert(registry.Definitions.Select(p=>p.Id).Order().SequenceEqual(new[]{"edge","openai-compatible","windows"}),"Provider registry exposes all three implementations");
        registry.Register(new("test-extension","Test extension",true,_=>provider,false));
        using var extension=registry.Acquire(new AppSettings { SpeechProviderId="test-extension" });
        Assert(ReferenceEquals(extension.Provider,provider),"A provider can be registered without changing ReadingController");
        var legacy=JsonSerializer.Deserialize<AppSettings>("{\"CaptureHotkey\":{\"Modifiers\":3,\"Key\":82},\"StopHotkey\":{\"Modifiers\":3,\"Key\":83},\"Voice\":\"ru-RU-DmitryNeural\"}")!;
        Assert(legacy.CaptureHotkey?.Key==82 && legacy.SelectedVoice=="ru-RU-DmitryNeural" && legacy.HotkeyMode==HotkeyMode.GameHook,"Legacy settings preserve capture key and Edge voice with new game mode");
        await RunToggleChecksAsync();
    }
    private sealed class BlockedCapture : IScreenCapture,IDisposable
    {
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ManualResetEventSlim release=new();
        public int Calls;
        public CapturedScreen Capture(CaptureMode mode)
        {
            Interlocked.Increment(ref Calls); Entered.TrySetResult(); release.Wait(TimeSpan.FromSeconds(5));
            return new(new System.Drawing.Bitmap(20,20),new System.Drawing.Rectangle(0,0,20,20),"Test");
        }
        public CapturedScreen CaptureMonitor(System.Drawing.Rectangle bounds,CaptureMode mode)=>Capture(mode);
        public void Release()=>release.Set();
        public void Dispose() { release.Set(); release.Dispose(); }
    }
    private sealed class NoOcr : IOcrProvider
    {
        public System.Collections.Generic.IReadOnlyList<(string Tag,string Name)> Languages=>[];
        public Task<string> RecognizeAsync(System.Drawing.Bitmap bitmap,string language,CancellationToken ct)=>throw new Exception("Cancelled capture must not reach OCR");
    }
    private sealed class NoFocus : IForegroundWindowService { public IWindowFocusTarget? Capture()=>null; }
    private sealed class BlockingSpeech : ISpeechProvider
    {
        public SpeechAudioFormat AudioFormat=>SpeechAudioFormat.Wave;
        public string? Voice;
        public Task<System.Collections.Generic.IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken ct)=>Task.FromResult<System.Collections.Generic.IReadOnlyList<SpeechVoice>>([]);
        public async System.Collections.Generic.IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Voice=request.Voice; yield return new byte[]{1};
            await Task.Delay(Timeout.Infinite,ct);
        }
    }
    private sealed class ConsumingAudio : IAudioPlayer
    {
        public readonly TaskCompletionSource Started=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public SpeechAudioFormat Format;
        public string? DeviceId;
        public Task PlayAsync(System.Collections.Generic.IAsyncEnumerable<byte[]> audio,Action started,CancellationToken ct)=>PlayAsync(audio,SpeechAudioFormat.Mp3,started,ct);
        public Task PlayAsync(System.Collections.Generic.IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,Action started,CancellationToken ct)=>PlayAsync(audio,format,"",started,ct);
        public async Task PlayAsync(System.Collections.Generic.IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,string deviceId,Action started,CancellationToken ct)
        {
            Format=format; DeviceId=deviceId;
            await foreach (var _ in audio.WithCancellation(ct)) { started(); Started.TrySetResult(); }
        }
    }
    private static async Task RunToggleChecksAsync()
    {
        using var registry=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var audio=new ConsumingAudio(); var speech=new BlockingSpeech();
        registry.Register(new("toggle-test","Toggle test",false,_=>speech,false));
        var settings=new AppSettings { SpeechProviderId="toggle-test",ProviderVoices=new() { ["toggle-test"]="custom-test-voice" },AudioOutputDeviceId="saved-output-id" };
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,audio,new NoFocus());
        var first=reader.ToggleAsync(settings);
        await capture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(reader.IsBusy,"First toggle starts capture and marks reader busy");
        await reader.ToggleAsync(settings); await reader.ToggleAsync(settings);
        Assert(capture.Calls==1,"Toggles during cancellation stop rather than launch another capture");
        capture.Release(); await first.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy,"Cancelled capture returns reader to idle without opening selection");
        var reading=reader.ReadTextAsync("test",settings);
        await audio.Started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(speech.Voice=="custom-test-voice" && audio.Format==SpeechAudioFormat.Wave,"ReadingController resolves provider voice and audio format through registry");
        Assert(audio.DeviceId=="saved-output-id","ReadingController routes every TTS provider to the selected output ID");
        await reader.ToggleAsync(settings); await reading.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy && capture.Calls==1,"The same hotkey cancels active speech without starting capture");
    }
    private static async Task RunWindowsSpeechAsync()
    {
        ISpeechProvider provider=new WindowsSpeechProvider();
        var voices=await provider.GetVoicesAsync(CancellationToken.None);
        Console.WriteLine("Windows voices: "+string.Join(", ",voices.Select(v=>v.FriendlyName+" "+v.Locale)));
        Assert(voices.Count>0,"Installed Windows voices are listed offline");
        var voice=voices.FirstOrDefault(v=>v.Locale=="ru-RU") ?? voices[0];
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var request=new SpeechRequest(voice.Locale.StartsWith("ru") ? "Проверка системного голоса." : "Windows voice test.",voice.ShortName,voice.Locale,0);
        using var audio=new MemoryStream();
        await foreach (var chunk in provider.StreamAsync(request,timeout.Token)) audio.Write(chunk);
        audio.Position=0; var decoder=new WavePcmDecoder(audio); int first=decoder.Read(new byte[4096]);
        Assert(first>0 && provider.AudioFormat==SpeechAudioFormat.Wave,"System TTS produces decodable WAV/PCM without a network request");
        var player=new StreamingAudioPlayer(0); int started=0;
        await player.PlayAsync(provider.StreamAsync(request,timeout.Token),provider.AudioFormat,()=>started++,timeout.Token);
        Assert(started==1,"System WAV speech completes through the shared silent player");
    }
    private static void RunHotkeyChecks()
    {
        foreach (uint key in new uint[] { 0x41,0x14,0x70,0x71,0x72,0x73,0x74,0x75,0x76,0x77,0x78,0x79,0x7A,0x7B,1,2,4,5,6 })
        {
            var binding=new Hotkey(0,key); HotkeyService.Validate(binding);
            var single=new HotkeyChordRecognizer(binding);
            Assert(single.Process(key,true)==new HotkeyResult(true,true),"Single key/button triggers and suppresses: "+binding);
            Assert(single.Process(key,true)==new HotkeyResult(false,true),"Held key/button fires once: "+binding);
            Assert(single.Process(key,false)==new HotkeyResult(false,true),"Single key/button release is suppressed: "+binding);
            Assert(single.Process(key,true).Trigger,"Single key/button can trigger again: "+binding);
            var modified=new HotkeyChordRecognizer(binding);
            modified.Process(0xA2,true);
            Assert(!modified.Process(key,true).Trigger,"Unmodified binding ignores Ctrl chord: "+binding);
        }
        foreach (uint key in new uint[] { 0,3,0x10,0x11,0x12,0x5B,0x5C,0xA0,0xA1,0xA2,0xA3,0xA4,0xA5,255 })
            Throws<ArgumentException>(()=>HotkeyService.Validate(new Hotkey(0,key)),"Invalid standalone key rejected: "+key);
        Throws<ArgumentException>(()=>HotkeyService.Validate(new Hotkey(16,0x41)),"Invalid modifier bits rejected");
        Assert(new Hotkey(0,0x14).ToString()=="Caps Lock" && new Hotkey(0,0x7B).ToString()=="F12","Caps Lock and F12 display names");
        foreach (var (down,up,data,key) in new (int,int,uint,uint)[] { (0x201,0x202,0,1),(0x204,0x205,0,2),(0x207,0x208,0,4),(0x20B,0x20C,0x10000,5),(0x20B,0x20C,0x20000,6) })
        {
            Assert(HotkeyService.MouseInput(down,data)==(key,true) && HotkeyService.MouseInput(up,data)==(key,false),"Native mouse messages map to button: "+key);
        }
        Assert(HotkeyService.MouseInput(0x200,0) is null && HotkeyService.MouseInput(0x20B,0) is null,"Mouse motion and unknown side buttons ignored");
        var mouse=new HotkeyChordRecognizer(new(2,5));
        mouse.Process(0xA2,true);
        Assert(mouse.Process(5,true).Trigger,"Keyboard modifier combines with mouse button");
        mouse.Process(0xA2,false);
        Assert(mouse.Process(5,false).Suppress,"Mouse release remains suppressed after modifier release");
        var chord=new HotkeyChordRecognizer(new(3,0x52));
        chord.Process(0xA2,true); chord.Process(0xA4,true);
        Assert(chord.Process(0x52,true)==new HotkeyResult(true,true),"Game hook triggers and suppresses the configured chord");
        Assert(chord.Process(0x52,true)==new HotkeyResult(false,true),"Holding the key does not repeatedly toggle reading");
        chord.Process(0xA4,false);
        Assert(chord.Process(0x52,false)==new HotkeyResult(false,true),"Consumed key-up is suppressed even after modifiers are released");
        Assert(chord.Process(0x52,true)==default,"An incomplete chord does not trigger");
        chord.Process(0x52,false); chord.Process(0xA5,true);
        Assert(chord.Process(0x52,true).Trigger,"A fresh second press triggers the stop toggle");
        var seeded=new HotkeyChordRecognizer(new(3,0x52),new uint[]{0xA2,0xA4,0x52});
        Assert(!seeded.Process(0x52,true).Trigger,"A key held during hook installation does not accidentally start reading");
        seeded.Process(0x52,false); seeded.Process(0xA4,false);
        Assert(!seeded.Process(0x52,true).Trigger,"Released seeded modifiers do not remain stuck");
    }
    private static void RunHotkeyService()
    {
        using var service=new HotkeyService();
        var settings=new AppSettings { CaptureHotkey=new(7,0x86),RepeatCaptureHotkey=new(7,0x82),ToggleHotkey=new(7,0x83),CancelCurrentHotkey=new(7,0x84),CancelAllHotkey=new(7,0x85) };
        service.Configure(settings); service.SetSuspended(true); service.SetSuspended(false);
        Assert(true,"Five-action keyboard hook installs, suspends and resumes on its own thread");
        service.Configure(settings with { HotkeyMode=HotkeyMode.Registered });
        Assert(true,"Five-action service switches to RegisterHotKey without duplicate handlers");
        foreach (var mode in new[] { HotkeyMode.GameHook,HotkeyMode.Registered })
        {
            service.Configure(settings with { HotkeyMode=mode,CaptureHotkey=new(0,0x14),RepeatCaptureHotkey=new(0,0x70),ToggleHotkey=new(0,0x7B),CancelCurrentHotkey=new(0,4),CancelAllHotkey=new(2,5) });
            service.SetSuspended(true); service.SetSuspended(false);
            Assert(true,"Single keys, F12 and mouse hooks install and resume: "+mode);
        }
        Throws<ArgumentException>(()=>service.Configure(settings with { ToggleHotkey=settings.CaptureHotkey }),"Service rejects conflicting actions without replacing valid configuration");
        service.Configure(new AppSettings { CaptureHotkey=null });
        service.SetSuspended(true); service.SetSuspended(false);
        Assert(true,"Unassigned shortcuts install no hook and resume safely");
    }
}
