using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ScreenReader.App;

public sealed record CachedReading(string Key,string Text,string SpokenText,bool Translated,SpeechAudioFormat Format,byte[] Audio,DateTimeOffset CreatedAt)
{
    public int AudioLength=>Audio.Length;
}
public sealed record CachedReadingInfo(string Key,string Text,string SpokenText,DateTimeOffset CreatedAt,int AudioLength)
{
    public string Summary=>$"{CreatedAt.LocalDateTime:g} · {AudioLength/1024.0:N1} KB · {Text}";
}

// One atomic file per successful reading. Credentials are never stored in cache metadata.
public sealed class ReadingCache(string? directory=null)
{
    private readonly string root=directory ?? Path.Combine(SettingsStore.DataDirectory,"cache");
    private readonly object gate=new();
    private long revision;
    public string DirectoryPath=>root;
    public event Action? Changed;
    public long Revision { get { lock (gate) return revision; } }
    public static string KeyFor(string text,AppSettings settings)
    {
        string Credential(string value)=>string.IsNullOrEmpty(value) ? "" : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SecretProtection.Unprotect(value))));
        object? Llm(LlmSettings value)=>!value.Enabled ? null : new { value.Provider,value.Prompt,value.Temperature,value.TimeoutSeconds,value.Selected.BaseUrl,value.Selected.Model,Credential=Credential(value.Selected.ApiKeyProtected) };
        object Speech(AppSettings value)=>new { value.SpeechProviderId,value.SelectedVoice,value.RatePercent,
            Server=value.SpeechProviderId=="openai-compatible" ? new { value.OpenAiEndpoint,value.OpenAiModel,value.OpenAiFormat,Credential=Credential(value.OpenAiApiKeyProtected) } : null };
        var identity=new { Version=3,Text=text,settings.Language,Translation=Llm(settings.Translation),
            Target=settings.Translation.Enabled ? settings.Translation.TargetLanguage : null,
            StressMarks=settings.Translation.Enabled && settings.Translation.AddStressMarks,
            StripMarkdown=settings.Translation.Enabled && settings.Translation.StripMarkdown,
            TranslationCorrections=settings.Translation.Enabled ? ScreenReader.App.TranslationCorrections.PromptFor(text,settings.Translation) : "",
            Default=Speech(settings) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(identity))));
    }
    private string EntryPath(string key)
    {
        if (key.Length!=64 || !key.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid cache key.");
        return Path.Combine(root,key+".json");
    }
    public CachedReading? Find(string key)
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(EntryPath(key))) return null;
                var entry=JsonSerializer.Deserialize<CachedReading>(File.ReadAllText(EntryPath(key)));
                return entry is not null && entry.Key==key && entry.Audio.Length>0 && Enum.IsDefined(entry.Format) ? entry : null;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or NullReferenceException)
            { AppLog.Write("Не удалось прочитать кэш.",e); return null; }
        }
    }
    public IReadOnlyList<CachedReadingInfo> List()
    {
        lock (gate)
        {
            if (!Directory.Exists(root)) return [];
            var entries=new List<CachedReadingInfo>();
            foreach (string file in Directory.EnumerateFiles(root,"*.json"))
            {
                string key=Path.GetFileNameWithoutExtension(file);
                if (key.Length!=64 || !key.All(Uri.IsHexDigit)) continue;
                try
                {
                    // Read metadata without retaining decoded audio for every row in the UI.
                    using var stream=File.OpenRead(file);
                    var info=JsonSerializer.Deserialize<CachedReadingInfo>(stream);
                    if (info is not null && info.Key==key && info.AudioLength>0) entries.Add(info);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
                { AppLog.Write("Не удалось прочитать список кэша.",e); }
            }
            return entries.OrderByDescending(e=>e.CreatedAt).ToArray();
        }
    }
    public void Save(CachedReading entry,long expectedRevision)
    {
        lock (gate)
        {
            if (revision!=expectedRevision || entry.Audio.Length==0) return;
            Directory.CreateDirectory(root);
            string path=EntryPath(entry.Key),temporary=path+".tmp";
            try { File.WriteAllText(temporary,JsonSerializer.Serialize(entry)); File.Move(temporary,path,true); }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
        Changed?.Invoke();
    }
    public void Delete(string key)
    {
        lock (gate) { revision++; File.Delete(EntryPath(key)); }
        Changed?.Invoke();
    }
    public void Clear()
    {
        lock (gate)
        {
            revision++;
            if (Directory.Exists(root)) foreach (string file in Directory.EnumerateFiles(root,"*.json")) File.Delete(file);
        }
        Changed?.Invoke();
    }
}
