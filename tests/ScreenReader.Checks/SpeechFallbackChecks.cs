using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using ScreenReader.App;
using ScreenReader.Speech;

internal static partial class Program
{
    private static async Task RunSpeechFallbackChecksAsync()
    {
        using var registry=new SpeechProviderRegistry(); using var capture=new BlockedCapture();
        var primary=new FallbackTestSpeech();
        var fallback=new FallbackTestSpeech { Fail=false,AudioFormat=SpeechAudioFormat.Wave };
        registry.Register(new("primary-test","Primary",true,_=>primary,false));
        registry.Register(new("fallback-test","Fallback",true,_=>fallback,false));
        registry.Register(new("broken-factory","Broken",true,_=>throw new IOException("factory failure")));
        var audio=new GainTestAudio();
        var reader=new ReadingController(capture,new NoOcr(),new IdentityTextTransform(),registry,audio,new NoFocus());
        Exception? failure=null; reader.Failed+=e=>failure=e;
        var settings=new AppSettings { SpeechProviderId="primary-test",VolumePercent=65,
            ProviderVoices=new() { ["primary-test"]="main" },
            FallbackRole=new() { ProviderId="fallback-test",ProviderVoices=new() { ["fallback-test"]="backup" },
                VolumePercent=23,RatePercent=15,AudioOutputDeviceId="backup-device" } };
        var restored=JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert(restored.VolumePercent==65 && restored.FallbackRole is { VolumePercent:23 },"Voice volumes and optional fallback survive serialization");
        var defaults=new AppSettings();
        var localVoices=await new WindowsSpeechProvider().GetVoicesAsync(CancellationToken.None);
        Assert(localVoices.Count==0 ? defaults.FallbackRole is null :
            defaults.FallbackRole is { ProviderId:"windows" } local &&
            localVoices.Any(v=>v.ShortName==local.ApplyTo(defaults).SelectedVoice),
            "Default fallback automatically selects an installed Windows voice when available");
        var legacy=JsonSerializer.Deserialize<AppSettings>("{}")!;
        Assert(legacy is { VolumePercent:100,ExclusiveHotkeys:true } &&
            legacy.FallbackRole?.ProviderId==defaults.FallbackRole?.ProviderId,
            "Settings without a fallback field inherit the local Windows default");
        Assert(JsonSerializer.Deserialize<AppSettings>("{\"FallbackRole\":null}")!.FallbackRole is null,
            "Explicitly disabled fallback remains disabled after loading");
        await reader.ReadTextAsync("Text",settings);
        Assert(failure is null && primary.Calls==1 && fallback.Calls==1 && fallback.Last is { Text:"Text",Voice:"backup",RatePercent:15 },"Stream failure before sound retries the same text once with fallback without cache enabled");
        Assert(audio.Volume==0.23f && audio.Device=="backup-device" && audio.Format==SpeechAudioFormat.Wave,"Fallback playback uses its volume, device and audio format");
        primary.Fail=false; await reader.ReadTextAsync("Text",settings);
        Assert(fallback.Calls==1 && audio.Volume==0.65f,"Successful primary uses its volume and does not call fallback");
        primary.Fail=true; primary.EmitFirst=true; failure=null;
        await reader.ReadTextAsync("Text",settings);
        Assert(failure is not null && fallback.Calls==1,"Failure after sound starts does not replay text with fallback");
        primary.EmitFirst=false; failure=null;
        await reader.ReadTextAsync("Text",settings with { FallbackRole=null });
        Assert(failure is not null && fallback.Calls==1,"Disabled fallback reports original speech failure");
        failure=null; fallback.Fail=true;
        await reader.ReadTextAsync("Text",settings);
        Assert(failure is not null && fallback.Calls==2,"Fallback failure stops after one attempt");
        fallback.Fail=false; failure=null;
        await reader.ReadTextAsync("Text",settings with { SpeechProviderId="broken-factory" });
        Assert(failure is null && fallback.Calls==3,"Provider construction failure also selects fallback");
        fallback.Fail=true; failure=null;
        await reader.ReadTextAsync("Text",settings with { SpeechProviderId="broken-factory" });
        Assert(failure is not null && fallback.Calls==4,"Failed fallback after provider construction failure is not retried");
        primary.Wait=true; failure=null;
        var pending=reader.ReadTextAsync("Text",settings);
        await primary.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3)); reader.Stop(); await pending.WaitAsync(TimeSpan.FromSeconds(3));
        Assert(failure is null && fallback.Calls==4,"Cancelling pending synthesis does not invoke fallback");
        Assert(!new SpeechRoleSettings { VolumePercent=-1 }.IsStructurallyValid() && !new SpeechRoleSettings { VolumePercent=101 }.IsStructurallyValid(),"Out-of-range voice volumes are rejected");
        Assert(ReadingCache.KeyFor("Text",settings)==ReadingCache.KeyFor("Text",settings with { VolumePercent=10 }),"Changing playback volume reuses cached unmodified audio");
    }
    private sealed class FallbackTestSpeech : ISpeechProvider
    {
        public bool Fail=true,EmitFirst,Wait;
        public int Calls;
        public SpeechRequest? Last;
        public SpeechAudioFormat AudioFormat { get; set; }
        public readonly TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken token)=>Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
        public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken token)
        {
            Calls++; Last=request;
            if (Wait) { Entered.TrySetResult(); await Task.Delay(Timeout.Infinite,token); }
            await Task.Yield();
            if (EmitFirst) yield return new byte[] { 1 };
            if (Fail) throw new IOException("test synthesis failure");
            yield return new byte[] { 2 };
        }
    }
    private sealed class GainTestAudio : IAudioPlayer
    {
        public float Volume; public string? Device; public SpeechAudioFormat Format;
        public Task PlayAsync(IAsyncEnumerable<byte[]> chunks,Action started,CancellationToken token)=>PlayAsync(chunks,SpeechAudioFormat.Mp3,"",1,started,token);
        public Task PlayAsync(IAsyncEnumerable<byte[]> chunks,SpeechAudioFormat format,Action started,CancellationToken token)=>PlayAsync(chunks,format,"",1,started,token);
        public async Task PlayAsync(IAsyncEnumerable<byte[]> chunks,SpeechAudioFormat format,string device,float volume,Action started,CancellationToken token)
        {
            Volume=volume; Device=device; Format=format;
            await foreach (var chunk in chunks.WithCancellation(token)) started();
        }
    }
}
