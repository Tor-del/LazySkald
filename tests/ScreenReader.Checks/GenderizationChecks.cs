using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;

internal static partial class Program
{
    private static async Task RunGenderizationChecksAsync()
    {
        foreach (string provider in new[] { "lmstudio","ollama","openai" })
        {
            var settings=new GenderizationSettings { Enabled=true,Provider=provider,Providers=new() { [provider]=new() { BaseUrl=LlmSettings.DefaultUrl(provider),Model="gender-model" } } };
            foreach (var (reply,expected) in new[] { ("female",SpeakerGender.Female),("male",SpeakerGender.Male),("unknown",SpeakerGender.Unknown),("ambiguous explanation",SpeakerGender.Unknown) })
            {
                using var http=new HttpClient(new Handler(async (request,ct)=>
                {
                    using var body=JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
                    var root=body.RootElement;
                    Assert(root.GetProperty("model").GetString()=="gender-model","Gender model selected: "+provider);
                    using var input=JsonDocument.Parse(root.GetProperty("messages")[1].GetProperty("content").GetString()!);
                    Assert(input.RootElement.GetProperty("originalText").GetString()=="Я пришла." && input.RootElement.GetProperty("spokenText").GetString()=="I arrived.","Gender receives original and translated text separately: "+provider);
                    return JsonResponse(provider=="ollama" ? JsonSerializer.Serialize(new { message=new { content=reply } }) : JsonSerializer.Serialize(new { choices=new[] { new { message=new { content=reply },finish_reason="stop" } } }));
                }));
                var client=new LlmGenderizationClient(http);
                Assert(await client.DetectAsync("Я пришла.","I arrived.",settings,CancellationToken.None)==expected,"Gender reply parsed safely: "+provider+" / "+reply);
                Assert(await client.DetectAsync("","",settings with { Enabled=false },CancellationToken.None)==SpeakerGender.Unknown,"Disabled gender detection skips HTTP");
                using var cancelled=new CancellationTokenSource(); cancelled.Cancel();
                bool stopped=false;
                try { await client.DetectAsync("","",settings,cancelled.Token); } catch (OperationCanceledException) { stopped=true; }
                Assert(stopped,"Cancelled gender detection skips HTTP");
            }
        }
        var old=JsonSerializer.Deserialize<AppSettings>("{\"Voice\":\"legacy-voice\"}")!;
        Assert(old.OcrProviderId=="windows" && !old.Genderization.Enabled && old.VoiceFor(SpeakerGender.Male)=="legacy-voice","Legacy settings default to Windows OCR and disabled genderization with original voice");
        var settingsWithRoles=new AppSettings { SpeechProviderId="roles-test",ProviderVoices=new() { ["roles-test"]="neutral" },FemaleProviderVoices=new() { ["roles-test"]="female-voice" },MaleProviderVoices=new() { ["roles-test"]="male-voice" },Translation=new() { Enabled=true },Genderization=new() { Enabled=true,Provider="lmstudio",Providers=new() { ["lmstudio"]=new() { BaseUrl="http://localhost/v1",Model="gender-model" } } } };
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settingsWithRoles))!;
        Assert(restored.VoiceFor(SpeakerGender.Female)=="female-voice" && restored.VoiceFor(SpeakerGender.Male)=="male-voice" && restored.Genderization.Selected.Model=="gender-model","Role voices and gender configuration round-trip through JSON");
        using var registry=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var speech=new RecordingTranslationSpeech(); registry.Register(new("roles-test","Test",false,_=>speech,false));
        var detector=new TestGenderization();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,new ConsumingAudio(),new NoFocus(),new TestTranslator(),detector);
        SpeakerGender? preview=null; Exception? failed=null;
        reader.GenderDetected+=value=>preview=value; reader.Failed+=error=>failed=error;
        foreach (var (gender,voice) in new[] { (SpeakerGender.Female,"female-voice"),(SpeakerGender.Male,"male-voice"),(SpeakerGender.Unknown,"neutral") })
        {
            detector.Result=gender;
            await reader.ReadTextAsync("Original",settingsWithRoles);
            Assert(detector.Original=="Original" && detector.Spoken=="Перевод","Translation precedes gender detection and preserves original context");
            Assert(speech.Last is { Text:"Перевод" } request && request.Gender==gender && request.Voice==voice && preview==gender,"Pipeline forwards gender and correct role voice: "+gender);
        }
        detector.Result=SpeakerGender.Male;
        await reader.ReadTextAsync("Original",settingsWithRoles with { MaleProviderVoices=new() });
        Assert(speech.Last is { Voice:"neutral",Gender:SpeakerGender.Male },"Missing role voice uses default while retaining detected gender");
        int calls=detector.Calls;
        await reader.ReadTextAsync("Original",settingsWithRoles with { Genderization=new() });
        Assert(detector.Calls==calls && speech.Last is { Voice:"neutral",Gender:SpeakerGender.Unknown },"Disabled gender stage preserves existing speech behavior");
        await reader.ReadTextAsync("Original",settingsWithRoles with { Translation=new() });
        Assert(detector.Spoken=="Original" && speech.Last is { Text:"Original",Gender:SpeakerGender.Male },"Gender detection works without translation");
        var femaleSpeech=new RecordingTranslationSpeech { AudioFormat=SpeechAudioFormat.Wave };
        var maleSpeech=new RecordingTranslationSpeech();
        AppSettings? femaleConfiguration=null,maleConfiguration=null;
        registry.Register(new("female-tts","Female TTS",true,config=> { femaleConfiguration=config; return femaleSpeech; },false));
        registry.Register(new("male-tts","Male TTS",true,config=> { maleConfiguration=config; return maleSpeech; },false));
        var independent=settingsWithRoles with
        {
            FemaleRole=new() { ProviderId="female-tts",ProviderVoices=new() { ["female-tts"]="female-independent" },RatePercent=25,AudioOutputDeviceId="female-device",Endpoint="http://localhost:6001/speech",Model="female-model",ApiKeyProtected=SecretProtection.Protect("female-secret"),Format=SpeechAudioFormat.Wave },
            MaleRole=new() { ProviderId="male-tts",ProviderVoices=new() { ["male-tts"]="male-independent" },RatePercent=-15,AudioOutputDeviceId="male-device",Endpoint="http://localhost:6002/speech",Model="male-model",ApiKeyProtected=SecretProtection.Protect("male-secret") }
        };
        var roundtrip=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(independent))!;
        Assert(roundtrip.FemaleRole?.ProviderId=="female-tts" && roundtrip.MaleRole?.ProviderId=="male-tts" && roundtrip.SpeechFor(SpeakerGender.Female).OpenAiModel=="female-model" && SecretProtection.Unprotect(roundtrip.MaleRole.ApiKeyProtected)=="male-secret","Independent full speech profiles survive serialization");
        var roleAudio=new ConsumingAudio();
        var roleReader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,roleAudio,new NoFocus(),new TestTranslator(),detector);
        detector.Result=SpeakerGender.Female; speech.Last=null; maleSpeech.Last=null;
        await roleReader.ReadTextAsync("Original",independent);
        Assert(femaleSpeech.Last is { Voice:"female-independent",RatePercent:25,Gender:SpeakerGender.Female } && maleSpeech.Last is null && speech.Last is null,"Female detection routes to its own TTS provider and rate");
        Assert(femaleConfiguration is { OpenAiEndpoint:"http://localhost:6001/speech",OpenAiModel:"female-model",OpenAiFormat:SpeechAudioFormat.Wave } && SecretProtection.Unprotect(femaleConfiguration.OpenAiApiKeyProtected)=="female-secret" && roleAudio.DeviceId=="female-device" && roleAudio.Format==SpeechAudioFormat.Wave,"Female provider receives its endpoint, model, key, format and output device");
        detector.Result=SpeakerGender.Male; femaleSpeech.Last=null;
        await roleReader.ReadTextAsync("Original",independent);
        Assert(maleSpeech.Last is { Voice:"male-independent",RatePercent:-15,Gender:SpeakerGender.Male } && femaleSpeech.Last is null && speech.Last is null,"Male detection routes to a different TTS provider");
        Assert(maleConfiguration is { OpenAiEndpoint:"http://localhost:6002/speech",OpenAiModel:"male-model",OpenAiFormat:SpeechAudioFormat.Mp3 } && SecretProtection.Unprotect(maleConfiguration.OpenAiApiKeyProtected)=="male-secret" && roleAudio.DeviceId=="male-device" && roleAudio.Format==SpeechAudioFormat.Mp3,"Male provider receives independent server and output parameters");
        detector.Result=SpeakerGender.Unknown;
        await roleReader.ReadTextAsync("Original",independent);
        Assert(speech.Last is { Voice:"neutral",Gender:SpeakerGender.Unknown },"Unknown gender selects default provider despite configured role providers");
        detector.Result=SpeakerGender.Male; maleSpeech.Last=null;
        await roleReader.ReadTextAsync("Original",independent with { Genderization=new() });
        Assert(speech.Last is { Voice:"neutral",Gender:SpeakerGender.Unknown } && maleSpeech.Last is null,"Disabling genderization bypasses all role providers");
        await roleReader.ReadTextAsync("Original",independent with { MaleRole=null,MaleProviderVoices=new() });
        Assert(speech.Last is { Voice:"neutral",Gender:SpeakerGender.Male } && maleSpeech.Last is null,"Removed male role falls back to default TTS provider");
        detector.Fail=true; speech.Last=null;
        await reader.ReadTextAsync("Original",settingsWithRoles);
        Assert(failed is not null && speech.Last is null && preview==SpeakerGender.Unknown,"Gender request failure prevents speech and clears old gender result");

        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var waitingHttp=new HttpClient(new Handler(async (_,ct)=> { entered.TrySetResult(); await Task.Delay(Timeout.Infinite,ct); return JsonResponse("{}"); }));
        reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,new ConsumingAudio(),new NoFocus(),new TestTranslator(),new LlmGenderizationClient(waitingHttp));
        bool failure=false; reader.Failed+=_=>failure=true; speech.Last=null;
        var pending=reader.ReadTextAsync("Original",settingsWithRoles);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); reader.Stop(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(!reader.IsBusy && !failure && speech.Last is null,"Stop cancels in-flight gender LLM request and prevents speech");
    }
    private sealed class TestGenderization : IGenderizationClient
    {
        public SpeakerGender Result;
        public string? Original,Spoken;
        public bool Fail;
        public int Calls;
        public Task<SpeakerGender> DetectAsync(string originalText,string spokenText,GenderizationSettings settings,CancellationToken token)
        {
            Calls++; Original=originalText; Spoken=spokenText;
            return Fail ? throw new InvalidOperationException("Gender test failure") : Task.FromResult(Result);
        }
    }
}
