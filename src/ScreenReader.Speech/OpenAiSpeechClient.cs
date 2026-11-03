using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;

namespace ScreenReader.Speech;

public sealed record OpenAiSpeechOptions(string Endpoint,string Model,string ApiKey,SpeechAudioFormat Format=SpeechAudioFormat.Mp3);

public sealed class OpenAiSpeechClient : ISpeechProvider,IDisposable
{
    private readonly OpenAiSpeechOptions options;
    private readonly HttpClient http;
    private readonly bool ownsClient;
    public SpeechAudioFormat AudioFormat=>options.Format;
    public OpenAiSpeechClient(OpenAiSpeechOptions options,HttpClient? http=null)
    {
        Validate(options);
        this.options=options;
        ownsClient=http is null;
        this.http=http ?? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect=false,ConnectTimeout=TimeSpan.FromSeconds(15) }) { Timeout=Timeout.InfiniteTimeSpan };
    }
    public static void Validate(OpenAiSpeechOptions options)
    {
        if (!Uri.TryCreate(options.Endpoint,UriKind.Absolute,out var uri) || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Fragment))
            throw new ArgumentException(SpeechText.Get("tts.url","Укажите полный HTTP(S)-адрес TTS, например http://127.0.0.1:5050/v1/audio/speech.","Enter the full HTTP(S) TTS URL, for example http://127.0.0.1:5050/v1/audio/speech."));
        if (string.IsNullOrWhiteSpace(options.Model)) throw new ArgumentException(SpeechText.Get("tts.model","Укажите модель TTS-сервера.","Enter the TTS server model."));
        if (!Enum.IsDefined(options.Format)) throw new ArgumentException(SpeechText.Get("tts.format","Неизвестный формат аудио.","Unknown audio format."));
    }
    // There is no universal voice-list endpoint in the compatible speech API.
    public Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)=>Task.FromResult<IReadOnlyList<SpeechVoice>>([]);
    public async IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,[EnumeratorCancellation] CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(request.Voice)) throw new ArgumentException(SpeechText.Get("tts.voice","Укажите имя голоса на TTS-сервере.","Enter the voice name used by the TTS server."));
        if (string.IsNullOrWhiteSpace(request.Text)) yield break;
        if (request.Text.Length>4096) throw new ArgumentException(SpeechText.Get("tts.limit","Для OpenAI-совместимого TTS выберите фрагмент до 4096 символов.","Select up to 4096 characters for the OpenAI-compatible TTS server."));
        using var message=new HttpRequestMessage(HttpMethod.Post,options.Endpoint);
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) message.Headers.Authorization=new AuthenticationHeaderValue("Bearer",options.ApiKey);
        message.Content=JsonContent.Create(new { model=options.Model,input=request.Text,voice=request.Voice,response_format=options.Format==SpeechAudioFormat.Mp3 ? "mp3" : "wav",speed=1+request.RatePercent/100.0 });
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        using var response=await http.SendAsync(message,HttpCompletionOption.ResponseHeadersRead,timeout.Token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException(SpeechText.Get("tts.http","TTS-сервер вернул HTTP {0}. Проверьте адрес, модель, голос и ключ.","TTS server returned HTTP {0}. Check the URL, model, voice and key.",(int)response.StatusCode),null,response.StatusCode);
        string type=response.Content.Headers.ContentType?.MediaType ?? "";
        if (type.Contains("json",StringComparison.OrdinalIgnoreCase) || type.StartsWith("text/",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(SpeechText.Get("tts.textResponse","TTS-сервер вернул текст вместо аудио. Проверьте полный адрес /v1/audio/speech.","TTS server returned text instead of audio. Check the full /v1/audio/speech URL."));
        if (options.Format==SpeechAudioFormat.Mp3 && type.Contains("wav",StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(SpeechText.Get("tts.wavResponse","TTS-сервер вернул WAV. Выберите WAV в настройках сервера.","TTS server returned WAV. Select WAV in the server settings."));
        using var stream=await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        var buffer=new byte[16384]; long length=0;
        while (true)
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            int read=await stream.ReadAsync(buffer,timeout.Token).ConfigureAwait(false);
            if (read==0) break;
            length+=read;
            if (length>100_000_000) throw new InvalidDataException(SpeechText.Get("tts.largeResponse","Ответ TTS-сервера превышает 100 МБ.","The TTS response exceeds 100 MB."));
            yield return buffer.AsSpan(0,read).ToArray();
        }
        if (length==0) throw new InvalidDataException(SpeechText.Get("tts.empty","TTS-сервер вернул пустое аудио.","TTS server returned empty audio."));
    }
    public void Dispose() { if (ownsClient) http.Dispose(); }
}
