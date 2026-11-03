// SPDX-License-Identifier: LGPL-3.0-or-later
// Protocol adapted from rany2/edge-tts; see THIRD-PARTY-NOTICES.md.
using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Security;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ScreenReader.Speech;

namespace ScreenReader.Edge;

public sealed class EdgeSpeechClient : ISpeechProvider, IDisposable
{
    private const string BaseUrl = "speech.platform.bing.com/consumer/speech/synthesize/readaloud";
    private const string Origin = "chrome-extension://jdiccldimpdaibmpdkjnbmckianbfold";
    private const string UserAgent = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/143.0.0.0 Safari/537.36 Edg/143.0.0.0";
    private readonly HttpClient http = new() { Timeout = TimeSpan.FromSeconds(15) };
    private TimeSpan clockOffset;
    private string Query => $"TrustedClientToken={EdgeToken.TrustedClientToken}&Sec-MS-GEC={EdgeToken.Generate(DateTimeOffset.UtcNow+clockOffset)}&Sec-MS-GEC-Version=2-{EdgeToken.ChromiumVersion}";

    public async Task<IReadOnlyList<SpeechVoice>> GetVoicesAsync(CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            using var message = new HttpRequestMessage(HttpMethod.Get,$"https://{BaseUrl}/voices/list?{Query}");
            message.Headers.TryAddWithoutValidation("User-Agent",UserAgent);
            message.Headers.TryAddWithoutValidation("Cookie",$"muid={Guid.NewGuid():N};");
            using var response = await http.SendAsync(message,cancellationToken).ConfigureAwait(false);
            if (response.Headers.Date is { } serverTime) clockOffset = serverTime-DateTimeOffset.UtcNow;
            if (response.StatusCode == HttpStatusCode.Forbidden && attempt == 0) continue;
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.EnumerateArray().Select(v => new SpeechVoice(
                v.GetProperty("ShortName").GetString()!,
                v.TryGetProperty("FriendlyName",out var n) ? n.GetString()! : v.GetProperty("ShortName").GetString()!,
                v.GetProperty("Locale").GetString()!,v.GetProperty("Gender").GetString()!)).ToArray();
        }
    }

    public IAsyncEnumerable<byte[]> StreamAsync(SpeechRequest request,CancellationToken cancellationToken) =>
        SpeechStreamRetry.Run(ct=>StreamWithoutRetryAsync(request,ct),request.Voice,cancellationToken);

    private async IAsyncEnumerable<byte[]> StreamWithoutRetryAsync(SpeechRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (!Regex.IsMatch(request.Voice,@"^[a-zA-Z0-9-]+$") || !Regex.IsMatch(request.Language,@"^[a-zA-Z0-9-]+$"))
            throw new ArgumentException(SpeechText.Get("edge.language","Некорректное имя голоса или языка.","Invalid voice or language name."));
        if (request.RatePercent is < -50 or > 100) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.Text.Length > 30000) throw new ArgumentException(SpeechText.Get("edge.limit","Слишком большой фрагмент: максимум 30 000 символов.","The text exceeds the 30,000 character limit."));
        foreach (string part in SplitText(request.Text))
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var socket = new ClientWebSocket();
            socket.Options.SetRequestHeader("User-Agent",UserAgent);
            socket.Options.SetRequestHeader("Origin",Origin);
            socket.Options.SetRequestHeader("Pragma","no-cache");
            socket.Options.SetRequestHeader("Cache-Control","no-cache");
            socket.Options.SetRequestHeader("Cookie",$"muid={Guid.NewGuid():N};");
            using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
            {
                connectTimeout.CancelAfter(TimeSpan.FromSeconds(12));
                await socket.ConnectAsync(new Uri($"wss://{BaseUrl}/edge/v1?{Query}&ConnectionId={Guid.NewGuid():N}"),connectTimeout.Token).ConfigureAwait(false);
            }
            string timestamp = DateTime.UtcNow.ToString("ddd MMM dd yyyy HH:mm:ss 'GMT+0000 (Coordinated Universal Time)'",CultureInfo.InvariantCulture);
            string config = "X-Timestamp:"+timestamp+"\r\nContent-Type:application/json; charset=utf-8\r\nPath:speech.config\r\n\r\n"+
                "{\"context\":{\"synthesis\":{\"audio\":{\"metadataoptions\":{\"sentenceBoundaryEnabled\":\"true\",\"wordBoundaryEnabled\":\"false\"},\"outputFormat\":\"audio-24khz-48kbitrate-mono-mp3\"}}}}\r\n";
            string rate = request.RatePercent.ToString("+0;-0;+0",CultureInfo.InvariantCulture)+"%";
            // Match Edge's envelope locale; the selected voice controls spoken language.
            string ssml = $"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='en-US'><voice name='{SecurityElement.Escape(ToServiceVoiceName(request.Voice))}'><prosody pitch='+0Hz' rate='{rate}' volume='+0%'>{SecurityElement.Escape(part)}</prosody></voice></speak>";
            await SendAsync(socket,config,cancellationToken).ConfigureAwait(false);
            await SendAsync(socket,$"X-RequestId:{Guid.NewGuid():N}\r\nContent-Type:application/ssml+xml\r\nX-Timestamp:{timestamp}Z\r\nPath:ssml\r\n\r\n{ssml}",cancellationToken).ConfigureAwait(false);
            bool audioReceived = false, turnEnded = false;
            byte[] buffer = new byte[16384];
            using var idleTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            while (!turnEnded)
            {
                using var frame = new MemoryStream();
                WebSocketReceiveResult received;
                do
                {
                    idleTimeout.CancelAfter(TimeSpan.FromSeconds(20));
                    received = await socket.ReceiveAsync(new ArraySegment<byte>(buffer),idleTimeout.Token).ConfigureAwait(false);
                    if (received.MessageType == WebSocketMessageType.Close) throw new IOException(SpeechText.Get("edge.closed","Edge TTS закрыл соединение до завершения речи.","Edge TTS closed the connection before speech finished."));
                    frame.Write(buffer,0,received.Count);
                    if (frame.Length > 1048576) throw new InvalidDataException(SpeechText.Get("edge.largeResponse","Слишком большой ответ Edge TTS.","The Edge TTS response is too large."));
                } while (!received.EndOfMessage);
                byte[] data = frame.ToArray();
                if (received.MessageType == WebSocketMessageType.Binary)
                {
                    byte[] audio = ParseAudioFrame(data);
                    if (audio.Length > 0) { audioReceived = true; yield return audio; }
                }
                else
                {
                    string headers = Encoding.UTF8.GetString(data).Split("\r\n\r\n",2)[0];
                    turnEnded = headers.Split("\r\n").Any(h => h.Equals("Path:turn.end",StringComparison.OrdinalIgnoreCase));
                }
            }
            if (!audioReceived) throw new IOException(SpeechText.Get("edge.empty","Edge TTS не вернул аудио. Проверьте голос и подключение к интернету.","Edge TTS returned no audio. Check the voice and internet connection."));
        }
    }
    private static Task SendAsync(ClientWebSocket socket, string text, CancellationToken ct) =>
        socket.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(text)),WebSocketMessageType.Text,true,ct);

    public static string ToServiceVoiceName(string shortName)
    {
        var match=Regex.Match(shortName,@"^([a-z]{2,})-([A-Z]{2,})-(.+Neural)$");
        if (!match.Success) throw new ArgumentException(SpeechText.Get("edge.voice","Некорректное имя голоса.","Invalid voice name."));
        string locale=match.Groups[1].Value+"-"+match.Groups[2].Value;
        string name=match.Groups[3].Value;
        int separator=name.IndexOf('-');
        if (separator>=0) { locale+="-"+name[..separator]; name=name[(separator+1)..]; }
        return $"Microsoft Server Speech Text to Speech Voice ({locale}, {name})";
    }

    public static byte[] ParseAudioFrame(ReadOnlySpan<byte> frame)
    {
        if (frame.Length < 2) throw new InvalidDataException(SpeechText.Get("edge.headerLength","Нет длины заголовка Edge TTS.","Missing Edge TTS header length."));
        int headerLength = BinaryPrimitives.ReadUInt16BigEndian(frame);
        if (headerLength > frame.Length-2) throw new InvalidDataException(SpeechText.Get("edge.header","Повреждённый заголовок Edge TTS.","Invalid Edge TTS header."));
        string headers = Encoding.UTF8.GetString(frame.Slice(2,headerLength));
        if (!headers.Split("\r\n").Any(h => h.Equals("Path:audio",StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException(SpeechText.Get("edge.message","Неожиданный тип бинарного сообщения Edge TTS.","Unexpected Edge TTS binary message type."));
        return frame[(2+headerLength)..].ToArray();
    }

    public static IEnumerable<string> SplitText(string text)
    {
        // Count escaped UTF-8 bytes; never cut inside a Unicode scalar or XML entity.
        var part = new StringBuilder(); int bytes = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value < 32 && rune.Value is not (9 or 10 or 13)) continue;
            string value = rune.ToString();
            int size = Encoding.UTF8.GetByteCount(SecurityElement.Escape(value)!);
            if (bytes+size > 3000)
            {
                string pending=part.ToString(); int split=pending.Length;
                for (int i=pending.Length-1; i>pending.Length/2; i--)
                    if (char.IsWhiteSpace(pending[i])) { split=i+1; break; }
                if (!string.IsNullOrWhiteSpace(pending[..split])) yield return pending[..split];
                part.Clear(); part.Append(pending[split..]);
                bytes=Encoding.UTF8.GetByteCount(SecurityElement.Escape(part.ToString())!);
            }
            part.Append(value); bytes += size;
        }
        if (!string.IsNullOrWhiteSpace(part.ToString())) yield return part.ToString();
    }
    public void Dispose() => http.Dispose();
}
