// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using ScreenReader.Speech;

namespace ScreenReader.Edge;

public sealed class SpeechServiceException(string voice,int attempts,bool audioStarted,Exception inner)
    : IOException(audioStarted
        ? SpeechText.Get("edge.interrupted","Edge TTS прервал озвучивание голосом {0}. Повторите чтение или выберите другой голос.","Edge TTS interrupted voice {0}. Try reading again or select another voice.",voice)
        : SpeechText.Get("edge.retries","Edge TTS не вернул аудио голоса {0} после {1} попыток. Попробуйте другой голос или повторите позже.","Edge TTS returned no audio for voice {0} after {1} attempts. Try another voice or retry later.",voice,attempts),inner)
{
    public string Voice { get; }=voice;
    public int Attempts { get; }=attempts;
    public bool AudioStarted { get; }=audioStarted;
}

public static class SpeechStreamRetry
{
    public static async IAsyncEnumerable<byte[]> Run(
        Func<CancellationToken,IAsyncEnumerable<byte[]>> open,string voice,
        [EnumeratorCancellation] CancellationToken cancellationToken,TimeSpan? retryDelay=null)
    {
        for (int attempt=1; attempt<=3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            bool audioStarted=false, retry=false;
            await using var source=open(cancellationToken).GetAsyncEnumerator(cancellationToken);
            while (true)
            {
                bool next;
                try { next=await source.MoveNextAsync().ConfigureAwait(false); }
                catch (Exception e) when (!cancellationToken.IsCancellationRequested &&
                    e is WebSocketException or HttpRequestException or OperationCanceledException or IOException)
                {
                    if (audioStarted || attempt==3) throw new SpeechServiceException(voice,attempt,audioStarted,e);
                    retry=true; break;
                }
                if (!next) break;
                if (source.Current.Length==0) continue;
                audioStarted=true;
                yield return source.Current;
            }
            if (!retry) yield break;
            await Task.Delay(retryDelay ?? TimeSpan.FromMilliseconds(350*attempt),cancellationToken).ConfigureAwait(false);
        }
    }
}
