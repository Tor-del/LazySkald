using System.Runtime.CompilerServices;
using Windows.Media.SpeechSynthesis;

namespace ScreenReader.App;

public sealed class WindowsSpeechProvider : ISpeechProvider
{
    public static SpeechRoleSettings? CreateDefaultFallback()
    {
        try
        {
            var voice=SpeechSynthesizer.AllVoices.FirstOrDefault();
            return voice is null ? null : new SpeechRoleSettings
            {
                ProviderId="windows",ProviderVoices=new() { ["windows"]=voice.Id }
            };
        }
        catch (Exception e)
        {
            AppLog.Write("Не удалось выбрать локальный резервный голос Windows.",e);
            return null;
        }
    }

    public SpeechAudioFormat AudioFormat=>SpeechAudioFormat.Wave;
    public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult<IReadOnlyList<SpeechVoice>>(SpeechSynthesizer.AllVoices.Select(v=>new SpeechVoice(v.Id,v.DisplayName,v.Language,v.Gender.ToString())).ToArray());
    }
    public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var voice=SpeechSynthesizer.AllVoices.FirstOrDefault(v=>v.Id==request.Voice)
            ?? throw new InvalidOperationException(UiText.T("error.systemVoice"));
        using var synth=new SpeechSynthesizer { Voice=voice };
        synth.Options.SpeakingRate=Math.Clamp(1+request.RatePercent/100.0,0.5,2);
        using var speech=await synth.SynthesizeTextToStreamAsync(request.Text).AsTask(cancellationToken);
        using var stream=speech.AsStreamForRead();
        var buffer=new byte[16384];
        while (true)
        {
            int read=await stream.ReadAsync(buffer,cancellationToken);
            if (read==0) break;
            yield return buffer.AsSpan(0,read).ToArray();
        }
    }
}
