namespace ScreenReader.Speech;

public enum SpeechAudioFormat { Mp3, Wave }
public sealed record SpeechVoice(string ShortName,string FriendlyName,string Locale,string Gender)
{
    public override string ToString()=>FriendlyName;
}
public sealed record SpeechRequest(string Text,string Voice,string Language,int RatePercent);
public interface ISpeechProvider
{
    SpeechAudioFormat AudioFormat=>SpeechAudioFormat.Mp3;
    Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken);
    IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,CancellationToken cancellationToken);
}
