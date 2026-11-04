namespace ScreenReader.App;

public sealed record SpeechRoleSettings
{
    public string ProviderId { get; init; }="edge";
    public Dictionary<string,string> ProviderVoices { get; init; }=new(StringComparer.OrdinalIgnoreCase);
    public int VolumePercent { get; init; }=100;
    public int RatePercent { get; init; }
    public string AudioOutputDeviceId { get; init; }="";
    public string Endpoint { get; init; }="http://127.0.0.1:5050/v1/audio/speech";
    public string Model { get; init; }="tts-1";
    public string ApiKeyProtected { get; init; }="";
    public SpeechAudioFormat Format { get; init; }=SpeechAudioFormat.Mp3;

    public bool IsStructurallyValid()=>VolumePercent is >= 0 and <= 100 && !string.IsNullOrWhiteSpace(ProviderId) && ProviderVoices is not null && RatePercent is >= -50 and <= 100 && Enum.IsDefined(Format);

    public static SpeechRoleSettings From(AppSettings settings)=>new()
    {
        ProviderId=settings.SpeechProviderId,
        ProviderVoices=new Dictionary<string,string>(settings.ProviderVoices ?? new()) { [settings.SpeechProviderId]=settings.SelectedVoice },
        VolumePercent=settings.VolumePercent,RatePercent=settings.RatePercent,AudioOutputDeviceId=settings.AudioOutputDeviceId,
        Endpoint=settings.OpenAiEndpoint,Model=settings.OpenAiModel,
        ApiKeyProtected=settings.OpenAiApiKeyProtected,Format=settings.OpenAiFormat
    };

    public AppSettings ApplyTo(AppSettings settings)=>settings with
    {
        VolumePercent=VolumePercent,SpeechProviderId=ProviderId,ProviderVoices=new(ProviderVoices),RatePercent=RatePercent,
        Voice=ProviderVoices.GetValueOrDefault("edge",settings.Voice),WindowsVoice=ProviderVoices.GetValueOrDefault("windows",settings.WindowsVoice),
        OpenAiVoice=ProviderVoices.GetValueOrDefault("openai-compatible",settings.OpenAiVoice),
        AudioOutputDeviceId=AudioOutputDeviceId,OpenAiEndpoint=Endpoint,OpenAiModel=Model,
        OpenAiApiKeyProtected=ApiKeyProtected,OpenAiFormat=Format
    };
}
