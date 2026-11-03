using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;

internal static partial class Program
{
    private static async Task RunCacheChecksAsync()
    {
        string directory=Path.Combine(Path.GetTempPath(),"ScreenReader-cache-check-"+Guid.NewGuid().ToString("N"));
        var cache=new ReadingCache(directory);
        var settings=new AppSettings { CacheEnabled=true,SpeechProviderId="cache-test",Translation=new() { Enabled=true },Genderization=new() { Enabled=true } };
        string Key(AppSettings value)=>ReadingCache.KeyFor("Original",value);
        string key=Key(settings);
        Assert(key==Key(settings with { UiLanguage="en",AudioOutputDeviceId="other",CaptureMode=CaptureMode.Gdi }),"Cache identity ignores UI, capture and output device");
        Assert(key!=ReadingCache.KeyFor("original",settings),"Cache text match is exact and case sensitive");
        Assert(key!=Key(settings with { RatePercent=10 }) && key!=Key(settings with { Voice="another" }),"Voice and speed invalidate cached audio");
        Assert(key!=Key(settings with { Translation=settings.Translation with { Prompt="different" } }) && key!=Key(settings with { Translation=settings.Translation with { TargetLanguage="en-US" } }),"Translation prompt and language invalidate cache");
        Assert(key!=Key(settings with { Translation=settings.Translation with { AddStressMarks=true } }),"Translation stress marks invalidate cached audio");
        Assert(Key(settings with { Translation=settings.Translation with { Enabled=false } })==Key(settings with { Translation=settings.Translation with { Enabled=false,AddStressMarks=true } }),"Stress marks do not affect cache when translation is disabled");
        Assert(key!=Key(settings with { Genderization=settings.Genderization with { Temperature=1 } }) && key!=Key(settings with { FemaleRole=new() { RatePercent=20 } }),"Gender settings and complete role profiles invalidate cache");
        var server=settings with { SpeechProviderId="openai-compatible",OpenAiApiKeyProtected=SecretProtection.Protect("key") };
        Assert(ReadingCache.KeyFor("text",server)==ReadingCache.KeyFor("text",server with { OpenAiApiKeyProtected=SecretProtection.Protect("key") }),"Re-encrypting unchanged API key preserves cache identity");
        Assert(ReadingCache.KeyFor("text",server)!=ReadingCache.KeyFor("text",server with { OpenAiModel="other" }),"TTS model invalidates cache");

        using var registry=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var speech=new CacheSpeech(); registry.Register(new("cache-test","Test",false,_=>speech,false));
        var translator=new TestTranslator(); var gender=new TestGenderization { Result=SpeakerGender.Female };
        var player=new CacheAudio();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,player,new NoFocus(),translator,gender,cache);
        Exception? failed=null; string? preview=null;
        reader.Failed+=e=>failed=e; reader.TextTranslated+=value=>preview=value;
        try
        {
            await reader.ReadTextAsync("Original",settings);
            Assert(failed is null && speech.Calls==1 && cache.List().Count==1,"Successful streaming reading populates persistent cache");
            var persisted=new ReadingCache(directory).Find(key);
            Assert(persisted is { SpokenText:"Перевод",Gender:SpeakerGender.Female,Format:SpeechAudioFormat.Wave } && persisted.Audio.SequenceEqual(new byte[] { 1,2,3 }),"New cache instance restores text, translation, gender, format and exact audio");
            translator.Fail=true; gender.Fail=true;
            await reader.ReadTextAsync("Original",settings);
            Assert(failed is null && speech.Calls==1 && gender.Calls==1 && player.Calls==2 && preview=="Перевод","Cache hit bypasses translation, genderization and TTS and restores preview");
            await reader.ReplayAsync(persisted!,settings);
            Assert(player.Calls==3 && speech.Calls==1,"Explicit replay uses saved track without provider calls");
            await reader.ReadTextAsync("Original",settings with { CacheEnabled=false });
            Assert(failed is not null && player.Calls==3 && cache.List().Count==1,"Disabled cache bypasses lookup and preserves saved entries");
            failed=null; translator.Fail=false; gender.Fail=false;
            await reader.ReadTextAsync("Original",settings with { RatePercent=5 });
            Assert(failed is null && speech.Calls==2 && cache.List().Count==2,"Changed settings create a separate cache entry");
            cache.Delete(key);
            Assert(cache.Find(key) is null && cache.List().Count==1,"Individual deletion leaves other settings variants intact");
            long revision=cache.Revision;
            cache.Clear(); cache.Save(persisted!,revision);
            Assert(cache.List().Count==0,"Clearing invalidates in-flight cache writes");
            speech.Fail=true;
            await reader.ReadTextAsync("Partial",settings);
            Assert(failed is not null && cache.List().Count==0,"Partial failed stream is never cached");
            speech.Fail=false; failed=null;
            player.AfterRead=reader.Stop;
            await reader.ReadTextAsync("Cancelled",settings);
            Assert(failed is null && cache.List().Count==0,"Cancellation after stream consumption prevents caching");
            player.AfterRead=null;
            File.WriteAllText(Path.Combine(directory,key+".json"),"invalid JSON");
            Assert(cache.Find(key) is null,"Corrupt cached entry behaves as a cache miss");
            await reader.ReadTextAsync("Original",settings);
            Assert(cache.Find(key) is not null,"Successful generation replaces corrupt cached entry");
        }
        finally { cache.Clear(); if (Directory.Exists(directory)) Directory.Delete(directory); }
    }
    private sealed class CacheSpeech : ISpeechProvider
    {
        public int Calls;
        public bool Fail;
        public SpeechAudioFormat AudioFormat=>SpeechAudioFormat.Wave;
        public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken ct)=>Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
        public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            Calls++; await Task.Yield(); ct.ThrowIfCancellationRequested();
            yield return new byte[] { 1,2 };
            if (Fail) throw new IOException("Partial stream");
            yield return new byte[] { 3 };
        }
    }
    private sealed class CacheAudio : IAudioPlayer
    {
        public int Calls;
        public Action? AfterRead;
        public Task PlayAsync(IAsyncEnumerable<byte[]> audio,Action started,CancellationToken ct)=>PlayAsync(audio,SpeechAudioFormat.Mp3,started,ct);
        public async Task PlayAsync(IAsyncEnumerable<byte[]> audio,SpeechAudioFormat format,Action started,CancellationToken ct)
        {
            Calls++;
            await foreach (var bytes in audio.WithCancellation(ct)) { ct.ThrowIfCancellationRequested(); }
            AfterRead?.Invoke();
        }
    }
}
