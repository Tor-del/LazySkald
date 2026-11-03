using ScreenReader.Edge;

namespace ScreenReader.App;

public sealed record SpeechProviderDefinition(string Id,string Name,bool CustomVoice,Func<AppSettings,ISpeechProvider> Create,bool DisposeAfterUse=true)
{
    public string DisplayName=>UiText.Keys.Contains("provider."+Id) ? UiText.T("provider."+Id) : Name;
}
public sealed record SpeechProviderLease(ISpeechProvider Provider,bool Owned) : IDisposable
{
    public void Dispose() { if (Owned && Provider is IDisposable disposable) disposable.Dispose(); }
}
public sealed class SpeechProviderRegistry : IDisposable
{
    private readonly Dictionary<string,SpeechProviderDefinition> definitions=new(StringComparer.OrdinalIgnoreCase);
    public EdgeSpeechClient Edge { get; }=new();
    private readonly WindowsSpeechProvider windows=new();
    public IReadOnlyList<SpeechProviderDefinition> Definitions=>definitions.Values.ToArray();
    public SpeechProviderRegistry()
    {
        Register(new("edge","Microsoft Edge TTS (онлайн)",false,_=>Edge,false));
        Register(new("windows","Windows TTS (локально)",false,_=>windows,false));
        Register(new("openai-compatible","OpenAI-совместимый сервер",true,s=>new OpenAiSpeechClient(new(s.OpenAiEndpoint,s.OpenAiModel,SecretProtection.Unprotect(s.OpenAiApiKeyProtected),s.OpenAiFormat))));
    }
    public void Register(SpeechProviderDefinition definition)=>definitions.Add(definition.Id,definition);
    public SpeechProviderDefinition Get(string id)=>definitions.TryGetValue(id,out var value) ? value : throw new ArgumentException(UiText.T("error.unknownProvider",id));
    public SpeechProviderLease Acquire(AppSettings settings)
    {
        var definition=Get(settings.SpeechProviderId);
        return new(definition.Create(settings),definition.DisposeAfterUse);
    }
    public void Dispose()=>Edge.Dispose();
}
